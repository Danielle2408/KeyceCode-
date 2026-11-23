using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace KeyceCode.Services;

public sealed record ExtensionHostMessage(string Type, string? RequestId, string? ExtensionId,
    string? Command, string? Message, string? Error, string? Level, IReadOnlyList<string>? Commands,
    JsonElement? Arguments, JsonElement? Result);

/// <summary>Runs VS Code-style Node extensions in a separate, user-authorized process.</summary>
public sealed class ExtensionHostService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions MessageJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _nodeExecutable;
    private readonly string _hostScript;
    private readonly string _storagePath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ExtensionHostMessage>> _pending = new(StringComparer.Ordinal);
    private Process? _process;
    private Task? _readTask;
    private Task? _errorTask;
    private Action<ExtensionHostMessage>? _messageHandler;
    private volatile bool _ready;

    public bool IsRunning => _process is { HasExited: false };

    public ExtensionHostService(string? nodeExecutable = null, string? hostScript = null, string? storagePath = null)
    {
        var bundledNode = Path.Combine(AppContext.BaseDirectory, "ExtensionHost", "node.exe");
        _nodeExecutable = string.IsNullOrWhiteSpace(nodeExecutable)
            ? File.Exists(bundledNode) ? bundledNode : "node"
            : nodeExecutable;
        _hostScript = Path.GetFullPath(hostScript ?? Path.Combine(AppContext.BaseDirectory, "ExtensionHost", "host.js"));
        _storagePath = Path.GetFullPath(storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyceCode", "extension-storage"));
    }

    public async Task StartAsync(Action<ExtensionHostMessage> messageHandler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageHandler);
        if (IsRunning) return;
        if (!File.Exists(_hostScript)) throw new FileNotFoundException("Le script de l’hôte d’extensions est absent.", _hostScript);
        Directory.CreateDirectory(_storagePath);
        var start = new ProcessStartInfo(_nodeExecutable)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(_hostScript);
        start.Environment["NODE_NO_WARNINGS"] = "1";
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("Node.js n’a pas pu démarrer l’hôte d’extensions.");
        _ready = false;
        _process = process;
        _messageHandler = messageHandler;
        _readTask = ReadMessagesAsync(process, cancellationToken);
        _errorTask = ReadErrorsAsync(process, cancellationToken);
        if (!_ready && await WaitForMessageAsync(message => message.Type == "ready", TimeSpan.FromSeconds(10), cancellationToken) is null)
        {
            await StopAsync();
            throw new InvalidOperationException("L’hôte Node.js n’a pas envoyé son état prêt dans le délai imparti.");
        }
    }

    public async Task<ExtensionHostMessage> ActivateAsync(InstalledExtension extension, string? workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extension);
        var requestId = NextRequestId();
        var completion = AddPending(requestId);
        await SendAsync(new
        {
            type = "activate", requestId, path = extension.Path, workspaceRoot, storagePath = _storagePath
        }, cancellationToken);
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
    }

    public async Task InvokeCommandAsync(string extensionId, string command, JsonElement? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var requestId = NextRequestId();
        var completion = AddPending(requestId);
        await SendAsync(new
        {
            type = "invokeCommand", requestId, extensionId, command,
            args = arguments is { ValueKind: JsonValueKind.Array } value ? value : JsonSerializer.SerializeToElement(Array.Empty<object>())
        }, cancellationToken);
        var response = await completion.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
        if (response.Type == "commandFailed") throw new InvalidOperationException(response.Error ?? "La commande d’extension a échoué.");
    }

    private TaskCompletionSource<ExtensionHostMessage> AddPending(string requestId)
    {
        var source = new TaskCompletionSource<ExtensionHostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, source)) throw new InvalidOperationException("Identifiant de requête d’extension dupliqué.");
        return source;
    }

    private static string NextRequestId() => Guid.NewGuid().ToString("N");

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null || process.HasExited) throw new InvalidOperationException("L’hôte d’extensions n’est pas démarré.");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    private async Task ReadMessagesAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null) break;
                ExtensionHostMessage? message;
                try { message = JsonSerializer.Deserialize<ExtensionHostMessage>(line, MessageJsonOptions); }
                catch (JsonException) { _messageHandler?.Invoke(new("hostError", null, null, null, null, "Message JSON invalide reçu de l’hôte.", null, null, null, null)); continue; }
                if (message is null) continue;
                if (message.Type == "ready") _ready = true;
                if (message.RequestId is { } requestId && _pending.TryGetValue(requestId, out var completion) &&
                    message.Type is "activated" or "activationFailed" or "commandCompleted" or "commandFailed")
                {
                    _pending.TryRemove(requestId, out _);
                    completion.TrySetResult(message);
                }
                _messageHandler?.Invoke(message);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        finally
        {
            foreach (var pending in _pending.Values) pending.TrySetException(new InvalidOperationException("L’hôte d’extensions s’est arrêté."));
            _pending.Clear();
        }
    }

    private static async Task ReadErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null) break;
                // Node extension console output is sent to stderr to protect the JSON-RPC stdout channel.
                System.Diagnostics.Trace.WriteLine("[Extension Host] " + line);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private async Task<ExtensionHostMessage?> WaitForMessageAsync(Func<ExtensionHostMessage, bool> predicate,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_ready && predicate(new("ready", null, null, null, null, null, null, null, null, null)))
            return new("ready", null, null, null, null, null, null, null, null, null);
        var source = new TaskCompletionSource<ExtensionHostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prior = _messageHandler;
        _messageHandler = message => { prior?.Invoke(message); if (predicate(message)) source.TrySetResult(message); };
        try { return await source.Task.WaitAsync(timeout, cancellationToken); }
        catch (TimeoutException) { return null; }
        finally { _messageHandler = prior; }
    }

    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try { if (!process.HasExited) { process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } }
        catch (TimeoutException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        catch (InvalidOperationException) { }
        process.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_readTask is not null) try { await _readTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        if (_errorTask is not null) try { await _errorTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        _writeGate.Dispose();
    }
}
