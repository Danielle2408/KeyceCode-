using System.IO;
using System.Text.Json;
using KeyceCode.Services.Lsp;

namespace KeyceCode;

public partial class MainWindow
{
    private readonly Dictionary<string, CancellationTokenSource> _lspRefactorRequests = new(StringComparer.Ordinal);

    private async Task HandleLspRenameRequestAsync(JsonElement message)
    {
        var requestId = ReadString(message, "requestId");
        if (requestId is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _lspRefactorRequests[requestId] = timeout;
        try
        {
            var document = FindDocument(message);
            var server = _languageServer;
            if (document is null || server is not { IsInitialized: true } || _workspace is null ||
                !_lspDocumentUris.TryGetValue(document.Id, out var uri) ||
                !IsInsideWorkspace(document.Path, _workspace.RootPath) || !HasLspCapability(server.ServerCapabilities, "renameProvider"))
            {
                await PostEditorMessageAsync(new { type = "lspRenameResult", requestId, success = false });
                return;
            }

            var newName = ReadString(message, "newName");
            if (string.IsNullOrWhiteSpace(newName) || newName.Length > 512 || newName.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new InvalidDataException("Le nouveau nom de symbole est invalide.");
            var text = SynchronizeRefactorDocument(document, message);
            if (text is null) throw new InvalidDataException("Le contenu de l’éditeur dépasse la limite de renommage LSP.");
            await server.ChangeDocumentAsync(uri, document.Text, timeout.Token);
            var result = await server.RequestAsync("textDocument/rename", new
            {
                textDocument = new { uri = uri.AbsoluteUri },
                position = new { line = Math.Max(0, ReadInt(message, "line", 1) - 1), character = Math.Max(0, ReadInt(message, "column", 1) - 1) },
                newName
            }, timeout.Token);
            if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                StatusMessage.Text = "Le serveur LSP ne peut pas renommer ce symbole à cet emplacement.";
                await PostEditorMessageAsync(new { type = "lspRenameResult", requestId, success = false });
                return;
            }
            await ApplyLspWorkspaceEditAsync(result, timeout.Token);
            await PostEditorMessageAsync(new { type = "lspRenameResult", requestId, success = true });
            StatusMessage.Text = "Renommage LSP appliqué aux fichiers concernés";
        }
        catch (OperationCanceledException)
        {
            await PostEditorMessageAsync(new { type = "lspRenameResult", requestId, success = false });
        }
        catch (Exception ex)
        {
            StatusMessage.Text = "Renommage indisponible";
            OutputPanel.AppendText("[LSP] Rename : " + ex.Message + Environment.NewLine);
            await PostEditorMessageAsync(new { type = "lspRenameResult", requestId, success = false, error = ex.Message });
        }
        finally
        {
            if (_lspRefactorRequests.TryGetValue(requestId, out var active) && ReferenceEquals(active, timeout))
                _lspRefactorRequests.Remove(requestId);
        }
    }

    private void CancelLspRefactorRequest(string? requestId)
    {
        if (requestId is not null && _lspRefactorRequests.TryGetValue(requestId, out var cancellation))
            cancellation.Cancel();
    }

    private async Task HandleLspCodeActionsAsync(JsonElement message)
    {
        var document = FindDocument(message);
        var server = _languageServer;
        if (document is null || server is not { IsInitialized: true } || _workspace is null ||
            !_lspDocumentUris.TryGetValue(document.Id, out var uri) || !IsInsideWorkspace(document.Path, _workspace.RootPath))
        {
            StatusMessage.Text = "Code Actions requièrent un document ouvert et un serveur LSP configuré.";
            return;
        }

        try
        {
            if (!HasLspCapability(server.ServerCapabilities, "codeActionProvider"))
            {
                StatusMessage.Text = "Le serveur LSP n’annonce pas la prise en charge des Code Actions.";
                return;
            }
            var text = SynchronizeRefactorDocument(document, message);
            if (text is null) throw new InvalidDataException("Le contenu de l’éditeur dépasse la limite de Code Actions LSP.");
            await server.ChangeDocumentAsync(uri, document.Text);
            var line = Math.Max(0, ReadInt(message, "startLine", 1) - 1);
            var character = Math.Max(0, ReadInt(message, "startColumn", 1) - 1);
            var endLine = Math.Max(line, ReadInt(message, "endLine", line + 1) - 1);
            var endCharacter = Math.Max(character, ReadInt(message, "endColumn", character + 1) - 1);
            var diagnostics = _lspDiagnostics.TryGetValue(Path.GetFullPath(document.Path), out var published)
                ? published.Items.Select(item => item.Raw).ToArray()
                : Array.Empty<JsonElement>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = await server.RequestAsync("textDocument/codeAction", new
                {
                    textDocument = new { uri = uri.AbsoluteUri },
                    range = new
                    {
                        start = new { line, character },
                        end = new { line = endLine, character = endCharacter }
                    },
                    context = new { diagnostics }
                }, timeout.Token);

            if (response.ValueKind != JsonValueKind.Array)
            {
                StatusMessage.Text = "Le serveur n’a proposé aucune Code Action.";
                return;
            }
            var actions = response.EnumerateArray()
                .Where(action => action.ValueKind == JsonValueKind.Object &&
                    action.TryGetProperty("edit", out var edit) && edit.ValueKind == JsonValueKind.Object &&
                    (!action.TryGetProperty("disabled", out var disabled) || disabled.ValueKind == JsonValueKind.Null) &&
                    action.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                .Select(action => new { Json = action.Clone(), Title = action.GetProperty("title").GetString()! })
                .Take(100).ToArray();
            if (actions.Length == 0)
            {
                StatusMessage.Text = "Aucune Code Action avec édition prise en charge. Les commandes seules restent désactivées.";
                return;
            }
            var labels = actions.Select((action, index) => $"{index + 1}. {action.Title}").ToArray();
            var selected = QuickPickWindow.Show(this, "Actions rapides (LSP)", labels);
            if (selected is null) return;
            var selectedIndex = Array.IndexOf(labels, selected);
            if (selectedIndex < 0) return;
            await ApplyLspWorkspaceEditAsync(actions[selectedIndex].Json.GetProperty("edit"));
            StatusMessage.Text = $"Code Action appliquée : {actions[selectedIndex].Title}";
        }
        catch (Exception ex)
        {
            StatusMessage.Text = "Code Actions indisponibles";
            OutputPanel.AppendText("[LSP] Code Action : " + ex.Message + Environment.NewLine);
        }
    }

    private string? SynchronizeRefactorDocument(EditorDocument document, JsonElement message)
    {
        var text = ReadString(message, "text");
        if (text is null) return document.Text;
        const int maximumCharacters = 8_000_000;
        if (text.Length > maximumCharacters) return null;
        if (!string.Equals(document.Text, text, StringComparison.Ordinal))
        {
            document.Text = text;
            document.IsDirty = true;
            CancelInlineRequestsForDocument(document.Id);
            RefreshTabs();
            UpdateFileStatus(document);
        }
        return document.Text;
    }

    private async Task ApplyLspWorkspaceEditAsync(JsonElement workspaceEdit, CancellationToken cancellationToken = default)
    {
        if (_workspace is null) throw new InvalidOperationException("Aucun workspace ouvert.");
        var changes = LspWorkspaceEditService.Parse(workspaceEdit, _workspace.RootPath);
        if (changes.Count == 0) throw new InvalidDataException("Le serveur n’a proposé aucune édition de texte.");

        var prepared = new List<(string Path, EditorDocument? OpenDocument, string Text, string EncodingId)>();
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var openDocument = _documents.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.Path) &&
                string.Equals(Path.GetFullPath(item.Path), change.Path, StringComparison.OrdinalIgnoreCase));
            var source = openDocument?.Text;
            var encoding = openDocument?.EncodingId ?? "utf-8";
            if (source is null)
            {
                (source, encoding) = await ReadTextFileAsync(change.Path);
            }
            var edited = LspWorkspaceEditService.ApplyTextEdits(source, change.Edits);
            prepared.Add((change.Path, openDocument, edited, encoding));
        }

