using System.Text.Json;
using System.IO;
using System.Text.RegularExpressions;

namespace KeyceCode.Services;

public sealed record WorkspaceTask(string Label, string Command, IReadOnlyList<string> Arguments, string? WorkingDirectory, string? ProblemMatcher);

/// <summary>Reads the portable subset of VS Code's .vscode/tasks.json.</summary>
public static class WorkspaceTaskService
{
    private const int MaximumTasksFileBytes = 2 * 1024 * 1024;

    public static IReadOnlyList<WorkspaceTask> Read(string workspaceRoot)
    {
        var file = Path.Combine(Path.GetFullPath(workspaceRoot), ".vscode", "tasks.json");
        if (!File.Exists(file)) return [];
        if (new FileInfo(file).Length > MaximumTasksFileBytes) throw new InvalidDataException("tasks.json dépasse la limite de 2 Mio.");
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
            var arguments = ReadArguments(task);
            results.Add(new WorkspaceTask(label, command, arguments, cwd, GetProblemMatcher(task)));
        }
        return results;
    }

    /// <summary>Expands the commonly used VS Code task variables; unknown variables remain visible for diagnosis.</summary>
    public static string ExpandVariables(string value, string workspaceRoot, string? activeFile,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var root = Path.GetFullPath(workspaceRoot);
        var file = string.IsNullOrWhiteSpace(activeFile) ? null : Path.GetFullPath(activeFile);
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["workspaceFolder"] = root,
            ["workspaceFolderBasename"] = Path.GetFileName(Path.TrimEndingDirectorySeparator(root)),
            ["file"] = file,
            ["fileWorkspaceFolder"] = file is not null && IsWithin(file, root) ? root : null,
            ["relativeFile"] = file is not null && IsWithin(file, root) ? Path.GetRelativePath(root, file) : null,
            ["fileDirname"] = file is null ? null : Path.GetDirectoryName(file),
            ["fileBasename"] = file is null ? null : Path.GetFileName(file),
            ["fileBasenameNoExtension"] = file is null ? null : Path.GetFileNameWithoutExtension(file),
            ["fileExtname"] = file is null ? null : Path.GetExtension(file),
            ["pathSeparator"] = Path.DirectorySeparatorChar.ToString()
        };
        return Regex.Replace(value, @"\$\{(?<name>[^{}]+)\}", match =>
        {
            var name = match.Groups["name"].Value;
            if (name.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            {
                var key = name[4..];
                var envValue = environment is not null && environment.TryGetValue(key, out var custom)
                    ? custom
                    : Environment.GetEnvironmentVariable(key);
                return envValue ?? match.Value;
            }
            return variables.TryGetValue(name, out var replacement) && replacement is not null ? replacement : match.Value;
        }, RegexOptions.CultureInvariant);
    }

    private static IReadOnlyList<string> ReadArguments(JsonElement task)
    {
        if (!task.TryGetProperty("args", out var args)) return Array.Empty<string>();
        if (args.ValueKind != JsonValueKind.Array) throw new JsonException("Le champ args d’une tâche doit être un tableau de chaînes.");
        if (args.GetArrayLength() > 256) throw new JsonException("Une tâche ne peut pas contenir plus de 256 arguments.");
        var result = new List<string>();
        foreach (var item in args.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new JsonException("Chaque élément de args doit être une chaîne.");
            var argument = item.GetString() ?? string.Empty;
            if (argument.Length > 16_000) throw new JsonException("Un argument de tâche dépasse 16 Kio.");
            result.Add(argument);
        }
        return result;
    }

    private static string? GetProblemMatcher(JsonElement task)
    {
        if (!task.TryGetProperty("problemMatcher", out var matcher)) return null;
        if (matcher.ValueKind == JsonValueKind.String) return matcher.GetString();
        if (matcher.ValueKind == JsonValueKind.Array)
            foreach (var item in matcher.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) return item.GetString();
        return null;
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
