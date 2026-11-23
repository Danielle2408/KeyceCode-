using KeyceCode.Services;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KeyceCode;

public partial class MainWindow
{
    private string? _activeTerminalProfile;
    private readonly WorkspaceSearchService _workspaceSearch = new();
    private CancellationTokenSource? _workspaceSearchCancellation;

    private async void SearchQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SearchWorkspaceAsync();
    }
    private async void SearchWorkspace_Click(object sender, RoutedEventArgs e) => await SearchWorkspaceAsync();

    private async Task SearchWorkspaceAsync()
    {
        SearchResults.Items.Clear();
        if (_workspace is null) { SearchStatus.Text = "Ouvrez un dossier pour rechercher dans ses fichiers."; return; }
        if (string.IsNullOrWhiteSpace(SearchQuery.Text)) { SearchStatus.Text = "Entrez un texte ou une expression régulière à rechercher."; return; }
        _workspaceSearchCancellation?.Cancel();
        _workspaceSearchCancellation?.Dispose();
        _workspaceSearchCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            SearchStatus.Text = "Recherche en cours…";
            var result = await _workspaceSearch.SearchAsync(_workspace, new WorkspaceSearchOptions(
                SearchQuery.Text,
                SearchFilesPattern.Text,
                SearchExcludePattern.Text,
                SearchUseRegex.IsChecked == true,
                SearchMatchCase.IsChecked == true,
                SearchWholeWord.IsChecked == true), _workspaceSearchCancellation.Token);
            foreach (var hit in result.Hits)
            {
                SearchResults.Items.Add(new ListBoxItem
                {
                    Content = $"{hit.RelativePath}:{hit.Line}:{hit.Column}  {hit.Text.Trim()}",
                    Tag = hit,
                    ToolTip = hit.Path
                });
            }
            SearchStatus.Text = $"{result.Hits.Count:N0} résultat(s) dans {result.FilesSearched:N0} fichier(s) · {result.FilesSkipped:N0} ignoré(s)" +
                (result.Truncated ? " · résultats limités; ajoutez un filtre pour réduire la recherche." : string.Empty);
            StatusMessage.Text = $"Recherche : {result.Hits.Count:N0} résultat(s)";
        }
        catch (RegexMatchTimeoutException) { SearchStatus.Text = "L’expression régulière a dépassé le délai d’une seconde; simplifiez-la."; }
        catch (ArgumentException ex) { SearchStatus.Text = "Expression incorrecte : " + ex.Message; }
        catch (OperationCanceledException) { SearchStatus.Text = "Recherche annulée."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { SearchStatus.Text = "La recherche a échoué : " + ex.Message; }
    }

    private async void PreviewReplace_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null) { SearchStatus.Text = "Ouvrez un dossier avant de remplacer dans les fichiers."; return; }
        if (string.IsNullOrWhiteSpace(SearchQuery.Text)) { SearchStatus.Text = "Entrez un texte ou une expression régulière à rechercher."; return; }
        try
        {
            foreach (var document in _documents.Where(document => document.IsDirty && !string.IsNullOrWhiteSpace(document.Path) && IsInsideWorkspace(document.Path, _workspace.RootPath)).ToArray())
                if (!await SaveDocumentAsync(document)) { SearchStatus.Text = "Remplacement annulé; les fichiers modifiés n’ont pas été prévisualisés."; return; }

            var options = new WorkspaceSearchOptions(SearchQuery.Text, SearchFilesPattern.Text, SearchExcludePattern.Text,
                SearchUseRegex.IsChecked == true, SearchMatchCase.IsChecked == true, SearchWholeWord.IsChecked == true);
            SearchStatus.Text = "Préparation de l’aperçu…";
            var preview = await _workspaceSearch.PreviewReplaceAsync(_workspace, options, SearchReplacement.Text);
            if (preview.TotalMatches == 0) { SearchStatus.Text = $"Aucun remplacement · {preview.FilesSkipped} fichier(s) ignoré(s)."; return; }
            var apply = WorkspaceReplacePreviewWindow.Show(this, preview);
            if (!apply) { SearchStatus.Text = "Aucun fichier modifié; aperçu annulé."; return; }
            if (preview.Truncated) { SearchStatus.Text = "Aucun changement appliqué : l’aperçu est tronqué. Ajoutez un filtre."; return; }

            var applied = await _workspaceSearch.ApplyPreviewAsync(_workspace, preview);
            var updatedDocuments = new List<object>();
            foreach (var document in _documents.Where(document => !string.IsNullOrWhiteSpace(document.Path)).ToArray())
            {
                var changed = preview.Files.Any(file => string.Equals(Path.GetFullPath(file.Path), Path.GetFullPath(document.Path!), StringComparison.OrdinalIgnoreCase));
                if (!changed) continue;
                var (text, encodingId) = await ReadTextFileAsync(document.Path!);
                document.Text = text;
                document.EncodingId = encodingId;
                document.IsDirty = false;
                document.LineEnding = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\n') ? "\n" : text.Contains('\r') ? "\r" : Environment.NewLine;
                updatedDocuments.Add(new { id = document.Id, text });
                if (_languageServer is { IsInitialized: true } && _lspDocumentUris.TryGetValue(document.Id, out var uri))
                {
                    try { await _languageServer.ChangeDocumentAsync(uri, text); }
                    catch (Exception ex) { OutputPanel.AppendText("[LSP] Synchronisation après remplacement : " + ex.Message + Environment.NewLine); }
                }
            }
            if (updatedDocuments.Count > 0)
                await PostEditorMessageAsync(new { type = "applyWorkspaceTexts", suppressChanges = true, documents = updatedDocuments });
            RefreshTabs();
            await RefreshExplorerAsync();
            SearchStatus.Text = $"{applied.MatchesReplaced:N0} remplacement(s) dans {applied.FilesChanged:N0} fichier(s).";
            StatusMessage.Text = "Remplacement terminé";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or DecoderFallbackException or RegexMatchTimeoutException)
        {
            SearchStatus.Text = "Aucun remplacement confirmé : " + ex.Message;
        }
    }

    private static bool GlobMatches(string path, string glob)
    {
        var pattern = "^" + Regex.Escape(glob.Replace('\\', '/'))
            .Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(path.Replace('\\', '/'), pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private async void SearchResults_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchResults.SelectedItem is not ListBoxItem { Tag: WorkspaceSearchHit match }) return;
        await OpenDocumentAsync(match.Path, pin: true);
        await PostEditorMessageAsync(new { type = "executeCommand", command = "editor.action.gotoLine", args = match.Line });
    }

    private async void ProblemsPanel_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProblemsPanel.SelectedItem is not LanguageProblem problem) return;
        var path = problem.FullPath;
        if (string.IsNullOrWhiteSpace(path) && _workspace is not null)
            path = Path.GetFullPath(Path.Combine(_workspace.RootPath, problem.File));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusMessage.Text = "Le fichier du problème n’est plus disponible.";
            return;
        }
        if (_workspace is not null && !IsInsideWorkspace(path, _workspace.RootPath))
        {
            StatusMessage.Text = "Ce problème pointe hors de l’espace de travail.";
            return;
        }
        await OpenDocumentAsync(path, pin: true);
        await PostEditorMessageAsync(new { type = "executeCommand", command = "editor.action.gotoLine", args = problem.Line });
    }

    private async void NewTerminal_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel("Terminal");
        var profile = _activeTerminalProfile ?? "PowerShell 7";
        await StartTerminalAsync(profile);
        PostTerminalMessage(new { type = "focus" });
    }

    private async void SelectTerminalProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = QuickPickWindow.Show(this, "Sélectionner le profil du terminal", new[] { "PowerShell 7", "Windows PowerShell", "Invite de commandes" });
        if (profile is not null) await CreateTerminalTabAsync(profile);
    }

    private async Task StartTerminalAsync(string profile) => await CreateTerminalTabAsync(profile);

    private async Task StopTerminalSessionAsync() => await StopAllTerminalTabsAsync();

    private async Task PumpTerminalOutputAsync(ConPtySession session)
    {
        try
        {
            await foreach (var output in session.Output.ReadAllAsync())
                await Dispatcher.InvokeAsync(() => QueueOrWriteTerminal(session, output));
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_terminalTabs.Values.Any(tab => ReferenceEquals(tab.Session, session)))
                    OutputPanel.AppendText("[ConPTY output] " + ex.Message + Environment.NewLine);
            });
        }
    }

    private async Task MonitorTerminalExitAsync(ConPtySession session, string profile)
    {
        try
        {
            var exitCode = await session.Completion;
            await session.Output.Completion;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_terminalTabs.Values.FirstOrDefault(tab => ReferenceEquals(tab.Session, session)) is { } tab)
                    QueueOrWriteTerminal(session, $"\r\n\u001b[90m[{profile} terminé · code {exitCode}]\u001b[0m\r\n");
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_terminalTabs.Values.Any(tab => ReferenceEquals(tab.Session, session))) OutputPanel.AppendText("[ConPTY] " + ex.Message + Environment.NewLine);
            });
        }
    }

    private void QueueOrWriteTerminal(ConPtySession session, string text)
    {
        var tab = _terminalTabs.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Session, session));
        if (tab is not null) WriteTerminalOutput(tab, text);
        if (!_buildOutputParsers.TryGetValue(session, out var parser)) return;
        foreach (var diagnostic in parser.Append(text))
        {
            var workingDirectory = _buildOutputWorkingDirectories.GetValueOrDefault(session) ?? _workspace?.RootPath ?? Environment.CurrentDirectory;
            string? fullPath = null;
            try
            {
                fullPath = Path.GetFullPath(diagnostic.File, workingDirectory);
                if (!Path.IsPathRooted(diagnostic.File) && !File.Exists(fullPath) && _workspace is not null)
                {
                    var workspaceCandidate = Path.GetFullPath(diagnostic.File, _workspace.RootPath);
                    if (File.Exists(workspaceCandidate)) fullPath = workspaceCandidate;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
            var displayPath = fullPath is not null && _workspace is not null && IsInsideWorkspace(fullPath, _workspace.RootPath)
                ? Path.GetRelativePath(_workspace.RootPath, fullPath)
                : diagnostic.File;
            _buildProblems.Add(new LanguageProblem(diagnostic.Severity, diagnostic.Message, displayPath,
                diagnostic.Line, diagnostic.Column, diagnostic.Code, fullPath));
        }
        if (_buildProblems.Count > 0) RefreshLanguageProblems();
    }

    private void AppendPendingTerminalOutput(string text)
    {
        _terminalPendingOutput.Append(text);
        const int maximumBufferedCharacters = 1_000_000;
        if (_terminalPendingOutput.Length > maximumBufferedCharacters)
            _terminalPendingOutput.Remove(0, _terminalPendingOutput.Length - maximumBufferedCharacters);
    }

    private async Task<bool> SendTerminalInputAsync(string text)
    {
        try
        {
            if (_terminalSession is null || _terminalSession.Completion.IsCompleted)
                await StartTerminalAsync(_activeTerminalProfile ?? "PowerShell 7");
            if (_terminalSession is null || _terminalSession.Completion.IsCompleted) return false;
            await _terminalSession.SendInputAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            OutputPanel.AppendText("[ConPTY input] " + ex.Message + Environment.NewLine);
            return false;
        }
    }

    private static string? FindExecutable(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var path in paths)
        {
            try { var candidate = Path.Combine(path, name); if (File.Exists(candidate)) return candidate; }
            catch (ArgumentException) { }
        }
        return null;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is null) { StatusMessage.Text = "Ouvrez un fichier ou projet avant de lancer."; return; }
        if (_activeDocument.IsDirty && !await SaveDocumentAsync(_activeDocument)) return;
        var path = _activeDocument.Path;
        if (string.IsNullOrWhiteSpace(path)) { StatusMessage.Text = "Enregistrez le fichier avant de lancer."; return; }
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var directory = Path.GetDirectoryName(path)!;
        string command;
        var isCmd = _activeTerminalProfile == "Invite de commandes";
        string QuotePath(string value) => isCmd ? QuoteCmd(value) : Quote(value);
        if (extension == ".csproj") command = $"dotnet run --project {QuotePath(path)}";
        else if (extension is ".sln" or ".slnx")
        {
            IReadOnlyList<string> projects;
            try { projects = DotNetSolutionService.GetProjects(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                StatusMessage.Text = "Impossible de lire la solution : " + ex.Message;
                return;
            }
            if (projects.Count == 0)
            {
                StatusMessage.Text = "Cette solution ne contient aucun projet C# utilisable.";
                return;
            }
            var projectLabels = projects.ToDictionary(project => Path.GetRelativePath(directory, project), project => project, StringComparer.OrdinalIgnoreCase);
            var selectedLabel = projects.Count == 1 ? null : QuickPickWindow.Show(this, "Choisir le projet de démarrage", projectLabels.Keys);
            var selectedProject = projects.Count == 1 ? projects[0] :
                selectedLabel is not null && projectLabels.TryGetValue(selectedLabel, out var choice) ? choice : null;
            if (selectedProject is null) return;
            command = $"dotnet run --project {QuotePath(selectedProject)}";
            directory = Path.GetDirectoryName(selectedProject)!;
        }
        else if (extension == ".py") command = $"python {QuotePath(path)}";
        else if (extension is ".js" or ".mjs") command = $"node {QuotePath(path)}";
        else if (extension == ".ps1") command = isCmd ? $"powershell.exe -ExecutionPolicy Bypass -File {QuoteCmd(path)}" : $"& {Quote(path)}";
        else if (extension is ".bat" or ".cmd") command = isCmd ? $"call {QuoteCmd(path)}" : $"& {Quote(path)}";
        else if (extension == ".cs")
        {
            var selection = SelectNearestProject(path, "Choisir le projet C# à exécuter");
            if (selection.HasCandidates)
            {
                if (selection.Project is null) return;
                command = $"dotnet run --project {QuotePath(selection.Project)}";
                directory = Path.GetDirectoryName(selection.Project)!;
            }
            else command = $"dotnet run --file {QuotePath(path)}";
        }
        else { StatusMessage.Text = $"Aucune commande Run configurée pour {extension}."; return; }

        await RunInIntegratedTerminalAsync(command, directory, $"Lancement : {Path.GetFileName(path)}");
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is null) { StatusMessage.Text = "Ouvrez un fichier ou projet avant de compiler."; return; }
        if (_activeDocument.IsDirty && !await SaveDocumentAsync(_activeDocument)) return;
        var path = _activeDocument.Path;
        if (string.IsNullOrWhiteSpace(path)) { StatusMessage.Text = "Enregistrez le fichier avant de compiler."; return; }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var isCmd = _activeTerminalProfile == "Invite de commandes";
        string QuotePath(string value) => isCmd ? QuoteCmd(value) : Quote(value);
        string command;
        var target = path;
        if (extension is ".sln" or ".slnx" or ".csproj") command = $"dotnet build {QuotePath(path)}";
        else if (extension == ".cs")
        {
            var selection = SelectNearestProject(path, "Choisir le projet C# à compiler");
            if (!selection.HasCandidates)
            {
                StatusMessage.Text = "Aucun projet C# parent. Utilisez Exécuter pour lancer ce fichier C# seul.";
                return;
            }
            if (selection.Project is null) return;
            target = selection.Project;
            command = $"dotnet build {QuotePath(target)}";
        }
        else if (extension == ".py") command = $"python -m py_compile {QuotePath(path)}";
        else if (extension is ".js" or ".mjs" or ".cjs") command = $"node --check {QuotePath(path)}";
        else
        {
            StatusMessage.Text = $"Aucune commande de compilation configurée pour {extension}.";
            return;
        }

        var buildTarget = target.Length == 0 ? path : target;
        var buildDirectory = Path.GetDirectoryName(buildTarget) ?? Path.GetDirectoryName(path)!;
        await RunInIntegratedTerminalAsync(command, buildDirectory,
            $"Compilation : {Path.GetFileName(buildTarget)}", captureBuildOutput: true);
    }

    private (string? Project, bool HasCandidates) SelectNearestProject(string sourcePath, string prompt)
    {
        var projects = DotNetProjectLocator.FindNearestProjects(sourcePath, _workspace?.RootPath);
        if (projects.Count == 0) return (null, false);
        if (projects.Count == 1) return (projects[0], true);
        var root = _workspace?.RootPath ?? Path.GetDirectoryName(sourcePath)!;
        var labels = projects.ToDictionary(project => Path.GetRelativePath(root, project), project => project, StringComparer.OrdinalIgnoreCase);
        var selected = QuickPickWindow.Show(this, prompt, labels.Keys);
        return (selected is not null && labels.TryGetValue(selected, out var projectPath) ? projectPath : null, true);
    }

    private async Task RunInIntegratedTerminalAsync(string command, string workingDirectory, string status,
        bool captureBuildOutput = false)
    {
        try
        {
            ShowPanel("Terminal");
            if (_terminalSession is null || _terminalSession.Completion.IsCompleted)
                await StartTerminalAsync(_activeTerminalProfile ?? "PowerShell 7");
            if (captureBuildOutput && _terminalSession is { } buildSession)
            {
                foreach (var parser in _buildOutputParsers.Values) parser.Clear();
                _buildProblems.Clear();
                _buildOutputWorkingDirectories[buildSession] = workingDirectory;
                RefreshLanguageProblems();
            }
            var isCmd = _activeTerminalProfile == "Invite de commandes";
            var line = isCmd
                ? $"cd /d {QuoteCmd(workingDirectory)} && {command}\r"
                : $"Set-Location -LiteralPath {Quote(workingDirectory)}; {command}\r";
            if (!await SendTerminalInputAsync(line))
            {
                StatusMessage.Text = "La commande n’a pas été envoyée au terminal. Vérifiez qu’un terminal est démarré.";
                return;
            }
            StatusMessage.Text = status;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusMessage.Text = "Impossible de lancer la commande dans le terminal : " + ex.Message;
        }
    }

    private void Debug_Click(object sender, RoutedEventArgs e)
    {
        ShowRun_Click(sender, e);
        if (_debugClient is not null) DebugContinue_Click(sender, e);
        else DebugStart_Click(sender, e);
    }

    private async void StopRun_Click(object sender, RoutedEventArgs e)
    {
        if (_debugClient is not null) { await StopDebugSessionAsync(terminateDebuggee: true); return; }
        try { if (_terminalSession is not null) await _terminalSession.SendCtrlCAsync(); }
        catch (Exception ex) { OutputPanel.AppendText(ex.Message + Environment.NewLine); }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    private static string QuoteCmd(string value) => "\"" + value.Replace("%", "^%", StringComparison.Ordinal) + "\"";

    private async Task RefreshGitAsync()
    {
        if (_workspace is null) { GitBranchLabel.Text = "Aucun dossier ouvert"; GitChanges.Items.Clear(); return; }
        try
        {
            var output = await RunGitAsync("status", "--porcelain=v1", "-z", "--branch");
            var records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            GitChanges.Items.Clear();
            for (var index = 0; index < records.Length; index++)
            {
                var line = records[index];
                if (line.StartsWith("## "))
                {
                    var branch = line[3..].Trim(); GitBranchLabel.Text = branch;
                    StatusGitBranch.Content = "⑂ " + branch.Split("...", StringSplitOptions.None)[0];
                    continue;
                }
                if (line.Length < 4) continue;
                var code = line.Length >= 2 ? line[..2] : "  ";
                var path = line[3..];
                var item = new ListBoxItem { Content = $"{code}  {Path.GetFileName(path)}", Tag = (Code: code, Path: path), ToolTip = path };
                var menu = new ContextMenu();
                var diffAction = MenuAction("Afficher le diff", () => ShowGitDiffAsync(path, code));
                if (!code.Contains('?') || _workspace is null || !Directory.Exists(Path.GetFullPath(Path.Combine(_workspace.RootPath, path))))
                    menu.Items.Add(diffAction);
                if (code.Contains('?') || code[1] != ' ')
                    menu.Items.Add(MenuAction("Stage", async () => { await RunGitAsync("add", "--", path); await RefreshGitAsync(); }));
                if (code[0] != ' ' && code[0] != '?')
                    menu.Items.Add(MenuAction("Unstage", async () => { await RunGitAsync("restore", "--staged", "--", path); await RefreshGitAsync(); }));
                if (code.Contains('?') && _workspace is not null && File.Exists(Path.GetFullPath(Path.Combine(_workspace.RootPath, path))))
                    menu.Items.Add(MenuAction("Supprimer le fichier non suivi…", async () =>
                    {
                        if (MessageBox.Show(this, $"Supprimer définitivement le fichier non suivi {path} ?", "Git", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                        var fullPath = Path.GetFullPath(Path.Combine(_workspace!.RootPath, path));
                        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_workspace.RootPath)) + Path.DirectorySeparatorChar;
                        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Le chemin sort de l’espace de travail.");
                        File.Delete(fullPath);
                        await RefreshGitAsync();
                    }));
                else if (code[1] != ' ')
                    menu.Items.Add(MenuAction("Abandonner les changements…", async () =>
                {
                    if (MessageBox.Show(this, $"Abandonner les modifications de {path} ?", "Git", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        await RunGitAsync("restore", "--worktree", "--", path); await RefreshGitAsync();
                    }
                }));
                item.ContextMenu = menu;
                GitChanges.Items.Add(item);
                if ((code[0] is 'R' or 'C') && index + 1 < records.Length) index++;
            }
            StatusMessage.Text = $"Git : {GitChanges.Items.Count} fichier(s) modifié(s)";
        }
        catch (InvalidOperationException) { GitBranchLabel.Text = "Git non détecté"; StatusGitBranch.Content = "⑂ —"; GitChanges.Items.Clear(); }
        catch (Exception ex) { GitBranchLabel.Text = "Erreur Git"; OutputPanel.AppendText(ex.Message + Environment.NewLine); }
    }

    private MenuItem MenuAction(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex)
            {
                OutputPanel.AppendText("[Action] " + ex.Message + Environment.NewLine);
                StatusMessage.Text = "Action impossible · consultez Sortie";
            }
        };
        return item;
    }

    private async Task<string> RunGitAsync(params string[] arguments)
    {
        if (_workspace is null) throw new InvalidOperationException("Aucun dossier ouvert.");
        var info = new ProcessStartInfo("git") { WorkingDirectory = _workspace.RootPath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Git n’a pas pu démarrer.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        return output;
    }

    private async void RefreshGit_Click(object sender, RoutedEventArgs e) => await RefreshGitAsync();

    private async void AssociateGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null) { StatusMessage.Text = "Ouvre un dossier avant d’associer un dépôt GitHub."; return; }
        var entered = QuickPickWindow.Prompt(this, "Associer un dépôt GitHub", "URL HTTPS ou SSH du dépôt", "https://github.com/");
        if (string.IsNullOrWhiteSpace(entered)) return;
        if (!TryParseGitHubRemote(entered, out var remoteUrl, out var webUrl))
        {
            MessageBox.Show(this, "Entre une adresse de dépôt github.com valide, par exemple https://github.com/compte/projet.git ou git@github.com:compte/projet.git.",
                "Adresse GitHub invalide", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            string? existing = null;
            try { existing = (await RunGitAsync("remote", "get-url", "origin")).Trim(); }
            catch (InvalidOperationException) { }
            if (!string.IsNullOrWhiteSpace(existing))
            {
                if (string.Equals(existing, remoteUrl, StringComparison.OrdinalIgnoreCase))
                {
                    StatusMessage.Text = "Ce workspace suit déjà ce dépôt GitHub via origin.";
                    return;
                }
                var replace = MessageBox.Show(this, $"Le remote origin pointe actuellement vers :\n{existing}\n\nLe remplacer par :\n{remoteUrl} ?",
                    "Remplacer origin", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (replace != MessageBoxResult.Yes) return;
                await RunGitAsync("remote", "set-url", "origin", remoteUrl);
            }
            else await RunGitAsync("remote", "add", "origin", remoteUrl);

            try
            {
                var branch = (await RunGitAsync("branch", "--show-current")).Trim();
                if (!string.IsNullOrWhiteSpace(branch))
                    await RunGitAsync("push", "--set-upstream", "origin", branch);
            }
            catch (Exception ex)
            {
                OutputPanel.AppendText("[GitHub] Remote associé, mais premier Push non terminé : " + ex.Message + Environment.NewLine);
                Process.Start(new ProcessStartInfo(webUrl.AbsoluteUri) { UseShellExecute = true });
                StatusMessage.Text = "Remote GitHub associé. Connecte-toi dans le navigateur, puis relance Push.";
                await RefreshGitAsync();
                return;
            }
            Process.Start(new ProcessStartInfo(webUrl.AbsoluteUri) { UseShellExecute = true });
            await RefreshGitAsync();
            StatusMessage.Text = "Dépôt GitHub associé et branche publiée : " + webUrl.Host + webUrl.AbsolutePath;
        }
        catch (Exception ex)
        {
            OutputPanel.AppendText("[GitHub] Association impossible : " + ex.Message + Environment.NewLine);
            StatusMessage.Text = "Association GitHub impossible · consulte Sortie";
        }
    }

    private async void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null) return;
        try
        {
            var remote = (await RunGitAsync("remote", "get-url", "origin")).Trim();
            if (!TryParseGitHubRemote(remote, out _, out var webUrl))
            {
                StatusMessage.Text = "Le remote origin n’est pas un dépôt GitHub.";
                return;
            }
            Process.Start(new ProcessStartInfo(webUrl.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusMessage.Text = "Aucun dépôt GitHub associé · " + ex.Message; }
    }

    private static bool TryParseGitHubRemote(string value, out string remoteUrl, out Uri webUrl)
    {
        remoteUrl = string.Empty;
        webUrl = new Uri("https://github.com/");
        var text = value.Trim();
        if (text.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            var path = text["git@github.com:".Length..].TrimEnd('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
            if (!Regex.IsMatch(path, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) return false;
            remoteUrl = $"git@github.com:{path}.git";
            webUrl = new Uri("https://github.com/" + path);
            return true;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var repositoryPath = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        if (repositoryPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repositoryPath = repositoryPath[..^4];
        if (!Regex.IsMatch(repositoryPath, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) return false;
        webUrl = new Uri("https://github.com/" + repositoryPath);
        remoteUrl = webUrl.AbsoluteUri.TrimEnd('/') + ".git";
        return true;
    }

    private async void GitBranch_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null) { StatusMessage.Text = "Ouvrez un dépôt Git."; return; }
        try
        {
            var branches = (await RunGitAsync("branch", "--format=%(refname:short)"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (branches.Length == 0) { StatusMessage.Text = "Aucune branche locale à sélectionner."; return; }
            var selected = QuickPickWindow.Show(this, "Basculer vers une branche locale", branches);
            if (string.IsNullOrWhiteSpace(selected)) return;
            await RunGitAsync("switch", "--", selected);
            await RefreshGitAsync();
            StatusMessage.Text = "Branche active : " + selected;
        }
        catch (Exception ex) { OutputPanel.AppendText("[Git] Changement de branche : " + ex.Message + Environment.NewLine); StatusMessage.Text = "Changement de branche impossible"; }
    }

    private async void GitPull_Click(object sender, RoutedEventArgs e) => await SynchronizeGitAsync(pull: true);
    private async void GitPush_Click(object sender, RoutedEventArgs e) => await SynchronizeGitAsync(pull: false);

    private async Task SynchronizeGitAsync(bool pull)
    {
        if (_workspace is null) { StatusMessage.Text = "Ouvrez un dépôt Git."; return; }
        try
        {
            var upstream = (await RunGitAsync("rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}")).Trim();
            if (upstream.Length == 0) throw new InvalidOperationException("Aucune branche distante suivie. Configurez un remote et un upstream dans Git avant de synchroniser.");
            if (pull)
            {
                var changes = await RunGitAsync("status", "--porcelain=v1");
                if (!string.IsNullOrWhiteSpace(changes))
                    throw new InvalidOperationException("Le workspace contient des changements. Enregistrez-les ou mettez-les de côté avant Pull.");
            }
            StatusMessage.Text = pull ? $"Pull depuis {upstream}…" : $"Push vers {upstream}…";
            await RunGitAsync(pull ? "pull" : "push");
            await RefreshGitAsync();
            StatusMessage.Text = pull ? $"Pull terminé depuis {upstream}" : $"Push terminé vers {upstream}";
        }
        catch (Exception ex)
        {
            OutputPanel.AppendText("[Git] " + ex.Message + Environment.NewLine);
            StatusMessage.Text = pull ? "Pull impossible · consultez Sortie" : "Push impossible · consultez Sortie";
        }
    }

    private async void GitChanges_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GitChanges.SelectedItem is not ListBoxItem { Tag: ValueTuple<string, string> entry }) return;
        await ShowGitDiffAsync(entry.Item2, entry.Item1);
    }

    private async Task ShowGitDiffAsync(string path, string statusCode)
    {
        try
        {
            if (statusCode.Contains('?') && _workspace is not null && Directory.Exists(Path.GetFullPath(Path.Combine(_workspace.RootPath, path))))
            {
                ShowPanel("Output");
                OutputPanel.Text = $"{path} est un dossier non suivi. Ouvrez un fichier individuel pour consulter son diff, ou utilisez Stage pour l’ajouter au suivi Git.";
                return;
            }
            var pieces = new List<string>();
            if (statusCode[0] != ' ' && statusCode[0] != '?')
            {
                var staged = await RunGitAsync("diff", "--cached", "--no-ext-diff", "--no-color", "--", path);
                if (!string.IsNullOrWhiteSpace(staged)) pieces.Add(staged);
            }
            if (statusCode[1] != ' ' && statusCode[1] != '?')
            {
                var unstaged = await RunGitAsync("diff", "--no-ext-diff", "--no-color", "--", path);
                if (!string.IsNullOrWhiteSpace(unstaged)) pieces.Add(unstaged);
            }
            if (statusCode.Contains('?'))
            {
                var untracked = await RunGitAllowExitCodeAsync("diff", "--no-index", "--no-ext-diff", "--no-color", "--", "/dev/null", path);
                if (!string.IsNullOrWhiteSpace(untracked)) pieces.Add(untracked);
            }
            ShowPanel("Output");
            OutputPanel.Text = pieces.Count == 0 ? $"Aucun diff disponible pour {path}." : string.Join(Environment.NewLine, pieces);
            StatusMessage.Text = "Diff Git : " + path;
        }
        catch (Exception ex)
        {
            OutputPanel.Text = "Impossible de lire le diff Git : " + ex.Message;
            ShowPanel("Output");
            StatusMessage.Text = "Diff Git indisponible";
        }
    }

    private async Task<string> RunGitAllowExitCodeAsync(params string[] arguments)
    {
        if (_workspace is null) throw new InvalidOperationException("Aucun dossier ouvert.");
        var info = new ProcessStartInfo("git") { WorkingDirectory = _workspace.RootPath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Git n’a pas pu démarrer.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode is not (0 or 1)) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        return output;
    }

    private async void Commit_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || string.IsNullOrWhiteSpace(CommitMessage.Text)) { StatusMessage.Text = "Ouvrez un dépôt Git et entrez un message de commit."; return; }
        try
        {
            await RunGitAsync("commit", "-m", CommitMessage.Text.Trim());
            CommitMessage.Clear(); await RefreshGitAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        var prompt = PromptBox.Text.Trim();
        if (prompt.Length == 0) return;
        PromptBox.Tag = null;
        if (!string.IsNullOrWhiteSpace(_selectedText)) prompt += "\n\nSélection explicitement partagée :\n" + _selectedText;
        AssistantOutput.Text = "KeyceIA réfléchit…";
        AiProposalPreview.Visibility = Visibility.Collapsed;
        try { AssistantOutput.Text = await _gemini.AskAsync(prompt); }
        catch (Exception ex) { AssistantOutput.Text = ex.Message; }
    }

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        await CompleteAtCursorAsync();
    }

    private async void EditFileWithAi_Click(object sender, RoutedEventArgs e)
    {
        var document = _activeDocument;
        var instruction = PromptBox.Text.Trim();
        if (document is null) { AssistantOutput.Text = "Ouvre un fichier avant de demander une modification."; return; }
        if (instruction.Length == 0) { AssistantOutput.Text = "Écris d’abord la modification souhaitée."; return; }
        PromptBox.Tag = null;
        AiProposalPreview.Visibility = Visibility.Collapsed;
        try
        {
            _assistantVisible = true;
            SecondarySidebarColumn.Width = new GridLength(330);
            AssistantOutput.Text = "KeyceIA prépare une version complète du fichier pour relecture…";
            var edited = await _gemini.EditDocumentAsync(instruction, document.Text, document.Language);
            if (!ReferenceEquals(document, _activeDocument)) { AssistantOutput.Text = "Proposition annulée : le fichier actif a changé."; return; }
            PromptBox.Tag = new AiEditProposal(document, document.Text, 0, edited, "document");
            AiProposalPreview.Text = edited;
            AiProposalPreview.Visibility = Visibility.Visible;
            AssistantOutput.Text = "Document modifié par KeyceIA en attente de validation. Relis la consigne et clique sur Appliquer au fichier.";
        }
        catch (Exception ex) { AssistantOutput.Text = ex.Message; }
    }

    private async Task CompleteAtCursorAsync()
    {
        var document = _activeDocument;
        if (document is null) { StatusMessage.Text = "Ouvrez un fichier avant de demander une complétion."; return; }
        PromptBox.Tag = null;
        AiProposalPreview.Visibility = Visibility.Collapsed;
        try
        {
            var offset = Math.Clamp(_cursorOffset, 0, document.Text.Length);
            var prefix = document.Text[..offset];
            if (prefix.Length > 12000) prefix = prefix[^12000..];
            if (prefix.Trim().Length == 0) { StatusMessage.Text = "Placez le curseur après du code à compléter."; return; }
            _assistantVisible = true;
            SecondarySidebarColumn.Width = new GridLength(330);
            AssistantOutput.Text = "KeyceIA prépare une suggestion au curseur…";
            var completion = await _gemini.CompleteAsync(prefix, document.Language);
            if (!ReferenceEquals(document, _activeDocument)) { AssistantOutput.Text = "Suggestion annulée : le fichier actif a changé."; return; }
            PromptBox.Tag = new AiEditProposal(document, document.Text, _cursorOffset, completion, "completion");
            AiProposalPreview.Text = completion;
            AiProposalPreview.Visibility = Visibility.Visible;
            AssistantOutput.Text = "Suggestion prête. Elle n’a pas encore été insérée.";
        }
        catch (Exception ex) { AssistantOutput.Text = ex.Message; }
    }

    private sealed record AiEditProposal(EditorDocument Document, string OriginalText, int Offset, string ProposedText, string Kind);

    private async void ApplyAiEdit_Click(object sender, RoutedEventArgs e)
    {
        if (PromptBox.Tag is not AiEditProposal proposal || !ReferenceEquals(proposal.Document, _activeDocument))
        {
            AssistantOutput.Text = "Aucune proposition applicable au fichier actif.";
            return;
        }
        if (!string.Equals(proposal.OriginalText, proposal.Document.Text, StringComparison.Ordinal))
        {
            AssistantOutput.Text = "Le fichier a changé depuis la proposition. Demande une nouvelle suggestion pour éviter d’écraser ton travail.";
            PromptBox.Tag = null;
            return;
        }
        var result = MessageBox.Show(this, $"Appliquer la modification proposée à {proposal.Document.Name} ?\n\nElle sera ajoutée comme une modification non enregistrée, annulable avec Ctrl+Z.",
            "Appliquer la proposition KeyceIA", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        if (!await ApplyAiEditToEditorAsync(proposal.Document, proposal))
        {
            AssistantOutput.Text = "Aucune modification appliquée. Le fichier a peut-être changé pendant la génération ; vérifie le message dans Sortie et demande une nouvelle proposition.";
            return;
        }
        PromptBox.Tag = null;
        AssistantOutput.Text = "Modification appliquée au fichier. Ctrl+Z permet de l’annuler ; Ctrl+S l’enregistre.";
    }

    private void RejectAiEdit_Click(object sender, RoutedEventArgs e)
    {
        PromptBox.Tag = null;
        AiProposalPreview.Visibility = Visibility.Collapsed;
        PromptBox.Clear();
        AssistantOutput.Text = "Proposition rejetée ; le fichier reste inchangé.";
    }
}
