using KeyceCode.Services.Lsp;
using System.Text.Json;
using Xunit;

namespace KeyceCode.Tests;

public sealed class LspWorkspaceSymbolServiceTests
{
    [Fact]
    public async Task Parse_ValidatesLocationAndKeepsOnlyFilesInsideWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "KeyceCode-WorkspaceSymbols-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "src", "File.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "class File { }");
        var outside = Path.Combine(Path.GetTempPath(), "Outside.cs");
        try
        {
            using var response = JsonDocument.Parse(JsonSerializer.Serialize(new object[]
            {
                new { name = "File", kind = 5, containerName = "Demo", location = new { uri = new Uri(file).AbsoluteUri, range = new { start = new { line = 4, character = 2 } } } },
                new { name = "Outside", kind = 5, location = new { uri = new Uri(outside).AbsoluteUri, range = new { start = new { line = 0, character = 0 } } } },
                new { name = "Remote", kind = 5, location = new { uri = "https://example.invalid/file.cs", range = new { start = new { line = 0, character = 0 } } } },
                new { name = "Malformed", kind = 5, location = new { uri = new Uri(file).AbsoluteUri, range = new { start = new { line = -1, character = 0 } } } }
            }));

            var symbols = LspWorkspaceSymbolService.Parse(response.RootElement, root);

            var symbol = Assert.Single(symbols);
            Assert.Equal("File", symbol.Name);
            Assert.Equal("Demo", symbol.ContainerName);
            Assert.Equal(4, symbol.Line);
            Assert.Equal("src/File.cs", symbol.Label(root).Split(" · ", StringSplitOptions.None)[^1].Split(':')[0]);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void Parse_ReturnsNoResultsForNonArrayResponse()
    {
        using var response = JsonDocument.Parse("{}");
        Assert.Empty(LspWorkspaceSymbolService.Parse(response.RootElement, Path.GetTempPath()));
    }
}
