using KeyceCode.Services;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace KeyceCode;

public partial class MainWindow
{
    private sealed class TerminalTab
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public required string Profile { get; init; }
        public required ConPtySession Session { get; init; }
        public StringBuilder PendingOutput { get; } = new();
    }

    private readonly Dictionary<string, TerminalTab> _terminalTabs = new(StringComparer.Ordinal);
    private string? _activeTerminalId;
    private bool _changingTerminalSelection;

    private async Task EnsureTerminalWebViewReadyAsync()
    {
        if (_terminalReady) return;
        try { await _terminalReadySignal.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { StatusMessage.Text = "xterm n’a pas répondu; le terminal ne peut pas démarrer."; throw; }
    }

    private async Task CreateTerminalTabAsync(string profile, string? requestedId = null, string? requestedName = null)
    {
        await EnsureTerminalWebViewReadyAsync();
        var id = requestedId ?? Guid.NewGuid().ToString("N");
        if (_terminalTabs.ContainsKey(id)) await RemoveTerminalTabAsync(id, selectNext: false);
        var isCmd = profile == "Invite de commandes";
        var windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var executable = isCmd ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
            : profile == "Windows PowerShell" ? windowsPowerShell
            : FindExecutable("pwsh.exe") ?? windowsPowerShell;
        IReadOnlyList<string> arguments = isCmd ? ["/D"] : ["-NoLogo", "-NoExit"];
        var workingDirectory = _workspace?.RootPath ?? Environment.CurrentDirectory;
        try
        {
            var session = await ConPtySession.StartAsync(executable, arguments, workingDirectory, columns: 120, rows: 30);
            var tab = new TerminalTab { Id = id, Name = requestedName ?? profile, Profile = profile, Session = session };
            _terminalTabs[id] = tab;
            AddTerminalTabItem(tab);
            PostTerminalMessage(new { type = "createSession", id, profile });
            ActivateTerminalTab(id);
            _ = PumpTerminalOutputAsync(session);
            _ = MonitorTerminalExitAsync(session, profile);
            QueueOrWriteTerminal(session, $"\r\n\u001b[90m[{profile} démarré dans {workingDirectory}]\u001b[0m\r\n");
            StatusMessage.Text = $"Terminal : {profile}";
        }
        catch (Exception ex)
        {
            StatusMessage.Text = "Échec du démarrage ConPTY";
            OutputPanel.AppendText("Impossible de démarrer le terminal : " + ex.Message + Environment.NewLine);
        }
    }

    private void AddTerminalTabItem(TerminalTab tab)
    {
        var item = new ListBoxItem { Content = tab.Name, Tag = tab.Id, ToolTip = $"{tab.Profile} · double-cliquez pour renommer" };
        TerminalTabs.Items.Add(item);
    }

    private void ActivateTerminalTab(string id)
    {
        if (!_terminalTabs.TryGetValue(id, out var tab)) return;
        _activeTerminalId = id;
        _terminalSession = tab.Session;
        _activeTerminalProfile = tab.Profile;
        TerminalProfileLabel.Text = tab.Profile;
        _changingTerminalSelection = true;
        try
        {
            TerminalTabs.SelectedItem = TerminalTabs.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, id));
        }
        finally { _changingTerminalSelection = false; }
        PostTerminalMessage(new { type = "switchSession", id });
    }

    private void TerminalTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingTerminalSelection || TerminalTabs.SelectedItem is not ListBoxItem { Tag: string id }) return;
        ActivateTerminalTab(id);
    }

    private void TerminalTabs_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TerminalTabs.SelectedItem is not ListBoxItem { Tag: string id } item || !_terminalTabs.TryGetValue(id, out var tab)) return;
        var name = QuickPickWindow.Prompt(this, "Renommer le terminal", "Nom du terminal", tab.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        tab.Name = name.Trim();
        item.Content = tab.Name;
    }

    private async void KillTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTerminalId is { } id) await RemoveTerminalTabAsync(id, selectNext: true);
    }

    private async void RestartTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTerminalId is not { } id || !_terminalTabs.TryGetValue(id, out var tab))
        {
            await CreateTerminalTabAsync(_activeTerminalProfile ?? "PowerShell 7");
            return;
        }
        var profile = tab.Profile;
        var name = tab.Name;
        await RemoveTerminalTabAsync(id, selectNext: false);
        await CreateTerminalTabAsync(profile, id, name);
    }

    private async Task RemoveTerminalTabAsync(string id, bool selectNext)
    {
        if (!_terminalTabs.Remove(id, out var tab)) return;
        var wasActive = _activeTerminalId == id;
        PostTerminalMessage(new { type = "removeSession", id });
        try { await tab.Session.DisposeAsync(); }
        catch (Exception ex) { OutputPanel.AppendText("Arrêt ConPTY : " + ex.Message + Environment.NewLine); }
        var item = TerminalTabs.Items.OfType<ListBoxItem>().FirstOrDefault(candidate => Equals(candidate.Tag, id));
        if (item is not null) TerminalTabs.Items.Remove(item);
        if (wasActive)
        {
            _activeTerminalId = null;
            _terminalSession = null;
            if (selectNext && _terminalTabs.Values.FirstOrDefault() is { } next) ActivateTerminalTab(next.Id);
        }
        if (_terminalTabs.Count == 0) _activeTerminalProfile = null;
    }

    private async Task StopAllTerminalTabsAsync()
    {
        foreach (var id in _terminalTabs.Keys.ToArray()) await RemoveTerminalTabAsync(id, selectNext: false);
        _terminalSession = null;
        _activeTerminalId = null;
        _activeTerminalProfile = null;
    }

    private void WriteTerminalOutput(TerminalTab tab, string text)
    {
        if (!_terminalReady)
        {
            tab.PendingOutput.Append(text);
            if (tab.PendingOutput.Length > 1_000_000) tab.PendingOutput.Remove(0, tab.PendingOutput.Length - 1_000_000);
            return;
        }
        PostTerminalMessage(new { type = "write", id = tab.Id, data = text });
    }

    private async Task HandleTerminalInputAsync(JsonElement root)
    {
        var id = ReadString(root, "id");
        var data = ReadString(root, "data");
        if (data is not null && id is not null && _terminalTabs.TryGetValue(id, out var tab)) await tab.Session.SendInputAsync(data);
    }

    private void HandleTerminalResize(JsonElement root)
    {
        var id = ReadString(root, "id");
        if (id is null || !_terminalTabs.TryGetValue(id, out var tab) || !TryReadDimensions(root, out var cols, out var rows)) return;
        try { tab.Session.Resize(cols, rows); }
        catch (InvalidOperationException) { }
        catch (System.Runtime.InteropServices.COMException ex) { StatusMessage.Text = "Redimensionnement ConPTY impossible : " + ex.Message; }
    }

    private void OnTerminalWebViewReady(JsonElement root)
    {
        _terminalReady = true;
        _terminalReadySignal.TrySetResult(true);
        var theme = ((App)Application.Current).ThemeService.CurrentTheme;
        PostTerminalMessage(new { type = "setTheme", theme = TerminalTheme(theme.Id) });
        foreach (var tab in _terminalTabs.Values)
        {
            PostTerminalMessage(new { type = "createSession", id = tab.Id, profile = tab.Profile });
            if (tab.PendingOutput.Length > 0)
            {
                PostTerminalMessage(new { type = "write", id = tab.Id, data = tab.PendingOutput.ToString() });
                tab.PendingOutput.Clear();
            }
        }
        if (_activeTerminalId is { } id) PostTerminalMessage(new { type = "switchSession", id });
        if (_activeTerminalId is not null && TryReadDimensions(root, out var cols, out var rows)) HandleTerminalResizeForActive(cols, rows);
    }

    private void HandleTerminalResizeForActive(int columns, int rows)
    {
        if (_activeTerminalId is not { } id || !_terminalTabs.TryGetValue(id, out var tab)) return;
        try { tab.Session.Resize(columns, rows); }
        catch (InvalidOperationException) { }
    }
}
