using KeyceCode.Services;
using Xunit;

namespace KeyceCode.Tests;

public sealed class VsCodeConfigurationImporterTests
{
    [Fact]
    public async Task Import_AppliesSupportedSettingsAndPreservesKeyceCodeOnlyOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "KeyceCode-VsCodeImport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, """
            {
              // VS Code permits comments in settings.json.
              "editor.fontFamily": "Cascadia Code",
              "editor.fontSize": 17,
              "editor.tabSize": 2,
              "editor.insertSpaces": false,
              "editor.wordWrap": "off",
              "editor.minimap.enabled": false,
              "editor.fontLigatures": true,
              "files.autoSave": "afterDelay",
              "editor.formatOnSave": true,
              "workbench.colorTheme": "Dark+ (default dark)"
            }
            """);
        try
        {
            var result = VsCodeConfigurationImporter.Import(settings, new EditorPreferences { GeminiInlineCompletionEnabled = true });

            Assert.Equal("Cascadia Code", result.Preferences.FontFamily);
            Assert.Equal(17, result.Preferences.FontSize);
            Assert.Equal(2, result.Preferences.TabSize);
            Assert.False(result.Preferences.InsertSpaces);
            Assert.False(result.Preferences.WordWrap);
            Assert.False(result.Preferences.Minimap);
            Assert.True(result.Preferences.FontLigatures);
            Assert.True(result.Preferences.AutoSave);
            Assert.True(result.Preferences.FormatOnSave);
            Assert.True(result.Preferences.GeminiInlineCompletionEnabled);
            Assert.Equal("dark-plus", result.ThemeId);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
