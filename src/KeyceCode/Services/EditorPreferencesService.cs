using System.IO;
using System.Text.Json;

namespace KeyceCode.Services;

public sealed record EditorPreferences
{
    public string FontFamily { get; init; } = "Cascadia Code, Consolas, monospace";
    public int FontSize { get; init; } = 14;
    public int TabSize { get; init; } = 4;
    public bool InsertSpaces { get; init; } = true;
    public bool WordWrap { get; init; }
    public bool Minimap { get; init; } = true;
    public bool FontLigatures { get; init; }
    public bool AutoSave { get; init; }
    public bool FormatOnSave { get; init; }
    /// <summary>Gemini is never contacted while typing unless the user opts in.</summary>
    public bool GeminiInlineCompletionEnabled { get; init; }
}

/// <summary>Persists non-secret editor preferences in the current user's AppData directory.</summary>
public sealed class EditorPreferencesService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyceCode", "editor-preferences.json");

    public EditorPreferences Load()
    {
        try
        {
            if (!File.Exists(_path)) return new EditorPreferences();
            return JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllText(_path)) ?? new EditorPreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new EditorPreferences();
        }
    }

    public void Save(EditorPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }
}
