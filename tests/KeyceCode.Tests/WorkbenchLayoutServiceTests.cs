using KeyceCode.Services;
using Xunit;

namespace KeyceCode.Tests;

public sealed class WorkbenchLayoutServiceTests
{
    [Fact]
    public void SaveAndLoad_PreservesWorkbenchArrangement()
    {
        var root = Path.Combine(Path.GetTempPath(), "KeyceCode-LayoutTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new WorkbenchLayoutService(Path.Combine(root, "layout.json"));
            var expected = new WorkbenchLayout
            {
                WindowWidth = 1320,
                WindowHeight = 820,
                WindowMaximized = true,
                SidebarVisible = false,
                SidebarWidth = 345,
                PanelVisible = true,
                PanelHeight = 310,
                AssistantVisible = true,
                AssistantWidth = 410,
                ActiveView = "SourceControl",
                ActivePanel = "Problems"
            };

            service.Save(expected);

            Assert.Equal(expected, service.Load());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Load_ClampsInvalidDimensionsAndIgnoresUnknownViews()
    {
        var root = Path.Combine(Path.GetTempPath(), "KeyceCode-LayoutTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "layout.json");
            File.WriteAllText(path, """{"WindowWidth":-4,"SidebarWidth":9000,"ActiveView":"Unknown","ActivePanel":"Unknown"}""");

            var layout = new WorkbenchLayoutService(path).Load();

            Assert.Equal(980, layout.WindowWidth);
            Assert.Equal(1200, layout.SidebarWidth);
            Assert.Equal("Explorer", layout.ActiveView);
            Assert.Equal("Terminal", layout.ActivePanel);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
