using KeyceCode.Services;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KeyceCode;

public partial class MainWindow
{
    private string? _activeTerminalProfile;

    private async void SearchQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SearchWorkspaceAsync();
    }
    private async void SearchWorkspace_Click(object sender, RoutedEventArgs e) => await SearchWorkspaceAsync();

    private async Task SearchWorkspaceAsync()
    {
        SearchResults.Items.Clear();
        if (_workspace is null || string.IsNullOrWhiteSpace(SearchQuery.Text)) return;
        try
        {
            var regex = new Regex(SearchQuery.Text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var files = await CollectWorkspaceFilesAsync(_workspace.RootPath);
            var matches = new List<SearchResult>();
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(_workspace.RootPath, file);
                if (!string.IsNullOrWhiteSpace(SearchFilesPattern.Text) && !GlobMatches(relative, SearchFilesPattern.Text.Trim())) continue;
                try
                {
                    if (new FileInfo(file).Length > 2_000_000) continue;
                    var lines = await File.ReadAllLinesAsync(file);
                    for (var i = 0; i < lines.Length; i++)
                        if (regex.IsMatch(lines[i]))
                        {
                            matches.Add(new SearchResult(file, i + 1, lines[i].Trim()));
                            if (matches.Count == 1000) break;
                        }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (matches.Count == 1000) break;
            }
            foreach (var match in matches)
                SearchResults.Items.Add(new ListBoxItem { Content = $"{Path.GetFileName(match.Path)}:{match.Line}  {match.Text}", Tag = match, ToolTip = match.Path });
            StatusMessage.Text = $"{matches.Count} résultat(s)";
        }
        catch (ArgumentException ex) { StatusMessage.Text = "Expression incorrecte : " + ex.Message; }
        catch (RegexMatchTimeoutException) { StatusMessage.Text = "La recherche a dépassé son délai."; }
    }

    private static bool GlobMatches(string path, string glob)
    {
        var pattern = "^" + Regex.Escape(glob.Replace('\\', '/'))
            .Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(path.Replace('\\', '/'), pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private async void SearchResults_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchResults.SelectedItem is not ListBoxItem { Tag: SearchResult match }) return;
        await OpenDocumentAsync(match.Path, pin: true);
        await PostEditorMessageAsync(new { type = "executeCommand", command = "editor.action.gotoLine", args = match.Line });
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
    }

    private void AppendPendingTerminalOutput(string text)
    {
        _terminalPendingOutput.Append(text);
        const int maximumBufferedCharacters = 1_000_000;
        if (_terminalPendingOutput.Length > maximumBufferedCharacters)
            _terminalPendingOutput.Remove(0, _terminalPendingOutput.Length - maximumBufferedCharacters);
    }

    private async Task SendTerminalInputAsync(string text)
    {
        try
        {
            if (_terminalSession is null || _terminalSession.Completion.IsCompleted)
                await StartTerminalAsync(_activeTerminalProfile ?? "PowerShell 7");
            if (_terminalSession is not null) await _terminalSession.SendInputAsync(text);
        }
        catch (Exception ex)
        {
            OutputPanel.AppendText("[ConPTY input] " + ex.Message + Environment.NewLine);
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
        else if (extension == ".sln" || extension == ".slnx") command = $"dotnet build {QuotePath(path)}";
        else if (extension == ".py") command = $"python {QuotePath(path)}";
        else if (extension is ".js" or ".mjs") command = $"node {QuotePath(path)}";
        else if (extension == ".ps1") command = isCmd ? $"powershell.exe -ExecutionPolicy Bypass -File {QuoteCmd(path)}" : $"& {Quote(path)}";
        else if (extension is ".bat" or ".cmd") command = isCmd ? $"call {QuoteCmd(path)}" : $"& {Quote(path)}";
        else if (extension == ".cs" && _workspace is not null)
        {
            var project = Directory.EnumerateFiles(_workspace.RootPath, "*.csproj", SearchOption.AllDirectories)
                .FirstOrDefault(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
            command = project is null
                ? isCmd ? "echo Aucun fichier projet .csproj trouve." : "Write-Error 'Aucun fichier projet .csproj trouvé.'"
                : $"dotnet run --project {QuotePath(project)}";
        }
        else { StatusMessage.Text = $"Aucune commande Run configurée pour {extension}."; return; }

        ShowPanel("Terminal");
        if (_terminalSession is null || _terminalSession.Completion.IsCompleted)
            await StartTerminalAsync(_activeTerminalProfile ?? "PowerShell 7");
        var line = isCmd ? $"cd /d {QuoteCmd(directory)} && {command}\r" : $"Set-Location -LiteralPath {Quote(directory)}; {command}\r";
        await SendTerminalInputAsync(line);
        StatusMessage.Text = $"Lancement : {Path.GetFileName(path)}";
    }

    private void Debug_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel("DebugConsole");
        DebugConsolePanel.Text = "Aucun adaptateur de débogage DAP n’est configuré. Le lancement F5 est indisponible tant qu’un adaptateur de langage n’est pas installé.";
        StatusMessage.Text = "Adaptateur DAP manquant";
    }

    private async void StopRun_Click(object sender, RoutedEventArgs e)
    {
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
                OutputPanel.AppendText("[Git] " + ex.Message + Environment.NewLine);
                StatusMessage.Text = "Action Git impossible · consultez Sortie";
            }
        };
        return item;
    }

    private async Task<string> RunGitAsync(params string[] arguments)
    {
        if (_workspace is null) throw new InvalidOperationException("Aucun dossier ouvert.");
        var info = new ProcessStartInfo("git") { WorkingDirectory = _workspace.RootPath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "Never";
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Git n’a pas pu démarrer.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        return output;
    }

    private async void RefreshGit_Click(object sender, RoutedEventArgs e) => await RefreshGitAsync();

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
        if (!string.IsNullOrWhiteSpace(_selectedText)) prompt += "\n\nSélection explicitement partagée :\n" + _selectedText;
        AssistantOutput.Text = "KeyceIA réfléchit…";
        try { AssistantOutput.Text = await _gemini.AskAsync(prompt); }
        catch (Exception ex) { AssistantOutput.Text = ex.Message; }
    }

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        await CompleteAtCursorAsync();
    }

    private async Task CompleteAtCursorAsync()
    {
        var document = _activeDocument;
        if (document is null) { StatusMessage.Text = "Ouvrez un fichier avant de demander une complétion."; return; }
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
            await PostEditorMessageAsync(new { type = "insertTextAtCursor", text = completion });
            AssistantOutput.Text = "Suggestion insérée. Ctrl+Z permet de l’annuler.";
        }
        catch (Exception ex) { AssistantOutput.Text = ex.Message; }
    }
}
