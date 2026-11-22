using KeyceCode.Services;
using System.Windows;
using System.Windows.Input;

namespace KeyceCode;

public partial class MainWindow
{
    private IReadOnlyList<ImportedKeybinding>? _importedKeybindings;
    private string? _pendingKeybindingPrefix;
    private DateTime _pendingKeybindingAt;

    private async Task<bool> TryHandleImportedKeybindingAsync(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return false;
        _importedKeybindings ??= VsCodeKeybindingImporter.LoadSaved();
        if (_importedKeybindings.Count == 0) return false;
        var stroke = GetKeyboardStroke(key, Keyboard.Modifiers);
        if (stroke is null) return false;
        var now = DateTime.UtcNow;
        if (_pendingKeybindingPrefix is { } prefix && now - _pendingKeybindingAt <= TimeSpan.FromSeconds(1.5))
        {
            _pendingKeybindingPrefix = null;
            var chord = prefix + " " + stroke;
            var match = FindImportedKeybinding(chord);
            if (match is not null) { await DispatchImportedKeybindingAsync(match.Command); return true; }
        }
        else _pendingKeybindingPrefix = null;

        var single = FindImportedKeybinding(stroke);
        if (single is not null) { await DispatchImportedKeybindingAsync(single.Command); return true; }
        if (_importedKeybindings.Any(binding => binding.Key.Contains(' ') && binding.Key.StartsWith(stroke + " ", StringComparison.OrdinalIgnoreCase) && ContextMatches(binding)))
        {
            _pendingKeybindingPrefix = stroke;
            _pendingKeybindingAt = now;
            StatusMessage.Text = $"Raccourci : {stroke} …";
            return true;
        }
        return false;
    }

    private ImportedKeybinding? FindImportedKeybinding(string key) => _importedKeybindings?
        .LastOrDefault(binding => string.Equals(binding.Key, key, StringComparison.OrdinalIgnoreCase) && ContextMatches(binding));

    private bool ContextMatches(ImportedKeybinding binding) => binding.When is null ||
        ((binding.When is "editorTextFocus" or "editorFocus" or "textInputFocus") && CodeEditor.IsKeyboardFocusWithin);

    private static string? GetKeyboardStroke(Key key, ModifierKeys modifiers)
    {
        var name = key switch
        {
            >= Key.A and <= Key.Z => key.ToString().ToLowerInvariant(),
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "numpad" + ((int)key - (int)Key.NumPad0),
            >= Key.F1 and <= Key.F12 => key.ToString().ToLowerInvariant(),
            Key.Space => "space", Key.Enter => "enter", Key.Escape => "escape", Key.Tab => "tab",
            Key.Back => "backspace", Key.Delete => "delete", Key.Insert => "insert", Key.Home => "home", Key.End => "end",
            Key.PageUp => "pageup", Key.PageDown => "pagedown", Key.Up => "up", Key.Down => "down", Key.Left => "left", Key.Right => "right",
            Key.Add => "numpad_add", Key.Subtract => "numpad_subtract", Key.Multiply => "numpad_multiply", Key.Divide => "numpad_divide", Key.Decimal => "numpad_decimal",
            Key.OemPlus => "=", Key.OemMinus => "-", Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/",
            Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemPipe => "\\", Key.Oem1 => ";", Key.OemQuotes => "'", Key.Oem3 => "`",
            _ => null
        };
        if (name is null) return null;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("alt");
        parts.Add(name);
        return string.Join('+', parts);
    }

    private async Task DispatchImportedKeybindingAsync(string command)
    {
        StatusMessage.Text = $"Raccourci : {command}";
        switch (command)
        {
            case "save": await SaveActiveAsync(); return;
            case "quickOpen": QuickOpen_Click(this, new RoutedEventArgs()); return;
            case "commandPalette": CommandPalette_Click(this, new RoutedEventArgs()); return;
            case "find": await PostEditorMessageAsync(new { type = "executeCommand", command = "find" }); return;
            case "replace": await PostEditorMessageAsync(new { type = "executeCommand", command = "replace" }); return;
            case "codeActions": await PostEditorMessageAsync(new { type = "executeCommand", command = "editor.action.quickFix" }); return;
            case "rename": await PostEditorMessageAsync(new { type = "executeCommand", command = "editor.action.rename" }); return;
            default:
                await PostEditorMessageAsync(new { type = "executeCommand", command });
                return;
        }
    }
}
