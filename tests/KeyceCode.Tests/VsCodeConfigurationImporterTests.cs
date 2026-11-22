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
              "editor.lineNumbers": "relative",
              "editor.cursorStyle": "block",
              "editor.lineHeight": 22,
              "editor.renderWhitespace": "all",
              "editor.renderLineHighlight": "gutter",
              "editor.guides.indentation": false,
              "editor.guides.bracketPairs": false,
              "editor.bracketPairColorization.enabled": false,
              "editor.smoothScrolling": true,
              "editor.stickyScroll.enabled": false,
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
            Assert.Equal("relative", result.Preferences.LineNumbers);
            Assert.Equal("block", result.Preferences.CursorStyle);
            Assert.Equal(22, result.Preferences.LineHeight);
            Assert.Equal("all", result.Preferences.RenderWhitespace);
            Assert.Equal("gutter", result.Preferences.RenderLineHighlight);
            Assert.False(result.Preferences.IndentationGuides);
            Assert.False(result.Preferences.BracketPairGuides);
            Assert.False(result.Preferences.BracketPairColorization);
            Assert.True(result.Preferences.SmoothScrolling);
            Assert.False(result.Preferences.StickyScroll);
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

    [Fact]
    public async Task Import_RejectsOversizedSettingsFileBeforeParsing()
    {
        var root = Path.Combine(Path.GetTempPath(), "KeyceCode-VsCodeImport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, new string(' ', VsCodeConfigurationImporter.MaximumSettingsBytes + 1));
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => VsCodeConfigurationImporter.Import(settings, new EditorPreferences()));
            Assert.Contains("2 Mio", exception.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Import_RejectsUnknownEditorOptionsAndClampsLineHeight()
    {
        var root = Path.Combine(Path.GetTempPath(), "KeyceCode-VsCodeImport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, """{"editor.lineNumbers":"made-up","editor.cursorStyle":"cursor","editor.lineHeight":999}""");
        try
        {
            var result = VsCodeConfigurationImporter.Import(settings, new EditorPreferences());
            Assert.Equal("on", result.Preferences.LineNumbers);
            Assert.Equal("line", result.Preferences.CursorStyle);
            Assert.Equal(100, result.Preferences.LineHeight);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
