using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace KeyceCode.Services;

public sealed record InstalledExtension(string Id, string DisplayName, string Version, string Path);
public sealed record VsCodeExtensionImportResult(IReadOnlyList<InstalledExtension> Imported, IReadOnlyList<string> Skipped);
public sealed record VsixInstallProgress(string Phase, long BytesProcessed, long TotalBytes);

/// <summary>Installs VSIX archives as inert files. This service never loads or starts extension code.</summary>
public sealed class VsixExtensionService
{
    private const int MaximumEntries = 20_000;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private readonly string _extensionRoot;

    public VsixExtensionService(string? extensionRoot = null)
    {
        _extensionRoot = Path.GetFullPath(extensionRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".keycecode", "extensions"));
    }

    public string ExtensionRoot => _extensionRoot;

    /// <summary>Copies installed VS Code extension packages into KeyceCode's inert VSIX store.</summary>
    public async Task<VsCodeExtensionImportResult> ImportFromVsCodeAsync(string sourceRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        var root = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        EnsureNotReparsePoint(root);
        var temporaryRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyceCode", "extensions", "import-staging");
        Directory.CreateDirectory(temporaryRoot);
        var imported = new List<InstalledExtension>();
        var skipped = new List<string>();

        foreach (var extensionDirectory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderName = Path.GetFileName(extensionDirectory);
            if (folderName.Equals(".obsolete", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(Path.Combine(extensionDirectory, "package.json"))) continue;
            var archivePath = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N") + ".vsix");
            try
            {
                CreateVsixFromInstalledFolder(extensionDirectory, archivePath, cancellationToken);
                imported.Add(await InstallAsync(archivePath, cancellationToken));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                skipped.Add($"{folderName}: {ex.Message}");
            }
            finally
            {
                try { File.Delete(archivePath); } catch (IOException) { }
            }
        }
        return new VsCodeExtensionImportResult(imported, skipped);
    }

    private static void CreateVsixFromInstalledFolder(string source, string archivePath, CancellationToken cancellationToken)
    {
        using var archiveFile = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(archiveFile, ZipArchiveMode.Create, leaveOpen: false);
        var pending = new Stack<string>();
        pending.Push(source);
        var entries = 0;
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            EnsureNotReparsePoint(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNotReparsePoint(path);
                if (Directory.Exists(path)) { pending.Push(path); continue; }
                var info = new FileInfo(path);
                totalBytes = checked(totalBytes + info.Length);
                if (totalBytes > MaximumExpandedBytes) throw new InvalidDataException("L’extension dépasse la limite de 512 Mo.");
                if (++entries > MaximumEntries) throw new InvalidDataException("L’extension dépasse la limite de 20 000 fichiers.");
                var relative = Path.GetRelativePath(source, path).Replace(Path.DirectorySeparatorChar, '/');
                archive.CreateEntryFromFile(path, "extension/" + relative, CompressionLevel.Fastest);
            }
        }
        if (entries == 0) throw new InvalidDataException("Le dossier d’extension est vide.");
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Les liens et points de réanalyse ne sont pas importés.");
    }

    public async Task<InstalledExtension> InstallAsync(string vsixPath, CancellationToken cancellationToken = default,
        IProgress<VsixInstallProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vsixPath);
        progress?.Report(new("Validation du paquet VSIX…", 0, 0));
        var source = Path.GetFullPath(vsixPath);
        if (!File.Exists(source) || !source.EndsWith(".vsix", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Sélectionnez un fichier VSIX existant.");

        Directory.CreateDirectory(_extensionRoot);
        var stagingRoot = Path.Combine(_extensionRoot, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            string manifestJson;
            await using (var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false))
            {
                if (archive.Entries.Count is 0 or > MaximumEntries)
                    throw new InvalidDataException("Le VSIX contient un nombre d’entrées invalide.");

                var manifest = archive.GetEntry("extension/package.json")
                    ?? throw new InvalidDataException("Le VSIX ne contient pas extension/package.json.");
                if (manifest.Length > 2 * 1024 * 1024)
                    throw new InvalidDataException("Le manifeste de l’extension dépasse 2 Mo.");
                await using (var manifestStream = manifest.Open())
                using (var reader = new StreamReader(manifestStream))
                    manifestJson = await reader.ReadToEndAsync(cancellationToken);

                var metadata = ParseManifest(manifestJson);
                var destinationName = $"{metadata.Id}-{metadata.Version}";
                var destination = SafeChildPath(_extensionRoot, destinationName);
                if (Directory.Exists(destination) || File.Exists(destination))
                    throw new IOException($"L’extension {metadata.Id}@{metadata.Version} est déjà installée.");

                var expandedTotal = archive.Entries.Aggregate(0L, (total, entry) => checked(total + entry.Length));
                if (expandedTotal > MaximumExpandedBytes)
                    throw new InvalidDataException("Le contenu décompressé du VSIX dépasse 512 Mo.");
                progress?.Report(new("Installation du VSIX…", 0, expandedTotal));
                long expandedCopied = 0;
                var lastProgress = Environment.TickCount64;
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectLink(entry);
                    var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var target = SafeChildPath(stagingRoot, relative);
                    if (entry.Name.Length == 0)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var input = entry.Open();
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    var buffer = new byte[81920];
                    long copied = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                    {
                        copied += read;
                        if (copied > entry.Length || copied > MaximumExpandedBytes)
                            throw new InvalidDataException("Une entrée VSIX dépasse sa taille déclarée.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        expandedCopied += read;
                        if (Environment.TickCount64 - lastProgress >= 100 || expandedCopied == expandedTotal)
                        {
                            progress?.Report(new("Installation du VSIX…", expandedCopied, expandedTotal));
                            lastProgress = Environment.TickCount64;
                        }
                    }
                    if (copied != entry.Length)
                        throw new InvalidDataException("La taille d’une entrée VSIX ne correspond pas à l’archive.");
                }

                // Ensure package metadata stays in the extraction tree; we install the complete VSIX unchanged.
                if (!File.Exists(Path.Combine(stagingRoot, "extension", "package.json")))
                    throw new InvalidDataException("Le manifeste est absent après extraction.");
                Directory.Move(stagingRoot, destination);
                progress?.Report(new("Extension installée.", expandedTotal, expandedTotal));
                return new InstalledExtension(metadata.Id, metadata.DisplayName, metadata.Version, destination);
            }
        }
        catch (InvalidDataException) { throw; }
        finally
        {
            if (Directory.Exists(stagingRoot))
                try { Directory.Delete(stagingRoot, recursive: true); } catch (IOException) { }
        }
    }

    public IReadOnlyList<InstalledExtension> GetInstalled()
    {
        if (!Directory.Exists(_extensionRoot)) return Array.Empty<InstalledExtension>();
        var result = new List<InstalledExtension>();
        foreach (var extensionDirectory in Directory.EnumerateDirectories(_extensionRoot))
        {
            var manifestPath = Path.Combine(extensionDirectory, "extension", "package.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var metadata = ParseManifest(File.ReadAllText(manifestPath));
                result.Add(new InstalledExtension(metadata.Id, metadata.DisplayName, metadata.Version, extensionDirectory));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { }
        }
        return result.OrderBy(extension => extension.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(extension => extension.Version, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static (string Id, string DisplayName, string Version) ParseManifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        static string? Read(JsonElement node, string key) =>
            node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var publisher = Read(root, "publisher");
        var name = Read(root, "name");
        var version = Read(root, "version");
        if (!IsSafeIdentifier(publisher) || !IsSafeIdentifier(name) || !IsSafeIdentifier(version))
            throw new InvalidDataException("Le manifeste VSIX doit contenir un publisher, name et version sûrs.");
        return ($"{publisher}.{name}", Read(root, "displayName") ?? name!, version!);
    }

    private static bool IsSafeIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static string SafeChildPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("L’archive contient un chemin absolu invalide.");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("L’archive tente de sortir de son dossier d’installation.");
        return full;
    }

    private static void RejectLink(ZipArchiveEntry entry)
    {
        // Unix ZIP mode: the high 16 bits contain the POSIX file type; symbolic links are 0120000.
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType == 0xA000)
            throw new InvalidDataException("Les liens symboliques dans un VSIX ne sont pas acceptés.");
    }
}
