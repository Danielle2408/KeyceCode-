using System.Text.Json;
using KeyceCode.Services.Lsp;
using Xunit;

namespace KeyceCode.Tests;

public sealed class LspWorkspaceEditServiceTests
{
    [Fact]
    public void Parse_ValidatesUriAndAppliesUtf16RangesOnCrLfText()
    {
        var root = NewWorkspace();
        try
        {
            var path = Path.Combine(root, "sample.cs");
            File.WriteAllText(path, "class Old\r\n{\r\n}");
            using var edit = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                changes = new Dictionary<string, object[]>
                {
                    [new Uri(path).AbsoluteUri] = [new { range = new { start = new { line = 0, character = 6 }, end = new { line = 0, character = 9 } }, newText = "New" }]
                }
            }));

            var document = Assert.Single(LspWorkspaceEditService.Parse(edit.RootElement, root));
            Assert.Equal(Path.GetFullPath(path), document.Path);
            Assert.Equal("class New\r\n{\r\n}", LspWorkspaceEditService.ApplyTextEdits(File.ReadAllText(path), document.Edits));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Parse_AppliesEditsAcrossMultipleFilesAndCountsUtf16CodeUnits()
    {
        var root = NewWorkspace();
        try
        {
            var firstPath = Path.Combine(root, "first.cs");
            var secondPath = Path.Combine(root, "second.cs");
            File.WriteAllText(firstPath, "😀x");
            File.WriteAllText(secondPath, "Old");
            using var edit = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                changes = new Dictionary<string, object[]>
                {
                    [new Uri(firstPath).AbsoluteUri] = [new { range = new { start = new { line = 0, character = 2 }, end = new { line = 0, character = 3 } }, newText = "y" }],
                    [new Uri(secondPath).AbsoluteUri] = [new { range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 3 } }, newText = "New" }]
                }
            }));

            var changes = LspWorkspaceEditService.Parse(edit.RootElement, root);
            Assert.Equal(2, changes.Count);
            Assert.Equal("😀y", LspWorkspaceEditService.ApplyTextEdits(File.ReadAllText(firstPath),
                changes.Single(change => change.Path == Path.GetFullPath(firstPath)).Edits));
            Assert.Equal("New", LspWorkspaceEditService.ApplyTextEdits(File.ReadAllText(secondPath),
                changes.Single(change => change.Path == Path.GetFullPath(secondPath)).Edits));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Parse_RejectsFileUrisOutsideWorkspace()
    {
        var root = NewWorkspace();
        var outside = Path.Combine(Path.GetTempPath(), "KeyceCode-outside-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(outside, "class Outside {}");
        try
        {
            using var edit = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                changes = new Dictionary<string, object[]>
                {
                    [new Uri(outside).AbsoluteUri] = [new { range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } }, newText = "// changed\n" }]
                }
            }));

            Assert.Throws<UnauthorizedAccessException>(() => LspWorkspaceEditService.Parse(edit.RootElement, root));
            Assert.Equal("class Outside {}", File.ReadAllText(outside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(outside);
        }
    }

    [Fact]
    public void ApplyTextEdits_RejectsOverlappingRanges()
    {
        using var edits = JsonDocument.Parse("""
            [
              { "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 3 } }, "newText": "x" },
              { "range": { "start": { "line": 0, "character": 2 }, "end": { "line": 0, "character": 4 } }, "newText": "y" }
            ]
            """);
        var cloned = edits.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
        Assert.Throws<InvalidDataException>(() => LspWorkspaceEditService.ApplyTextEdits("abcdef", cloned));
    }

    private static string NewWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "KeyceCode-LspEdits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
