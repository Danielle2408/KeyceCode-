using System.IO;

namespace KeyceCode.Services;

/// <summary>Finds project files in the closest ancestor directory, staying inside the workspace.</summary>
public static class DotNetProjectLocator
{
    public static IReadOnlyList<string> FindNearestProjects(string sourcePath, string? workspaceRoot, string extension = ".csproj")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        if (!extension.StartsWith(".", StringComparison.Ordinal)) extension = "." + extension;

        var directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        var root = string.IsNullOrWhiteSpace(workspaceRoot) ? null : Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (directory is not null)
        {
            if (root is not null && !IsWithin(directory, root)) break;
            try
            {
                var projects = Directory.EnumerateFiles(directory, "*" + extension, SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
                if (projects.Length > 0) return projects;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }

            if (root is not null && string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
                break;
            directory = Directory.GetParent(directory)?.FullName;
        }
        return Array.Empty<string>();
    }

    private static bool IsWithin(string directory, string root)
    {
        var relative = Path.GetRelativePath(root, directory);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
