using System.IO;
using System.Text.Json;

namespace KeyceCode.Services;

public sealed record WorkspaceSession(string? WorkspacePath, IReadOnlyList<string> OpenFiles, string? ActiveFile);

/// <summary>Stores workspace and editor-tab restoration state for the current Windows user.</summary>
public sealed class WorkspaceSessionService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyceCode", "workspace-session.json");

    public WorkspaceSession Load()
    {
        try
        {
            if (!File.Exists(_path)) return new WorkspaceSession(null, [], null);
            return JsonSerializer.Deserialize<WorkspaceSession>(File.ReadAllText(_path))
                   ?? new WorkspaceSession(null, [], null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new WorkspaceSession(null, [], null);
        }
    }

    public void Save(WorkspaceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, overwrite: true);
    }
}
