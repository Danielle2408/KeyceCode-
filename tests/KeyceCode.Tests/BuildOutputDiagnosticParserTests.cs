using KeyceCode.Services;
using Xunit;

namespace KeyceCode.Tests;

public sealed class BuildOutputDiagnosticParserTests
{
    [Fact]
    public void Append_ParsesRoslynDiagnosticsAcrossChunksAndAnsiOutput()
    {
        var parser = new BuildOutputDiagnosticParser();
        Assert.Empty(parser.Append("\u001b[31mC:\\work\\Program.cs(12,"));

        var parsed = parser.Append("7): error CS1002: ; expected [KeyceCode]\u001b[0m\r\n" +
                                   "C:\\work\\Other.cs(3,2): warning CS0219: unused variable\n");

        Assert.Collection(parsed,
            error =>
            {
                Assert.Equal("Error", error.Severity);
                Assert.Equal("C:\\work\\Program.cs", error.File);
                Assert.Equal(12, error.Line);
                Assert.Equal(7, error.Column);
                Assert.Equal("CS1002", error.Code);
                Assert.Equal("; expected", error.Message);
            },
            warning =>
            {
                Assert.Equal("Warning", warning.Severity);
                Assert.Equal("C:\\work\\Other.cs", warning.File);
                Assert.Equal(3, warning.Line);
                Assert.Equal(2, warning.Column);
                Assert.Equal("CS0219", warning.Code);
                Assert.Equal("unused variable", warning.Message);
            });
    }

    [Fact]
    public void Append_IgnoresNonDiagnosticsAndBoundsOversizedLines()
    {
        var parser = new BuildOutputDiagnosticParser();
        var parsed = parser.Append("ordinary output\n" + new string('x', 9000) + "\n" +
                                   "main.cs(4,1): error CS1001: Identifier expected\n");

        var diagnostic = Assert.Single(parsed);
        Assert.Equal(4, diagnostic.Line);
        Assert.Equal("main.cs", diagnostic.File);
    }

    [Fact]
    public void Clear_DiscardsAnIncompleteLine()
    {
        var parser = new BuildOutputDiagnosticParser();
        parser.Append("file.cs(1,1): error CS1002: ");
        parser.Clear();

        Assert.Empty(parser.Append("not a diagnostic\n"));
    }
}
