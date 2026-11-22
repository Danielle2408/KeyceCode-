using System.Text.Json;
using KeyceCode.Services.Dap;
using Xunit;

namespace KeyceCode.Tests;

public sealed class VsCodeLaunchConfigurationServiceTests
{
    [Fact]
    public void Read_ParsesCommentsAndFiltersInvalidConfigurations()
    {
        var root = NewDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".vscode"));
            File.WriteAllText(Path.Combine(root, ".vscode", "launch.json"), """
                { // comments are valid in VS Code configuration files
                  "configurations": [
                    { "name": "Python", "type": "debugpy", "request": "launch", "program": "${file}" },
                    { "name": "Bad request", "type": "debugpy", "request": "other" },
                    { "name": "No type", "request": "launch" }
                  ]
                }
                """);
            var config = Assert.Single(VsCodeLaunchConfigurationService.Read(root));
            Assert.Equal("Python", config.Name);
            Assert.Equal("debugpy", config.Type);
            Assert.Equal("${file}", config.Value.GetProperty("program").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ResolveAdapter_UsesInstalledPythonDebuggerWithExplicitInterpreter()
    {
        var profile = NewDirectory();
        var python = Path.Combine(profile, "python.exe");
        try
        {
            File.WriteAllText(python, "placeholder");
            var extensionPath = Path.Combine(profile, ".vscode", "extensions", "ms-python.debugpy-1.2.3");
            var libraryRoot = Path.Combine(extensionPath, "bundled", "libs");
            Directory.CreateDirectory(extensionPath);
            File.WriteAllText(Path.Combine(extensionPath, "package.json"), "{\"name\":\"debugpy\",\"publisher\":\"ms-python\",\"license\":\"MIT\"}");
            Directory.CreateDirectory(Path.Combine(libraryRoot, "debugpy", "adapter"));
            var adapter = VsCodeLaunchConfigurationService.ResolveAdapter("debugpy", python, profile);
            Assert.True(adapter.IsAvailable);
            Assert.Equal(python, adapter.ExecutablePath);
            Assert.Equal(new[] { "-m", "debugpy.adapter" }, adapter.Arguments);
            Assert.Equal(libraryRoot, adapter.Environment!["PYTHONPATH"]);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void ResolveAdapter_DoesNotLaunchRestrictedVsDbg()
    {
        var result = VsCodeLaunchConfigurationService.ResolveAdapter("coreclr", "python.exe", NewDirectory());
        Assert.False(result.IsAvailable);
        Assert.Contains("licence", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExpandWorkspaceVariables_HandlesNestedProperties()
    {
        var root = NewDirectory();
        try
        {
            var file = Path.Combine(root, "src", "Program.cs");
            using var source = JsonDocument.Parse("""{"program":"${file}","cwd":"${workspaceFolder}","name":"${workspaceFolderBasename}"}""");
            var expanded = VsCodeLaunchConfigurationService.ExpandWorkspaceVariables(source.RootElement, root, file);
            Assert.Equal(Path.GetFullPath(file), expanded.GetProperty("program").GetString());
            Assert.Equal(Path.GetFullPath(root), expanded.GetProperty("cwd").GetString());
            Assert.Equal(Path.GetFileName(root), expanded.GetProperty("name").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "KeyceCode-Dap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
