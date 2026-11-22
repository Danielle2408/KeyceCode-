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
    private readonly VsCodeSnippetService _snippetService = new();
    private EditorPreferences _editorPreferences;
    private readonly List<EditorDocument> _documents = [];
    private readonly List<string> _navigationHistory = [];
    private int _navigationHistoryIndex = -1;
    private bool _navigatingHistory;
    private readonly Dictionary<string, Uri> _lspDocumentUris = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LspDiagnostics> _lspDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ConPtySession, BuildOutputDiagnosticParser> _buildOutputParsers = [];
    private readonly Dictionary<ConPtySession, string> _buildOutputWorkingDirectories = [];
    private readonly List<LanguageProblem> _buildProblems = [];
    private readonly StringBuilder _terminalPendingOutput = new();
    private readonly TaskCompletionSource<bool> _terminalReadySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ConPtySession? _terminalSession;
    private bool _terminalReady;
    private readonly Dictionary<string, CancellationTokenSource> _lspNavigationRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _geminiInlineRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Version, int Offset)> _editorSnapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<string?>> _pendingFormatOnSave = new(StringComparer.Ordinal);
    private LanguageServerHost? _languageServer;
    private WorkspaceService? _workspace;
    private IReadOnlyList<string> _recentWorkspaces = [];
    private EditorDocument? _activeDocument;
    private string _activePanel = "Terminal";
    private string? _selectedText;
    private int _cursorOffset;
    private bool _webReady;
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
        OutputPanel.Text = "KeyceCode output\r\n";
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        ((App)Application.Current).ThemeService.ThemeChanged += (_, theme) =>
        {
            StatusMessage.Text = $"Thème appliqué : {theme.DisplayName}";
            _ = PostEditorMessageAsync(new { type = "setTheme", theme = MonacoTheme(theme.Id) });
            PostTerminalMessage(new { type = "setTheme", theme = TerminalTheme(theme.Id) });
        };
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        await StopDebugSessionAsync(terminateDebuggee: true);
        await StopTerminalSessionAsync();
        foreach (var request in _geminiInlineRequests.Values) request.Cancel();
        _geminiInlineRequests.Clear();
        _gemini.Dispose();
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
        LspLaunchConfiguration configuration;
        try
        {
            configuration = LspLaunchConfiguration.Resolve(
                _editorPreferences.LanguageServerExecutable,
                _editorPreferences.LanguageServerArgumentsJson,
                Environment.GetEnvironmentVariable("KEYCECODE_LSP_EXECUTABLE"),
                Environment.GetEnvironmentVariable("KEYCECODE_LSP_ARGS"));
        }
        catch (JsonException ex)
        {
            StatusMessage.Text = "Arguments LSP invalides";
            OutputPanel.AppendText("Language server non démarré : les arguments doivent être un tableau JSON de chaînes.\r\n" + ex.Message + "\r\n");
            return;
        }
        var executable = configuration.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            StatusMessage.Text = "LSP non configuré";
            OutputPanel.AppendText("Language server désactivé : configurez l’exécutable et les arguments LSP dans Paramètres, puis rouvrez le dossier.\r\n");
            return;
        }

        if (Path.GetFileName(executable).StartsWith("roslyn-language-server", StringComparison.OrdinalIgnoreCase) &&
            MessageBox.Show(this,
                $"Le serveur Roslyn chargera les projets de cet espace de travail et peut exécuter des cibles MSBuild. N’autorisez que les dossiers de confiance.\n\n{workspacePath}\n\nDémarrer le serveur ?",
                "Confiance de l’espace de travail", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            StatusMessage.Text = "IntelliSense C# désactivé pour cet espace de travail non confirmé.";
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
                Arguments = configuration.Arguments,
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
        var problems = new List<LanguageProblem>();
        foreach (var (path, diagnostics) in _lspDiagnostics)
        {
            var relativePath = _workspace is not null && IsInsideWorkspace(path, _workspace.RootPath)
                ? Path.GetRelativePath(_workspace.RootPath, path)
                : path;
            foreach (var diagnostic in diagnostics.Items)
                problems.Add(new LanguageProblem(diagnostic.Severity, diagnostic.Message, relativePath,
                    diagnostic.Range.StartLine + 1, diagnostic.Range.StartCharacter + 1, diagnostic.Code, path));
        }
        problems.AddRange(_buildProblems);
        var distinct = problems.DistinctBy(problem => (problem.FullPath, problem.Line, problem.Column, problem.Code, problem.Message));
        foreach (var problem in distinct) ProblemsPanel.Items.Add(problem);
        StatusErrors.Content = $"× {distinct.Count(problem => problem.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))}";
        StatusWarnings.Content = $"⚠ {distinct.Count(problem => problem.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase))}";
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

    private async Task HandleLspNavigationRequestAsync(JsonElement message)
    {
        var requestId = ReadString(message, "requestId");
        var operation = ReadString(message, "operation");
        if (requestId is null || operation is null) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _lspNavigationRequests[requestId] = timeout;
        try
        {
            var document = FindDocument(message);
            var server = _languageServer;
            if (document is null || server is not { IsInitialized: true } || _workspace is null ||
                !_lspDocumentUris.TryGetValue(document.Id, out var documentUri))
            {
                await PostEditorMessageAsync(new { type = "lspNavigationResult", requestId, operation, locations = Array.Empty<object>() });
                return;
            }

            var capabilities = server.ServerCapabilities;
            var capability = operation switch
            {
                "definition" => "definitionProvider",
                "references" => "referencesProvider",
                "hover" => "hoverProvider",
                _ => string.Empty
            };
            if (string.IsNullOrEmpty(capability) || !HasLspCapability(capabilities, capability))
            {
                await PostEditorMessageAsync(new { type = "lspNavigationResult", requestId, operation, locations = Array.Empty<object>() });
                return;
            }

            var line = Math.Max(0, ReadInt(message, "line", 1) - 1);
            var character = Math.Max(0, ReadInt(message, "column", 1) - 1);
            var parameters = new Dictionary<string, object?>
            {
                ["textDocument"] = new { uri = documentUri.AbsoluteUri },
                ["position"] = new { line, character }
            };
            if (operation == "references") parameters["context"] = new { includeDeclaration = true };

            var method = operation switch
            {
                "definition" => "textDocument/definition",
                "references" => "textDocument/references",
                "hover" => "textDocument/hover",
                _ => throw new InvalidOperationException("Unsupported LSP navigation operation.")
            };
            var response = await server.RequestAsync(method, parameters, timeout.Token);
            if (operation == "hover")
            {
                await PostEditorMessageAsync(new { type = "lspNavigationResult", requestId, operation, hover = response });
                return;
            }

            var candidates = ExtractLspLocations(response);
            var locations = new List<object>();
            foreach (var candidate in candidates.Take(500))
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!TryGetWorkspaceLocation(candidate, out var locationUri, out var range)) continue;
                var fullPath = Path.GetFullPath(locationUri.LocalPath);
                if (!IsInsideWorkspace(fullPath, _workspace.RootPath) || !File.Exists(fullPath)) continue;
                var relativePath = Path.GetRelativePath(_workspace.RootPath, fullPath);
                try { fullPath = _workspace.ResolvePath(relativePath); }
                catch (UnauthorizedAccessException) { continue; }
                if (!File.Exists(fullPath)) continue;

                string text;
                var openDocument = _documents.FirstOrDefault(item =>
                    !string.IsNullOrWhiteSpace(item.Path) &&
                    string.Equals(Path.GetFullPath(item.Path), fullPath, StringComparison.OrdinalIgnoreCase));
                if (openDocument is not null) text = openDocument.Text;
                else
                {
                    if (new FileInfo(fullPath).Length > 4_000_000) continue;
                    text = await _workspace.ReadFileAsync(relativePath, timeout.Token);
                }

                locations.Add(new
                {
                    path = fullPath,
                    workspacePath = relativePath.Replace('\\', '/'),
                    text,
                    startLine = range.StartLine,
                    startCharacter = range.StartCharacter,
                    endLine = range.EndLine,
                    endCharacter = range.EndCharacter
                });
            }

            await PostEditorMessageAsync(new { type = "lspNavigationResult", requestId, operation, locations });
        }
        catch (OperationCanceledException)
        {
            await PostEditorMessageAsync(new { type = "lspNavigationResult", requestId, operation, locations = Array.Empty<object>() });
        }
        catch (Exception ex)
        {
            OutputPanel.AppendText($"[LSP] {operation} : {ex.Message}{Environment.NewLine}");
            await PostEditorMessageAsync(new { type = "lspNavigationResult", requestId, operation, locations = Array.Empty<object>(), error = ex.Message });
        }
        finally
        {
            _lspNavigationRequests.Remove(requestId);
        }
    }

    private static bool HasLspCapability(JsonElement capabilities, string name) =>
        capabilities.ValueKind == JsonValueKind.Object && capabilities.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.Object);

    private static IEnumerable<JsonElement> ExtractLspLocations(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in response.EnumerateArray()) yield return item;
            yield break;
        }
        if (response.ValueKind == JsonValueKind.Object) yield return response;
    }

    private static bool TryGetWorkspaceLocation(JsonElement item, out Uri uri, out LspRange range)
    {
        uri = null!;
        range = new LspRange(0, 0, 0, 0);
        var uriText = ReadString(item, "uri") ?? ReadString(item, "targetUri");
        if (uriText is null || !Uri.TryCreate(uriText, UriKind.Absolute, out var parsedUri) || !parsedUri.IsFile)
            return false;
        var rangeElement = item.TryGetProperty("range", out var ordinaryRange) ? ordinaryRange :
            item.TryGetProperty("targetSelectionRange", out var selectionRange) ? selectionRange : default;
        if (rangeElement.ValueKind != JsonValueKind.Object ||
            !TryGetLspPosition(rangeElement, "start", out var startLine, out var startCharacter) ||
            !TryGetLspPosition(rangeElement, "end", out var endLine, out var endCharacter)) return false;
        uri = parsedUri;
        range = new LspRange(startLine, startCharacter, endLine, endCharacter);
        return true;
    }

    private static bool TryGetLspPosition(JsonElement range, string endpoint, out int line, out int character)
    {
        line = 0;
        character = 0;
        return range.TryGetProperty(endpoint, out var position) &&
               position.TryGetProperty("line", out var lineElement) && lineElement.TryGetInt32(out line) && line >= 0 &&
               position.TryGetProperty("character", out var characterElement) && characterElement.TryGetInt32(out character) && character >= 0;
    }

    private void CancelLspNavigationRequest(string? requestId)
    {
        if (requestId is not null && _lspNavigationRequests.TryGetValue(requestId, out var cancellation))
            cancellation.Cancel();
    }

    private async Task ActivateLspNavigationModelAsync(JsonElement message)
    {
        if (_workspace is null || ReadString(message, "path") is not { } relativePath) return;
        try
        {
            var fullPath = _workspace.ResolvePath(relativePath);
            if (File.Exists(fullPath)) await OpenDocumentAsync(fullPath, pin: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            OutputPanel.AppendText("[LSP] Cible de navigation ignorée : " + ex.Message + Environment.NewLine);
        }
    }

    private sealed record LanguageProblem(string Severity, string Message, string File, int Line,
        int Column = 1, string? Code = null, string? FullPath = null);

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            InitializeEditorPanelHost();
            await CodeEditor.EnsureCoreWebView2Async();
            CodeEditor.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            CodeEditor.CoreWebView2.Settings.IsStatusBarEnabled = false;
            CodeEditor.CoreWebView2.Settings.AreDevToolsEnabled = System.Diagnostics.Debugger.IsAttached;
            CodeEditor.CoreWebView2.WebMessageReceived += Editor_WebMessageReceived;
            var editorRoot = Path.Combine(AppContext.BaseDirectory, "WebEditor", "dist");
            if (File.Exists(Path.Combine(editorRoot, "index.html")) || File.Exists(Path.Combine(editorRoot, "terminal.html")))
            {
                CodeEditor.CoreWebView2.SetVirtualHostNameToFolderMapping("keyce.local", editorRoot, CoreWebView2HostResourceAccessKind.DenyCors);
                if (File.Exists(Path.Combine(editorRoot, "index.html"))) CodeEditor.CoreWebView2.Navigate("https://keyce.local/index.html");
                else CodeEditor.NavigateToString("<html><body>Monaco n’est pas encore généré.</body></html>");
            }
            else
            {
                CodeEditor.NavigateToString("<html><body style='background:#1e1e1e;color:#ddd;font:14px Segoe UI;padding:28px'><h2>Monaco n’est pas encore généré</h2><p>Exécutez npm ci puis npm run build dans WebEditor.</p></body></html>");
            }
            await InitializeTerminalWebViewAsync(editorRoot);
            RestoreWorkbenchLayout();
            await RestoreSessionAsync();
            await StartTerminalAsync(_activeTerminalProfile ?? "PowerShell 7");
        }
        catch (Exception ex)
        {
            StatusMessage.Text = "WebView2 n’a pas pu démarrer";
            OutputPanel.AppendText(ex.Message + Environment.NewLine);
        }
    }

    private async Task InitializeTerminalWebViewAsync(string editorRoot)
    {
        await TerminalWebView.EnsureCoreWebView2Async();
        TerminalWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        TerminalWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        TerminalWebView.CoreWebView2.Settings.AreDevToolsEnabled = System.Diagnostics.Debugger.IsAttached;
        TerminalWebView.CoreWebView2.WebMessageReceived += TerminalWebView_WebMessageReceived;
        if (File.Exists(Path.Combine(editorRoot, "terminal.html")))
            TerminalWebView.CoreWebView2.SetVirtualHostNameToFolderMapping("keyce.local", editorRoot, CoreWebView2HostResourceAccessKind.DenyCors);
        var terminalPage = Path.Combine(editorRoot, "terminal.html");
        if (File.Exists(terminalPage)) TerminalWebView.CoreWebView2.Navigate("https://keyce.local/terminal.html");
        else
        {
            TerminalWebView.NavigateToString("<html><body style='background:#181818;color:#ddd;font:14px Segoe UI;padding:24px'>Ressources xterm manquantes. Exécutez npm ci puis npm run build dans WebEditor.</body></html>");
            StatusMessage.Text = "Client xterm non généré";
        }
    }

    private async void TerminalWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;
            var type = ReadString(root, "type");
            if (type == "ready")
            {
                OnTerminalWebViewReady(root);
            }
            else if (type == "input")
            {
                await HandleTerminalInputAsync(root);
            }
            else if (type == "resize")
            {
                HandleTerminalResize(root);
            }
            else if (type == "closed")
            {
                _terminalReady = false;
                await StopTerminalSessionAsync();
            }
            else if (type == "terminalError")
            {
                var message = ReadString(root, "message") ?? "Erreur xterm inconnue.";
                OutputPanel.AppendText("[Terminal] " + message + Environment.NewLine);
                StatusMessage.Text = "Erreur du client xterm";
            }
        }
        catch (Exception ex) { OutputPanel.AppendText("[Terminal] Message xterm invalide : " + ex.Message + Environment.NewLine); }
    }

    private void PostTerminalMessage(object message)
    {
        if (!_terminalReady || TerminalWebView.CoreWebView2 is null) return;
        TerminalWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private static bool TryReadDimensions(JsonElement root, out int columns, out int rows)
    {
        columns = ReadInt(root, "cols", 0);
        rows = ReadInt(root, "rows", 0);
        return columns is > 0 and <= short.MaxValue && rows is > 0 and <= short.MaxValue;
    }

    private static string TerminalTheme(string id) => id is "light-plus" or "light-high-contrast" ? "light" : "dark";

    private async Task RestoreSessionAsync()
    {
        var session = _sessionService.Load();
        _recentWorkspaces = session.RecentWorkspaces ?? [];
        RefreshRecentWorkspacesMenu();
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
            _layoutService.Save(CaptureWorkbenchLayout());
            _sessionService.Save(new WorkspaceSession(
                _workspace?.RootPath,
                _documents.Where(document => !string.IsNullOrWhiteSpace(document.Path) && File.Exists(document.Path))
                    .Select(document => document.Path).Take(50).ToArray(),
                _activeDocument?.Path,
                _recentWorkspaces));
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
                await SendUserSnippetsAsync();
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
                if (doc is not null)
                {
                    doc.Text = ReadString(root, "text") ?? doc.Text;
                    var version = ReadInt(root, "version", 0);
                    var offset = _editorSnapshots.TryGetValue(doc.Id, out var snapshot) ? snapshot.Offset : 0;
                    _editorSnapshots[doc.Id] = (version, Math.Min(offset, doc.Text.Length));
                    CancelInlineRequestsForDocument(doc.Id);
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
            else if (type == "lspNavigationRequest") await HandleLspNavigationRequestAsync(root);
            else if (type == "lspNavigationCancel") CancelLspNavigationRequest(ReadString(root, "requestId"));
            else if (type == "lspRenameRequest") await HandleLspRenameRequestAsync(root);
            else if (type == "lspRefactorCancel") CancelLspRefactorRequest(ReadString(root, "requestId"));
            else if (type == "lspCodeActionRequest") await HandleLspCodeActionsAsync(root);
            else if (type == "lspModelActivated") await ActivateLspNavigationModelAsync(root);
            else if (type == "activePaneChanged")
            {
                var document = FindDocument(root);
                if (document is not null)
                {
                    _activeDocument = document;
                    StatusLanguage.Content = document.Language;
                    RefreshTabs();
                    UpdateFileStatus(document);
                }
            }
            else if (type == "geminiInlineCompletionRequest") await HandleGeminiInlineCompletionRequestAsync(root);
            else if (type == "geminiInlineCompletionCancel") CancelInlineRequest(ReadString(root, "requestId"));
            else if (type == "cursorChanged")
            {
                StatusCursor.Content = $"Ln {ReadInt(root, "line", 1)}, Col {ReadInt(root, "column", 1)}";
                _selectedText = ReadString(root, "selectedText");
                _cursorOffset = Math.Clamp(ReadInt(root, "offset", _cursorOffset), 0, _activeDocument?.Text.Length ?? 0);
                if (_activeDocument is { } active && ReadString(root, "id") == active.Id)
                {
                    var version = ReadInt(root, "version", 0);
                    _editorSnapshots[active.Id] = (version, _cursorOffset);
                    CancelInlineRequestsForDocument(active.Id);
                }
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
            else if (type == "formatForSaveResult")
            {
                var requestId = ReadString(root, "requestId");
                if (requestId is not null && _pendingFormatOnSave.TryGetValue(requestId, out var completion))
                    completion.TrySetResult(ReadString(root, "text"));
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
            case "lspCodeActions":
                if (_activeDocument is { } document)
                {
                    var offset = Math.Clamp(_cursorOffset, 0, document.Text.Length);
                    var lineStart = document.Text.LastIndexOf('\n', Math.Max(0, offset - 1));
                    if (offset == 0 || lineStart < 0) lineStart = -1;
                    var line = document.Text[..offset].Count(character => character == '\n') + 1;
                    var column = offset - lineStart;
                    await HandleLspCodeActionsAsync(JsonSerializer.SerializeToElement(new
                    {
                        id = document.Id, path = document.Path,
                        startLine = line, startColumn = column, endLine = line, endColumn = column
                    }));
                }
                else StatusMessage.Text = "Ouvrez un fichier avant de demander des actions rapides.";
                break;
            default: StatusMessage.Text = $"Commande non reconnue : {command}"; break;
        }
    }

    private async Task HandleGeminiInlineCompletionRequestAsync(JsonElement message)
    {
        var requestId = ReadString(message, "requestId");
        var documentId = ReadString(message, "id");
        var version = ReadInt(message, "version", -1);
        var offset = ReadInt(message, "offset", -1);
        var prefix = ReadString(message, "prefix");
        var language = ReadString(message, "language") ?? "plaintext";
        var document = _activeDocument;
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(documentId) || prefix is null ||
            !_editorPreferences.GeminiInlineCompletionEnabled || document is null || document.Id != documentId ||
            ReadBool(message, "selectionEmpty") != true || !string.IsNullOrEmpty(_selectedText) ||
            offset < 0 || offset > document.Text.Length || prefix.Length > 4000)
        {
            await PostEditorMessageAsync(new { type = "geminiInlineCompletionResult", requestId, text = "", version, offset, id = documentId });
            return;
        }

        var expectedPrefix = document.Text[..offset];
        if (expectedPrefix.Length > 4000) expectedPrefix = expectedPrefix[^4000..];
        if (!string.Equals(prefix, expectedPrefix, StringComparison.Ordinal) ||
            !_editorSnapshots.TryGetValue(documentId, out var snapshot) ||
            snapshot.Version != version || snapshot.Offset != offset)
        {
            await PostEditorMessageAsync(new { type = "geminiInlineCompletionResult", requestId, text = "", version, offset, id = documentId });
            return;
        }

        CancelInlineRequest(requestId);
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _geminiInlineRequests[requestId] = cancellation;
        try
        {
            var completion = await _gemini.CompleteAsync(prefix, language, cancellation.Token);
            if (cancellation.IsCancellationRequested || !_editorPreferences.GeminiInlineCompletionEnabled ||
                !ReferenceEquals(document, _activeDocument) ||
                !_editorSnapshots.TryGetValue(documentId, out var latest) || latest.Version != version || latest.Offset != offset)
                return;
            await PostEditorMessageAsync(new { type = "geminiInlineCompletionResult", requestId, text = completion, version, offset, id = documentId });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
            {
                StatusMessage.Text = "KeyceIA : autocomplétion indisponible";
                await PostEditorMessageAsync(new { type = "geminiInlineCompletionResult", requestId, text = "", version, offset, id = documentId, error = ex.Message });
            }
        }
        finally
        {
            if (_geminiInlineRequests.TryGetValue(requestId, out var active) && ReferenceEquals(active, cancellation))
                _geminiInlineRequests.Remove(requestId);
            cancellation.Dispose();
        }
    }

    private void CancelInlineRequestsForDocument(string documentId)
    {
        foreach (var (requestId, cancellation) in _geminiInlineRequests.ToArray())
            if (requestId.StartsWith(documentId + ":", StringComparison.Ordinal))
                cancellation.Cancel();
    }

    private void CancelInlineRequest(string? requestId)
    {
        if (requestId is not null && _geminiInlineRequests.TryGetValue(requestId, out var cancellation))
            cancellation.Cancel();
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
    private static bool? ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;

    private async Task PostEditorMessageAsync(object message)
    {
        if (!_webReady || CodeEditor.CoreWebView2 is null) return;
        CodeEditor.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        await Task.CompletedTask;
    }

    private async Task SendDocumentAsync(EditorDocument document, bool replaceEditorText = false)
    {
        await PostEditorMessageAsync(new { type = "setDocument", id = document.Id, path = document.Path, text = document.Text, language = document.Language, replaceText = replaceEditorText });
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

    private async Task SendEditorOptionsAsync()
    {
        await PostEditorMessageAsync(new
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
                fontLigatures = _editorPreferences.FontLigatures,
                lineNumbers = _editorPreferences.LineNumbers,
                cursorStyle = _editorPreferences.CursorStyle,
                lineHeight = _editorPreferences.LineHeight,
                renderWhitespace = _editorPreferences.RenderWhitespace,
                renderLineHighlight = _editorPreferences.RenderLineHighlight,
                guides = new { indentation = _editorPreferences.IndentationGuides, highlightActiveIndentation = true, bracketPairs = _editorPreferences.BracketPairGuides },
                bracketPairColorization = new { enabled = _editorPreferences.BracketPairColorization, independentColorPoolPerBracketType = true },
                smoothScrolling = _editorPreferences.SmoothScrolling,
                stickyScroll = new { enabled = _editorPreferences.StickyScroll }
            }
        });
        await PostEditorMessageAsync(new { type = "setAiCompletionEnabled", value = _editorPreferences.GeminiInlineCompletionEnabled });
    }

    private Task SendUserSnippetsAsync() => PostEditorMessageAsync(new { type = "setUserSnippets", snippets = _snippetService.Load() });

    private static Brush ParseBrush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;

    private static string MonacoTheme(string id) => id switch
    {
        "dark-plus" => "vs-dark",
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

}
