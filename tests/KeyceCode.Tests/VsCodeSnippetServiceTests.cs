using KeyceCode.Services;
using Xunit;

namespace KeyceCode.Tests;

public sealed class VsCodeSnippetServiceTests
{
    [Fact]
    public async Task Import_CopiesCommentedJsonAndLoadsScopedPrefixBodyAndPlaceholders()
    {
        var root = Path.Combine(Path.GetTempPath(), "KeyceCode-snippets-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Code", "User", "snippets");
        var destination = Path.Combine(root, "KeyceCode", "snippets");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "csharp.json"), """
            {
              // VS Code snippet files allow comments and trailing commas.
              "Console output": {
                "scope": "csharp",
                "prefix": ["cw", "write"],
                "body": ["Console.WriteLine(${1:value});", "$0"],
                "description": "Write a line",
              },
              "broken": { "prefix": "bad", "body": 7 }
            }
            """);
        try
        {
            var service = new VsCodeSnippetService(source, destination);
            var import = service.ImportFromVsCode();
            var snippets = service.Load();

            Assert.Equal(1, import.ImportedFiles);
            Assert.Equal(2, import.ImportedSnippets);
            Assert.Equal(1, import.SkippedSnippets);
            Assert.Equal("cw", snippets[0].Prefix);
            Assert.Equal("Console.WriteLine(${1:value});\n$0", snippets[0].Body);
            Assert.Equal("Write a line", snippets[0].Description);
            Assert.Equal(new[] { "csharp" }, snippets[0].Scopes);
            Assert.Equal("write", snippets[1].Prefix);
            Assert.True(File.Exists(Path.Combine(destination, "csharp.json")));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Import_EnforcesFileSizeLimitAndInfersScopeFromFilename()
    {
        var root = Path.Combine(Path.GetTempPath(), "KeyceCode-snippets-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "global.code-snippets.json"), "{\"hello\":{\"prefix\":\"hi\",\"body\":\"Hello\"}}");
        await File.WriteAllBytesAsync(Path.Combine(source, "large.json"), new byte[VsCodeSnippetService.MaximumFileBytes + 1]);
        try
        {
            var service = new VsCodeSnippetService(source, Path.Combine(root, "destination"));
            var result = service.ImportFromVsCode();
            var snippet = Assert.Single(service.Load());
            Assert.Equal(1, result.ImportedFiles);
            Assert.Equal(1, result.SkippedFiles);
            Assert.Empty(snippet.Scopes);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
