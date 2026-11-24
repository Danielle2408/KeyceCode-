using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KeyceCode.Services;

/// <summary>A file or directory displayed in the workspace explorer.</summary>
public sealed record WorkspaceEntry(
    string Name,
    string RelativePath,
    string FullPath,
    bool IsDirectory,
    string Icon,
    string Language,
    IReadOnlyList<WorkspaceEntry> Children);

/// <summary>Provides bounded file access for the currently opened workspace.</summary>
public sealed class WorkspaceService
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules", ".idea"
    };

    private readonly string _root;

    public WorkspaceService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException($"Workspace folder not found: {_root}");
    }

    public string RootPath => _root;

    public Task<IReadOnlyList<WorkspaceEntry>> GetEntriesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<WorkspaceEntry>>(() => ReadDirectory(_root, cancellationToken), cancellationToken);

    public Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var safePath = ResolvePath(path);
        if (!File.Exists(safePath))
            throw new FileNotFoundException("File not found in the workspace.", safePath);
        return File.ReadAllTextAsync(safePath, Encoding.UTF8, cancellationToken);
    }

    public async Task WriteFileAsync(string path, string content, Encoding? encoding = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var safePath = ResolvePath(path);
        var parent = Path.GetDirectoryName(safePath)!;
        Directory.CreateDirectory(parent);
        // Recheck after creating parents in case a path component was replaced by a link.
        safePath = ResolvePath(path);
        await File.WriteAllTextAsync(safePath, content, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }

    public string CreateFile(string path)
    {
        var safePath = ResolvePath(path);
        ValidateEntryName(Path.GetFileName(path));
        if (!Directory.Exists(Path.GetDirectoryName(safePath)))
            throw new DirectoryNotFoundException("The parent folder does not exist.");
        if (File.Exists(safePath) || Directory.Exists(safePath))
            throw new IOException("An entry with that name already exists.");
        using (File.Create(safePath)) { }
        return safePath;
    }

    public string CreateDirectory(string path)
    {
        var safePath = ResolvePath(path);
        ValidateEntryName(Path.GetFileName(path));
        if (!Directory.Exists(Path.GetDirectoryName(safePath)))
            throw new DirectoryNotFoundException("The parent folder does not exist.");
        if (File.Exists(safePath) || Directory.Exists(safePath))
            throw new IOException("An entry with that name already exists.");
        Directory.CreateDirectory(safePath);
        return safePath;
    }

    public string RenameEntry(string path, string newName)
    {
        ValidateEntryName(newName);

        var source = ResolvePath(path);
        if (string.Equals(source, _root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The workspace root cannot be renamed here.");
        if (!File.Exists(source) && !Directory.Exists(source)) throw new FileNotFoundException("The entry no longer exists.", source);
        var target = ResolvePath(Path.Combine(Path.GetDirectoryName(source)!, newName));
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("An entry with that name already exists.");
        if (Directory.Exists(source)) Directory.Move(source, target);
        else File.Move(source, target);
        return target;
    }

    private static void ValidateEntryName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".." || Path.GetFileName(name) != name ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Enter a valid name without a directory path.", nameof(name));
    }

    public void DeleteEntry(string path)
    {
        var safePath = ResolvePath(path);
        if (string.Equals(safePath, _root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The workspace root cannot be deleted.");
        if (Directory.Exists(safePath)) Directory.Delete(safePath, recursive: true);
        else if (File.Exists(safePath)) File.Delete(safePath);
        else throw new FileNotFoundException("The entry no longer exists.", safePath);
    }

    public string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var candidate = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path));
        var relative = Path.GetRelativePath(_root, candidate);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The requested path is outside the workspace.");

        // Do not follow symbolic links/reparse points: lexical containment alone is not sufficient.
        var current = _root;
        foreach (var part in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Workspace symbolic links are not accessible.");
            }
        }
        return candidate;
    }

    public static (string Icon, string Language) GetFileKind(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension switch
        {
            ".cs" => ("C#", "csharp"),
            ".xaml" => ("XML", "xml"),
            ".json" or ".jsonc" => ("{}", "json"),
            ".xml" => ("XML", "xml"),
            ".html" or ".htm" => ("HTML", "html"),
            ".php" or ".php4" or ".php5" or ".phtml" or ".ctp" => ("PHP", "php"),
            ".css" => ("#", "css"),
            ".js" or ".mjs" => ("JS", "javascript"),
            ".ts" or ".tsx" => ("TS", "typescript"),
            ".py" => ("Py", "python"),
            ".md" => ("M", "markdown"),
            ".sql" => ("DB", "sql"),
            ".ps1" => (">_", "powershell"),
            ".yml" or ".yaml" => ("Y", "yaml"),
            ".txt" => ("TXT", "plaintext"),
            _ => ("FILE", "plaintext")
        };
    }

    private IReadOnlyList<WorkspaceEntry> ReadDirectory(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<WorkspaceEntry>();
        foreach (var childPath in Directory.EnumerateFileSystemEntries(directory)
                     .OrderBy(p => Directory.Exists(p) ? 0 : 1)
                     .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(childPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var name = Path.GetFileName(childPath);
            if (isDirectory && IgnoredDirectories.Contains(name))
                continue;

            var relative = Path.GetRelativePath(_root, childPath);
            var kind = isDirectory ? ("DIR", "") : GetFileKind(name);
            IReadOnlyList<WorkspaceEntry> children = isDirectory
                ? ReadDirectory(childPath, cancellationToken)
                : Array.Empty<WorkspaceEntry>();
            result.Add(new WorkspaceEntry(name, relative, childPath, isDirectory, kind.Item1, kind.Item2, children));
        }
        return result;
    }
}
