using KeyceCode.Services;
using System.IO;
using System.Windows;

namespace KeyceCode;

public partial class MainWindow
{
    private async void RunTask_Click(object sender, RoutedEventArgs e) => await RunWorkspaceTaskAsync();

    private async Task RunWorkspaceTaskAsync()
    {
        if (_workspace is null) { StatusMessage.Text = "Ouvrez un dossier avant de lancer une tâche."; return; }
        IReadOnlyList<WorkspaceTask> tasks;
        try { tasks = WorkspaceTaskService.Read(_workspace.RootPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, "Impossible de lire .vscode/tasks.json :\n" + ex.Message, "Tâches", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (tasks.Count == 0)
        {
            MessageBox.Show(this, "Aucune tâche shell avec une commande n’a été trouvée dans .vscode/tasks.json.", "Tâches", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var selected = QuickPickWindow.Show(this, "Exécuter la tâche", tasks.Select(t => t.Label));
        var task = tasks.FirstOrDefault(t => t.Label == selected);
        if (task is null) return;
        var isCmd = _activeTerminalProfile == "Invite de commandes";
        var activeFile = _activeDocument?.Path;
        string Expand(string value) => WorkspaceTaskService.ExpandVariables(value, _workspace.RootPath, activeFile);
        var cwdValue = string.IsNullOrWhiteSpace(task.WorkingDirectory) ? _workspace.RootPath : Expand(task.WorkingDirectory);
        if (cwdValue.Contains("${", StringComparison.Ordinal))
        {
            MessageBox.Show(this, $"Une variable de tâche n’a pas pu être résolue : {cwdValue}", "Tâches", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var cwd = Path.GetFullPath(Path.IsPathRooted(cwdValue) ? cwdValue : Path.Combine(_workspace.RootPath, cwdValue));
        if (!Directory.Exists(cwd))
        {
            MessageBox.Show(this, $"Le répertoire de la tâche n’existe pas : {cwd}", "Tâches", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var command = Expand(task.Command);
        var arguments = task.Arguments.Select(argument => Expand(argument)).ToArray();
        var unresolved = arguments.FirstOrDefault(argument => argument.Contains("${", StringComparison.Ordinal));
        if (command.Contains("${", StringComparison.Ordinal) || unresolved is not null)
        {
            MessageBox.Show(this, $"Une variable de tâche n’a pas pu être résolue : {unresolved ?? command}", "Tâches", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Func<string, string> quoteArgument = isCmd ? QuoteCmd : Quote;
        var commandLine = command + string.Concat(arguments.Select(argument => " " + quoteArgument(argument)));
        var preview = $"Tâche : {task.Label}\nRépertoire : {cwd}\n\n{commandLine}\n\nLes tâches exécutent des commandes définies par le projet. Voulez-vous continuer ?";
        if (MessageBox.Show(this, preview, "Confirmer l’exécution", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var document in _documents.Where(document => document.IsDirty).ToArray())
            if (!await SaveDocumentAsync(document)) return;
        ShowPanel("Terminal");
        if (_terminalSession is null || _terminalSession.Completion.IsCompleted)
            await StartTerminalAsync(_activeTerminalProfile ?? "PowerShell 7");
        var line = isCmd ? $"cd /d {QuoteCmd(cwd)} && {commandLine}\r" : $"Set-Location -LiteralPath {Quote(cwd)}; {commandLine}\r";
        if (!await SendTerminalInputAsync(line))
        {
            StatusMessage.Text = "La tâche n’a pas été envoyée au terminal.";
            return;
        }
        StatusMessage.Text = $"Tâche lancée : {task.Label}";
    }
}
