using KeyceCode.Services;
using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KeyceCode;

public partial class MainWindow
{
    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Ouvrir un dossier de projet" };
        if (dialog.ShowDialog(this) == true) await OpenWorkspaceAsync(dialog.FolderName);
    }

    private async Task OpenWorkspaceAsync(string path)
    {
        if (!await ConfirmDirtyDocumentsAsync()) return;
        try
        {
            _workspace = new WorkspaceService(path);
            _navigationHistory.Clear();
            _navigationHistoryIndex = -1;
            UpdateNavigationButtons();
            WorkspaceTitle.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            WorkspaceFolderLabel.Text = WorkspaceTitle.Text.ToUpperInvariant();
            await RefreshExplorerAsync();
            _documents.Clear(); _activeDocument = null; RefreshTabs();
            await RefreshGitAsync();
            await ConfigureLanguageServerAsync(_workspace.RootPath);
            ShowView("Explorer");
            _recentWorkspaces = WorkspaceSessionService.AddRecentWorkspace(_recentWorkspaces, _workspace.RootPath);
            RefreshRecentWorkspacesMenu();
            SaveSession();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RefreshRecentWorkspacesMenu()
    {
        RecentWorkspacesMenu.Items.Clear();
        var workspaces = WorkspaceSessionService.NormalizeRecentWorkspaces(_recentWorkspaces);
        _recentWorkspaces = workspaces;
        if (workspaces.Count == 0)
            RecentWorkspacesMenu.Items.Add(new MenuItem { Header = "Aucun dossier récent", IsEnabled = false });
        else
        {
            foreach (var path in workspaces)
            {
                var label = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                var item = new MenuItem { Header = string.IsNullOrWhiteSpace(label) ? path : label, ToolTip = path };
                item.Click += async (_, _) => await OpenWorkspaceAsync(path);
                RecentWorkspacesMenu.Items.Add(item);
            }
        }
        RecentWorkspacesMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Effacer les dossiers récents", IsEnabled = workspaces.Count > 0 };
        clear.Click += (_, _) => ClearRecentWorkspaces_Click();
        RecentWorkspacesMenu.Items.Add(clear);
    }

    private void ClearRecentWorkspaces_Click()
    {
        _recentWorkspaces = [];
        RefreshRecentWorkspacesMenu();
        SaveSession();
        StatusMessage.Text = "Historique des dossiers récents effacé";
    }

    private async Task RefreshExplorerAsync()
    {
        if (_workspace is null) return;
        var expanded = ExplorerTree.Items.Cast<TreeViewItem>().SelectMany(FlattenTreeItems)
            .Where(item => item.IsExpanded && item.Tag is WorkspaceEntry)
            .Select(item => ((WorkspaceEntry)item.Tag).FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ExplorerTree.Items.Clear();
        foreach (var entry in await _workspace.GetEntriesAsync()) ExplorerTree.Items.Add(CreateTreeItem(entry, expanded));
    }

    private static IEnumerable<TreeViewItem> FlattenTreeItems(TreeViewItem item)
    {
        yield return item;
        foreach (var child in item.Items.OfType<TreeViewItem>())
            foreach (var nested in FlattenTreeItems(child)) yield return nested;
    }

    private TreeViewItem CreateTreeItem(WorkspaceEntry entry, HashSet<string>? expanded = null)
    {
        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(new TextBlock { Text = entry.Icon, Width = 30, Foreground = ParseBrush(entry.IsDirectory ? "#FFD7BA7D" : "#FF75BEFF") });
        panel.Children.Add(new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        var node = new TreeViewItem { Header = panel, Tag = entry, IsExpanded = expanded?.Contains(entry.FullPath) ?? false };
        foreach (var child in entry.Children) node.Items.Add(CreateTreeItem(child, expanded));
        var menu = new ContextMenu();
        menu.Items.Add(MenuAction("Nouveau fichier…", () => CreateWorkspaceEntryAsync(entry, directory: false)));
        menu.Items.Add(MenuAction("Nouveau dossier…", () => CreateWorkspaceEntryAsync(entry, directory: true)));
        if (!entry.IsDirectory)
        {
            menu.Items.Add(MenuAction("Ouvrir dans le groupe secondaire", async () =>
            {
                await PostEditorMessageAsync(new { type = "ensureSplit", orientation = "vertical" });
                await OpenDocumentAsync(entry.FullPath, pin: true);
            }));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuAction("Renommer…", () => RenameWorkspaceEntryAsync(entry)));
        menu.Items.Add(MenuAction("Supprimer", () => DeleteWorkspaceEntryAsync(entry)));
        node.ContextMenu = menu;
        node.ContextMenuOpening += (_, _) => node.IsSelected = true;
        return node;
    }

    private string? GetExplorerParent(WorkspaceEntry? selected)
    {
        if (_workspace is null) return null;
        if (selected is null) return _workspace.RootPath;
        return selected.IsDirectory ? selected.FullPath : Path.GetDirectoryName(selected.FullPath);
    }

    private async Task CreateWorkspaceEntryAsync(WorkspaceEntry? selected, bool directory)
    {
        if (_workspace is null) { StatusMessage.Text = "Ouvrez un dossier avant de créer un fichier."; return; }
        var parent = GetExplorerParent(selected);
        if (parent is null) return;
        var kind = directory ? "dossier" : "fichier";
        var name = QuickPickWindow.Prompt(this, $"Nouveau {kind}", $"Nom du {kind}");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var relativePath = Path.GetRelativePath(_workspace.RootPath, Path.Combine(parent, name.Trim()));
            var path = directory ? _workspace.CreateDirectory(relativePath) : _workspace.CreateFile(relativePath);
            await RefreshExplorerAsync();
            if (!directory) await OpenDocumentAsync(path, pin: true);
            else StatusMessage.Text = $"Dossier créé : {name}";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ExplorerNewFile_Click(object sender, RoutedEventArgs e)
    {
        var selected = (ExplorerTree.SelectedItem as TreeViewItem)?.Tag as WorkspaceEntry;
        await CreateWorkspaceEntryAsync(selected, directory: false);
    }

    private async void ExplorerNewFolder_Click(object sender, RoutedEventArgs e)
    {
        var selected = (ExplorerTree.SelectedItem as TreeViewItem)?.Tag as WorkspaceEntry;
        await CreateWorkspaceEntryAsync(selected, directory: true);
    }

    private async void ExplorerRefresh_Click(object sender, RoutedEventArgs e)
    {
        try { await RefreshExplorerAsync(); StatusMessage.Text = "Explorateur actualisé"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ExplorerMore_Click(object sender, RoutedEventArgs e)
    {
        var selected = (ExplorerTree.SelectedItem as TreeViewItem)?.Tag as WorkspaceEntry;
        var menu = new ContextMenu();
        menu.Items.Add(MenuAction("Nouveau fichier…", () => CreateWorkspaceEntryAsync(selected, directory: false)));
        menu.Items.Add(MenuAction("Nouveau dossier…", () => CreateWorkspaceEntryAsync(selected, directory: true)));
        menu.Items.Add(MenuAction("Actualiser l’Explorateur", async () => await RefreshExplorerAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuAction("Réduire tous les dossiers", () =>
        {
            foreach (var node in ExplorerTree.Items.Cast<TreeViewItem>().SelectMany(FlattenTreeItems)) node.IsExpanded = false;
            return Task.CompletedTask;
        }));
        var copyPath = MenuAction("Copier le chemin sélectionné", () =>
        {
            if (selected is not null) Clipboard.SetText(selected.FullPath);
            return Task.CompletedTask;
        });
        copyPath.IsEnabled = selected is not null;
        menu.Items.Add(copyPath);
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async Task RenameWorkspaceEntryAsync(WorkspaceEntry entry)
    {
        if (_workspace is null) return;
        if (entry.IsDirectory && _documents.Any(document => IsInsideWorkspace(document.Path, entry.FullPath)))
        {
            StatusMessage.Text = "Fermez d’abord les éditeurs ouverts dans ce dossier, puis réessayez.";
            return;
        }
        var name = QuickPickWindow.Prompt(this, "Renommer", "Nouveau nom", entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == entry.Name) return;
        try
        {
            var newPath = _workspace.RenameEntry(entry.RelativePath, name.Trim());
            for (var i = 0; i < _navigationHistory.Count; i++)
                if (string.Equals(_navigationHistory[i], entry.FullPath, StringComparison.OrdinalIgnoreCase))
                    _navigationHistory[i] = newPath;
            var document = _documents.FirstOrDefault(item => string.Equals(item.Path, entry.FullPath, StringComparison.OrdinalIgnoreCase));
            if (document is not null)
            {
                document.Path = newPath;
                document.Alias = null;
                document.Language = WorkspaceService.GetFileKind(newPath).Language;
                if (ReferenceEquals(document, _activeDocument)) await SendDocumentAsync(document);
                StatusLanguage.Content = document.Language;
            }
            await RefreshExplorerAsync();
            RefreshTabs();
            StatusMessage.Text = $"Renommé : {name.Trim()}";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task DeleteWorkspaceEntryAsync(WorkspaceEntry entry)
    {
        if (_workspace is null) return;
        var affected = _documents.Where(document => entry.IsDirectory
            ? IsInsideWorkspace(document.Path, entry.FullPath)
            : string.Equals(document.Path, entry.FullPath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (affected.Count > 0)
        {
            StatusMessage.Text = "Fermez d’abord les éditeurs concernés avant de supprimer cette entrée.";
            return;
        }
        var answer = MessageBox.Show(this, $"Supprimer {(entry.IsDirectory ? "le dossier et tout son contenu" : "le fichier")} « {entry.Name} » ?", "Confirmer la suppression", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            _workspace.DeleteEntry(entry.RelativePath);
            await RefreshExplorerAsync();
            StatusMessage.Text = $"Supprimé : {entry.Name}";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ExplorerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ExplorerTree.SelectedItem is not TreeViewItem { Tag: WorkspaceEntry entry } || entry.IsDirectory) return;
        await OpenDocumentAsync(entry.FullPath);
    }

    private async void ExplorerTree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExplorerTree.SelectedItem is TreeViewItem { Tag: WorkspaceEntry entry } && !entry.IsDirectory)
        {
            var doc = _documents.FirstOrDefault(d => d.Path == entry.FullPath);
            if (doc is not null) doc.IsPreview = false;
            await OpenDocumentAsync(entry.FullPath, pin: true);
        }
    }

    private async Task OpenDocumentAsync(string path, bool pin = false)
    {
        var existing = _documents.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _activeDocument = existing;
            if (pin) existing.IsPreview = false;
            RecordNavigation(existing.Path);
            RefreshTabs(); await SendDocumentAsync(existing); return;
        }
        if (_activeDocument is { IsPreview: true, IsDirty: false } preview) _documents.Remove(preview);
        try
        {
            path = _workspace is not null && IsInsideWorkspace(path, _workspace.RootPath)
                ? _workspace.ResolvePath(path)
                : Path.GetFullPath(path);
            var (text, encodingId) = await ReadTextFileAsync(path);
            var lineEnding = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
                : text.Contains('\n') ? "\n" : text.Contains('\r') ? "\r" : Environment.NewLine;
            var kind = WorkspaceService.GetFileKind(path);
            var document = new EditorDocument(path, text, kind.Language) { IsPreview = !pin, EncodingId = encodingId, LineEnding = lineEnding };
            _documents.Add(document); _activeDocument = document;
            RecordNavigation(document.Path);
            StatusLanguage.Content = kind.Language;
            RefreshTabs(); await SendDocumentAsync(document, replaceEditorText: true);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static bool IsInsideWorkspace(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private void RefreshTabs()
    {
        EditorTabs.Children.Clear();
        foreach (var document in _documents)
        {
            var content = new DockPanel();
            var dirty = new TextBlock { Text = document.IsDirty ? "●" : "", Foreground = ParseBrush("#FFE0C080"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
            content.Children.Add(dirty);
            content.Children.Add(new TextBlock { Text = document.Name, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis });
            var close = new Button { Content = "×", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(8, 0, 0, 0), Tag = document, ToolTip = "Fermer l’éditeur" };
            close.Click += CloseTab_Click; content.Children.Add(close);
            var active = ReferenceEquals(document, _activeDocument);
            var tab = new Button { Content = content, Tag = document, Height = 35, Padding = new Thickness(10, 2, 8, 2), BorderThickness = new Thickness(0, 1, 0, 0) };
            tab.SetResourceReference(BackgroundProperty, active ? "TabActiveBackground" : "TabBarBackground");
            tab.SetResourceReference(ForegroundProperty, active ? "ForegroundBrush" : "MutedBrush");
            tab.SetResourceReference(BorderBrushProperty, active ? "AccentBrush" : "BorderBrush");
            var tabMenu = new ContextMenu();
            tabMenu.Items.Add(MenuAction("Ouvrir dans le groupe secondaire", () => OpenDocumentInSecondaryPaneAsync(document)));
            tab.ContextMenu = tabMenu;
            tab.Click += Tab_Click; EditorTabs.Children.Add(tab);
        }
        if (_activeDocument is not null)
            Breadcrumbs.Text = _workspace is null ? _activeDocument.Path : Path.GetRelativePath(_workspace.RootPath, _activeDocument.Path);
        else Breadcrumbs.Text = _workspace?.RootPath ?? "Ouvrez un dossier pour commencer";
    }

    private async Task OpenDocumentInSecondaryPaneAsync(EditorDocument document)
    {
        await PostEditorMessageAsync(new { type = "ensureSplit", orientation = "vertical" });
        _activeDocument = document;
        if (!string.IsNullOrWhiteSpace(document.Path)) RecordNavigation(document.Path);
        RefreshTabs();
        await SendDocumentAsync(document);
    }

    private async void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EditorDocument document }) return;
        _activeDocument = document; RecordNavigation(document.Path); RefreshTabs(); await SendDocumentAsync(document);
    }

    private void RecordNavigation(string path)
    {
        if (_navigatingHistory || string.IsNullOrWhiteSpace(path)) return;
        var fullPath = Path.GetFullPath(path);
        if (_navigationHistoryIndex >= 0 && string.Equals(_navigationHistory[_navigationHistoryIndex], fullPath, StringComparison.OrdinalIgnoreCase)) return;
        if (_navigationHistoryIndex + 1 < _navigationHistory.Count)
            _navigationHistory.RemoveRange(_navigationHistoryIndex + 1, _navigationHistory.Count - _navigationHistoryIndex - 1);
        _navigationHistory.Add(fullPath);
        if (_navigationHistory.Count > 200) _navigationHistory.RemoveAt(0);
        _navigationHistoryIndex = _navigationHistory.Count - 1;
        UpdateNavigationButtons();
    }

    private void UpdateNavigationButtons()
    {
        if (NavigateBackButton is not null) NavigateBackButton.IsEnabled = _navigationHistoryIndex > 0;
        if (NavigateForwardButton is not null) NavigateForwardButton.IsEnabled = _navigationHistoryIndex >= 0 && _navigationHistoryIndex < _navigationHistory.Count - 1;
    }

    private async Task NavigateHistoryAsync(int direction)
    {
        var target = _navigationHistoryIndex + direction;
        while (target >= 0 && target < _navigationHistory.Count)
        {
            var path = _navigationHistory[target];
            if (File.Exists(path))
            {
                _navigationHistoryIndex = target;
                UpdateNavigationButtons();
                _navigatingHistory = true;
                try { await OpenDocumentAsync(path, pin: true); }
                finally { _navigatingHistory = false; }
                return;
            }
            target += direction;
        }
        _navigationHistoryIndex = direction < 0 ? 0 : _navigationHistory.Count - 1;
        UpdateNavigationButtons();
    }

    private async void NavigateBack_Click(object sender, RoutedEventArgs e) => await NavigateHistoryAsync(-1);
    private async void NavigateForward_Click(object sender, RoutedEventArgs e) => await NavigateHistoryAsync(1);

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EditorDocument document }) await CloseDocumentAsync(document);
    }

    private async Task CloseDocumentAsync(EditorDocument document)
    {
        if (document.IsDirty)
        {
            var result = MessageBox.Show(this, $"Enregistrer les modifications de {document.Name} ?", "KeyceCode", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) return;
            if (result == MessageBoxResult.Yes && !await SaveDocumentAsync(document)) return;
        }
        var index = _documents.IndexOf(document);
        if (_languageServer is not null && _lspDocumentUris.Remove(document.Id, out var lspUri))
        {
            try { await _languageServer.CloseDocumentAsync(lspUri); }
            catch (Exception ex) { OutputPanel.AppendText("[LSP] Fermeture document : " + ex.Message + Environment.NewLine); }
            _lspDiagnostics.Remove(lspUri.LocalPath);
            RefreshLanguageProblems();
        }
        _documents.Remove(document);
        if (ReferenceEquals(_activeDocument, document))
        {
            _activeDocument = _documents.Count == 0 ? null : _documents[Math.Clamp(index - 1, 0, _documents.Count - 1)];
            if (_activeDocument is not null) await SendDocumentAsync(_activeDocument);
            else await PostEditorMessageAsync(new { type = "setDocument", id = "welcome", path = "", text = "// Bienvenue dans KeyceCode", language = "csharp" });
        }
        await PostEditorMessageAsync(new { type = "closeDocument", id = document.Id, fallbackId = _activeDocument?.Id ?? "welcome" });
        RefreshTabs();
    }

    private async Task<bool> ConfirmDirtyDocumentsAsync()
    {
        foreach (var document in _documents.Where(d => d.IsDirty).ToList())
        {
            var result = MessageBox.Show(this, $"Enregistrer les modifications de {document.Name} ?", "KeyceCode", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) return false;
            if (result == MessageBoxResult.Yes && !await SaveDocumentAsync(document)) return false;
        }
        return true;
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Ouvrir un fichier", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            foreach (var file in dialog.FileNames) await OpenDocumentAsync(file, pin: true);
    }

    private async void NewFile_Click(object sender, RoutedEventArgs e)
    {
        var document = new EditorDocument("", "", "csharp", $"Sans titre-{_documents.Count + 1}.cs") { IsDirty = true, IsPreview = false };
        _documents.Add(document); _activeDocument = document; RefreshTabs(); await SendDocumentAsync(document);
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveActiveAsync();
    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is null) return;
        var dialog = new SaveFileDialog { FileName = _activeDocument.Name, Filter = "Tous les fichiers|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        _activeDocument.Path = dialog.FileName; _activeDocument.Alias = null;
        _activeDocument.Language = WorkspaceService.GetFileKind(dialog.FileName).Language;
        await SaveDocumentAsync(_activeDocument);
    }

    private async Task SaveActiveAsync()
    {
        if (_activeDocument is not null) await SaveDocumentAsync(_activeDocument);
    }

    private async Task<bool> SaveDocumentAsync(EditorDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Path))
        {
            var dialog = new SaveFileDialog { FileName = document.Name, Filter = "Tous les fichiers|*.*" };
            if (dialog.ShowDialog(this) != true) return false;
            document.Path = dialog.FileName; document.Alias = null;
            document.Language = WorkspaceService.GetFileKind(dialog.FileName).Language;
        }
        try
        {
            var formatNote = _editorPreferences.FormatOnSave
                ? await FormatDocumentBeforeSaveAsync(document)
                : null;
            var text = NormalizeLineEndings(document.Text, document.LineEnding);
            if (_workspace is not null && IsInsideWorkspace(document.Path, _workspace.RootPath))
                await _workspace.WriteFileAsync(document.Path, text, GetTextEncoding(document.EncodingId));
            else await File.WriteAllTextAsync(document.Path, text, GetTextEncoding(document.EncodingId));
            await EnsureLspDocumentAsync(document);
            if (_languageServer is not null && _lspDocumentUris.TryGetValue(document.Id, out var lspUri))
                await _languageServer.SaveDocumentAsync(lspUri, document.Text);
            document.IsDirty = false; document.IsPreview = false; RefreshTabs(); UpdateFileStatus(document);
            StatusMessage.Text = formatNote ?? "Fichier enregistré";
            return true;
        }
        catch (Exception ex) { ShowError(ex); return false; }
    }

    private async Task<string?> FormatDocumentBeforeSaveAsync(EditorDocument document)
    {
        if (document.Language is "json" or "jsonc" or "javascript" or "javascriptreact" or "typescript" or "typescriptreact" or "html" or "css" or "scss")
        {
            var formattedText = await FormatDocumentInMonacoAsync(document);
            if (formattedText is not null)
            {
                var changed = formattedText != document.Text;
                if (changed)
                {
                    document.Text = formattedText;
                    document.IsDirty = true;
                    RefreshTabs();
                }
                return changed
                    ? "Fichier enregistré après formatage Monaco"
                    : "Fichier enregistré · aucun changement de formatage";
            }
        }

        var server = _languageServer;
        if (server is null || !server.IsInitialized || _workspace is null ||
            string.IsNullOrWhiteSpace(document.Path) || !IsInsideWorkspace(document.Path, _workspace.RootPath))
            return "Fichier enregistré · formatage ignoré (aucun serveur LSP configuré)";

        try
        {
            await EnsureLspDocumentAsync(document);
            if (!_lspDocumentUris.TryGetValue(document.Id, out var uri))
                return "Fichier enregistré · aucun document LSP ouvert pour le formatage";

            var capabilities = server.ServerCapabilities;
            if (!capabilities.TryGetProperty("documentFormattingProvider", out var provider) ||
                provider.ValueKind is not (JsonValueKind.True or JsonValueKind.Object))
                return "Fichier enregistré · le serveur LSP n’annonce pas de formateur";

            // Send the latest buffer first: the save keystroke can arrive before Monaco's debounce didChange.
            await server.ChangeDocumentAsync(uri, document.Text);
            var edits = await server.RequestAsync("textDocument/formatting", new
            {
                textDocument = new { uri = uri.AbsoluteUri },
                options = new { tabSize = _editorPreferences.TabSize, insertSpaces = _editorPreferences.InsertSpaces }
            });
            if (edits.ValueKind != JsonValueKind.Array || edits.GetArrayLength() == 0)
                return "Fichier enregistré · le formateur n’a proposé aucun changement";

            var formattedText = ApplyLspTextEdits(document.Text, edits);
            if (formattedText == document.Text) return "Fichier enregistré · aucun changement de formatage";

            await server.ChangeDocumentAsync(uri, formattedText);
            document.Text = formattedText;
            if (_webReady)
                await PostEditorMessageAsync(new { type = "applyTextEdits", edits, suppressChanges = true });
            return "Fichier enregistré après formatage LSP";
        }
        catch (OperationCanceledException)
        {
            OutputPanel.AppendText("[LSP] Formatage annulé; le contenu original est conservé et sera enregistré.\r\n");
            return "Fichier enregistré sans formatage (annulé)";
        }
        catch (Exception ex)
        {
            OutputPanel.AppendText("[LSP] Formatage avant sauvegarde ignoré; le contenu original est conservé : " + ex.Message + Environment.NewLine);
            return "Fichier enregistré sans formatage (erreur LSP)";
        }
    }

    private async Task<string?> FormatDocumentInMonacoAsync(EditorDocument document)
    {
        if (!_webReady) return null;
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingFormatOnSave[requestId] = completion;
        try
        {
            await PostEditorMessageAsync(new { type = "formatForSave", requestId, id = document.Id });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            OutputPanel.AppendText("[Monaco] Délai dépassé pendant le formatage avant sauvegarde.\r\n");
            return null;
        }
        finally { _pendingFormatOnSave.Remove(requestId); }
    }

    private static string ApplyLspTextEdits(string source, JsonElement edits)
    {
        var parsed = new List<(int Start, int End, string Text)>();
        foreach (var edit in edits.EnumerateArray())
        {
            if (!edit.TryGetProperty("range", out var range) ||
                !TryGetLspOffset(source, range, "start", out var start) ||
                !TryGetLspOffset(source, range, "end", out var end) || start > end ||
                !edit.TryGetProperty("newText", out var newText) || newText.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Le serveur LSP a renvoyé une édition de formatage invalide.");
            parsed.Add((start, end, newText.GetString() ?? string.Empty));
        }

        var ascending = parsed.OrderBy(edit => edit.Start).ThenBy(edit => edit.End).ToArray();
        for (var index = 1; index < ascending.Length; index++)
            if (ascending[index].Start < ascending[index - 1].End)
                throw new InvalidDataException("Le serveur LSP a renvoyé des éditions de formatage qui se chevauchent.");

        var result = new StringBuilder(source);
        foreach (var edit in ascending.OrderByDescending(edit => edit.Start))
        {
            result.Remove(edit.Start, edit.End - edit.Start);
            result.Insert(edit.Start, edit.Text);
        }
        return result.ToString();
    }

    private static bool TryGetLspOffset(string text, JsonElement range, string point, out int offset)
    {
        offset = 0;
        if (!range.TryGetProperty(point, out var position) ||
            !position.TryGetProperty("line", out var lineJson) || !lineJson.TryGetInt32(out var line) || line < 0 ||
            !position.TryGetProperty("character", out var characterJson) || !characterJson.TryGetInt32(out var character) || character < 0)
            return false;

        var currentLine = 0;
        while (currentLine < line)
        {
            var newline = text.IndexOf('\n', offset);
            if (newline < 0) return false;
            offset = newline + 1;
            currentLine++;
        }
        var lineEnd = text.IndexOf('\n', offset);
        if (lineEnd < 0) lineEnd = text.Length;
        var lineLength = lineEnd - offset;
        if (character > lineLength) return false;
        offset += character;
        return true;
    }

    private static Task<List<string>> CollectWorkspaceFilesAsync(string root) => Task.Run(() =>
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".vs", "bin", "obj", "node_modules", ".idea" };
        var files = new List<string>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0 && files.Count < 20_000)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (!ignored.Contains(Path.GetFileName(path))) pending.Push(path);
                        }
                        else files.Add(path);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    if (files.Count >= 20_000) break;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    });

    private async void CloseEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is not null) await CloseDocumentAsync(_activeDocument);
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_documents.Any(d => d.IsDirty))
        {
            e.Cancel = true;
            if (await ConfirmDirtyDocumentsAsync()) { SaveSession(); _documents.Clear(); _gemini.Dispose(); Close(); }
        }
        else
        {
            SaveSession();
            _gemini.Dispose();
        }
    }
}
