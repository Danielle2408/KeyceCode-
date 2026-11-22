using KeyceCode.Services;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace KeyceCode;

public partial class MainWindow : Window
{
    private readonly GeminiService _gemini = new();
    private readonly EditorPreferencesService _preferencesService = new();
    private EditorPreferences _editorPreferences;
    private readonly List<EditorDocument> _documents = [];
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
        _editorPreferences = _preferencesService.Load();
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        TerminalSessions.Items.Add("PowerShell");
        TerminalSessions.SelectedIndex = 0;
        TerminalOutput.Text = "KeyceCode integrated terminal\r\n";
        OutputPanel.Text = "KeyceCode output\r\n";
        Loaded += MainWindow_Loaded;
        ((App)Application.Current).ThemeService.ThemeChanged += (_, theme) =>
        {
            StatusMessage.Text = $"Thème appliqué : {theme.DisplayName}";
            _ = PostEditorMessageAsync(new { type = "setTheme", theme = MonacoTheme(theme.Id) });
        };
    }

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
        }
        catch (Exception ex)
        {
            StatusMessage.Text = "WebView2 n’a pas pu démarrer";
            OutputPanel.AppendText(ex.Message + Environment.NewLine);
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
                if (_activeDocument is not null) await SendDocumentAsync(_activeDocument);
                else
                {
                    var theme = ((App)Application.Current).ThemeService.CurrentTheme;
                    await PostEditorMessageAsync(new { type = "setTheme", theme = MonacoTheme(theme.Id) });
                    await SendEditorOptionsAsync();
                    await PostEditorMessageAsync(new { type = "setDocument", id = "welcome", path = "", text = "// Bienvenue dans KeyceCode\n// Ouvrez un dossier ou fichier pour commencer.", language = "csharp" });
                }
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
                    if (_editorPreferences.AutoSave) _autoSaveTimer.Start();
                }
            }
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
        public string Name => Alias ?? System.IO.Path.GetFileName(Path);
        public bool IsDirty { get; set; }
        public bool IsPreview { get; set; }
    }

    private sealed record SearchResult(string Path, int Line, string Text);
}
