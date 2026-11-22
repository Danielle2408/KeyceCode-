using System.Text.Json;
using KeyceCode.Services;
using Xunit;

namespace KeyceCode.Tests;

public sealed class WorkspaceSessionServiceTests
{
    [Fact]
    public void AddRecentWorkspace_PutsWorkspaceFirstDeduplicatesAndKeepsAtMostTen()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = Enumerable.Range(0, 12)
                .Select(index => Directory.CreateDirectory(Path.Combine(root.FullName, $"workspace-{index}")).FullName)
                .ToArray();

            var recent = WorkspaceSessionService.AddRecentWorkspace(new[] { paths[11], paths[11] }.Concat(paths), paths[11]);

            Assert.Equal(WorkspaceSessionService.MaximumRecentWorkspaces, recent.Count);
            Assert.Equal(paths[11], recent[0]);
            Assert.Equal(recent.Count, recent.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.DoesNotContain(paths[9], recent);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void Load_LegacySessionAddsItsWorkspaceToRecentList()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "project")).FullName;
            var sessionPath = Path.Combine(root.FullName, "workspace-session.json");
            File.WriteAllText(sessionPath, JsonSerializer.Serialize(new
            {
                WorkspacePath = workspace,
                OpenFiles = Array.Empty<string>(),
                ActiveFile = (string?)null
            }));

            var session = new WorkspaceSessionService(sessionPath).Load();

            Assert.Equal(workspace, Assert.Single(session.RecentWorkspaces!));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void SaveAndLoad_PersistRecentWorkspacesAndSkipDeletedFolders()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var existing = Directory.CreateDirectory(Path.Combine(root.FullName, "existing")).FullName;
            var removed = Directory.CreateDirectory(Path.Combine(root.FullName, "removed")).FullName;
            var service = new WorkspaceSessionService(Path.Combine(root.FullName, "workspace-session.json"));
            service.Save(new WorkspaceSession(existing, [], null, [existing, removed]));
            Directory.Delete(removed);

            var session = service.Load();

            Assert.Equal(existing, Assert.Single(session.RecentWorkspaces!));
        }
        finally { root.Delete(recursive: true); }
    }
}
