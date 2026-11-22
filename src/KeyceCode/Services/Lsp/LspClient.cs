using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeyceCode.Services.Lsp;

/// <summary>Launch configuration for a language server. The executable is supplied by the user or discovery layer.</summary>
public sealed record LspServerOptions
{
    public required string ExecutablePath { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public string? RootPath { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumMessageBytes { get; init; } = 32 * 1024 * 1024;
}

/// <summary>Minimal JSON-RPC 2.0 client for Language Server Protocol servers over stdio.</summary>
/// <remarks>
/// This transport does not yet implement the complete LSP client capability set, dynamic registration,
/// workspace edits, progress reporting, or server-initiated workspace requests. Unknown server requests
/// receive JSON-RPC method-not-found rather than being left pending.
/// </remarks>
public sealed class LspClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private Stream? _serverInput;
    private Stream? _serverOutput;
    private Task? _readTask;
    private Task? _stderrTask;
    private int _nextRequestId;
    private bool _started;
    private bool _initialized;
    private bool _disposed;
    private TimeSpan _requestTimeout = TimeSpan.FromSeconds(30);
    private int _maximumMessageBytes = 32 * 1024 * 1024;
    private string? _rootPath;

    public event EventHandler<JsonElement>? NotificationReceived;
    public event EventHandler<string>? ServerLogReceived;
    public event EventHandler<Exception>? TransportError;

    public bool IsRunning => _process is { HasExited: false };
    public bool IsInitialized => _initialized;
    public JsonElement ServerCapabilities { get; private set; }

    public async Task StartAsync(LspServerOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);
        if (_started) throw new InvalidOperationException("The language server process has already been started.");
        if (options.MaximumMessageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumMessageBytes));

        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            WorkingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
                ? (string.IsNullOrWhiteSpace(options.RootPath) ? Environment.CurrentDirectory : Path.GetFullPath(options.RootPath))
                : Path.GetFullPath(options.WorkingDirectory),
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
        process.Exited += (_, _) => FailPending(new InvalidOperationException(
            $"Language server process exited with code {SafeExitCode(process)}."));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("The language server process could not be started.");
        }
        catch
        {
            process.Dispose();
            throw;
        }

        _process = process;
        _serverInput = process.StandardInput.BaseStream;
        _serverOutput = process.StandardOutput.BaseStream;
        _requestTimeout = options.RequestTimeout > TimeSpan.Zero ? options.RequestTimeout : TimeSpan.FromSeconds(30);
        _maximumMessageBytes = options.MaximumMessageBytes;
        _rootPath = string.IsNullOrWhiteSpace(options.RootPath) ? null : Path.GetFullPath(options.RootPath);
        _started = true;
        _readTask = Task.Run(() => ReadMessagesAsync(_lifetime.Token));
        _stderrTask = Task.Run(() => ReadStandardErrorAsync(process, line =>
        {
            try { ServerLogReceived?.Invoke(this, line); }
            catch (Exception ex) { RaiseTransportError(ex); }
        }, _lifetime.Token));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Sends the LSP initialize request and then the initialized notification.</summary>
    public async Task<JsonElement> InitializeAsync(
        string? rootPath = null,
        object? capabilities = null,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        if (_initialized) throw new InvalidOperationException("The language server is already initialized.");

        var normalizedRoot = string.IsNullOrWhiteSpace(rootPath) ? _rootPath : Path.GetFullPath(rootPath);
        var rootUri = normalizedRoot is null ? null : new Uri(normalizedRoot).AbsoluteUri;
        var workspaceFolders = normalizedRoot is null
            ? null
            : new[] { new { uri = rootUri, name = Path.GetFileName(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) } };

        var result = await RequestAsync<JsonElement>("initialize", new
        {
            processId = Environment.ProcessId,
            clientInfo = new { name = "KeyceCode", version = typeof(LspClient).Assembly.GetName().Version?.ToString() ?? "0.1.0" },
            rootUri,
            rootPath = normalizedRoot,
            initializationOptions = (object?)null,
            capabilities = capabilities ?? new
            {
                general = new { positionEncodings = new[] { "utf-16" } },
                textDocument = new
                {
                    synchronization = new { dynamicRegistration = false, willSave = false, willSaveWaitUntil = false, didSave = true },
                    completion = new { dynamicRegistration = false, completionItem = new { snippetSupport = true, documentationFormat = new[] { "markdown", "plaintext" }, resolveSupport = new { properties = new[] { "documentation", "detail", "additionalTextEdits" } } } },
                    hover = new { dynamicRegistration = false, contentFormat = new[] { "markdown", "plaintext" } },
                    definition = new { dynamicRegistration = false },
                    references = new { dynamicRegistration = false },
                    documentSymbol = new { dynamicRegistration = false, hierarchicalDocumentSymbolSupport = true },
                    publishDiagnostics = new { relatedInformation = true, versionSupport = true },
                    formatting = new { dynamicRegistration = false },
                    codeAction = new { dynamicRegistration = false, codeActionLiteralSupport = new { codeActionKind = new { valueSet = new[] { "", "quickfix", "refactor", "source" } } } }
                },
                workspace = new { workspaceFolders = true, configuration = true, applyEdit = true }
            },
            trace = "off",
            workspaceFolders
        }, cancellationToken).ConfigureAwait(false);

        if (result.TryGetProperty("capabilities", out var serverCapabilities))
            ServerCapabilities = serverCapabilities.Clone();
        else
        {
            using var emptyCapabilities = JsonDocument.Parse("{}");
            ServerCapabilities = emptyCapabilities.RootElement.Clone();
        }

        await NotifyAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
        _initialized = true;
        return result;
    }

    public Task<JsonElement> RequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        RequestAsync<JsonElement>(method, parameters, cancellationToken);

    public async Task<T?> RequestAsync<T>(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        cancellationToken.ThrowIfCancellationRequested();

        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("Could not register JSON-RPC request.");

        try
        {
            await WriteMessageAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
            var response = await completion.Task.WaitAsync(GetTimeout(cancellationToken), cancellationToken).ConfigureAwait(false);
            if (typeof(T) == typeof(JsonElement)) return (T)(object)response;
            return response.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? default
                : response.Deserialize<T>(JsonOptions);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return WriteMessageAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);
    }

    public Task DidOpenTextDocumentAsync(Uri documentUri, string languageId, int version, string text,
        CancellationToken cancellationToken = default) =>
        NotifyAsync("textDocument/didOpen", new
        {
            textDocument = new { uri = documentUri.AbsoluteUri, languageId, version, text }
        }, cancellationToken);

    public Task DidChangeTextDocumentAsync(Uri documentUri, int version, string fullText,
        CancellationToken cancellationToken = default) =>
        NotifyAsync("textDocument/didChange", new
        {
            textDocument = new { uri = documentUri.AbsoluteUri, version },
            contentChanges = new[] { new { text = fullText } }
        }, cancellationToken);

    public Task DidSaveTextDocumentAsync(Uri documentUri, string? text = null,
        CancellationToken cancellationToken = default) =>
        NotifyAsync("textDocument/didSave", new
        {
            textDocument = new { uri = documentUri.AbsoluteUri },
            text
        }, cancellationToken);

    public Task DidCloseTextDocumentAsync(Uri documentUri, CancellationToken cancellationToken = default) =>
        NotifyAsync("textDocument/didClose", new
        {
            textDocument = new { uri = documentUri.AbsoluteUri }
        }, cancellationToken);

    /// <summary>Sends the standard shutdown request and exit notification.</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (!_started || _process is null || _process.HasExited) return;
        if (_initialized)
        {
            using var shutdownTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            shutdownTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            try { await RequestAsync<JsonElement>("shutdown", null, shutdownTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (LspRequestException) { }
            catch (IOException) { }
            finally { _initialized = false; }
        }
        try { await NotifyAsync("exit", null, cancellationToken).ConfigureAwait(false); }
        catch (IOException) { }
        try { _serverInput?.Close(); } catch (IOException) { }
        if (_process is { HasExited: false })
        {
            try { await _process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        var output = _serverInput ?? throw new InvalidOperationException("Language server input is unavailable.");
        var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
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
                var body = await ReadBodyAsync(_serverOutput!, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                if (root.TryGetProperty("id", out var idElement))
                {
                    if (idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out var id) && _pending.TryGetValue(id, out var pending))
                    {
                        if (root.TryGetProperty("error", out var error))
                            pending.TrySetException(new LspRequestException(id, error));
                        else if (root.TryGetProperty("result", out var result))
                            pending.TrySetResult(result.Clone());
                        else
                            pending.TrySetResult(JsonDocument.Parse("null").RootElement.Clone());
                    }
                    else if (root.TryGetProperty("method", out var serverMethod) && serverMethod.ValueKind == JsonValueKind.String)
                    {
                        var serverId = idElement.Clone();
                        await WriteMessageAsync(new
                        {
                            jsonrpc = "2.0",
                            id = serverId,
                            error = new { code = -32601, message = "Client-initiated server requests are not implemented by KeyceCode yet." }
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }

                if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
                    RaiseNotification(root.Clone());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            FailPending(ex);
            RaiseTransportError(ex);
        }
    }

    private async Task<byte[]> ReadBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var headerBytes = new MemoryStream();
        var oneByte = new byte[1];
        var matched = 0;
        var terminator = new byte[] { 13, 10, 13, 10 };
        while (matched < terminator.Length)
        {
            var count = await stream.ReadAsync(oneByte.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Language server closed its output stream.");
            headerBytes.WriteByte(oneByte[0]);
            matched = oneByte[0] == terminator[matched] ? matched + 1 : oneByte[0] == terminator[0] ? 1 : 0;
            if (headerBytes.Length > 16 * 1024) throw new InvalidDataException("LSP response headers exceed 16 KiB.");
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var contentLength = -1;
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            if (line[..separator].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(separator + 1)..].Trim(), out var parsedLength))
                contentLength = parsedLength;
        }

        if (contentLength < 0) throw new InvalidDataException("LSP response is missing a valid Content-Length header.");
        if (contentLength > _maximumMessageBytes)
            throw new InvalidDataException($"LSP response exceeds the configured {_maximumMessageBytes}-byte transport limit.");
        var body = new byte[contentLength];
        var offset = 0;
        while (offset < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Language server closed output before the full LSP message arrived.");
            offset += read;
        }
        return body;
    }

    private static async Task ReadStandardErrorAsync(Process process, Action<string> onLine, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                onLine(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
    }

    private TimeSpan GetTimeout(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        // Request timeout is controlled by callers via cancellation. This default guard prevents orphaned requests.
        return _requestTimeout;
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started || !IsRunning) throw new InvalidOperationException("The language server is not running.");
    }

    private void FailPending(Exception exception)
    {
        foreach (var entry in _pending.ToArray())
            if (_pending.TryRemove(entry.Key, out var completion)) completion.TrySetException(exception);
    }

    private void RaiseNotification(JsonElement message)
    {
        try { NotificationReceived?.Invoke(this, message); }
        catch (Exception ex) { RaiseTransportError(ex); }
    }

    private void RaiseTransportError(Exception exception)
    {
        try { TransportError?.Invoke(this, exception); }
        catch { /* Event handlers must not terminate the transport reader. */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await ShutdownAsync().ConfigureAwait(false); }
        catch { }
        _disposed = true;
        _lifetime.Cancel();
        FailPending(new ObjectDisposedException(nameof(LspClient)));
        if (_readTask is not null) { try { await _readTask.ConfigureAwait(false); } catch { } }
        if (_stderrTask is not null) { try { await _stderrTask.ConfigureAwait(false); } catch { } }
        _serverInput?.Dispose();
        _serverOutput?.Dispose();
        _process?.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
    }
}

public sealed class LspRequestException(long requestId, JsonElement error)
    : Exception($"LSP request {requestId} failed: {FormatError(error)}")
{
    public long RequestId { get; } = requestId;
    public JsonElement Error { get; } = error.Clone();

    private static string FormatError(JsonElement error)
    {
        var code = error.TryGetProperty("code", out var codeElement) ? codeElement.ToString() : "?";
        var message = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : "Unknown LSP error";
        return $"[{code}] {message}";
    }
}
