using KeyceCode.Services;
using Microsoft.Win32;
using System.IO;
using System.Text;
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
            WorkspaceTitle.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            WorkspaceFolderLabel.Text = WorkspaceTitle.Text.ToUpperInvariant();
            ExplorerTree.Items.Clear();
            foreach (var entry in await _workspace.GetEntriesAsync()) ExplorerTree.Items.Add(CreateTreeItem(entry));
            _documents.Clear(); _activeDocument = null; RefreshTabs();
            await RefreshGitAsync();
            StatusMessage.Text = path;
            ShowView("Explorer");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private TreeViewItem CreateTreeItem(WorkspaceEntry entry)
    {
        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(new TextBlock { Text = entry.Icon, Width = 30, Foreground = ParseBrush(entry.IsDirectory ? "#FFD7BA7D" : "#FF75BEFF") });
        panel.Children.Add(new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        var node = new TreeViewItem { Header = panel, Tag = entry, IsExpanded = false };
        foreach (var child in entry.Children) node.Items.Add(CreateTreeItem(child));
        return node;
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
            RefreshTabs(); await SendDocumentAsync(existing); return;
        }
        if (_activeDocument is { IsPreview: true, IsDirty: false } preview) _documents.Remove(preview);
        try
        {
            var text = _workspace is not null && IsInsideWorkspace(path, _workspace.RootPath)
                ? await _workspace.ReadFileAsync(path)
                : await File.ReadAllTextAsync(path, Encoding.UTF8);
            var kind = WorkspaceService.GetFileKind(path);
            var document = new EditorDocument(path, text, kind.Language) { IsPreview = !pin };
            _documents.Add(document); _activeDocument = document;
            StatusLanguage.Content = kind.Language;
            StatusMessage.Text = path;
            RefreshTabs(); await SendDocumentAsync(document);
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
            var tab = new Button { Content = content, Tag = document, Height = 35, Padding = new Thickness(10, 2, 8, 2), Background = active ? ParseBrush("#FF1E1E1E") : ParseBrush("#FF2D2D2D"), BorderBrush = active ? ParseBrush("#FF007ACC") : Brushes.Transparent, BorderThickness = new Thickness(0, 1, 0, 0), Style = null };
            tab.Click += Tab_Click; EditorTabs.Children.Add(tab);
        }
        if (_activeDocument is not null)
            Breadcrumbs.Text = _workspace is null ? _activeDocument.Path : Path.GetRelativePath(_workspace.RootPath, _activeDocument.Path);
        else Breadcrumbs.Text = _workspace?.RootPath ?? "Ouvrez un dossier pour commencer";
    }

    private async void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EditorDocument document }) return;
        _activeDocument = document; RefreshTabs(); await SendDocumentAsync(document);
    }

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
        _documents.Remove(document);
        if (ReferenceEquals(_activeDocument, document))
        {
            _activeDocument = _documents.Count == 0 ? null : _documents[Math.Clamp(index - 1, 0, _documents.Count - 1)];
            if (_activeDocument is not null) await SendDocumentAsync(_activeDocument);
            else await PostEditorMessageAsync(new { type = "setDocument", id = "welcome", path = "", text = "// Bienvenue dans KeyceCode", language = "csharp" });
        }
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
            if (_workspace is not null && IsInsideWorkspace(document.Path, _workspace.RootPath)) await _workspace.WriteFileAsync(document.Path, document.Text);
            else await File.WriteAllTextAsync(document.Path, document.Text, new UTF8Encoding(false));
            document.IsDirty = false; document.IsPreview = false; RefreshTabs(); StatusMessage.Text = "Fichier enregistré"; return true;
        }
        catch (Exception ex) { ShowError(ex); return false; }
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
            if (await ConfirmDirtyDocumentsAsync()) { _documents.Clear(); _gemini.Dispose(); Close(); }
        }
        else
        {
            _gemini.Dispose();
            try { if (_terminalProcess is { HasExited: false }) _terminalProcess.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }
}
