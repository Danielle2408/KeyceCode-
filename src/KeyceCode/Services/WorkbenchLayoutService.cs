using System.IO;
using System.Text.Json;

namespace KeyceCode.Services;

public sealed record WorkbenchLayout
{
    public double WindowWidth { get; init; } = 1480;
    public double WindowHeight { get; init; } = 900;
    public bool WindowMaximized { get; init; }
    public bool SidebarVisible { get; init; } = true;
    public double SidebarWidth { get; init; } = 250;
    public bool PanelVisible { get; init; } = true;
    public double PanelHeight { get; init; } = 230;
    public bool AssistantVisible { get; init; }
    public double AssistantWidth { get; init; } = 330;
    public string ActiveView { get; init; } = "Explorer";
    public string ActivePanel { get; init; } = "Terminal";
}

/// <summary>Persists the user's workbench arrangement independently of the active workspace.</summary>
public sealed class WorkbenchLayoutService
{
    private readonly string _path;

    public WorkbenchLayoutService(string? path = null) => _path = Path.GetFullPath(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyceCode", "workbench-layout.json"));

    public WorkbenchLayout Load()
    {
        try
        {
            if (!File.Exists(_path)) return new WorkbenchLayout();
            using var stream = File.OpenRead(_path);
            var layout = JsonSerializer.Deserialize<WorkbenchLayout>(stream) ?? new WorkbenchLayout();
            return Sanitize(layout);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new WorkbenchLayout();
        }
    }

    public void Save(WorkbenchLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, Sanitize(layout), new JsonSerializerOptions { WriteIndented = true });
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static WorkbenchLayout Sanitize(WorkbenchLayout layout) => layout with
    {
        WindowWidth = Clamp(layout.WindowWidth, 980, 10000, 1480),
        WindowHeight = Clamp(layout.WindowHeight, 620, 10000, 900),
        SidebarWidth = Clamp(layout.SidebarWidth, 180, 1200, 250),
        PanelHeight = Clamp(layout.PanelHeight, 100, 1200, 230),
        AssistantWidth = Clamp(layout.AssistantWidth, 240, 1200, 330),
        ActiveView = layout.ActiveView is "Explorer" or "Search" or "SourceControl" or "Run" or "Extensions" ? layout.ActiveView : "Explorer",
        ActivePanel = layout.ActivePanel is "Terminal" or "Problems" or "Output" or "DebugConsole" ? layout.ActivePanel : "Terminal"
    };

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
