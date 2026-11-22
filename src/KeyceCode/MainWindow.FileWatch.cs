using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace KeyceCode;

public partial class MainWindow
{
    private readonly ConcurrentDictionary<string, (DateTime SeenAt, bool Deleted)> _pendingDiskChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _diskWatchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private FileSystemWatcher? _diskWatcher;
    private string? _diskWatcherRoot;
    private bool _processingDiskChanges;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        _diskWatchTimer.Tick += DiskWatchTimer_Tick;
        Loaded += (_, _) => _diskWatchTimer.Start();
        Closed += (_, _) =>
        {
            _diskWatchTimer.Stop();
            _diskWatchTimer.Tick -= DiskWatchTimer_Tick;
            StopDiskWatcher();
        };
    }

    private async void DiskWatchTimer_Tick(object? sender, EventArgs e)
    {
        SyncDiskWatcherRoot();
        if (_processingDiskChanges) return;
        var ready = _pendingDiskChanges.Where(item => DateTime.UtcNow - item.Value.SeenAt >= TimeSpan.FromMilliseconds(350)).ToArray();
        if (ready.Length == 0) return;
        _processingDiskChanges = true;
        try
        {
            foreach (var item in ready)
            {
                if (!_pendingDiskChanges.TryRemove(item.Key, out var change)) continue;
                if (_diskWatcherRoot is null || _workspace is null || !IsInsideWorkspace(item.Key, _diskWatcherRoot)) continue;
                var document = _documents.FirstOrDefault(open => !string.IsNullOrWhiteSpace(open.Path) &&
                    string.Equals(Path.GetFullPath(open.Path), Path.GetFullPath(item.Key), StringComparison.OrdinalIgnoreCase));
                if (document is null) continue;

                if (change.Deleted || !File.Exists(item.Key))
                {
                    MessageBox.Show(this,
                        $"Le fichier « {document.Name} » a été supprimé ou renommé sur le disque. Le contenu de l’éditeur est conservé{(document.IsDirty ? " (avec des modifications non enregistrées)" : "")}.",
                        "Fichier supprimé à l’extérieur", MessageBoxButton.OK, MessageBoxImage.Warning);
                    continue;
                }

                try
                {
                    if (new FileInfo(item.Key).Length > 20L * 1024 * 1024)
                    {
                        StatusMessage.Text = $"Changement externe ignoré pour {document.Name} (fichier supérieur à 20 Mo).";
                        continue;
                    }
                    var (diskText, diskEncoding) = await ReadTextFileAsync(item.Key);
                    if (string.Equals(diskText, document.Text, StringComparison.Ordinal)) continue;

                    var decision = new ExternalFileChangeWindow(document.Name, document.Path, document.Text, diskText, document.IsDirty)
                    {
                        Owner = this
                    }.ShowDialog();
                    if (decision == true)
                    {
                        document.Text = diskText;
                        document.EncodingId = diskEncoding;
                        document.LineEnding = diskText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
                            : diskText.Contains('\r') ? "\r" : "\n";
                        document.IsDirty = false;
                        RefreshTabs();
                        if (ReferenceEquals(document, _activeDocument))
                        {
                            await SendDocumentAsync(document);
                            UpdateFileStatus(document);
                        }
                        if (_languageServer is not null && _lspDocumentUris.TryGetValue(document.Id, out var uri))
                            try { await _languageServer.ChangeDocumentAsync(uri, document.Text); }
                            catch (Exception ex) { OutputPanel.AppendText("[LSP] Synchronisation du rechargement : " + ex.Message + Environment.NewLine); }
                        StatusMessage.Text = $"Rechargé depuis le disque : {document.Name}";
                    }
                    else
                    {
                        // Mark the divergence so a later save requires an explicit user action.
                        document.IsDirty = true;
                        RefreshTabs();
                        StatusMessage.Text = $"Conflit externe conservé dans l’éditeur : {document.Name}. Enregistrer remplacera la version sur disque.";
                    }
                }
                catch (IOException)
                {
                    QueueDiskChange(item.Key, deleted: false);
                }
                catch (UnauthorizedAccessException ex)
                {
                    StatusMessage.Text = $"Impossible de relire {document.Name} après son changement externe : {ex.Message}";
                }
                catch (InvalidDataException ex)
                {
                    StatusMessage.Text = $"Le fichier externe {document.Name} n’est pas du texte lisible : {ex.Message}";
                }
            }
        }
        finally { _processingDiskChanges = false; }
    }

    private void SyncDiskWatcherRoot()
    {
        var root = _workspace?.RootPath;
        if (string.Equals(root, _diskWatcherRoot, StringComparison.OrdinalIgnoreCase)) return;
        StopDiskWatcher();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 16 * 1024,
                EnableRaisingEvents = false
            };
            watcher.Changed += DiskWatcher_Changed;
            watcher.Created += DiskWatcher_Changed;
            watcher.Deleted += DiskWatcher_Deleted;
            watcher.Renamed += DiskWatcher_Renamed;
            watcher.Error += DiskWatcher_Error;
            _diskWatcher = watcher;
            _diskWatcherRoot = Path.GetFullPath(root);
            watcher.EnableRaisingEvents = true;
            StatusMessage.Text = "Surveillance des fichiers du workspace active";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _diskWatcher = null;
            _diskWatcherRoot = null;
            StatusMessage.Text = "Surveillance disque indisponible : " + ex.Message;
        }
    }

    private void StopDiskWatcher()
    {
        var watcher = _diskWatcher;
        _diskWatcher = null;
        _diskWatcherRoot = null;
        if (watcher is null) return;
        watcher.EnableRaisingEvents = false;
        watcher.Changed -= DiskWatcher_Changed;
        watcher.Created -= DiskWatcher_Changed;
        watcher.Deleted -= DiskWatcher_Deleted;
        watcher.Renamed -= DiskWatcher_Renamed;
        watcher.Error -= DiskWatcher_Error;
        watcher.Dispose();
        _pendingDiskChanges.Clear();
    }

    private void DiskWatcher_Changed(object sender, FileSystemEventArgs e) => QueueDiskChange(e.FullPath, deleted: false);
    private void DiskWatcher_Deleted(object sender, FileSystemEventArgs e) => QueueDiskChange(e.FullPath, deleted: true);

    private void DiskWatcher_Renamed(object sender, RenamedEventArgs e)
    {
        QueueDiskChange(e.OldFullPath, deleted: true);
        QueueDiskChange(e.FullPath, deleted: false);
    }

    private void DiskWatcher_Error(object sender, ErrorEventArgs e)
    {
        var message = e.GetException().Message;
        Dispatcher.BeginInvoke(() => StatusMessage.Text = "Surveillance disque saturée ou interrompue : " + message);
    }

    private void QueueDiskChange(string path, bool deleted)
    {
        if (IsTemporaryPath(path)) return;
        var root = _diskWatcherRoot;
        if (root is null) return;
        try
        {
            if (!IsInsideWorkspace(path, root) || IsIgnoredWatcherPath(path, root)) return;
            if (!_documents.Any(document => !string.IsNullOrWhiteSpace(document.Path) &&
                    string.Equals(Path.GetFullPath(document.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) return;
        }
        catch (ArgumentException) { return; }
        _pendingDiskChanges.AddOrUpdate(Path.GetFullPath(path),
            _ => (DateTime.UtcNow, deleted), (_, _) => (DateTime.UtcNow, deleted));
        if (_pendingDiskChanges.Count > 512)
        {
            foreach (var stale in _pendingDiskChanges.OrderBy(entry => entry.Value.SeenAt).Take(_pendingDiskChanges.Count - 512))
                _pendingDiskChanges.TryRemove(stale.Key, out _);
        }
    }

    private static bool IsIgnoredWatcherPath(string path, string root)
    {
        var ignoredDirectories = new[] { ".git", ".vs", ".idea", "bin", "obj", "node_modules", ".cache", "build", "out", "dist", "coverage", ".next" };
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => ignoredDirectories.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsTemporaryPath(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        return name.StartsWith("~$", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".temp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".swp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".swo", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bak", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".download", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(".tmp.", StringComparison.OrdinalIgnoreCase);
    }
}
