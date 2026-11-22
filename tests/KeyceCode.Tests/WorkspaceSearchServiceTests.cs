using KeyceCode.Services;
using Xunit;

namespace KeyceCode.Tests;

public sealed class WorkspaceSearchServiceTests
{
    [Fact]
    public async Task Search_UsesGlobsWholeWordCaseAndWorkspaceExclusions()
    {
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            await File.WriteAllTextAsync(Path.Combine(root, "a.cs"), "Cat scatter cat\ncat");
            await File.WriteAllTextAsync(Path.Combine(root, "nested", "b.cs"), "Cat");
            await File.WriteAllTextAsync(Path.Combine(root, "nested", "skip.cs"), "Cat");
            await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "Cat");
            await File.WriteAllTextAsync(Path.Combine(root, "bin", "generated.cs"), "Cat");
            var service = new WorkspaceSearchService();

            var result = await service.SearchAsync(new WorkspaceService(root), new WorkspaceSearchOptions(
                "cat", "**/*.cs", "**/skip.cs", UseRegex: false, MatchCase: false, WholeWord: true));

            Assert.Equal(4, result.Hits.Count);
            Assert.Equal(new[] { "nested/b.cs", "a.cs", "a.cs", "a.cs" }, result.Hits.Select(hit => hit.RelativePath).ToArray());
            Assert.Equal(new[] { 1, 1, 1, 2 }, result.Hits.Select(hit => hit.Line).ToArray());
            Assert.Equal(new[] { 1, 1, 13, 1 }, result.Hits.Select(hit => hit.Column).ToArray());
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task PreviewReplace_IsNonDestructiveAndAppliesRegexGroupsPreservingUtf8Bom()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "sample.cs");
            var content = "class C { string value = \"cat12\"; }\r\n";
            var preamble = new byte[] { 0xEF, 0xBB, 0xBF };
            await File.WriteAllBytesAsync(path, preamble.Concat(System.Text.Encoding.UTF8.GetBytes(content)).ToArray());
            var service = new WorkspaceSearchService();
            var preview = await service.PreviewReplaceAsync(new WorkspaceService(root),
                new WorkspaceSearchOptions("cat(\\d+)", "**/*.cs", UseRegex: true), "dog$1");

            Assert.Equal(1, preview.TotalMatches);
            Assert.Contains("dog12", preview.Files.Single().Preview);
            Assert.Equal(content, System.Text.Encoding.UTF8.GetString((await File.ReadAllBytesAsync(path))[3..]));

            var applied = await service.ApplyPreviewAsync(new WorkspaceService(root), preview);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.Equal("class C { string value = \"dog12\"; }\r\n", System.Text.Encoding.UTF8.GetString(bytes[3..]));
            Assert.Equal(1, applied.FilesChanged);
            Assert.Equal(1, applied.MatchesReplaced);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyPreviewAsync(new WorkspaceService(root), preview));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ApplyPreview_RejectsStaleFilesWithoutChangingOtherTargets()
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "old\n");
            var second = Path.Combine(root, "b.txt");
            await File.WriteAllTextAsync(second, "old\n");
            var service = new WorkspaceSearchService();
            var preview = await service.PreviewReplaceAsync(new WorkspaceService(root), new WorkspaceSearchOptions("old"), "new");
            await File.WriteAllTextAsync(second, "changed externally\n");

            await Assert.ThrowsAsync<IOException>(() => service.ApplyPreviewAsync(new WorkspaceService(root), preview));
            Assert.Equal("old\n", await File.ReadAllTextAsync(Path.Combine(root, "a.txt")));
            Assert.Equal("changed externally\n", await File.ReadAllTextAsync(second));
        }
        finally { DeleteRoot(root); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "KeyceCode-Search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
