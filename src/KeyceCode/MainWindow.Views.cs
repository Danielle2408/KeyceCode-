using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
using Microsoft.Win32;
using KeyceCode.Services;

namespace KeyceCode;

public partial class MainWindow
{
    private void ShowExplorer_Click(object sender, RoutedEventArgs e) => ShowView("Explorer");
    private void ShowSearch_Click(object sender, RoutedEventArgs e) { ShowView("Search"); SearchQuery.Focus(); }
    private async void ShowSourceControl_Click(object sender, RoutedEventArgs e) { ShowView("SourceControl"); await RefreshGitAsync(); }
    private void ShowRun_Click(object sender, RoutedEventArgs e) => ShowView("Run");
    private void ShowExtensions_Click(object sender, RoutedEventArgs e) => ShowView("Extensions");

    private void ShowView(string name)
    {
        _activeView = name;
        ExplorerView.Visibility = name == "Explorer" ? Visibility.Visible : Visibility.Collapsed;
        SearchView.Visibility = name == "Search" ? Visibility.Visible : Visibility.Collapsed;
        SourceControlView.Visibility = name == "SourceControl" ? Visibility.Visible : Visibility.Collapsed;
        RunView.Visibility = name == "Run" ? Visibility.Visible : Visibility.Collapsed;
        ExtensionsView.Visibility = name == "Extensions" ? Visibility.Visible : Visibility.Collapsed;
        SidebarHeading.Text = name switch
        {
            "Explorer" => "EXPLORATEUR", "Search" => "RECHERCHE", "SourceControl" => "CONTRÔLE DE CODE SOURCE",
            "Run" => "EXÉCUTER ET DÉBOGUER", "Extensions" => "EXTENSIONS", _ => name.ToUpperInvariant()
        };
        ActivityExplorer.BorderThickness = new Thickness(name == "Explorer" ? 3 : 0, 0, 0, 0);
        ActivitySearch.BorderThickness = new Thickness(name == "Search" ? 3 : 0, 0, 0, 0);
        ActivitySourceControl.BorderThickness = new Thickness(name == "SourceControl" ? 3 : 0, 0, 0, 0);
        ActivityRun.BorderThickness = new Thickness(name == "Run" ? 3 : 0, 0, 0, 0);
        ActivityExtensions.BorderThickness = new Thickness(name == "Extensions" ? 3 : 0, 0, 0, 0);
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _sidebarVisible = !_sidebarVisible;
        SidebarColumn.Width = _sidebarVisible ? new GridLength(250) : new GridLength(0);
        GridSplitterColumn(2, _sidebarVisible);
    }

