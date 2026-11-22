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
    private Process? _terminalProcess;

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
        var profile = TerminalSessions.SelectedItem?.ToString() ?? "PowerShell";
        await StartTerminalAsync(profile);
        TerminalInput.Focus();
    }

    private void SelectTerminalProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = QuickPickWindow.Show(this, "Sélectionner le profil du terminal", new[] { "PowerShell 7", "Windows PowerShell", "Invite de commandes" });
        if (profile is not null)
        {
            if (!TerminalSessions.Items.Contains(profile)) TerminalSessions.Items.Add(profile);
            TerminalSessions.SelectedItem = profile;
            _ = StartTerminalAsync(profile);
        }
    }

    private async Task StartTerminalAsync(string profile)
    {
        if (_terminalProcess is { HasExited: false } existing)
        {
            try { existing.StandardInput.WriteLine("exit"); } catch (IOException) { }
            await existing.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
            existing.Dispose();
        }
        var executable = profile switch
        {
            "Invite de commandes" => Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            "Windows PowerShell" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            _ => FindExecutable("pwsh.exe") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
        };
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _workspace?.RootPath ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (profile.Contains("PowerShell", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add("-NoLogo"); info.ArgumentList.Add("-NoExit");
        }
        try
        {
            _terminalProcess = new Process { StartInfo = info, EnableRaisingEvents = true };
            _terminalProcess.OutputDataReceived += (_, args) => AppendTerminalLine(args.Data);
            _terminalProcess.ErrorDataReceived += (_, args) => AppendTerminalLine(args.Data);
            _terminalProcess.Exited += (_, _) => Dispatcher.Invoke(() => TerminalOutput.AppendText("\r\n[session terminée]" + Environment.NewLine));
            _terminalProcess.Start();
            _terminalProcess.BeginOutputReadLine(); _terminalProcess.BeginErrorReadLine();
            TerminalOutput.AppendText($"\r\n[{profile} démarré dans {_terminalProcess.StartInfo.WorkingDirectory}]\r\n");
            StatusMessage.Text = $"Terminal : {profile}";
        }
        catch (Exception ex) { TerminalOutput.AppendText("Impossible de démarrer le terminal : " + ex.Message + "\r\n"); }
        await Task.CompletedTask;
    }

    private void AppendTerminalLine(string? line)
    {
        if (line is null) return;
        Dispatcher.BeginInvoke(() => { TerminalOutput.AppendText(line + Environment.NewLine); TerminalOutput.ScrollToEnd(); });
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

    private async void TerminalInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(TerminalInput.Text)) return;
        var command = TerminalInput.Text; TerminalInput.Clear();
        if (_terminalProcess is not { HasExited: false }) await StartTerminalAsync(TerminalSessions.SelectedItem?.ToString() ?? "PowerShell");
        try
        {
            TerminalOutput.AppendText($"PS> {command}\r\n");
            await _terminalProcess!.StandardInput.WriteLineAsync(command);
            await _terminalProcess.StandardInput.FlushAsync();
        }
        catch (Exception ex) { TerminalOutput.AppendText(ex.Message + "\r\n"); }
        e.Handled = true;
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
        if (extension == ".csproj") command = $"dotnet run --project {Quote(path)}";
        else if (extension == ".sln" || extension == ".slnx") command = $"dotnet build {Quote(path)}";
        else if (extension == ".py") command = $"python {Quote(path)}";
        else if (extension is ".js" or ".mjs") command = $"node {Quote(path)}";
        else if (extension == ".ps1") command = $"& {Quote(path)}";
        else if (extension is ".bat" or ".cmd") command = $"& {Quote(path)}";
        else if (extension == ".cs" && _workspace is not null)
        {
            var project = Directory.EnumerateFiles(_workspace.RootPath, "*.csproj", SearchOption.AllDirectories)
                .FirstOrDefault(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
            command = project is null ? "Write-Error 'Aucun fichier projet .csproj trouvé.'" : $"dotnet run --project {Quote(project)}";
        }
        else { StatusMessage.Text = $"Aucune commande Run configurée pour {extension}."; return; }

        ShowPanel("Terminal");
        if (_terminalProcess is not { HasExited: false }) await StartTerminalAsync(TerminalSessions.SelectedItem?.ToString() ?? "PowerShell");
        await _terminalProcess!.StandardInput.WriteLineAsync($"Set-Location {Quote(directory)}; {command}");
        await _terminalProcess.StandardInput.FlushAsync();
        StatusMessage.Text = $"Lancement : {Path.GetFileName(path)}";
    }

    private void Debug_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel("DebugConsole");
        DebugConsolePanel.Text = "Aucun adaptateur de débogage DAP n’est configuré. Le lancement F5 est indisponible tant qu’un adaptateur de langage n’est pas installé.";
        StatusMessage.Text = "Adaptateur DAP manquant";
    }

    private void StopRun_Click(object sender, RoutedEventArgs e)
    {
        try { if (_terminalProcess is { HasExited: false }) _terminalProcess.StandardInput.WriteLine("`u0003"); }
        catch (Exception ex) { OutputPanel.AppendText(ex.Message + Environment.NewLine); }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private async Task RefreshGitAsync()
    {
        if (_workspace is null) { GitBranchLabel.Text = "Aucun dossier ouvert"; GitChanges.Items.Clear(); return; }
        try
        {
            var output = await RunGitAsync("status", "--porcelain=v1", "--branch");
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            GitChanges.Items.Clear();
            foreach (var line in lines)
            {
                if (line.StartsWith("## "))
                {
                    var branch = line[3..].Trim(); GitBranchLabel.Text = branch;
                    StatusGitBranch.Content = "⑂ " + branch.Split("...", StringSplitOptions.None)[0];
                    continue;
                }
                var code = line.Length >= 2 ? line[..2] : "  ";
                var path = line.Length > 3 ? line[3..].Trim('"') : line;
                var item = new ListBoxItem { Content = $"{code}  {Path.GetFileName(path)}", Tag = (Code: code, Path: path), ToolTip = path };
                var menu = new ContextMenu();
                menu.Items.Add(MenuAction("Stage", async () => { await RunGitAsync("add", "--", path); await RefreshGitAsync(); }));
                menu.Items.Add(MenuAction("Unstage", async () => { await RunGitAsync("restore", "--staged", "--", path); await RefreshGitAsync(); }));
                menu.Items.Add(MenuAction("Discard changes…", async () =>
                {
                    if (MessageBox.Show(this, $"Abandonner les modifications de {path} ?", "Git", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        await RunGitAsync("restore", "--worktree", "--", path); await RefreshGitAsync();
                    }
                }));
                item.ContextMenu = menu;
                GitChanges.Items.Add(item);
            }
            var changed = lines.Count(line => !line.StartsWith("## "));
            StatusMessage.Text = $"Git : {changed} fichier(s) modifié(s)";
        }
        catch (InvalidOperationException) { GitBranchLabel.Text = "Git non détecté"; StatusGitBranch.Content = "⑂ —"; GitChanges.Items.Clear(); }
        catch (Exception ex) { GitBranchLabel.Text = "Erreur Git"; OutputPanel.AppendText(ex.Message + Environment.NewLine); }
    }

    private static MenuItem MenuAction(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private async Task<string> RunGitAsync(params string[] arguments)
    {
        if (_workspace is null) throw new InvalidOperationException("Aucun dossier ouvert.");
        var info = new ProcessStartInfo("git") { WorkingDirectory = _workspace.RootPath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Git n’a pas pu démarrer.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
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
