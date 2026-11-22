using KeyceCode.Services;
using Xunit;

namespace KeyceCode.Tests;

public sealed class DotNetProjectLocatorTests
{
    [Fact]
    public void FindNearestProjects_ReturnsAllProjectsFromClosestAncestor()
    {
        var root = NewDirectory();
        try
        {
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(root, "src", "Feature")).FullName;
            Directory.CreateDirectory(Path.Combine(root, "src", "Feature", "Deep"));
            var first = Path.Combine(sourceDirectory, "Feature.App.csproj");
            var second = Path.Combine(sourceDirectory, "Feature.Tests.csproj");
            var parent = Path.Combine(root, "src", "Parent.csproj");
            File.WriteAllText(first, "<Project />");
            File.WriteAllText(second, "<Project />");
            File.WriteAllText(parent, "<Project />");
            var source = Path.Combine(sourceDirectory, "Deep", "Program.cs");

            Assert.Equal(new[] { first, second }, DotNetProjectLocator.FindNearestProjects(source, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindNearestProjects_DoesNotCrossWorkspaceRoot()
    {
        var root = NewDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "outside.csproj"), "<Project />");
            var nestedWorkspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var source = Path.Combine(nestedWorkspace, "Program.cs");

            Assert.Empty(DotNetProjectLocator.FindNearestProjects(source, nestedWorkspace));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "KeyceCode-ProjectLocator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
