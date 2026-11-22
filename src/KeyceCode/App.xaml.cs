using System.Windows;
using KeyceCode.Services;

namespace KeyceCode;

public partial class App : Application
{
    public ThemeService ThemeService { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeService.Initialize(this);
        base.OnStartup(e);
    }
}
