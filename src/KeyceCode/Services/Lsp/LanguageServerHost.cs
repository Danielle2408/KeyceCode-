using System.Text.Json;

namespace KeyceCode.Services.Lsp;

/// <summary>Completion information returned by a language server.</summary>
public sealed record LspCompletionItem(
    string Label,
    string? Detail,
    string? Documentation,
    string? InsertText,
    string? Kind,
    string? SortText,
    JsonElement Raw);

/// <summary>Range in LSP's zero-based line/character coordinate system.</summary>
public sealed record LspRange(int StartLine, int StartCharacter, int EndLine, int EndCharacter);

/// <summary>A diagnostic published by a language server.</summary>
public sealed record LspDiagnostic(
    LspRange Range,
    string Message,
    string Severity,
    string? Code,
    string? Source,
    JsonElement Raw);

/// <summary>Diagnostics for one document, as published by the language server.</summary>
public sealed record LspDiagnostics(Uri DocumentUri, int? Version, IReadOnlyList<LspDiagnostic> Items);

/// <summary>
/// Owns a single language server lifecycle and exposes the common editor-facing LSP operations.
/// The caller is responsible for selecting/locating the language server executable and its arguments.
/// </summary>
/// <remarks>
/// This initial host supports startup, initialization, full text synchronization, completion requests,
/// diagnostics notifications, generic requests/notifications, and clean shutdown. It does not yet
/// implement incremental text edits, workspace configuration/applyEdit requests, dynamic capability
/// registration, code-action application, document formatting helpers, or an extension installer.
/// </remarks>
public sealed class LanguageServerHost : IAsyncDisposable
{
    private readonly LspClient _client;
    private bool _started;
    private bool _disposed;
    private int _documentVersion;

    public LanguageServerHost(LspClient? client = null)
    {
        _client = client ?? new LspClient();
        _client.NotificationReceived += OnNotificationReceived;
        _client.ServerLogReceived += OnServerLogReceived;
        _client.TransportError += OnTransportError;
    }

    public event EventHandler<LspDiagnostics>? DiagnosticsPublished;
    public event EventHandler<string>? ServerLogReceived;
    public event EventHandler<Exception>? TransportError;

    public bool IsRunning => _client.IsRunning;
    public bool IsInitialized => _client.IsInitialized;
    public JsonElement ServerCapabilities => _client.ServerCapabilities;

    /// <summary>Starts the configured process and completes the LSP initialize handshake.</summary>
    public async Task<JsonElement> StartAsync(LspServerOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new InvalidOperationException("The language server host is already started.");

        try
        {
            await _client.StartAsync(options, cancellationToken).ConfigureAwait(false);
            _started = true;
            return await _client.InitializeAsync(options.RootPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _started = false;
            throw;
        }
    }

    /// <summary>Opens a document and sends its initial full text to the server.</summary>
    public async Task<int> OpenDocumentAsync(Uri documentUri, string languageId, string text,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var version = Interlocked.Increment(ref _documentVersion);
        await _client.DidOpenTextDocumentAsync(documentUri, languageId, version, text, cancellationToken).ConfigureAwait(false);
        return version;
    }

    /// <summary>Sends a full-text replacement after the document has changed.</summary>
    public async Task<int> ChangeDocumentAsync(Uri documentUri, string text,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var version = Interlocked.Increment(ref _documentVersion);
        await _client.DidChangeTextDocumentAsync(documentUri, version, text, cancellationToken).ConfigureAwait(false);
        return version;
    }

    public Task SaveDocumentAsync(Uri documentUri, string? text = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return _client.DidSaveTextDocumentAsync(documentUri, text, cancellationToken);
    }

    public Task CloseDocumentAsync(Uri documentUri, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return _client.DidCloseTextDocumentAsync(documentUri, cancellationToken);
    }

    /// <summary>Requests completion at a zero-based document position.</summary>
    public async Task<IReadOnlyList<LspCompletionItem>> CompleteAsync(
        Uri documentUri,
        int line,
        int character,
        int triggerKind = 1,
        string? triggerCharacter = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (line < 0) throw new ArgumentOutOfRangeException(nameof(line));
        if (character < 0) throw new ArgumentOutOfRangeException(nameof(character));

        var parameters = new Dictionary<string, object?>
        {
            ["textDocument"] = new { uri = documentUri.AbsoluteUri },
            ["position"] = new { line, character },
            ["context"] = new { triggerKind, triggerCharacter }
        };
        var response = await _client.RequestAsync<JsonElement>("textDocument/completion", parameters, cancellationToken)
            .ConfigureAwait(false);
        return ParseCompletionItems(response);
    }

    /// <summary>Forwards another LSP request when the caller needs an operation not yet wrapped here.</summary>
    public Task<JsonElement> RequestAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return _client.RequestAsync(method, parameters, cancellationToken);
    }

