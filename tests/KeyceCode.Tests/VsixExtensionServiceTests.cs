using System.IO.Compression;
using System.Text;
using KeyceCode.Services;
using Xunit;

namespace KeyceCode.Tests;

public sealed class VsixExtensionServiceTests
{
    [Fact]
    public async Task InstallAsync_ExtractsValidVsixAndListsIt()
    {
        using var fixture = new VsixFixture();
        var archivePath = fixture.CreateVsix("vendor", "sample", "1.2.3");
        var service = new VsixExtensionService(fixture.ExtensionRoot);

        var installed = await service.InstallAsync(archivePath);

        Assert.Equal("vendor.sample", installed.Id);
        Assert.Equal("Sample extension", installed.DisplayName);
        Assert.Equal("1.2.3", installed.Version);
        Assert.True(File.Exists(Path.Combine(installed.Path, "extension", "package.json")));
        Assert.Single(service.GetInstalled());
    }

    [Fact]
    public async Task InstallAsync_RejectsDuplicateVersion()
    {
        using var fixture = new VsixFixture();
        var archivePath = fixture.CreateVsix("vendor", "duplicate", "1.0.0");
        var service = new VsixExtensionService(fixture.ExtensionRoot);
        await service.InstallAsync(archivePath);

        await Assert.ThrowsAsync<IOException>(() => service.InstallAsync(archivePath));
        Assert.Single(service.GetInstalled());
    }

    [Fact]
    public async Task ImportFromVsCodeAsync_CopiesInstalledExtensionAsInactivePackage()
    {
        using var fixture = new VsixFixture();
        var source = Path.Combine(fixture.Root, "vscode-extensions", "vendor.sample-1.2.3");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "package.json"),
            "{\"publisher\":\"vendor\",\"name\":\"sample\",\"version\":\"1.2.3\",\"displayName\":\"Sample extension\"}");
        await File.WriteAllTextAsync(Path.Combine(source, "main.js"), "// inert test package");
        var service = new VsixExtensionService(fixture.ExtensionRoot);

        var result = await service.ImportFromVsCodeAsync(Path.GetDirectoryName(source)!);

        var installed = Assert.Single(result.Imported);
        Assert.Empty(result.Skipped);
        Assert.Equal("vendor.sample", installed.Id);
        Assert.Equal("// inert test package", await File.ReadAllTextAsync(Path.Combine(installed.Path, "extension", "main.js")));
    }

    [Fact]
    public async Task InstallAsync_RejectsPathTraversal()
    {
        using var fixture = new VsixFixture();
        var archivePath = fixture.CreateVsix("vendor", "traversal", "1.0.0", ("extension/../../escape.txt", "outside"));
        var service = new VsixExtensionService(fixture.ExtensionRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(archivePath));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escape.txt")));
        Assert.Empty(service.GetInstalled());
    }

    [Fact]
    public async Task InstallAsync_RejectsSymbolicLinkEntry()
    {
        using var fixture = new VsixFixture();
        var archivePath = fixture.CreateVsix("vendor", "symlink", "1.0.0");
        using (var file = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Update))
        {
            var link = archive.CreateEntry("extension/link");
            link.ExternalAttributes = (0xA000 | 0x1A4) << 16; // POSIX symlink, mode 0644.
            using var writer = new StreamWriter(link.Open(), Encoding.UTF8);
            writer.Write("target");
        }

        var service = new VsixExtensionService(fixture.ExtensionRoot);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(archivePath));
        Assert.Empty(service.GetInstalled());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"publisher\":\"../vendor\",\"name\":\"bad\",\"version\":\"1.0.0\"}")]
    [InlineData("{\"publisher\":\"vendor\",\"name\":\"bad\"}")]
    public async Task InstallAsync_RejectsInvalidManifest(string manifest)
    {
        using var fixture = new VsixFixture();
        var archivePath = fixture.CreateVsix(manifestOverride: manifest);
        var service = new VsixExtensionService(fixture.ExtensionRoot);

        await Assert.ThrowsAnyAsync<Exception>(() => service.InstallAsync(archivePath));
        Assert.Empty(service.GetInstalled());
    }

    private sealed class VsixFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "KeyceCode-VsixTests-" + Guid.NewGuid().ToString("N"));
        public string ExtensionRoot => Path.Combine(Root, "installed");

        public VsixFixture() => Directory.CreateDirectory(Root);

        public string CreateVsix(string publisher = "vendor", string name = "sample", string version = "1.0.0",
            params (string Path, string Content)[] entries) => CreateVsix(publisher, name, version, null, entries);

        public string CreateVsix(string publisher = "vendor", string name = "sample", string version = "1.0.0",
            string? manifestOverride = null, params (string Path, string Content)[] entries)
        {
            var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".vsix");
            using var file = File.Create(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            var manifest = manifestOverride ?? $$"""{"publisher":"{{publisher}}","name":"{{name}}","version":"{{version}}","displayName":"Sample extension"}""";
            AddEntry(archive, "extension/package.json", manifest);
            foreach (var entry in entries) AddEntry(archive, entry.Path, entry.Content);
            return path;
        }

        private static void AddEntry(ZipArchive archive, string path, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
