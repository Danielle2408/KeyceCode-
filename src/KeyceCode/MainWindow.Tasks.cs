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
        var cwd = string.IsNullOrWhiteSpace(task.WorkingDirectory) ? _workspace.RootPath :
            Path.GetFullPath(Path.IsPathRooted(task.WorkingDirectory) ? task.WorkingDirectory : Path.Combine(_workspace.RootPath, task.WorkingDirectory));
        var command = task.Command;
        var preview = $"Tâche : {task.Label}\nRépertoire : {cwd}\n\n{command}\n\nLes tâches exécutent des commandes définies par le projet. Voulez-vous continuer ?";
        if (MessageBox.Show(this, preview, "Confirmer l’exécution", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        ShowPanel("Terminal");
        if (_terminalSession is null || _terminalSession.Completion.IsCompleted)
            await StartTerminalAsync(_activeTerminalProfile ?? "PowerShell 7");
        var line = isCmd ? $"cd /d {QuoteCmd(cwd)} && {command}\r" : $"Set-Location -LiteralPath {Quote(cwd)}; {command}\r";
        await SendTerminalInputAsync(line);
        StatusMessage.Text = $"Tâche lancée : {task.Label}";
    }
}
