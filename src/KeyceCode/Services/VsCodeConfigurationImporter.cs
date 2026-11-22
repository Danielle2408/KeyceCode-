using System.IO;
using System.Text.Json;

namespace KeyceCode.Services;

public sealed record VsCodeImportResult(EditorPreferences Preferences, string? ThemeId, int ExtensionCount, string SourcePath);

/// <summary>Imports the editor settings KeyceCode currently supports from a VS Code settings.json.</summary>
public static class VsCodeConfigurationImporter
{
    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "settings.json");

    public static VsCodeImportResult Import(string settingsPath, EditorPreferences current)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentNullException.ThrowIfNull(current);
        var fullPath = Path.GetFullPath(settingsPath);
        using var document = JsonDocument.Parse(File.ReadAllText(fullPath), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 32
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Le fichier settings.json doit contenir un objet JSON.");

        var root = document.RootElement;
        var preferences = current with
        {
            FontFamily = GetString(root, "editor.fontFamily") ?? current.FontFamily,
            FontSize = Clamp(GetInt(root, "editor.fontSize"), 8, 48, current.FontSize),
            TabSize = Clamp(GetInt(root, "editor.tabSize"), 1, 16, current.TabSize),
            InsertSpaces = GetBool(root, "editor.insertSpaces") ?? current.InsertSpaces,
            WordWrap = GetString(root, "editor.wordWrap") is { } wrapMode
                ? !string.Equals(wrapMode, "off", StringComparison.OrdinalIgnoreCase)
                : GetBool(root, "editor.wordWrap") ?? current.WordWrap,
            Minimap = GetBool(root, "editor.minimap.enabled") ?? current.Minimap,
            FontLigatures = GetBool(root, "editor.fontLigatures") ?? current.FontLigatures,
            LineNumbers = Select(GetString(root, "editor.lineNumbers"), ["on", "off", "relative", "interval"], current.LineNumbers),
            CursorStyle = Select(GetString(root, "editor.cursorStyle"), ["line", "line-thin", "block", "block-outline", "underline", "underline-thin"], current.CursorStyle),
            LineHeight = Clamp(GetInt(root, "editor.lineHeight"), 0, 100, current.LineHeight),
            RenderWhitespace = Select(GetString(root, "editor.renderWhitespace"), ["none", "boundary", "selection", "trailing", "all"], current.RenderWhitespace),
            SmoothScrolling = GetBool(root, "editor.smoothScrolling") ?? current.SmoothScrolling,
            StickyScroll = GetBool(root, "editor.stickyScroll.enabled") ?? current.StickyScroll,
            AutoSave = GetString(root, "files.autoSave") is { } autoSave
                ? !string.Equals(autoSave, "off", StringComparison.OrdinalIgnoreCase)
                : current.AutoSave,
            FormatOnSave = GetBool(root, "editor.formatOnSave") ?? current.FormatOnSave
        };

        var themeName = GetString(root, "workbench.colorTheme");
        string? themeId = themeName?.ToLowerInvariant() switch
        {
            "dark+ (default dark)" or "dark+" or "default dark+" or "dark modern" or "default dark modern" => "dark-plus",
            "light+ (default light)" or "light+" or "default light+" or "light modern" or "default light modern" => "light-plus",
            "high contrast" or "hc black" => "dark-high-contrast",
            "high contrast light" or "hc light" => "light-high-contrast",
            _ => null
        };

        var userDirectory = Path.GetDirectoryName(fullPath)!;
        var extensionDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        var extensionCount = Directory.Exists(extensionDirectory)
            ? Directory.EnumerateDirectories(extensionDirectory).Count()
            : 0;
        return new VsCodeImportResult(preferences, themeId, extensionCount, fullPath);
    }

    private static string? GetString(JsonElement root, string dottedPath)
    {
        if (!TryGet(root, dottedPath, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool? GetBool(JsonElement root, string dottedPath)
    {
        if (!TryGet(root, dottedPath, out var value)) return null;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    }

    private static int? GetInt(JsonElement root, string dottedPath) =>
        TryGet(root, dottedPath, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static bool TryGet(JsonElement root, string dottedPath, out JsonElement value)
    {
        if (root.TryGetProperty(dottedPath, out value)) return true;
        var current = root;
        foreach (var segment in dottedPath.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                value = default;
                return false;
            }
        }
        value = current;
        return true;
    }

    private static int Clamp(int? candidate, int minimum, int maximum, int fallback) =>
        candidate is { } number ? Math.Clamp(number, minimum, maximum) : fallback;

    private static string Select(string? candidate, IReadOnlyCollection<string> allowed, string fallback) =>
        candidate is not null && allowed.Contains(candidate, StringComparer.OrdinalIgnoreCase)
            ? allowed.First(value => value.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            : fallback;
}
