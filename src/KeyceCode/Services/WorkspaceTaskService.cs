using System.Text.Json;
using System.IO;

namespace KeyceCode.Services;

public sealed record WorkspaceTask(string Label, string Command, string? WorkingDirectory, string? ProblemMatcher);

/// <summary>Reads the portable subset of VS Code's .vscode/tasks.json.</summary>
public static class WorkspaceTaskService
{
    public static IReadOnlyList<WorkspaceTask> Read(string workspaceRoot)
    {
        var file = Path.Combine(Path.GetFullPath(workspaceRoot), ".vscode", "tasks.json");
        if (!File.Exists(file)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 32 });
        if (!document.RootElement.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array) return [];
        var results = new List<WorkspaceTask>();
        foreach (var task in tasks.EnumerateArray())
        {
            if (task.ValueKind != JsonValueKind.Object || !task.TryGetProperty("command", out var commandElement) || commandElement.ValueKind != JsonValueKind.String) continue;
            var command = commandElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(command) || command.Length > 16_000) continue;
            var label = GetString(task, "label") ?? GetString(task, "taskName") ?? command;
            string? cwd = null;
            if (task.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object) cwd = GetString(options, "cwd");
            results.Add(new WorkspaceTask(label, command, cwd, GetString(task, "problemMatcher")));
        }
        return results;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
