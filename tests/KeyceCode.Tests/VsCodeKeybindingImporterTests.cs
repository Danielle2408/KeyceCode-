using KeyceCode.Services;
using Xunit;

namespace KeyceCode.Tests;

public sealed class VsCodeKeybindingImporterTests
{
    [Fact]
    public async Task Import_MapsSupportedCommandsAndSkipsUnknownActionsAndContexts()
    {
        var path = await CreateFileAsync("""
            [
              // VS Code keybindings.json allows comments and trailing commas.
              { "key": "ctrl+shift+p", "command": "workbench.action.showCommands" },
              { "key": "cmd+k cmd+s", "command": "workbench.action.quickOpen", "when": "editorTextFocus" },
              { "key": "ctrl+alt+f", "command": "editor.action.startFindReplaceAction", "when": "editorTextFocus && !editorReadonly" },
              { "key": "ctrl+x", "command": "some.extension.command" },
              { "key": "ctrl+g", "command": "workbench.action.quickOpen", "when": "resourceScheme == file" },
              { "key": "not a key", "command": "undo" },
            ]
            """);
        try
        {
            var result = VsCodeKeybindingImporter.Import(path);
            Assert.Equal(2, result.Keybindings.Count);
            Assert.Equal(new ImportedKeybinding("ctrl+shift+p", "commandPalette", null), result.Keybindings[0]);
            Assert.Equal(new ImportedKeybinding("ctrl+k ctrl+s", "quickOpen", "editorTextFocus"), result.Keybindings[1]);
            Assert.Equal(4, result.SkippedCount);
        }
        finally { DeleteDirectory(path); }
    }

    [Fact]
    public async Task Import_RejectsNonArrayJson()
    {
        var path = await CreateFileAsync("{ \"key\": \"ctrl+s\" }");
        try { Assert.Throws<InvalidDataException>(() => VsCodeKeybindingImporter.Import(path)); }
        finally { DeleteDirectory(path); }
    }

    [Fact]
    public async Task Import_RejectsOversizedFileBeforeParsing()
    {
        var path = await CreateFileAsync(new string(' ', VsCodeKeybindingImporter.MaximumFileBytes + 1));
        try { Assert.Throws<InvalidDataException>(() => VsCodeKeybindingImporter.Import(path)); }
        finally { DeleteDirectory(path); }
    }

    [Fact]
    public async Task Import_LastBindingWinsForSameKeyAndContext()
    {
        var path = await CreateFileAsync("""
            [
              { "key": "ctrl+shift+p", "command": "workbench.action.quickOpen" },
              { "key": "CTRL+SHIFT+P", "command": "workbench.action.showCommands" }
            ]
            """);
        try
        {
            var result = VsCodeKeybindingImporter.Import(path);
            var binding = Assert.Single(result.Keybindings);
            Assert.Equal("commandPalette", binding.Command);
            Assert.Equal(1, result.OverriddenCount);
            Assert.Equal(0, result.SkippedCount);
        }
        finally { DeleteDirectory(path); }
    }

    [Fact]
    public async Task SaveAndLoad_PersistOnlyWhitelistedBindingsInOrder()
    {
        var source = await CreateFileAsync("""
            [
              { "key": "ctrl+s", "command": "workbench.action.files.save" },
              { "key": "ctrl+shift+p", "command": "workbench.action.showCommands" }
            ]
            """);
        var storage = Path.Combine(Path.GetDirectoryName(source)!, "KeyceCode", "keybindings.json");
        try
        {
            var imported = VsCodeKeybindingImporter.Import(source);
            VsCodeKeybindingImporter.SaveImported(imported, storage);
            var loaded = VsCodeKeybindingImporter.LoadSaved(storage);
            Assert.Equal(imported.Keybindings, loaded);
        }
        finally { DeleteDirectory(source); }
    }

    private static async Task<string> CreateFileAsync(string content)
    {
        var root = Path.Combine(Path.GetTempPath(), "KeyceCode-Keybindings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "keybindings.json");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
