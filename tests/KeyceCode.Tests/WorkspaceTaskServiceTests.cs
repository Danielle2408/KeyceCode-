using KeyceCode.Services;
using System.Text.Json;
using Xunit;

namespace KeyceCode.Tests;

public sealed class WorkspaceTaskServiceTests
{
    [Fact]
    public async Task Read_ParsesShellArgumentsAndMatcherLists()
    {
        var root = NewDirectory();
        try
        {
            var config = Directory.CreateDirectory(Path.Combine(root, ".vscode"));
            await File.WriteAllTextAsync(Path.Combine(config.FullName, "tasks.json"), """
                {
                  "version": "2.0.0",
                  "tasks": [{
                    "label": "Build project",
                    "type": "shell",
                    "command": "dotnet",
                    "args": ["build", "My Project.csproj", "--configuration", "Release"],
                    "options": { "cwd": "${workspaceFolder}/src" },
                    "problemMatcher": ["$msCompile", "$tsc"]
                  }]
                }
                """);

            var task = Assert.Single(WorkspaceTaskService.Read(root));

            Assert.Equal("Build project", task.Label);
            Assert.Equal("dotnet", task.Command);
            Assert.Equal(new[] { "build", "My Project.csproj", "--configuration", "Release" }, task.Arguments);
            Assert.Equal("${workspaceFolder}/src", task.WorkingDirectory);
            Assert.Equal("$msCompile", task.ProblemMatcher);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExpandVariables_ExpandsWorkspaceFileAndEnvironmentVariables()
    {
        var root = NewDirectory();
        try
        {
            var source = Path.Combine(root, "src", "Hello World.cs");
            var result = WorkspaceTaskService.ExpandVariables(
                "${workspaceFolderBasename}|${file}|${relativeFile}|${fileDirname}|${fileBasenameNoExtension}|${fileExtname}|${env:DOTNET_ROOT}|${pathSeparator}",
                root, source, new Dictionary<string, string?> { ["DOTNET_ROOT"] = "C:\\dotnet" });

            Assert.Equal($"{Path.GetFileName(root)}|{source}|src{Path.DirectorySeparatorChar}Hello World.cs|{Path.GetDirectoryName(source)}|Hello World|.cs|C:\\dotnet|{Path.DirectorySeparatorChar}", result);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Read_RejectsNonStringArguments()
    {
        var root = NewDirectory();
        try
        {
            var config = Directory.CreateDirectory(Path.Combine(root, ".vscode"));
            File.WriteAllText(Path.Combine(config.FullName, "tasks.json"), """
                { "tasks": [{ "label": "Invalid", "command": "dotnet", "args": [4] }] }
                """);

            Assert.Throws<JsonException>(() => WorkspaceTaskService.Read(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "KeyceCode-WorkspaceTasks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