    private void GridSplitterColumn(int index, bool visible)
    {
        if (index == 2 && WorkbenchGridSplitter is not null) WorkbenchGridSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TogglePanel_Click(object sender, RoutedEventArgs e)
    {
        _panelVisible = !_panelVisible;
        PanelRow.Height = _panelVisible ? new GridLength(230) : new GridLength(0);
        PanelSplitter.Visibility = _panelVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleAssistant_Click(object sender, RoutedEventArgs e)
    {
        _assistantVisible = !_assistantVisible;
        SecondarySidebarColumn.Width = _assistantVisible ? new GridLength(330) : new GridLength(0);
    }

    private void ShowProblems_Click(object sender, RoutedEventArgs e) => ShowPanel("Problems");
    private void ShowOutput_Click(object sender, RoutedEventArgs e) => ShowPanel("Output");
    private void ShowDebugConsole_Click(object sender, RoutedEventArgs e) => ShowPanel("DebugConsole");
    private void ShowTerminal_Click(object sender, RoutedEventArgs e) => ShowPanel("Terminal");

    private void ShowPanel(string name)
    {
        _activePanel = name;
        TerminalPanel.Visibility = name == "Terminal" ? Visibility.Visible : Visibility.Collapsed;
        ProblemsPanel.Visibility = name == "Problems" ? Visibility.Visible : Visibility.Collapsed;
        OutputPanel.Visibility = name == "Output" ? Visibility.Visible : Visibility.Collapsed;
        DebugConsolePanel.Visibility = name == "DebugConsole" ? Visibility.Visible : Visibility.Collapsed;
        _panelVisible = true; PanelRow.Height = new GridLength(230); PanelSplitter.Visibility = Visibility.Visible;
    }

    private void ClearPanel_Click(object sender, RoutedEventArgs e)
    {
        if (_activePanel == "Terminal") PostTerminalMessage(new { type = "clear" });
        else if (_activePanel == "Output") OutputPanel.Clear();
        else if (_activePanel == "DebugConsole") DebugConsolePanel.Clear();
    }
    private void ClearTerminal_Click(object sender, RoutedEventArgs e) => PostTerminalMessage(new { type = "clear" });

    private async void Undo_Click(object sender, RoutedEventArgs e) => await PostEditorMessageAsync(new { type = "executeCommand", command = "undo" });
    private async void Redo_Click(object sender, RoutedEventArgs e) => await PostEditorMessageAsync(new { type = "executeCommand", command = "redo" });
    private async void SelectAll_Click(object sender, RoutedEventArgs e) => await PostEditorMessageAsync(new { type = "executeCommand", command = "selectAll" });
    private async void AddCursorDown_Click(object sender, RoutedEventArgs e) => await PostEditorMessageAsync(new { type = "executeCommand", command = "editor.action.insertCursorBelow" });
    private async void FormatDocument_Click(object sender, RoutedEventArgs e) => await FormatActiveDocumentAsync();

    private async Task FormatActiveDocumentAsync()
    {
        if (_activeDocument is null) return;
        if (_activeDocument.Language is "json" or "javascript" or "typescript" or "html" or "css" or "scss")
            await PostEditorMessageAsync(new { type = "executeCommand", command = "formatDocument" });
        else StatusMessage.Text = $"Aucun formateur installé pour {_activeDocument.Language}.";
    }

    private void CommandCenter_Click(object sender, MouseButtonEventArgs e) => CommandPalette_Click(sender, new RoutedEventArgs());

    private void CommandPalette_Click(object sender, RoutedEventArgs e)
    {
        (string Name, Action Action)[] commands =
        [
            ("Fichier: Ouvrir un dossier…", () => OpenFolder_Click(sender, new RoutedEventArgs())),
            ("Fichier: Ouvrir un fichier…", () => OpenFile_Click(sender, new RoutedEventArgs())),
            ("Fichier: Enregistrer", () => Save_Click(sender, new RoutedEventArgs())),
            ("Affichage: Explorateur", () => ShowExplorer_Click(sender, new RoutedEventArgs())),
            ("Affichage: Recherche", () => ShowSearch_Click(sender, new RoutedEventArgs())),
            ("Affichage: Terminal", () => ShowTerminal_Click(sender, new RoutedEventArgs())),
            ("Exécuter: Démarrer", () => Run_Click(sender, new RoutedEventArgs())),
            ("KeyceIA: Ouvrir l’assistant", () => ToggleAssistant_Click(sender, new RoutedEventArgs())),
            ("Préférences: Thème de couleurs", () => Theme_Click(sender, new RoutedEventArgs()))
        ];
        var selected = QuickPickWindow.Show(this, "Palette de commandes", commands.Select(c => c.Name));
        if (selected is not null) commands.First(c => c.Name == selected).Action();
    }

    private async void QuickOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null) { OpenFile_Click(sender, e); return; }
        var files = await CollectWorkspaceFilesAsync(_workspace.RootPath);
        var relativePaths = files.Select(file => Path.GetRelativePath(_workspace.RootPath, file)).ToList();
        var selected = QuickPickWindow.Show(this, "Accéder au fichier", relativePaths);
        if (selected is not null) await OpenDocumentAsync(Path.Combine(_workspace.RootPath, selected));
    }

    private async void GoToLine_Click(object sender, RoutedEventArgs e)
    {
        var number = QuickPickWindow.Prompt(this, "Accéder à la ligne", "Numéro de ligne");
        if (int.TryParse(number, out var line) && line > 0)
            await PostEditorMessageAsync(new { type = "executeCommand", command = "editor.action.gotoLine", args = line });
    }

