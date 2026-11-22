using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace KeyceCode.Services.Dap;

public sealed record DapAdapterOptions
{
    public required string ExecutablePath { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int MaximumMessageBytes { get; init; } = 16 * 1024 * 1024;
}

public sealed class DapEventArgs(string eventName, JsonElement body) : EventArgs
{
    public string EventName { get; } = eventName;
    public JsonElement Body { get; } = body.Clone();
}

/// <summary>Minimal Debug Adapter Protocol client over a stdio adapter process.</summary>
/// <remarks>
/// This transport handles DAP framing, request/response correlation, events and the initialize/launch
/// lifecycle. UI state, launch.json resolution, breakpoints and debug views still require integration.
/// </remarks>
public sealed class DapClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private Stream? _input;
    private Stream? _output;
    private Task? _reader;
    private Task? _stderrReader;
    private int _nextSequence;
    private int _maximumMessageBytes;
    private TimeSpan _requestTimeout;
    private bool _started;
    private bool _initialized;
    private bool _disposed;

    public event EventHandler<DapEventArgs>? EventReceived;
    public event EventHandler<string>? OutputReceived;
    public event EventHandler<string>? ProtocolStatusReceived;
    public event EventHandler<Exception>? TransportError;
    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(DapAdapterOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);
        if (_started) throw new InvalidOperationException("The debug adapter has already been started.");
        if (options.MaximumMessageBytes is < 1 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(options.MaximumMessageBytes));

        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            WorkingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? Environment.CurrentDirectory : Path.GetFullPath(options.WorkingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in options.Arguments) startInfo.ArgumentList.Add(argument);
        foreach (var (key, value) in options.Environment)
        {
            if (value is null) startInfo.Environment.Remove(key);
            else startInfo.Environment[key] = value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += (_, _) => FailPending(new InvalidOperationException($"Debug adapter exited with code {SafeExitCode(process)}."));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The debug adapter process could not be started.");
        }
        catch { process.Dispose(); throw; }
        _process = process;
        _input = process.StandardInput.BaseStream;
        _output = process.StandardOutput.BaseStream;
        _maximumMessageBytes = options.MaximumMessageBytes;
        _requestTimeout = options.RequestTimeout > TimeSpan.Zero ? options.RequestTimeout : TimeSpan.FromSeconds(15);
        _started = true;
        _reader = Task.Run(() => ReadMessagesAsync(_lifetime.Token));
        _stderrReader = Task.Run(() => ReadStandardErrorAsync(process, _lifetime.Token));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<JsonElement> InitializeAsync(string adapterId, string locale = "en-US", CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        if (_initialized) throw new InvalidOperationException("The debug adapter is already initialized.");
        var response = await RequestAsync("initialize", new
        {
            clientID = "keycecode",
            clientName = "KeyceCode",
            adapterID = adapterId,
            locale,
            linesStartAt1 = true,
            columnsStartAt1 = true,
            pathFormat = "path",
            supportsVariableType = true,
            supportsVariablePaging = true,
            supportsRunInTerminalRequest = false,
            supportsMemoryReferences = false,
            supportsProgressReporting = true,
            supportsInvalidatedEvent = true
        }, cancellationToken).ConfigureAwait(false);
        _initialized = true;
        return response;
    }

    public Task<JsonElement> LaunchAsync(object configuration, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return RequestAsync("launch", configuration, cancellationToken);
    }

    public Task<JsonElement> AttachAsync(object configuration, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return RequestAsync("attach", configuration, cancellationToken);
    }

