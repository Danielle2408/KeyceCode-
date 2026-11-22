using KeyceCode.Services;
using KeyceCode.Services.Lsp;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Text.Json;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace KeyceCode;

public partial class MainWindow : Window
{
    private readonly GeminiService _gemini = new();
    private readonly EditorPreferencesService _preferencesService = new();
    private readonly WorkspaceSessionService _sessionService = new();
    private EditorPreferences _editorPreferences;
    private readonly List<EditorDocument> _documents = [];
    private readonly List<string> _navigationHistory = [];
    private int _navigationHistoryIndex = -1;
    private bool _navigatingHistory;
    private readonly Dictionary<string, Uri> _lspDocumentUris = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LspDiagnostics> _lspDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private LanguageServerHost? _languageServer;
    private WorkspaceService? _workspace;
    private EditorDocument? _activeDocument;
    private string _activePanel = "Terminal";
    private string? _selectedText;
    private int _cursorOffset;
    private bool _webReady;
    private bool _syncingDocument;
    private bool _sidebarVisible = true;
    private bool _panelVisible = true;
    private bool _assistantVisible;
    private string _activeView = "Explorer";
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        InitializeExtensionList();
        _editorPreferences = _preferencesService.Load();
        FormatOnSaveMenuItem.IsChecked = _editorPreferences.FormatOnSave;
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        TerminalSessions.Items.Add("PowerShell");
        TerminalSessions.SelectedIndex = 0;
        TerminalOutput.Text = "KeyceCode integrated terminal\r\n";
        OutputPanel.Text = "KeyceCode output\r\n";
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        ((App)Application.Current).ThemeService.ThemeChanged += (_, theme) =>
        {
            StatusMessage.Text = $"Thème appliqué : {theme.DisplayName}";
            _ = PostEditorMessageAsync(new { type = "setTheme", theme = MonacoTheme(theme.Id) });
        };
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        if (_languageServer is not null)
        {
            try { await _languageServer.DisposeAsync(); }
            catch (Exception ex) { OutputPanel.AppendText("Arrêt LSP : " + ex.Message + Environment.NewLine); }
            _languageServer = null;
        }
        _extensionGallery.Dispose();
    }

    private async Task ConfigureLanguageServerAsync(string workspacePath)
    {
        await StopLanguageServerAsync();
        var executable = Environment.GetEnvironmentVariable("KEYCECODE_LSP_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(executable))
        {
            StatusMessage.Text = "LSP non configuré · KEYCECODE_LSP_EXECUTABLE";
            OutputPanel.AppendText("Language server désactivé : définissez KEYCECODE_LSP_EXECUTABLE (chemin du binaire) et, si besoin, KEYCECODE_LSP_ARGS (tableau JSON d’arguments), puis rouvrez le dossier.\r\n");
            return;
        }

        string[] arguments;
        var argsJson = Environment.GetEnvironmentVariable("KEYCECODE_LSP_ARGS");
        try
        {
            arguments = string.IsNullOrWhiteSpace(argsJson)
                ? Array.Empty<string>()
                : JsonSerializer.Deserialize<string[]>(argsJson) ?? Array.Empty<string>();
        }
        catch (JsonException ex)
        {
            StatusMessage.Text = "Arguments LSP invalides · KEYCECODE_LSP_ARGS doit être un tableau JSON";
            OutputPanel.AppendText("Language server non démarré : KEYCECODE_LSP_ARGS invalide. Exemple : [\"--stdio\"]\r\n" + ex.Message + "\r\n");
            return;
        }

        var host = new LanguageServerHost();
        host.DiagnosticsPublished += LanguageServer_DiagnosticsPublished;
        host.ServerLogReceived += (_, line) => Dispatcher.BeginInvoke(() => OutputPanel.AppendText("[LSP] " + line + Environment.NewLine));
        host.TransportError += (_, error) => Dispatcher.BeginInvoke(() =>
        {
            StatusMessage.Text = "Language server : erreur de transport";
            OutputPanel.AppendText("[LSP] " + error.Message + Environment.NewLine);
        });
        try
        {
            await host.StartAsync(new LspServerOptions
            {
                ExecutablePath = executable,
                Arguments = arguments,
                WorkingDirectory = workspacePath,
                RootPath = workspacePath
            });
            _languageServer = host;
            StatusMessage.Text = $"Language server prêt · {Path.GetFileName(executable)}";
            OutputPanel.AppendText($"Language server initialisé : {Path.GetFileName(executable)} (capabilities reçues).\r\n");
            foreach (var document in _documents.Where(d => IsInsideWorkspace(d.Path, workspacePath)))
                await EnsureLspDocumentAsync(document);
        }
        catch (Exception ex)
        {
            host.DiagnosticsPublished -= LanguageServer_DiagnosticsPublished;
            await host.DisposeAsync();
            _languageServer = null;
            StatusMessage.Text = "Language server indisponible";
            OutputPanel.AppendText($"Language server non démarré ({Path.GetFileName(executable)}) : {ex.Message}\r\n");
        }
    }

    private async Task StopLanguageServerAsync()
    {
        var server = _languageServer;
        _languageServer = null;
        _lspDocumentUris.Clear();
        _lspDiagnostics.Clear();
        RefreshLanguageProblems();
        if (server is null) return;
        server.DiagnosticsPublished -= LanguageServer_DiagnosticsPublished;
        await server.DisposeAsync();
    }

    private async Task EnsureLspDocumentAsync(EditorDocument document)
    {
        var server = _languageServer;
        if (server is null || !server.IsInitialized) return;

        var belongsToWorkspace = _workspace is not null && !string.IsNullOrWhiteSpace(document.Path) &&
            IsInsideWorkspace(document.Path, _workspace.RootPath);
        if (!belongsToWorkspace)
        {
            if (_lspDocumentUris.Remove(document.Id, out var previousUri))
            {
                await server.CloseDocumentAsync(previousUri);
                _lspDiagnostics.Remove(previousUri.LocalPath);
                RefreshLanguageProblems();
            }
            return;
        }

        var targetUri = new Uri(Path.GetFullPath(document.Path));
        if (_lspDocumentUris.TryGetValue(document.Id, out var openUri))
        {
            if (Uri.Compare(openUri, targetUri, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0)
                return;
            await server.CloseDocumentAsync(openUri);
            _lspDocumentUris.Remove(document.Id);
            _lspDiagnostics.Remove(openUri.LocalPath);
            RefreshLanguageProblems();
        }

        await server.OpenDocumentAsync(targetUri, document.Language, document.Text);
        _lspDocumentUris[document.Id] = targetUri;
    }

    private async void LanguageServer_DiagnosticsPublished(object? sender, LspDiagnostics diagnostics)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            var fullPath = Path.GetFullPath(diagnostics.DocumentUri.LocalPath);
            _lspDiagnostics[fullPath] = diagnostics;
            RefreshLanguageProblems();

            var document = _documents.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Path) &&
                string.Equals(Path.GetFullPath(d.Path), fullPath, StringComparison.OrdinalIgnoreCase));
            if (document is not null)
            {
                var markers = diagnostics.Items.Select(item => new
                {
                    startLineNumber = item.Range.StartLine + 1,
                    startColumn = item.Range.StartCharacter + 1,
                    endLineNumber = item.Range.EndLine + 1,
                    endColumn = Math.Max(item.Range.EndCharacter + 1, item.Range.StartCharacter + 2),
                    message = item.Message,
                    severity = item.Severity switch { "Error" => 8, "Warning" => 4, "Information" => 2, "Hint" => 1, _ => 1 },
                    source = item.Source ?? "LSP",
                    code = item.Code
                }).ToArray();
                _ = PostEditorMessageAsync(new { type = "setDiagnostics", id = document.Id, markers });
            }
        });
    }

    private void RefreshLanguageProblems()
    {
        ProblemsPanel.Items.Clear();
        foreach (var (path, diagnostics) in _lspDiagnostics)
        {
            var relativePath = _workspace is not null && IsInsideWorkspace(path, _workspace.RootPath)
                ? Path.GetRelativePath(_workspace.RootPath, path)
                : path;
            foreach (var diagnostic in diagnostics.Items)
                ProblemsPanel.Items.Add(new LanguageProblem(diagnostic.Severity, diagnostic.Message, relativePath,
                    diagnostic.Range.StartLine + 1));
        }
        StatusErrors.Content = $"× {_lspDiagnostics.Values.Sum(d => d.Items.Count(item => item.Severity == "Error"))}";
        StatusWarnings.Content = $"⚠ {_lspDiagnostics.Values.Sum(d => d.Items.Count(item => item.Severity == "Warning"))}";
    }

    private async Task ApplyDiagnosticsForDocumentAsync(EditorDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Path) || !_lspDiagnostics.TryGetValue(Path.GetFullPath(document.Path), out var diagnostics))
            return;
        var markers = diagnostics.Items.Select(item => new
        {
            startLineNumber = item.Range.StartLine + 1,
            startColumn = item.Range.StartCharacter + 1,
            endLineNumber = item.Range.EndLine + 1,
            endColumn = Math.Max(item.Range.EndCharacter + 1, item.Range.StartCharacter + 2),
            message = item.Message,
            severity = item.Severity switch { "Error" => 8, "Warning" => 4, "Information" => 2, "Hint" => 1, _ => 1 },
            source = item.Source ?? "LSP",
            code = item.Code
        }).ToArray();
        await PostEditorMessageAsync(new { type = "setDiagnostics", id = document.Id, markers });
    }

    private async Task HandleLspCompletionRequestAsync(JsonElement message)
    {
        var requestId = ReadString(message, "requestId");
        if (requestId is null) return;
        var document = FindDocument(message);
        var server = _languageServer;
        if (document is null || server is null || !server.IsInitialized ||
            !_lspDocumentUris.TryGetValue(document.Id, out var uri))
        {
            await PostEditorMessageAsync(new { type = "lspCompletionResult", requestId, items = Array.Empty<object>() });
            return;
        }

        try
        {
            var items = await server.CompleteAsync(uri,
                Math.Max(0, ReadInt(message, "line", 1) - 1),
                Math.Max(0, ReadInt(message, "column", 1) - 1),
                ReadInt(message, "triggerKind", 1), ReadString(message, "triggerCharacter"));
            var payload = items.Select(item =>
            {
                var kind = int.TryParse(item.Kind, out var parsedKind) ? parsedKind : (int?)null;
                var snippet = item.Raw.TryGetProperty("insertTextFormat", out var format) && format.TryGetInt32(out var formatValue) && formatValue == 2;
                return new
                {
                    label = item.Label,
                    detail = item.Detail,
                    documentation = item.Documentation,
                    insertText = item.InsertText,
                    kind,
                    sortText = item.SortText,
                    insertTextRules = snippet ? 4 : (int?)null
                };
            }).ToArray();
            await PostEditorMessageAsync(new { type = "lspCompletionResult", requestId, items = payload });
        }
        catch (Exception ex)
        {
            await PostEditorMessageAsync(new { type = "lspCompletionResult", requestId, items = Array.Empty<object>(), error = ex.Message });
        }
    }

    private sealed record LanguageProblem(string Severity, string Message, string File, int Line);

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await CodeEditor.EnsureCoreWebView2Async();
            CodeEditor.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            CodeEditor.CoreWebView2.Settings.IsStatusBarEnabled = false;
            CodeEditor.CoreWebView2.Settings.AreDevToolsEnabled = System.Diagnostics.Debugger.IsAttached;
            CodeEditor.CoreWebView2.WebMessageReceived += Editor_WebMessageReceived;
            var editorRoot = Path.Combine(AppContext.BaseDirectory, "WebEditor", "dist");
            if (File.Exists(Path.Combine(editorRoot, "index.html")))
            {
                CodeEditor.CoreWebView2.SetVirtualHostNameToFolderMapping("keyce.local", editorRoot, CoreWebView2HostResourceAccessKind.DenyCors);
                CodeEditor.CoreWebView2.Navigate("https://keyce.local/index.html");
            }
            else
            {
                CodeEditor.NavigateToString("<html><body style='background:#1e1e1e;color:#ddd;font:14px Segoe UI;padding:28px'><h2>Monaco n’est pas encore généré</h2><p>Exécutez npm ci puis npm run build dans WebEditor.</p></body></html>");
            }
            await StartTerminalAsync("PowerShell 7");
            await RestoreSessionAsync();
        }
        catch (Exception ex)
        {
            StatusMessage.Text = "WebView2 n’a pas pu démarrer";
            OutputPanel.AppendText(ex.Message + Environment.NewLine);
        }
    }

    private async Task RestoreSessionAsync()
    {
        var session = _sessionService.Load();
        if (!string.IsNullOrWhiteSpace(session.WorkspacePath) && Directory.Exists(session.WorkspacePath))
        {
            await OpenWorkspaceAsync(session.WorkspacePath);
            foreach (var path in session.OpenFiles.Take(50))
                if (File.Exists(path) && _workspace is not null && IsInsideWorkspace(path, _workspace.RootPath))
                    await OpenDocumentAsync(path, pin: true);
            var active = _documents.FirstOrDefault(document => string.Equals(document.Path, session.ActiveFile, StringComparison.OrdinalIgnoreCase));
            if (active is not null)
            {
                _activeDocument = active;
                RefreshTabs();
                await SendDocumentAsync(active);
            }
        }
    }

    private static async Task<(string Text, string EncodingId)> ReadTextFileAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var id = bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }) ? "utf-32be"
            : bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }) ? "utf-32"
            : bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? "utf-8-bom"
            : bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }) ? "utf-16be"
            : bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) ? "utf-16"
            : "utf-8";
        if (id == "utf-8" && bytes.Contains((byte)0))
            throw new InvalidDataException("Ce fichier contient des octets nuls et ne peut pas être ouvert comme texte.");
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(stream, GetTextEncoding(id), detectEncodingFromByteOrderMarks: true);
            return (await reader.ReadToEndAsync(), id);
        }
        catch (DecoderFallbackException) when (id == "utf-8")
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(stream, GetTextEncoding("windows-1252"), detectEncodingFromByteOrderMarks: false);
            return (await reader.ReadToEndAsync(), "windows-1252");
        }
    }

    private static Encoding GetTextEncoding(string id) => id switch
    {
        "utf-8-bom" => new UTF8Encoding(true, true),
        "utf-16" => new UnicodeEncoding(false, true, true),
        "utf-16be" => new UnicodeEncoding(true, true, true),
        "utf-32" => new UTF32Encoding(false, true, true),
        "utf-32be" => new UTF32Encoding(true, true, true),
        "windows-1252" => Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => new UTF8Encoding(false, true)
    };

    private static string NormalizeLineEndings(string text, string lineEnding) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", lineEnding, StringComparison.Ordinal);

    private void SaveSession()
    {
        try
        {
            _sessionService.Save(new WorkspaceSession(
                _workspace?.RootPath,
                _documents.Where(document => !string.IsNullOrWhiteSpace(document.Path) && File.Exists(document.Path))
                    .Select(document => document.Path).Take(50).ToArray(),
                _activeDocument?.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            OutputPanel.AppendText("Session non enregistrée : " + ex.Message + Environment.NewLine);
        }
    }

    private async void Editor_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;
            var type = ReadString(root, "type");
            if (type == "ready")
            {
                _webReady = true;
                var theme = ((App)Application.Current).ThemeService.CurrentTheme;
                await PostEditorMessageAsync(new { type = "setTheme", theme = MonacoTheme(theme.Id) });
                await SendEditorOptionsAsync();
                if (_activeDocument is not null) await SendDocumentAsync(_activeDocument);
                else
                {
                    await PostEditorMessageAsync(new { type = "setDocument", id = "welcome", path = "", text = "// Bienvenue dans KeyceCode\n// Ouvrez un dossier ou fichier pour commencer.", language = "csharp" });
                }
                if (_activeDocument is not null) await ApplyDiagnosticsForDocumentAsync(_activeDocument);
                StatusMessage.Text = "Éditeur Monaco prêt";
            }
            else if (type == "contentChanged")
            {
                var doc = FindDocument(root);
                if (doc is not null && !_syncingDocument)
                {
                    doc.Text = ReadString(root, "text") ?? doc.Text;
                    doc.IsDirty = true;
                    RefreshTabs();
                    if (_languageServer is not null && _lspDocumentUris.TryGetValue(doc.Id, out var lspUri))
                    {
                        try { await _languageServer.ChangeDocumentAsync(lspUri, doc.Text); }
                        catch (Exception ex) { OutputPanel.AppendText("[LSP] Synchronisation : " + ex.Message + Environment.NewLine); }
                    }
                    if (_editorPreferences.AutoSave) _autoSaveTimer.Start();
                }
            }
            else if (type == "lspCompletionRequest") await HandleLspCompletionRequestAsync(root);
            else if (type == "cursorChanged")
            {
                StatusCursor.Content = $"Ln {ReadInt(root, "line", 1)}, Col {ReadInt(root, "column", 1)}";
                _selectedText = ReadString(root, "selectedText");
                _cursorOffset = Math.Clamp(ReadInt(root, "offset", _cursorOffset), 0, _activeDocument?.Text.Length ?? 0);
            }
            else if (type == "requestSave")
            {
                var doc = FindDocument(root);
                if (doc is not null)
                {
                    doc.Text = ReadString(root, "text") ?? doc.Text;
                    await SaveDocumentAsync(doc);
                }
            }
            else if (type == "requestFormat")
            {
                var language = ReadString(root, "language") ?? "plaintext";
                if (language is "json" or "javascript" or "typescript" or "html" or "css" or "scss")
                    await PostEditorMessageAsync(new { type = "executeCommand", command = "formatDocument" });
                else if (language == "csharp" && _languageServer is { IsInitialized: true } server &&
                         FindDocument(root) is { } document && _lspDocumentUris.TryGetValue(document.Id, out var uri))
                {
                    try
                    {
                        var edits = await server.RequestAsync("textDocument/formatting", new
                        {
                            textDocument = new { uri = uri.AbsoluteUri },
                            options = new { tabSize = _editorPreferences.TabSize, insertSpaces = _editorPreferences.InsertSpaces }
                        });
                        if (edits.ValueKind == JsonValueKind.Array && edits.GetArrayLength() > 0)
                            await PostEditorMessageAsync(new { type = "applyTextEdits", edits });
                        else StatusMessage.Text = "Le serveur n’a proposé aucun changement de formatage.";
                    }
                    catch (Exception ex)
                    {
                        StatusMessage.Text = "Formatage C# indisponible via le serveur LSP.";
                        OutputPanel.AppendText("[LSP] Formatage : " + ex.Message + Environment.NewLine);
                    }
                }
                else StatusMessage.Text = $"Aucun formateur configuré pour {language}";
            }
            else if (type == "command") await HandleEditorCommandAsync(ReadString(root, "command"));
            else if (type == "editorError") OutputPanel.AppendText("Éditeur : " + ReadString(root, "message") + Environment.NewLine);
        }
        catch (Exception ex) { OutputPanel.AppendText("Message éditeur invalide : " + ex.Message + Environment.NewLine); }
    }

    private async void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        foreach (var document in _documents.Where(d => d.IsDirty && !string.IsNullOrWhiteSpace(d.Path)).ToList())
            await SaveDocumentAsync(document);
    }

    private async Task HandleEditorCommandAsync(string? command)
    {
        switch (command)
        {
            case "save": await SaveActiveAsync(); break;
            case "quickOpen": QuickOpen_Click(this, new RoutedEventArgs()); break;
            case "find": await PostEditorMessageAsync(new { type = "executeCommand", command = "find" }); break;
            case "replace": await PostEditorMessageAsync(new { type = "executeCommand", command = "replace" }); break;
            case "geminiComplete": await CompleteAtCursorAsync(); break;
            default: StatusMessage.Text = $"Commande non reconnue : {command}"; break;
        }
    }

    private EditorDocument? FindDocument(JsonElement message)
    {
        var id = ReadString(message, "id");
        var path = ReadString(message, "path");
        return _documents.FirstOrDefault(d => (id is not null && d.Id == id) || (path is not null && d.Path == path));
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int ReadInt(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : fallback;

    private async Task PostEditorMessageAsync(object message)
    {
        if (!_webReady || CodeEditor.CoreWebView2 is null) return;
        CodeEditor.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        await Task.CompletedTask;
    }

    private async Task SendDocumentAsync(EditorDocument document)
    {
        _syncingDocument = true;
        await PostEditorMessageAsync(new { type = "setDocument", id = document.Id, path = document.Path, text = document.Text, language = document.Language });
        _syncingDocument = false;
        await EnsureLspDocumentAsync(document);
        UpdateFileStatus(document);
    }

    private void UpdateFileStatus(EditorDocument document)
    {
        if (!ReferenceEquals(document, _activeDocument)) return;
        StatusEncoding.Content = document.EncodingId switch
        {
            "utf-8-bom" => "UTF-8 BOM", "utf-16" => "UTF-16 LE", "utf-16be" => "UTF-16 BE",
            "utf-32" => "UTF-32 LE", "utf-32be" => "UTF-32 BE", "windows-1252" => "Windows 1252", _ => "UTF-8"
        };
        StatusEol.Content = document.LineEnding switch { "\n" => "LF", "\r" => "CR", _ => "CRLF" };
        StatusIndentation.Content = $"{(_editorPreferences.InsertSpaces ? "Espaces" : "Tabulations")}: {_editorPreferences.TabSize}";
        StatusLanguage.Content = document.Language;
    }

    private Task SendEditorOptionsAsync() => PostEditorMessageAsync(new
    {
        type = "setOptions",
        options = new
        {
            fontFamily = _editorPreferences.FontFamily,
            fontSize = _editorPreferences.FontSize,
            tabSize = _editorPreferences.TabSize,
            insertSpaces = _editorPreferences.InsertSpaces,
            wordWrap = _editorPreferences.WordWrap ? "on" : "off",
            minimap = new { enabled = _editorPreferences.Minimap },
            fontLigatures = _editorPreferences.FontLigatures
        }
    });

    private static Brush ParseBrush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;

    private static string MonacoTheme(string id) => id switch
    {
        "keyce-night" => "keyce-brand-night",
        "light-plus" => "vs",
        "dark-high-contrast" => "hc-black",
        "light-high-contrast" => "hc-light",
        _ => "keyce-night"
    };

    private sealed class EditorDocument(string path, string text, string language, string? alias = null)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Path { get; set; } = path;
        public string Text { get; set; } = text;
        public string Language { get; set; } = language;
        public string? Alias { get; set; } = alias;
        public string EncodingId { get; set; } = "utf-8";
        public string LineEnding { get; set; } = Environment.NewLine;
        public string Name => Alias ?? System.IO.Path.GetFileName(Path);
        public bool IsDirty { get; set; }
        public bool IsPreview { get; set; }
    }

    private sealed record SearchResult(string Path, int Line, string Text);
}
