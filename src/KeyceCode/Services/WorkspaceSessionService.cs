using System.IO;
using System.Text.Json;

namespace KeyceCode.Services;

public sealed record WorkspaceSession(string? WorkspacePath, IReadOnlyList<string> OpenFiles, string? ActiveFile,
    IReadOnlyList<string>? RecentWorkspaces = null);

/// <summary>Stores workspace and editor-tab restoration state for the current Windows user.</summary>
public sealed class WorkspaceSessionService
{
    public const int MaximumRecentWorkspaces = 10;
    private readonly string _path;

    public WorkspaceSessionService(string? storagePath = null) => _path = Path.GetFullPath(storagePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyceCode", "workspace-session.json"));

    public WorkspaceSession Load()
    {
        try
        {
            if (!File.Exists(_path)) return new WorkspaceSession(null, [], null, []);
            var session = JsonSerializer.Deserialize<WorkspaceSession>(File.ReadAllText(_path))
                          ?? new WorkspaceSession(null, [], null, []);
            var recent = session.RecentWorkspaces is null
                ? NormalizeRecentWorkspaces(null, session.WorkspacePath)
                : NormalizeRecentWorkspaces(session.RecentWorkspaces);
            return session with { RecentWorkspaces = recent };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new WorkspaceSession(null, [], null, []);
        }
    }

    public void Save(WorkspaceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session = session with { RecentWorkspaces = NormalizeRecentWorkspaces(session.RecentWorkspaces) };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, overwrite: true);
    }

    public static IReadOnlyList<string> AddRecentWorkspace(IEnumerable<string>? existing, string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        return NormalizeRecentWorkspaces(existing, workspacePath);
    }

    public static IReadOnlyList<string> NormalizeRecentWorkspaces(IEnumerable<string>? workspaces, string? first = null)
    {
        var result = new List<string>(MaximumRecentWorkspaces);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? candidate)
        {
            if (result.Count >= MaximumRecentWorkspaces || string.IsNullOrWhiteSpace(candidate)) return;
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (Directory.Exists(fullPath) && seen.Add(fullPath)) result.Add(fullPath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }

        Add(first);
        if (workspaces is not null)
            foreach (var workspace in workspaces)
            {
                Add(workspace);
                if (result.Count >= MaximumRecentWorkspaces) break;
            }
        return result;
    }
}
