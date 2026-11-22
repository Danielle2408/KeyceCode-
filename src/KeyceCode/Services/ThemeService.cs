using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace KeyceCode.Services;

/// <summary>Describes a bundled KeyceCode workbench palette.</summary>
public sealed record ThemeDefinition(string Id, string DisplayName, string ResourcePath, bool IsHighContrast);

/// <summary>
/// Loads a bundled resource dictionary and persists the user's selected theme outside the workspace.
/// Call Initialize before constructing the main window so static resource lookups start with the selected palette.
/// </summary>
public sealed class ThemeService
{
    private const string SettingsFileName = "theme.json";
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyceCode");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, SettingsFileName);

    private static readonly IReadOnlyList<ThemeDefinition> ThemeList =
    [
        new("dark-plus", "Dark+", "/KeyceCode;component/Themes/DarkPlus.xaml", false),
        new("keyce-night", "KeyceCode Night", "/KeyceCode;component/Themes/KeyceNight.xaml", false),
        new("light-plus", "Light+", "/KeyceCode;component/Themes/LightPlus.xaml", false),
        new("dark-high-contrast", "Dark High Contrast", "/KeyceCode;component/Themes/DarkHighContrast.xaml", true),
        new("light-high-contrast", "Light High Contrast", "/KeyceCode;component/Themes/LightHighContrast.xaml", true)
    ];

    private ResourceDictionary? _activeDictionary;

    public IReadOnlyList<ThemeDefinition> Themes => ThemeList;
    public string CurrentThemeId { get; private set; } = "dark-plus";
    public ThemeDefinition CurrentTheme => ThemeList.First(theme => theme.Id == CurrentThemeId);

    public event EventHandler<ThemeDefinition>? ThemeChanged;

    /// <summary>Applies the saved theme, or Dark+ on first launch.</summary>
    public void Initialize(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        var themeId = ReadSavedThemeId();
        ApplyTheme(application, themeId, persist: false);
    }

    /// <summary>Applies and persists a theme selection.</summary>
    public void SetTheme(Application application, string themeId)
    {
        ArgumentNullException.ThrowIfNull(application);
        ApplyTheme(application, themeId, persist: true);
    }

    private void ApplyTheme(Application application, string themeId, bool persist)
    {
        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.Invoke(() => ApplyTheme(application, themeId, persist), DispatcherPriority.Send);
            return;
        }

        var definition = ThemeList.FirstOrDefault(theme =>
            string.Equals(theme.Id, themeId, StringComparison.OrdinalIgnoreCase)) ?? ThemeList[0];
        var dictionary = new ResourceDictionary { Source = new Uri(definition.ResourcePath, UriKind.Relative) };

        // Remove legacy resources from the app-level dictionary so the selected merged dictionary owns them.
        // This also clears the original implicit Button style from the initial prototype.
        foreach (var key in ThemeResourceKeys)
            application.Resources.Remove(key);

        if (_activeDictionary is not null)
            application.Resources.MergedDictionaries.Remove(_activeDictionary);
        application.Resources.MergedDictionaries.Add(dictionary);
        _activeDictionary = dictionary;

        var changed = !string.Equals(CurrentThemeId, definition.Id, StringComparison.Ordinal);
        CurrentThemeId = definition.Id;
        if (persist) SaveThemeId(definition.Id);
        if (changed) ThemeChanged?.Invoke(this, definition);
    }

    private static readonly object[] ThemeResourceKeys =
    [
        typeof(System.Windows.Controls.Button), "AppBackground", "WindowBackground", "PanelBackground",
        "SidebarBackground", "ActivityBarBackground", "TitleBarBackground", "TabBarBackground",
        "TabActiveBackground", "StatusBarBackground", "BorderBrush", "ForegroundBrush", "MutedBrush",
        "AccentBrush", "EditorBackground", "EditorForeground", "HoverBackground", "SelectionBackground",
        "ErrorBrush", "WarningBrush", "SuccessBrush", "KeyceAccentBrush"
    ];

    private static string ReadSavedThemeId()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return "dark-plus";
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return document.RootElement.TryGetProperty("theme", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "dark-plus"
                : "dark-plus";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return "dark-plus";
        }
    }

    private static void SaveThemeId(string themeId)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(new { theme = themeId }, new JsonSerializerOptions { WriteIndented = true });
            var temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A theme selection should still take effect if roaming-profile persistence is unavailable.
        }
    }
}
