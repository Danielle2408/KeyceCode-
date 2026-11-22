using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
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
        if (_activePanel == "Terminal") TerminalOutput.Clear();
        else if (_activePanel == "Output") OutputPanel.Clear();
        else if (_activePanel == "DebugConsole") DebugConsolePanel.Clear();
    }
    private void ClearTerminal_Click(object sender, RoutedEventArgs e) => TerminalOutput.Clear();

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

    private void SearchMarketplace_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ExtensionSearch.Text)) { StatusMessage.Text = "Entrez un nom ou une catégorie."; return; }
        var uri = "https://open-vsx.org/extension/query?search=" + Uri.EscapeDataString(ExtensionSearch.Text.Trim());
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        StatusMessage.Text = "Open VSX s’est ouvert dans le navigateur; l’installation intégrée n’est pas disponible.";
    }

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

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var themeService = ((App)Application.Current).ThemeService;
        var settings = new SettingsWindow(_editorPreferences, themeService.Themes, themeService.CurrentThemeId) { Owner = this };
        if (settings.ShowDialog() != true) return;
        _editorPreferences = settings.Preferences;
        _preferencesService.Save(_editorPreferences);
        if (settings.SelectedThemeId is not null && settings.SelectedThemeId != themeService.CurrentThemeId)
            themeService.SetTheme(Application.Current, settings.SelectedThemeId);
        await SendEditorOptionsAsync();
    }

    private void Encoding_Click(object sender, RoutedEventArgs e) => StatusMessage.Text = "Encodage actuel : UTF-8";
    private void Eol_Click(object sender, RoutedEventArgs e) => StatusMessage.Text = "Fin de ligne actuelle : CRLF";
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
        Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)); Foreground = Brushes.White;
        _source = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var dock = new DockPanel { Margin = new Thickness(10) };
        _search.Height = 34; _search.Margin = new Thickness(0, 0, 0, 7); _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => Filter(); _search.KeyDown += Search_KeyDown;
        DockPanel.SetDock(_search, Dock.Top); dock.Children.Add(_search);
        _list.Background = Brushes.Transparent; _list.BorderThickness = new Thickness(0); _list.Foreground = Brushes.White;
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

    public static string? Prompt(Window owner, string title, string placeholder)
    {
        var window = new QuickPickWindow(title, []) { Owner = owner };
        window._search.ToolTip = placeholder;
        window._search.KeyDown += (sender, e) => { if (e.Key == Key.Enter && int.TryParse(window._search.Text, out var _)) { window.Tag = window._search.Text; window.DialogResult = true; } };
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
    private readonly ComboBox _theme = new();
    private readonly TextBox _search = new();

    public EditorPreferences Preferences { get; private set; } = new();
    public string? SelectedThemeId => _theme.SelectedValue as string;

    public SettingsWindow(EditorPreferences initial, IReadOnlyList<ThemeDefinition> themes, string themeId)
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
        content.Children.Add(new TextBlock { Text = "KeyceIA", FontSize = 16, Margin = new Thickness(0, 18, 0, 8) });
        content.Children.Add(new TextBlock { Text = "La clé Gemini doit être définie avec GEMINI_API_KEY dans l’environnement. Aucun secret n’est enregistré dans les paramètres.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 8, 0, 0) });
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
            AutoSave = _autoSave.IsChecked == true
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