    private void SplitEditor_Click(object sender, RoutedEventArgs e) =>
        StatusMessage.Text = "Les groupes d’éditeurs côte à côte sont encore en développement.";

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var service = ((App)Application.Current).ThemeService;
        var theme = QuickPickWindow.Show(this, "Thème de couleurs", service.Themes.Select(t => t.DisplayName));
        if (theme is not null)
        {
            var selected = service.Themes.FirstOrDefault(t => t.DisplayName == theme);
            if (selected is not null) service.SetTheme(Application.Current, selected.Id);
        }
    }

    private async void ImportVsCodeConfiguration_Click(object sender, RoutedEventArgs e)
    {
        var settingsPath = VsCodeConfigurationImporter.DefaultSettingsPath;
        if (!File.Exists(settingsPath))
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choisir le settings.json de Visual Studio Code",
                Filter = "Paramètres VS Code (settings.json)|settings.json|Fichiers JSON (*.json)|*.json|Tous les fichiers (*.*)|*.*",
                InitialDirectory = Path.GetDirectoryName(settingsPath),
                FileName = "settings.json",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;
            settingsPath = dialog.FileName;
        }

        try
        {
            var imported = VsCodeConfigurationImporter.Import(settingsPath, _editorPreferences);
            _editorPreferences = imported.Preferences;
            _preferencesService.Save(_editorPreferences);
            FormatOnSaveMenuItem.IsChecked = _editorPreferences.FormatOnSave;
            if (imported.ThemeId is { } themeId)
                ((App)Application.Current).ThemeService.SetTheme(Application.Current, themeId);
            await SendEditorOptionsAsync();
            StatusMessage.Text = "Préférences VS Code importées";
            MessageBox.Show(this,
                $"Préférences compatibles importées depuis :\n{imported.SourcePath}\n\n" +
                $"{imported.ExtensionCount} extension(s) VS Code détectée(s) dans le profil local. Vous pouvez copier leurs paquets depuis la vue Extensions; ils resteront inactifs car KeyceCode n’a pas encore d’hôte d’extensions. Les raccourcis clavier personnalisés et thèmes tiers ne sont pas appliqués.",
                "Import Visual Studio Code", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show(this, "Impossible d’importer cette configuration VS Code : " + ex.Message,
                "Import Visual Studio Code", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var themeService = ((App)Application.Current).ThemeService;
        var credentialStore = new GeminiCredentialStore();
        var settings = new SettingsWindow(_editorPreferences, themeService.Themes, themeService.CurrentThemeId, credentialStore.HasStoredKey) { Owner = this };
        if (settings.ShowDialog() != true) return;
        _editorPreferences = settings.Preferences;
        _preferencesService.Save(_editorPreferences);
        if (!_editorPreferences.GeminiInlineCompletionEnabled)
            foreach (var request in _geminiInlineRequests.Values) request.Cancel();
        FormatOnSaveMenuItem.IsChecked = _editorPreferences.FormatOnSave;
        try
        {
            if (settings.ClearGeminiApiKey) credentialStore.Delete();
            else if (settings.GeminiApiKey is { } apiKey) credentialStore.Save(apiKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show(this, "La clé Gemini n’a pas pu être enregistrée de manière sécurisée : " + ex.Message,
                "KeyceIA", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        if (settings.SelectedThemeId is not null && settings.SelectedThemeId != themeService.CurrentThemeId)
            themeService.SetTheme(Application.Current, settings.SelectedThemeId);
        await SendEditorOptionsAsync();
    }

    private void FormatOnSaveMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _editorPreferences = _editorPreferences with { FormatOnSave = FormatOnSaveMenuItem.IsChecked };
        _preferencesService.Save(_editorPreferences);
        StatusMessage.Text = _editorPreferences.FormatOnSave
            ? "Formatage à l’enregistrement activé · nécessite un serveur LSP compatible"
            : "Formatage à l’enregistrement désactivé";
    }

    private void Encoding_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is not { } document) return;
        var choices = new[] { "UTF-8", "UTF-8 with BOM", "UTF-16 LE", "UTF-16 BE", "UTF-32 LE", "UTF-32 BE", "Windows 1252" };
        var selected = QuickPickWindow.Show(this, "Enregistrer avec l’encodage", choices);
        if (selected is null) return;
        document.EncodingId = selected switch
        {
            "UTF-8 with BOM" => "utf-8-bom", "UTF-16 LE" => "utf-16", "UTF-16 BE" => "utf-16be",
            "UTF-32 LE" => "utf-32", "UTF-32 BE" => "utf-32be", "Windows 1252" => "windows-1252", _ => "utf-8"
        };
        document.IsDirty = true;
        RefreshTabs();
        UpdateFileStatus(document);
        StatusMessage.Text = "L’encodage choisi sera appliqué à l’enregistrement.";
    }

    private void Eol_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is not { } document) return;
        var selected = QuickPickWindow.Show(this, "Fin de ligne", ["Windows (CRLF)", "Unix (LF)", "Mac classique (CR)"]);
        if (selected is null) return;
        document.LineEnding = selected switch { "Unix (LF)" => "\n", "Mac classique (CR)" => "\r", _ => "\r\n" };
        document.IsDirty = true;
        RefreshTabs();
        UpdateFileStatus(document);
        StatusMessage.Text = "La fin de ligne choisie sera appliquée à l’enregistrement.";
    }
    private async void Language_Click(object sender, RoutedEventArgs e)
    {
        var languages = new[] { "csharp", "javascript", "typescript", "json", "html", "css", "markdown", "python", "powershell", "plaintext" };
        var selected = QuickPickWindow.Show(this, "Mode de langage", languages);
        if (selected is null) return;
        if (_activeDocument is not null) _activeDocument.Language = selected;
        StatusLanguage.Content = selected;
        await PostEditorMessageAsync(new { type = "setLanguage", language = selected });
    }

    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "KeyceCode\nÉditeur de code C# avec Monaco et WebView2. Les fonctions sans serveur de langage ou adaptateur installé sont signalées comme indisponibles.",
        "À propos de KeyceCode", MessageBoxButton.OK, MessageBoxImage.Information);

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ShowError(Exception exception) => MessageBox.Show(this, exception.Message,
        "KeyceCode", MessageBoxButton.OK, MessageBoxImage.Error);

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.S: await SaveActiveAsync(); e.Handled = true; break;
                case Key.O: OpenFile_Click(sender, new RoutedEventArgs()); e.Handled = true; break;
                case Key.P: QuickOpen_Click(sender, new RoutedEventArgs()); e.Handled = true; break;
                case Key.B: ToggleSidebar_Click(sender, new RoutedEventArgs()); e.Handled = true; break;
                case Key.J: TogglePanel_Click(sender, new RoutedEventArgs()); e.Handled = true; break;
                case Key.W: if (_activeDocument is not null) await CloseDocumentAsync(_activeDocument); e.Handled = true; break;
                case Key.N: NewFile_Click(sender, new RoutedEventArgs()); e.Handled = true; break;
            }
        }
        else if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.Control) { Run_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None) { Debug_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.Shift) { StopRun_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { ShowSearch_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { CommandPalette_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.E && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { ShowExplorer_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.X && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { ShowExtensions_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control) { GoToLine_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control) { Settings_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Alt) { await FormatActiveDocumentAsync(); e.Handled = true; }
    }
}

internal sealed class QuickPickWindow : Window
{
    private readonly TextBox _search = new();
    private readonly ListBox _list = new();
    private readonly List<string> _source;

    private QuickPickWindow(string title, IEnumerable<string> values)
    {
        Title = title; Width = 660; Height = 470; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow; ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "ForegroundBrush");
        _source = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var dock = new DockPanel { Margin = new Thickness(10) };
        _search.Height = 34; _search.Margin = new Thickness(0, 0, 0, 7); _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => Filter(); _search.KeyDown += Search_KeyDown;
        DockPanel.SetDock(_search, Dock.Top); dock.Children.Add(_search);
        _list.Background = Brushes.Transparent; _list.BorderThickness = new Thickness(0);
        _list.MouseDoubleClick += (_, _) => AcceptSelection(); dock.Children.Add(_list);
        Content = dock; Filter(); Loaded += (_, _) => _search.Focus();
    }

    private void Filter()
    {
        var query = _search.Text.Trim();
        _list.ItemsSource = _source.Where(value => Matches(value, query))
            .OrderBy(value => value.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : value.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .Take(300).ToList();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private static bool Matches(string value, string query)
    {
        if (query.Length == 0) return true;
        var position = 0;
        foreach (var character in value)
            if (char.ToUpperInvariant(character) == char.ToUpperInvariant(query[position]) && ++position == query.Length) return true;
        return false;
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AcceptSelection(); e.Handled = true; }
        else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        else if (e.Key == Key.Down) { _list.Focus(); e.Handled = true; }
    }

    private void AcceptSelection()
    {
        if (_list.SelectedItem is string value) { Tag = value; DialogResult = true; }
    }

    public static string? Show(Window owner, string title, IEnumerable<string> values)
    {
        var window = new QuickPickWindow(title, values) { Owner = owner };
        return window.ShowDialog() == true ? window.Tag?.ToString() : null;
    }

    public static string? Prompt(Window owner, string title, string placeholder, string? initialValue = null)
    {
        var window = new QuickPickWindow(title, []) { Owner = owner };
        window._search.ToolTip = placeholder;
        window._search.Text = initialValue ?? string.Empty;
        window._search.KeyDown += (sender, e) =>
        {
            if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(window._search.Text)) return;
            window.Tag = window._search.Text.Trim();
            window.DialogResult = true;
        };
        return window.ShowDialog() == true ? window.Tag?.ToString() : null;
    }
}

internal sealed class SettingsWindow : Window
{
    private readonly TextBox _fontFamily = new();
    private readonly TextBox _fontSize = new();
    private readonly TextBox _tabSize = new();
    private readonly CheckBox _insertSpaces = new();
    private readonly CheckBox _wordWrap = new();
    private readonly CheckBox _minimap = new();
    private readonly CheckBox _ligatures = new();
    private readonly CheckBox _autoSave = new();
    private readonly CheckBox _formatOnSave = new();
    private readonly CheckBox _geminiInlineCompletion = new();
    private readonly ComboBox _theme = new();
    private readonly TextBox _search = new();
    private readonly PasswordBox _geminiKey = new();
    private readonly CheckBox _clearGeminiKey = new();

    public EditorPreferences Preferences { get; private set; } = new();
    public string? SelectedThemeId => _theme.SelectedValue as string;
    public string? GeminiApiKey => string.IsNullOrWhiteSpace(_geminiKey.Password) ? null : _geminiKey.Password;
    public bool ClearGeminiApiKey => _clearGeminiKey.IsChecked == true;

    public SettingsWindow(EditorPreferences initial, IReadOnlyList<ThemeDefinition> themes, string themeId, bool hasGeminiKey)
    {
        Title = "Paramètres — KeyceCode"; Width = 720; Height = 700; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "ForegroundBrush");
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new TextBlock { Text = "Paramètres", FontSize = 23, Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(header, 0); root.Children.Add(header);
        _search.Height = 34; _search.Margin = new Thickness(0, 0, 0, 15); _search.ToolTip = "Rechercher un paramètre";
        Grid.SetRow(_search, 1); root.Children.Add(_search);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var content = new StackPanel();
        _search.TextChanged += (_, _) =>
        {
            var query = _search.Text.Trim();
            foreach (var child in content.Children.OfType<FrameworkElement>())
                if (child.Tag is string label) child.Visibility = label.Contains(query, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        };
        content.Children.Add(new TextBlock { Text = "Éditeur", FontSize = 16, Margin = new Thickness(0, 0, 0, 8) });
        _theme.ItemsSource = themes;
        _theme.DisplayMemberPath = nameof(ThemeDefinition.DisplayName);
        _theme.SelectedValuePath = nameof(ThemeDefinition.Id);
        _theme.SelectedValue = themeId;
        content.Children.Add(Row("Thème", _theme));
        _fontFamily.Text = initial.FontFamily; content.Children.Add(Row("Famille de police", _fontFamily));
        _fontSize.Text = initial.FontSize.ToString(); content.Children.Add(Row("Taille de police", _fontSize));
        _tabSize.Text = initial.TabSize.ToString(); content.Children.Add(Row("Espaces par tabulation", _tabSize));
        _insertSpaces.Content = "Insérer des espaces"; _insertSpaces.IsChecked = initial.InsertSpaces; content.Children.Add(Row("Indentation", _insertSpaces));
        _wordWrap.Content = "Activer le retour à la ligne"; _wordWrap.IsChecked = initial.WordWrap; content.Children.Add(Row("Retour à la ligne", _wordWrap));
        _minimap.Content = "Afficher la minimap"; _minimap.IsChecked = initial.Minimap; content.Children.Add(Row("Minimap", _minimap));
        _ligatures.Content = "Activer les ligatures"; _ligatures.IsChecked = initial.FontLigatures; content.Children.Add(Row("Ligatures", _ligatures));
        _autoSave.Content = "Sauvegarder automatiquement après 1 seconde"; _autoSave.IsChecked = initial.AutoSave; content.Children.Add(Row("Sauvegarde", _autoSave));
        _formatOnSave.Content = "Formater avec le serveur LSP avant chaque sauvegarde"; _formatOnSave.IsChecked = initial.FormatOnSave; content.Children.Add(Row("Formatage", _formatOnSave));
        content.Children.Add(new TextBlock { Text = "KeyceIA", FontSize = 16, Margin = new Thickness(0, 18, 0, 8) });
        _geminiInlineCompletion.Content = "Activer les suggestions Gemini inline pendant la frappe";
        _geminiInlineCompletion.IsChecked = initial.GeminiInlineCompletionEnabled;
        content.Children.Add(Row("Autocomplétion Gemini", _geminiInlineCompletion));
        var inlineHelp = new TextBlock
        {
            Text = "Désactivé par défaut. Lorsque cette option est activée, seul le code avant le curseur (jusqu’à 4 000 caractères) est envoyé à Gemini après une courte pause. La sélection et le reste du fichier ne sont pas envoyés. Échap annule la suggestion, Tab l’accepte.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 10), Tag = "Autocomplétion Gemini"
        };
        inlineHelp.SetResourceReference(ForegroundProperty, "MutedBrush");
        content.Children.Add(inlineHelp);
        var keyHelp = new TextBlock { Text = hasGeminiKey
            ? "Une clé Gemini est enregistrée de manière chiffrée pour votre compte Windows. Saisissez une nouvelle clé pour la remplacer."
            : "Saisissez une clé Gemini. Elle sera chiffrée avec DPAPI pour votre compte Windows. GEMINI_API_KEY reste disponible pour le développement.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        keyHelp.SetResourceReference(ForegroundProperty, "MutedBrush");
        content.Children.Add(keyHelp);
        _geminiKey.ToolTip = "Clé API Gemini (la valeur enregistrée n’est jamais affichée)";
        content.Children.Add(Row("Clé API Gemini", _geminiKey));
        _clearGeminiKey.Content = "Supprimer la clé enregistrée";
        _clearGeminiKey.Margin = new Thickness(2, 5, 2, 0);
        content.Children.Add(_clearGeminiKey);
        scroll.Content = content; Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Annuler", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; };
        var save = new Button { Content = "Enregistrer", MinWidth = 100, Background = (Brush)Application.Current.Resources["AccentBrush"] };
        save.Click += Save_Click;
        footer.Children.Add(cancel); footer.Children.Add(save); Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(_fontSize.Text, out var fontSize) || fontSize is < 8 or > 48)
        {
            MessageBox.Show(this, "La taille de police doit être entre 8 et 48.", "Paramètres", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(_tabSize.Text, out var tabSize) || tabSize is < 1 or > 16)
        {
            MessageBox.Show(this, "L’indentation doit être entre 1 et 16.", "Paramètres", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Preferences = new EditorPreferences
        {
            FontFamily = string.IsNullOrWhiteSpace(_fontFamily.Text) ? "Cascadia Code, Consolas, monospace" : _fontFamily.Text.Trim(),
            FontSize = fontSize,
            TabSize = tabSize,
            InsertSpaces = _insertSpaces.IsChecked == true,
            WordWrap = _wordWrap.IsChecked == true,
            Minimap = _minimap.IsChecked == true,
            FontLigatures = _ligatures.IsChecked == true,
            AutoSave = _autoSave.IsChecked == true,
            FormatOnSave = _formatOnSave.IsChecked == true,
            GeminiInlineCompletionEnabled = _geminiInlineCompletion.IsChecked == true
        };
        DialogResult = true;
    }

    private static FrameworkElement Row(string label, Control control)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6), Tag = label };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 0); Grid.SetColumn(control, 1); grid.Children.Add(name); grid.Children.Add(control);
        return grid;
    }
}
