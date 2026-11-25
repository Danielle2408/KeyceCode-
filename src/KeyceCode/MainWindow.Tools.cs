using KeyceCode.Services;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace KeyceCode;

public partial class MainWindow
{
    private const int TerminalAiTranscriptLimit = 24_000;
    private static readonly Regex TerminalEscapeSequence = new(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))", RegexOptions.Compiled);
    private static readonly Regex TerminalErrorIndicator = new(@"(?im)(\berror\b|\bexception\b|\btraceback\b|\bfatal\b|\bfailed\b|\bfailure\b|\bnot recognized\b|\bnot found\b|\bmissing\b|\baccess is denied\b|\bpermission denied\b|\bcannot\b|\bcould not\b|\bunable to\b|\bCS\d{4}\b|\bMSB\d{4}\b|\bNU\d{4}\b|npm ERR!|SyntaxError|TypeError:|ModuleNotFoundError)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private DateTimeOffset? _geminiInlineCompletionPausedUntil;
    private DispatcherTimer? _geminiInlineCompletionPauseTimer;
    private bool IsGeminiInlineCompletionAvailable => _editorPreferences.GeminiInlineCompletionEnabled &&
        (_geminiInlineCompletionPausedUntil is null || _geminiInlineCompletionPausedUntil <= DateTimeOffset.UtcNow);

    private void GeminiCompletionPauseButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(Control.BackgroundProperty, "SidebarBackground");
        menu.SetResourceReference(Control.ForegroundProperty, "ForegroundBrush");
        if (_geminiInlineCompletionPausedUntil is not null)
        {
            var resume = new MenuItem { Header = "Reprendre l’autocomplétion maintenant" };
            resume.Click += async (_, _) => await SetGeminiInlineCompletionPauseAsync(null);
            menu.Items.Add(resume);
            menu.Items.Add(new Separator());
        }

        if (!_editorPreferences.GeminiInlineCompletionEnabled)
        {
            menu.Items.Add(new MenuItem { Header = "Autocomplétion désactivée dans les paramètres", IsEnabled = false });
            var openSettings = new MenuItem { Header = "Ouvrir les paramètres…" };
            openSettings.Click += (_, _) => Settings_Click(sender, new RoutedEventArgs());
            menu.Items.Add(openSettings);
        }
        else
        {
            var suspend = new MenuItem { Header = "Suspendre pendant…" };
            for (var minutes = 5; minutes <= 60; minutes += 5)
            {
                var item = new MenuItem { Header = $"{minutes} minutes" };
                item.Click += async (_, _) => await SetGeminiInlineCompletionPauseAsync(minutes);
                suspend.Items.Add(item);
            }
            menu.Items.Add(suspend);
        }

        menu.PlacementTarget = GeminiCompletionPauseButton;
        menu.Placement = PlacementMode.Top;
        GeminiCompletionPauseButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private async Task SetGeminiInlineCompletionPauseAsync(int? minutes)
    {
        _geminiInlineCompletionPausedUntil = minutes is > 0 ? DateTimeOffset.UtcNow.AddMinutes(minutes.Value) : null;
        if (_geminiInlineCompletionPausedUntil is null)
        {
            _geminiInlineCompletionPauseTimer?.Stop();
            _geminiInlineCompletionPauseTimer = null;
        }
        else
        {
            _geminiInlineCompletionPauseTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _geminiInlineCompletionPauseTimer.Tick -= GeminiInlineCompletionPauseTimer_Tick;
            _geminiInlineCompletionPauseTimer.Tick += GeminiInlineCompletionPauseTimer_Tick;
            _geminiInlineCompletionPauseTimer.Start();
        }

        foreach (var request in _geminiInlineRequests.Values) request.Cancel();
        UpdateGeminiInlineCompletionPauseButton();
        await PostEditorMessageAsync(new { type = "setAiCompletionEnabled", value = IsGeminiInlineCompletionAvailable });
        StatusMessage.Text = minutes is > 0
            ? $"Autocomplétion Gemini suspendue pendant {minutes} minutes."
            : "Autocomplétion Gemini reprise selon les paramètres.";
    }

    private async void GeminiInlineCompletionPauseTimer_Tick(object? sender, EventArgs e)
    {
        if (_geminiInlineCompletionPausedUntil is not { } resumeAt) return;
        var remaining = resumeAt - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            UpdateGeminiInlineCompletionPauseButton();
            return;
        }

        _geminiInlineCompletionPausedUntil = null;
        _geminiInlineCompletionPauseTimer?.Stop();
        _geminiInlineCompletionPauseTimer = null;
        UpdateGeminiInlineCompletionPauseButton();
        foreach (var request in _geminiInlineRequests.Values) request.Cancel();
        await PostEditorMessageAsync(new { type = "setAiCompletionEnabled", value = IsGeminiInlineCompletionAvailable });
        StatusMessage.Text = "La pause est terminée · autocomplétion Gemini reprise.";
    }

    private void UpdateGeminiInlineCompletionPauseButton()
    {
        var remaining = _geminiInlineCompletionPausedUntil - DateTimeOffset.UtcNow;
        if (remaining is { } value && value > TimeSpan.Zero)
        {
            GeminiCompletionPauseButton.Content = "🤖 ⏸";
            GeminiCompletionPauseButton.ToolTip = $"Autocomplétion suspendue · reprise dans {Math.Ceiling(value.TotalMinutes):00}:{value.Seconds:00}. Cliquer pour les options.";
        }
        else
        {
            GeminiCompletionPauseButton.Content = "🤖";
            GeminiCompletionPauseButton.ToolTip = _editorPreferences.GeminiInlineCompletionEnabled
                ? "Autocomplétion Gemini active · cliquer pour la suspendre par tranches de 5 minutes"
                : "Autocomplétion Gemini désactivée dans les paramètres · cliquer pour les options";
        }
    }

    private const int MaximumAssistantContextCharacters = 60_000;
    private const int MaximumAssistantFileBytes = 256 * 1024;
    private const int MaximumAssistantContextFiles = 60;
    private readonly List<AssistantContextAttachment> _assistantContextAttachments = [];
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
        if (tab is not null)
        {
            var cleanText = TerminalEscapeSequence.Replace(text, string.Empty);
            tab.Transcript.Append(cleanText);
            if (tab.Transcript.Length > TerminalAiTranscriptLimit)
                tab.Transcript.Remove(0, tab.Transcript.Length - TerminalAiTranscriptLimit);
            if (!tab.HasDetectedError && TerminalErrorIndicator.IsMatch(tab.Transcript.ToString()))
            {
                tab.HasDetectedError = true;
                if (_activeTerminalId == tab.Id)
                {
                    ExplainTerminalErrorButton.IsEnabled = true;
                    StatusMessage.Text = "Erreur détectée dans le terminal · KeyceIA peut l’expliquer.";
                }
            }
            WriteTerminalOutput(tab, text);
        }
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
        else if (extension is ".c" or ".cc" or ".cpp" or ".cxx" or ".c++")
        {
            var compiler = FindNativeCompiler(extension != ".c");
            if (compiler is null) { ShowNativeCompilerMissing(extension); return; }
            var output = Path.ChangeExtension(path, ".exe");
            var compile = BuildNativeCompileCommand(compiler, path, output, extension != ".c", isCmd, QuotePath);
            command = isCmd ? $"{compile} && {QuoteCmd(output)}"
                : $"{compile}; if ($LASTEXITCODE -eq 0) {{ & {Quote(output)} }}";
        }
        else if (extension == ".exe") command = isCmd ? QuoteCmd(path) : "& " + Quote(path);
        else if (extension == ".php")
        {
            var php = FindPhpExecutable();
            if (php is null) { ShowPhpMissing(); return; }
            command = $"{QuotePath(php)} {QuotePath(path)}";
        }
        else if (extension is ".html" or ".htm" or ".css")
        {
            LiveServer_Click(sender, e);
            return;
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

        var nativeSource = extension is ".c" or ".cc" or ".cpp" or ".cxx" or ".c++";
        await RunInIntegratedTerminalAsync(command, directory,
            nativeSource ? $"Compilation et lancement : {Path.GetFileName(path)}" : $"Lancement : {Path.GetFileName(path)}",
            captureBuildOutput: nativeSource);
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
        else if (extension == ".php")
        {
            var php = FindPhpExecutable();
            if (php is null) { ShowPhpMissing(); return; }
            command = $"{QuotePath(php)} -l {QuotePath(path)}";
        }
        else if (extension is ".html" or ".htm" or ".css")
        {
            StatusMessage.Text = "HTML et CSS ne se compilent pas; utilise Run ou Go Live pour les prévisualiser dans le navigateur.";
            return;
        }
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
        else if (extension is ".c" or ".cc" or ".cpp" or ".cxx" or ".c++")
        {
            var isCpp = extension != ".c";
            var compiler = FindNativeCompiler(isCpp);
            if (compiler is null) { ShowNativeCompilerMissing(extension); return; }
            target = Path.ChangeExtension(path, ".exe");
            command = BuildNativeCompileCommand(compiler, path, target, isCpp, isCmd, QuotePath);
        }
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

    private async void LiveServer_Click(object sender, RoutedEventArgs e)
    {
        if (_liveServer.IsRunning)
        {
            await _liveServer.StopAsync();
            LiveServerButton.Content = "▷ Go Live";
            StatusMessage.Text = "Live Server arrêté.";
            return;
        }
        if (_activeDocument is not { } document || string.IsNullOrWhiteSpace(document.Path))
        {
            StatusMessage.Text = "Ouvre un fichier HTML, CSS ou PHP avant de démarrer Live Server.";
            return;
        }
        if (document.IsDirty && !await SaveDocumentAsync(document)) return;

        var activePath = Path.GetFullPath(document.Path);
        var containingDirectory = Path.GetDirectoryName(activePath)!;
        var root = _workspace is not null && IsInsideWorkspace(activePath, _workspace.RootPath)
            ? Path.GetFullPath(_workspace.RootPath) : containingDirectory;
        var extension = Path.GetExtension(activePath).ToLowerInvariant();
        var defaultPhp = Path.Combine(root, "index.php");
        var usePhp = extension == ".php" || File.Exists(defaultPhp);
        string? entryFile = extension is ".html" or ".htm" ? activePath : null;

        if (extension == ".css")
        {
            entryFile = new[] { "index.html", "index.htm" }.Select(name => Path.Combine(root, name)).FirstOrDefault(File.Exists)
                ?? Directory.EnumerateFiles(root, "*.html", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (entryFile is null && File.Exists(defaultPhp)) usePhp = true;
            else if (entryFile is null)
            {
                StatusMessage.Text = "Aucune page HTML trouvée à prévisualiser dans ce dossier.";
                return;
            }
        }
        else if (extension is not (".html" or ".htm" or ".php"))
        {
            entryFile = new[] { "index.html", "index.htm" }.Select(name => Path.Combine(root, name)).FirstOrDefault(File.Exists);
            if (entryFile is null && File.Exists(defaultPhp)) usePhp = true;
            else if (entryFile is null)
            {
                StatusMessage.Text = "Ouvre une page HTML, CSS ou PHP avant de démarrer Live Server.";
                return;
            }
        }

        try
        {
            string url;
            if (usePhp)
            {
                var php = FindPhpExecutable();
                if (php is null) { ShowPhpMissing(); return; }
                url = await _liveServer.StartPhpAsync(php, root, extension == ".php" ? activePath : entryFile);
                ShowPanel("Output");
                OutputPanel.AppendText($"PHP Live Server démarré : {url}\r\n");
            }
            else url = await _liveServer.StartAsync(root, entryFile);

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            LiveServerButton.Content = "■ Stop Live Server";
            StatusMessage.Text = $"Live Server actif · {url}";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            await _liveServer.StopAsync();
            LiveServerButton.Content = "▷ Go Live";
            StatusMessage.Text = "Démarrage de Live Server impossible.";
            OutputPanel.AppendText("Live Server : " + ex.Message + Environment.NewLine);
            ShowPanel("Output");
        }
    }

    private static string? FindPhpExecutable()
    {
        var roots = new List<string>();
        foreach (var variable in new[] { "WAMP_DIR", "WAMP64", "WAMP" })
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                roots.Add(Environment.GetEnvironmentVariable(variable)!);
        roots.AddRange([
            @"C:\wamp64", @"C:\wamp",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wamp64"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wampserver"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Wamp")
        ]);

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            var phpRoot = Path.Combine(root, "bin", "php");
            var configPath = Path.Combine(root, "wampmanager.conf");
            if (File.Exists(configPath))
            {
                try
                {
                    var match = Regex.Match(File.ReadAllText(configPath), @"(?im)^\s*phpVersion\s*=\s*""(?<version>[^""]+)""", RegexOptions.CultureInvariant);
                    if (match.Success)
                    {
                        var configured = Path.Combine(phpRoot, "php" + match.Groups["version"].Value, "php.exe");
                        if (File.Exists(configured)) return configured;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            if (Directory.Exists(phpRoot))
            {
                try
                {
                    var installed = Directory.EnumerateDirectories(phpRoot, "php*")
                        .OrderByDescending(directory => Version.TryParse(Path.GetFileName(directory).Length > 3 ? Path.GetFileName(directory)[3..] : "", out var version) ? version : new Version(0, 0))
                        .Select(directory => Path.Combine(directory, "php.exe"))
                        .FirstOrDefault(File.Exists);
                    if (installed is not null) return installed;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        return FindExecutable("php.exe") ?? FindExecutable("php");
    }

    private void ShowPhpMissing()
    {
        StatusMessage.Text = "PHP introuvable.";
        OutputPanel.AppendText("Aucun php.exe trouvé. KeyceCode détecte WampServer (version sélectionnée) et PHP dans le PATH; vérifie l’installation de Wamp ou configure le PATH.\r\n");
        ShowPanel("Output");
    }

    private static string? FindNativeCompiler(bool cPlusPlus)
    {
        var candidates = cPlusPlus
            ? new[] { "g++.exe", "clang++.exe", "clang-cl.exe", "cl.exe" }
            : new[] { "gcc.exe", "clang.exe", "clang-cl.exe", "cl.exe" };
        var fromPath = candidates.Select(FindExecutable).FirstOrDefault(path => path is not null);
        if (fromPath is not null) return fromPath;

        // Common MSYS2 toolchain locations let the editor work without changing the user's global PATH.
        var roots = new[] { @"C:\msys64\mingw64\bin", @"C:\msys64\ucrt64\bin", @"C:\msys64\clang64\bin" };
        return roots.SelectMany(root => candidates.Select(name => Path.Combine(root, name)))
            .FirstOrDefault(File.Exists);
    }

    private static string BuildNativeCompileCommand(string compiler, string sourcePath, string outputPath,
        bool cPlusPlus, bool commandPrompt, Func<string, string> quotePath)
    {
        var compilerName = Path.GetFileName(compiler);
        var compilerDirectory = Path.GetDirectoryName(compiler)!;
        var isMsvcStyle = compilerName.Equals("cl.exe", StringComparison.OrdinalIgnoreCase) ||
            compilerName.Equals("clang-cl.exe", StringComparison.OrdinalIgnoreCase);
        var invocation = commandPrompt ? QuoteCmd(compiler) : "& " + Quote(compiler);
        // GCC/Clang binaries shipped by MSYS2 need their sibling DLLs (and cc1.exe)
        // discoverable even when the compiler was found outside the terminal PATH.
        var pathSetup = commandPrompt
            ? $"set \"PATH={compilerDirectory};%PATH%\" && "
            : $"$env:PATH = {Quote(compilerDirectory + ";")} + $env:PATH; ";
        if (isMsvcStyle)
        {
            var flags = cPlusPlus ? "/nologo /W4 /EHsc /std:c++17" : "/nologo /W4 /std:c17";
            return $"{pathSetup}{invocation} {flags} /Fe:{quotePath(outputPath)} {quotePath(sourcePath)}";
        }
        var standard = cPlusPlus ? "c++17" : "c17";
        return $"{pathSetup}{invocation} -std={standard} -Wall -Wextra {quotePath(sourcePath)} -o {quotePath(outputPath)}";
    }

    private void ShowNativeCompilerMissing(string extension)
    {
        var language = extension == ".c" ? "C" : "C++";
        StatusMessage.Text = $"Compilateur {language} introuvable.";
        OutputPanel.AppendText("Compilation C/C++ impossible : installe GCC (MinGW-w64), LLVM/Clang ou Microsoft C++ Build Tools, puis ajoute le compilateur au PATH du terminal.\r\n");
        ShowPanel("Output");
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
        var entered = QuickPickWindow.Prompt(this, "Associer un dépôt GitHub", "URL HTTPS ou SSH du dépôt", "https://github.com/", "Associer");
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

    private async void ValidateGitHubLink_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null) { StatusMessage.Text = "Ouvre un dossier avant de valider le lien GitHub."; return; }
        try
        {
            var remote = (await RunGitAsync("remote", "get-url", "origin")).Trim();
            if (!TryParseGitHubRemote(remote, out _, out var webUrl))
            {
                StatusMessage.Text = "Aucun remote GitHub valide n’est configuré dans origin.";
                return;
            }

            // No --exit-code: an empty remote has no refs yet but is still a valid reachable repository.
            await RunGitAsync("ls-remote", "--heads", "origin");
            GitBranchLabel.Text = "GitHub lié : " + webUrl.Host + webUrl.AbsolutePath;
            StatusMessage.Text = "Lien GitHub validé : le dépôt distant est accessible.";
            OutputPanel.AppendText($"[GitHub] Lien validé : {webUrl.AbsoluteUri}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            GitBranchLabel.Text = "Lien GitHub non vérifié";
            StatusMessage.Text = "Le dépôt GitHub est inaccessible · vérifie l’URL ou la connexion Git.";
            OutputPanel.AppendText("[GitHub] Validation du lien impossible : " + ex.Message + Environment.NewLine);
        }
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
        var userMessage = PromptBox.Text.Trim();
        if (userMessage.Length == 0) return;
        PromptBox.Tag = null;
        SetAssistantOutput("Lecture du contexte joint…");
        AiProposalPreview.Visibility = Visibility.Collapsed;
        try
        {
            var prompt = await BuildAssistantPromptAsync(userMessage);
            SetAssistantOutput("KeyceIA réfléchit…");
            SetAssistantOutput(await _gemini.AskAsync(prompt));
            if (string.Equals(PromptBox.Text.Trim(), userMessage, StringComparison.Ordinal)) PromptBox.Clear();
        }
        catch (Exception ex) { SetAssistantOutput(ex.Message); }
    }

    private async Task<string> BuildAssistantPromptAsync(string message)
    {
        var prompt = new StringBuilder(message);
        if (!string.IsNullOrWhiteSpace(_selectedText))
            prompt.Append("\n\nSélection de code explicitement partagée :\n````\n").Append(_selectedText).Append("\n````");
        if (_assistantContextAttachments.Count == 0) return prompt.ToString();

        prompt.Append("\n\nContexte des fichiers joints (texte envoyé à Gemini pour répondre à cette demande) :\n");
        var total = 0;
        var fileCount = 0;
        foreach (var attachment in _assistantContextAttachments.ToArray())
        {
            if (fileCount >= MaximumAssistantContextFiles || total >= MaximumAssistantContextCharacters) break;
            if (attachment.ContentSnapshot is not null)
            {
                AppendAssistantContext(prompt, attachment.DisplayName, attachment.ContentSnapshot, ref total);
                fileCount++;
                continue;
            }

            if (File.Exists(attachment.Path))
            {
                var content = attachment.ContentSnapshot ?? _documents.FirstOrDefault(document =>
                    string.Equals(document.Path, attachment.Path, StringComparison.OrdinalIgnoreCase))?.Text
                    ?? await ReadAssistantTextFileAsync(attachment.Path);
                if (content is not null) { AppendAssistantContext(prompt, attachment.DisplayName, content, ref total); fileCount++; }
                continue;
            }

            if (!Directory.Exists(attachment.Path)) continue;
            foreach (var file in EnumerateAssistantContextFiles(attachment.Path))
            {
                if (fileCount >= MaximumAssistantContextFiles || total >= MaximumAssistantContextCharacters) break;
                var content = _documents.FirstOrDefault(document =>
                    string.Equals(document.Path, file, StringComparison.OrdinalIgnoreCase))?.Text
                    ?? await ReadAssistantTextFileAsync(file);
                if (content is null) continue;
                var relative = Path.GetRelativePath(attachment.Path, file);
                AppendAssistantContext(prompt, Path.Combine(attachment.DisplayName, relative), content, ref total);
                fileCount++;
            }
        }
        if (fileCount == 0) prompt.Append("\n[Aucun fichier texte lisible trouvé dans les pièces jointes.]\n");
        if (fileCount >= MaximumAssistantContextFiles || total >= MaximumAssistantContextCharacters)
            prompt.Append("\n[Contexte tronqué pour rester dans la limite d’envoi.]\n");
        AssistantContextStatus.Text = $"{fileCount} fichier(s) lus · {total:N0} caractères envoyés à Gemini avec ce message";
        return prompt.ToString();
    }

    private static void AppendAssistantContext(StringBuilder prompt, string name, string content, ref int total)
    {
        var remaining = Math.Max(0, MaximumAssistantContextCharacters - total);
        if (remaining == 0) return;
        var excerpt = content.Length > remaining ? content[..remaining] : content;
        prompt.Append("\n--- Fichier : ").Append(name).Append(" ---\n````\n").Append(excerpt).Append("\n````\n");
        total += excerpt.Length;
    }

    private static async Task<string?> ReadAssistantTextFileAsync(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaximumAssistantFileBytes) return null;
            var bytes = await File.ReadAllBytesAsync(file);
            if (bytes.AsSpan(0, Math.Min(bytes.Length, 4096)).Contains((byte)0)) return null;
            return await File.ReadAllTextAsync(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    private static IEnumerable<string> EnumerateAssistantContextFiles(string root)
    {
        var ignoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".git", ".vs", "bin", "obj", "node_modules", "packages", "artifacts", "dist", "coverage" };
        var stack = new Stack<string>();
        stack.Push(root);
        var collected = new List<string>();
        while (stack.Count > 0 && collected.Count < MaximumAssistantContextFiles)
        {
            var directory = stack.Pop();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (!ignoredDirectories.Contains(Path.GetFileName(entry))) stack.Push(entry);
                        }
                        else if (collected.Count < MaximumAssistantContextFiles && IsAssistantTextFile(entry)) collected.Add(entry);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return collected;
    }

    private static bool IsAssistantTextFile(string file) => Path.GetExtension(file).ToLowerInvariant() is
        ".cs" or ".csx" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".py" or ".java" or
        ".html" or ".htm" or ".css" or ".scss" or ".json" or ".jsonc" or ".md" or ".txt" or ".xml" or ".xaml" or
        ".yaml" or ".yml" or ".sql" or ".sh" or ".ps1" or ".bat" or ".cmd" or ".c" or ".cpp" or ".h" or ".hpp" or
        ".go" or ".rs" or ".php" or ".vue" or ".svelte" or ".toml" or ".ini" or ".config" or ".props" or
        ".targets" or ".csproj" or ".sln" or ".gradle" or ".dart" or ".rb" or ".swift" or ".kt" or ".kts";

    private void AttachAssistantFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Joindre un fichier au message KeyceIA", Filter = "Tous les fichiers|*.*", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames) AddAssistantContext(file, null);
    }

    private void AttachAssistantFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Joindre un dossier au message KeyceIA", Multiselect = false };
        if (dialog.ShowDialog(this) == true) AddAssistantContext(dialog.FolderName, null);
    }

    private void AttachActiveDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is not { } document) { AssistantContextStatus.Text = "Aucun fichier ouvert à joindre."; return; }
        AddAssistantContext(document.Path, string.IsNullOrWhiteSpace(document.Path) ? document.Text : null, document.Name);
    }

    private void AddAssistantContext(string path, string? snapshot, string? displayName = null)
    {
        var fullPath = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
        if (_assistantContextAttachments.Any(item => item.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase))) return;
        var attachment = new AssistantContextAttachment(fullPath, snapshot,
            displayName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath)));
        _assistantContextAttachments.Add(attachment);
        AssistantContextList.Items.Add(attachment);
        AssistantContextList.Visibility = Visibility.Visible;
        AssistantContextStatus.Text = $"{_assistantContextAttachments.Count} élément(s) joint(s) · envoyé(s) uniquement avec Envoyer";
    }

    private void RemoveAssistantContext_Click(object sender, RoutedEventArgs e)
    {
        if (AssistantContextList.SelectedItem is not AssistantContextAttachment attachment) return;
        _assistantContextAttachments.Remove(attachment);
        AssistantContextList.Items.Remove(attachment);
        AssistantContextList.Visibility = _assistantContextAttachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AssistantContextStatus.Text = _assistantContextAttachments.Count == 0 ? "Aucun fichier joint · colle aussi du code avec Ctrl+V" : $"{_assistantContextAttachments.Count} élément(s) joint(s)";
    }

    private void PasteCodeIntoPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (!Clipboard.ContainsText()) { AssistantContextStatus.Text = "Le presse-papiers ne contient pas de texte."; return; }
        PromptBox.Focus();
        PromptBox.SelectedText = Clipboard.GetText();
        AssistantContextStatus.Text = "Code collé dans le message · clique Envoyer ou fais Ctrl+Entrée";
    }

    private void PromptBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Ask_Click(sender, new RoutedEventArgs());
        }
    }

    private sealed record AssistantContextAttachment(string Path, string? ContentSnapshot, string DisplayName);

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        await CompleteAtCursorAsync();
    }

    private async void EditFileWithAi_Click(object sender, RoutedEventArgs e)
    {
        var document = _activeDocument;
        var instruction = PromptBox.Text.Trim();
        if (document is null) { SetAssistantOutput("Ouvre un fichier avant de demander une modification."); return; }
        if (instruction.Length == 0) { SetAssistantOutput("Écris d’abord la modification souhaitée."); return; }
        PromptBox.Tag = null;
        AiProposalPreview.Visibility = Visibility.Collapsed;
        try
        {
            _assistantVisible = true;
            SecondarySidebarColumn.Width = new GridLength(330);
            SetAssistantOutput("KeyceIA prépare une version complète du fichier pour relecture…");
            var edited = await _gemini.EditDocumentAsync(instruction, document.Text, document.Language);
            if (!ReferenceEquals(document, _activeDocument)) { SetAssistantOutput("Proposition annulée : le fichier actif a changé."); return; }
            PromptBox.Tag = new AiEditProposal(document, document.Text, 0, edited, "document");
            AiProposalPreview.Text = edited;
            AiProposalPreview.Visibility = Visibility.Visible;
            SetAssistantOutput("Document modifié par KeyceIA en attente de validation. Relis la consigne et clique sur Appliquer au fichier.");
        }
        catch (Exception ex) { SetAssistantOutput(ex.Message); }
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
            SetAssistantOutput("KeyceIA prépare une suggestion au curseur…");
            var completion = await _gemini.CompleteAsync(prefix, document.Language);
            if (!ReferenceEquals(document, _activeDocument)) { SetAssistantOutput("Suggestion annulée : le fichier actif a changé."); return; }
            PromptBox.Tag = new AiEditProposal(document, document.Text, _cursorOffset, completion, "completion");
            AiProposalPreview.Text = completion;
            AiProposalPreview.Visibility = Visibility.Visible;
            SetAssistantOutput("Suggestion prête. Elle n’a pas encore été insérée.");
        }
        catch (Exception ex) { SetAssistantOutput(ex.Message); }
    }

    private sealed record AiEditProposal(EditorDocument Document, string OriginalText, int Offset, string ProposedText, string Kind);

    private async void ApplyAiEdit_Click(object sender, RoutedEventArgs e)
    {
        if (PromptBox.Tag is not AiEditProposal proposal || !ReferenceEquals(proposal.Document, _activeDocument))
        {
            SetAssistantOutput("Aucune proposition applicable au fichier actif.");
            return;
        }
        if (!string.Equals(proposal.OriginalText, proposal.Document.Text, StringComparison.Ordinal))
        {
            SetAssistantOutput("Le fichier a changé depuis la proposition. Demande une nouvelle suggestion pour éviter d’écraser ton travail.");
            PromptBox.Tag = null;
            return;
        }
        var result = MessageBox.Show(this, $"Appliquer la modification proposée à {proposal.Document.Name} ?\n\nElle sera ajoutée comme une modification non enregistrée, annulable avec Ctrl+Z.",
            "Appliquer la proposition KeyceIA", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        if (!await ApplyAiEditToEditorAsync(proposal.Document, proposal))
        {
            SetAssistantOutput("Aucune modification appliquée. Le fichier a peut-être changé pendant la génération ; vérifie le message dans Sortie et demande une nouvelle proposition.");
            return;
        }
        PromptBox.Tag = null;
        SetAssistantOutput("Modification appliquée au fichier. Ctrl+Z permet de l’annuler ; Ctrl+S l’enregistre.");
    }

    private void RejectAiEdit_Click(object sender, RoutedEventArgs e)
    {
        PromptBox.Tag = null;
        AiProposalPreview.Visibility = Visibility.Collapsed;
        PromptBox.Clear();
        SetAssistantOutput("Proposition rejetée ; le fichier reste inchangé.");
    }
}