    public Task<JsonElement> ConfigurationDoneAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return RequestAsync("configurationDone", new { }, cancellationToken);
    }

    public async Task<JsonElement> RequestAsync(string command, object? arguments = null, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        cancellationToken.ThrowIfCancellationRequested();
        var sequence = Interlocked.Increment(ref _nextSequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(sequence, completion)) throw new InvalidOperationException("Could not register DAP request.");
        try
        {
            RaiseProtocolStatus($"→ request {command} seq={sequence}");
            await WriteMessageAsync(new { seq = sequence, type = "request", command, arguments }, cancellationToken).ConfigureAwait(false);
            var response = await completion.Task.WaitAsync(_requestTimeout, cancellationToken).ConfigureAwait(false);
            if (response.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            {
                var message = response.TryGetProperty("message", out var detail) ? detail.GetString() : null;
                throw new DapRequestException(command, message ?? "The adapter rejected the request.", response);
            }
            return response.TryGetProperty("body", out var body) ? body.Clone() : EmptyObject();
        }
        finally { _pending.TryRemove(sequence, out _); }
    }

    public async Task DisconnectAsync(bool terminateDebuggee = true, CancellationToken cancellationToken = default)
    {
        if (!IsRunning) return;
        if (_initialized)
        {
            try { await RequestAsync("disconnect", new { restart = false, terminateDebuggee }, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or TimeoutException or DapRequestException or InvalidOperationException) { }
            _initialized = false;
        }
        if (_process is { HasExited: false })
        {
            try { await _process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        }
    }

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        var output = _input ?? throw new InvalidOperationException("Debug adapter input is unavailable.");
        var body = JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    private async Task ReadMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var document = JsonDocument.Parse(await ReadBodyAsync(_output!, cancellationToken).ConfigureAwait(false));
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                var messageLabel = type switch
                {
                    "response" => $"← response {ReadString(root, "command")} request_seq={ReadInt(root, "request_seq")} success={ReadBool(root, "success")}",
                    "event" => $"← event {ReadString(root, "event")}",
                    "request" => $"← adapter request {ReadString(root, "command")} seq={ReadInt(root, "seq")}",
                    _ => "← unknown DAP message"
                };
                RaiseProtocolStatus(messageLabel);
                if (type == "response" && root.TryGetProperty("request_seq", out var sequenceElement) && sequenceElement.TryGetInt32(out var requestSequence) && _pending.TryGetValue(requestSequence, out var pending))
                    pending.TrySetResult(root.Clone());
                else if (type == "event" && root.TryGetProperty("event", out var eventElement) && eventElement.GetString() is { Length: > 0 } eventName)
                    RaiseEvent(new DapEventArgs(eventName, root.TryGetProperty("body", out var eventBody) ? eventBody : EmptyObject()));
                else if (type == "request" && root.TryGetProperty("seq", out var adapterSequence) && adapterSequence.TryGetInt32(out var adapterRequestSequence))
                    await WriteMessageAsync(new { seq = Interlocked.Increment(ref _nextSequence), type = "response", request_seq = adapterRequestSequence, success = false, command = root.TryGetProperty("command", out var adapterCommand) ? adapterCommand.GetString() : null, message = "This DAP client does not support adapter-initiated requests." }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { FailPending(ex); RaiseTransportError(ex); }
    }

    private async Task<byte[]> ReadBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var headers = new MemoryStream();
        var one = new byte[1];
        var matched = 0;
        byte[] terminator = [13, 10, 13, 10];
        while (matched < terminator.Length)
        {
            var read = await stream.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Debug adapter closed its output stream.");
            headers.WriteByte(one[0]);
            matched = one[0] == terminator[matched] ? matched + 1 : one[0] == terminator[0] ? 1 : 0;
            if (headers.Length > 16 * 1024) throw new InvalidDataException("DAP headers exceed 16 KiB.");
        }
        var length = -1;
        foreach (var line in Encoding.ASCII.GetString(headers.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator > 0 && line[..separator].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && int.TryParse(line[(separator + 1)..].Trim(), out var parsed)) length = parsed;
        }
        if (length < 0 || length > _maximumMessageBytes) throw new InvalidDataException("DAP message is missing a valid Content-Length or exceeds the configured limit.");
        var body = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Debug adapter closed output before the full DAP message arrived.");
            offset += read;
        }
        return body;
    }

    private async Task ReadStandardErrorAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                try { OutputReceived?.Invoke(this, line); } catch (Exception ex) { RaiseError(ex); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
    }

    private void EnsureRunning()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started || !IsRunning) throw new InvalidOperationException("The debug adapter is not running.");
    }

    private void EnsureInitialized() { EnsureRunning(); if (!_initialized) throw new InvalidOperationException("Initialize the debug adapter before launching a debug session."); }
    private static JsonElement EmptyObject() { using var document = JsonDocument.Parse("{}"); return document.RootElement.Clone(); }
    private static int SafeExitCode(Process process) { try { return process.ExitCode; } catch (InvalidOperationException) { return -1; } }
    private void FailPending(Exception exception) { foreach (var pair in _pending.ToArray()) if (_pending.TryRemove(pair.Key, out var pending)) pending.TrySetException(exception); }
    private void RaiseEvent(DapEventArgs args) { try { EventReceived?.Invoke(this, args); } catch (Exception ex) { RaiseError(ex); } }
    private void RaiseTransportError(Exception exception) { try { TransportError?.Invoke(this, exception); } catch { } }
    private void RaiseProtocolStatus(string message) { try { ProtocolStatusReceived?.Invoke(this, message); } catch { } }
    private void RaiseError(Exception exception) { try { TransportError?.Invoke(this, exception); } catch { } }
    private static string ReadString(JsonElement json, string name) => json.TryGetProperty(name, out var value) ? value.ToString() : "?";
    private static string ReadInt(JsonElement json, string name) => json.TryGetProperty(name, out var value) ? value.ToString() : "?";
    private static string ReadBool(JsonElement json, string name) => json.TryGetProperty(name, out var value) ? value.ToString() : "?";

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await DisconnectAsync().ConfigureAwait(false); } catch { }
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
        }
        _disposed = true;
        _lifetime.Cancel();
        FailPending(new ObjectDisposedException(nameof(DapClient)));
        if (_reader is not null) { try { await _reader.ConfigureAwait(false); } catch { } }
        if (_stderrReader is not null) { try { await _stderrReader.ConfigureAwait(false); } catch { } }
        _input?.Dispose(); _output?.Dispose(); _process?.Dispose(); _writeGate.Dispose(); _lifetime.Dispose();
    }
}

public sealed class DapRequestException(string command, string message, JsonElement response)
    : Exception($"DAP command '{command}' failed: {message}")
{
    public string Command { get; } = command;
    public JsonElement Response { get; } = response.Clone();
}
