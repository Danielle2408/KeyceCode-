using KeyceCode.Services;
using Xunit;

namespace KeyceCode.Tests;

public sealed class DotNetSolutionServiceTests
{
    [Fact]
    public void GetProjects_ParsesCSharpProjectsFromSolutionAndSkipsMissingAndNonCSharpProjects()
    {
        var root = NewDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "App"));
            Directory.CreateDirectory(Path.Combine(root, "Library"));
            var app = Path.Combine(root, "App", "App.csproj");
            var library = Path.Combine(root, "Library", "Library.csproj");
            File.WriteAllText(app, "<Project />");
            File.WriteAllText(library, "<Project />");
            var solution = Path.Combine(root, "Sample.sln");
            File.WriteAllText(solution, """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}") = "App", "App\\App.csproj", "{{A}}"
                EndProject
                Project("{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}") = "Library", "Library\\Library.csproj", "{{B}}"
                EndProject
                Project("{{00000000-0000-0000-0000-000000000000}}") = "Web", "Web\\Web.esproj", "{{C}}"
                EndProject
                Project("{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}") = "Missing", "Missing\\Missing.csproj", "{{D}}"
                EndProject
                """);

            Assert.Equal(new[] { app, library }, DotNetSolutionService.GetProjects(solution));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void GetProjects_ReadsSlnxProjectPathAttributes()
    {
        var root = NewDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "App"));
            var project = Path.Combine(root, "src", "App", "App.csproj");
            File.WriteAllText(project, "<Project />");
            var solution = Path.Combine(root, "Sample.slnx");
            File.WriteAllText(solution, "<Solution><Folder Name=\"src\"><Project Path=\"src/App/App.csproj\" /></Folder></Solution>");
            Assert.Equal(new[] { project }, DotNetSolutionService.GetProjects(solution));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "KeyceCode-Solution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