        await CommitClosedWorkspaceEditsAsync(prepared.Where(item => item.OpenDocument is null).ToArray(), cancellationToken);

        var openChanges = new List<object>();
        foreach (var item in prepared.Where(item => item.OpenDocument is not null))
        {
            var document = item.OpenDocument!;
            document.Text = item.Text;
            document.IsDirty = true;
            if (_languageServer is { IsInitialized: true } && _lspDocumentUris.TryGetValue(document.Id, out var uri))
            {
                try { await _languageServer.ChangeDocumentAsync(uri, document.Text); }
                catch (Exception ex) { OutputPanel.AppendText("[LSP] Synchronisation après édition : " + ex.Message + Environment.NewLine); }
            }
            openChanges.Add(new { id = document.Id, text = document.Text });
        }
        RefreshTabs();
        if (openChanges.Count > 0)
            await PostEditorMessageAsync(new { type = "applyWorkspaceTexts", documents = openChanges });
    }

    private async Task CommitClosedWorkspaceEditsAsync(
        IReadOnlyList<(string Path, EditorDocument? OpenDocument, string Text, string EncodingId)> closedEdits,
        CancellationToken cancellationToken)
    {
        if (_workspace is null || closedEdits.Count == 0) return;
        var staged = new List<(string Path, string TemporaryPath, string BackupPath)>();
        var committed = new List<(string Path, string BackupPath)>();
        var preserveBackups = false;
        try
        {
            foreach (var edit in closedEdits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = _workspace.ResolvePath(edit.Path);
                var suffix = ".keycecode-" + Guid.NewGuid().ToString("N");
                var temporaryPath = path + suffix + ".tmp";
                var backupPath = path + suffix + ".bak";
                staged.Add((path, temporaryPath, backupPath));
                await File.WriteAllTextAsync(temporaryPath, edit.Text, GetTextEncoding(edit.EncodingId), cancellationToken);
            }

            foreach (var item in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = _workspace.ResolvePath(item.Path);
                File.Replace(item.TemporaryPath, item.Path, item.BackupPath, ignoreMetadataErrors: true);
                committed.Add((item.Path, item.BackupPath));
            }
        }
        catch (Exception failure)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var item in committed.AsEnumerable().Reverse())
            {
                try
                {
                    _ = _workspace.ResolvePath(item.Path);
                    File.Move(item.BackupPath, item.Path, overwrite: true);
                }
                catch (Exception rollbackFailure) { rollbackFailures.Add(rollbackFailure); }
            }
            if (rollbackFailures.Count > 0)
            {
                var retainedBackups = committed.Select(item => item.BackupPath).Where(File.Exists).ToArray();
                preserveBackups = true;
                throw new IOException("L’application du WorkspaceEdit a échoué et le rollback de certains fichiers a aussi échoué. " +
                    "Récupération manuelle depuis : " + string.Join("; ", retainedBackups),
                    new AggregateException([failure, .. rollbackFailures]));
            }
            throw;
        }
        finally
        {
            foreach (var item in staged)
            {
                try { File.Delete(item.TemporaryPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (!preserveBackups)
                    try { File.Delete(item.BackupPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