    public Task NotifyAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return _client.NotifyAsync(method, parameters, cancellationToken);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        _client.ShutdownAsync(cancellationToken);

    private static IReadOnlyList<LspCompletionItem> ParseCompletionItems(JsonElement response)
    {
        JsonElement items;
        if (response.ValueKind == JsonValueKind.Array)
        {
            items = response;
        }
        else if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("items", out var list))
        {
            items = list;
        }
        else
        {
            return Array.Empty<LspCompletionItem>();
        }

        if (items.ValueKind != JsonValueKind.Array) return Array.Empty<LspCompletionItem>();
        var results = new List<LspCompletionItem>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var label = GetString(item, "label");
            if (string.IsNullOrWhiteSpace(label)) continue;
            results.Add(new LspCompletionItem(
                label,
                GetString(item, "detail"),
                GetDocumentation(item),
                GetString(item, "insertText"),
                GetScalarString(item, "kind"),
                GetString(item, "sortText"),
                item.Clone()));
        }
        return results;
    }

    private static string? GetDocumentation(JsonElement item)
    {
        if (!item.TryGetProperty("documentation", out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var markup))
            return markup.ValueKind == JsonValueKind.String ? markup.GetString() : null;
        return null;
    }

    private void OnNotificationReceived(object? sender, JsonElement message)
    {
        if (!message.TryGetProperty("method", out var method) || method.GetString() != "textDocument/publishDiagnostics") return;
        if (!message.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("uri", out var uriElement) || uriElement.GetString() is not { } uriText ||
            !Uri.TryCreate(uriText, UriKind.Absolute, out var uri)) return;

        var version = GetInt32(parameters, "version");
        var diagnostics = new List<LspDiagnostic>();
        if (parameters.TryGetProperty("diagnostics", out var collection) && collection.ValueKind == JsonValueKind.Array)
        {
            foreach (var diagnostic in collection.EnumerateArray())
            {
                if (!diagnostic.TryGetProperty("range", out var rangeJson)) continue;
                var range = new LspRange(
                    GetPositionValue(rangeJson, "start", "line"),
                    GetPositionValue(rangeJson, "start", "character"),
                    GetPositionValue(rangeJson, "end", "line"),
                    GetPositionValue(rangeJson, "end", "character"));
                diagnostics.Add(new LspDiagnostic(
                    range,
                    GetString(diagnostic, "message") ?? string.Empty,
                    GetSeverity(diagnostic),
                    GetScalarString(diagnostic, "code"),
                    GetString(diagnostic, "source"),
                    diagnostic.Clone()));
            }
        }

        try { DiagnosticsPublished?.Invoke(this, new LspDiagnostics(uri, version, diagnostics)); }
        catch (Exception ex) { RaiseTransportError(ex); }
    }

    private static string GetSeverity(JsonElement diagnostic)
    {
        return GetInt32(diagnostic, "severity") switch
        {
            1 => "Error",
            2 => "Warning",
            3 => "Information",
            4 => "Hint",
            _ => "Unknown"
        };
    }

    private static int GetPositionValue(JsonElement range, string endpoint, string coordinate)
    {
        return range.TryGetProperty(endpoint, out var position) ? GetInt32(position, coordinate) ?? 0 : 0;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static string? GetScalarString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;
    }

    private void OnServerLogReceived(object? sender, string message)
    {
        try { ServerLogReceived?.Invoke(this, message); }
        catch (Exception ex) { RaiseTransportError(ex); }
    }

    private void OnTransportError(object? sender, Exception exception) => RaiseTransportError(exception);

    private void RaiseTransportError(Exception exception)
    {
        try { TransportError?.Invoke(this, exception); }
        catch { }
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_client.IsInitialized) throw new InvalidOperationException("The language server has not completed initialization.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _client.NotificationReceived -= OnNotificationReceived;
        _client.ServerLogReceived -= OnServerLogReceived;
        _client.TransportError -= OnTransportError;
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
