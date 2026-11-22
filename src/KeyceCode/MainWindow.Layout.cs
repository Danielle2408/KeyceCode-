using KeyceCode.Services;
using System.IO;
using System.Windows;

namespace KeyceCode;

public partial class MainWindow
{
    private readonly WorkbenchLayoutService _layoutService = new();
    private double _rememberedSidebarWidth = 250;
    private double _rememberedPanelHeight = 230;
    private double _rememberedAssistantWidth = 330;

    private void RestoreWorkbenchLayout()
    {
        var layout = _layoutService.Load();
        _rememberedSidebarWidth = layout.SidebarWidth;
        _rememberedPanelHeight = layout.PanelHeight;
        _rememberedAssistantWidth = layout.AssistantWidth;
        Width = Math.Max(MinWidth, Math.Min(layout.WindowWidth, SystemParameters.WorkArea.Width));
        Height = Math.Max(MinHeight, Math.Min(layout.WindowHeight, SystemParameters.WorkArea.Height));
        ShowView(layout.ActiveView);
        ShowPanel(layout.ActivePanel);
        _sidebarVisible = layout.SidebarVisible;
        SidebarColumn.Width = layout.SidebarVisible ? new GridLength(_rememberedSidebarWidth) : new GridLength(0);
        GridSplitterColumn(2, layout.SidebarVisible);
        _panelVisible = layout.PanelVisible;
        PanelRow.Height = layout.PanelVisible ? new GridLength(_rememberedPanelHeight) : new GridLength(0);
        PanelSplitter.Visibility = layout.PanelVisible ? Visibility.Visible : Visibility.Collapsed;
        _assistantVisible = layout.AssistantVisible;
        SecondarySidebarColumn.Width = layout.AssistantVisible ? new GridLength(_rememberedAssistantWidth) : new GridLength(0);
        if (layout.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private WorkbenchLayout CaptureWorkbenchLayout()
    {
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);
        return new WorkbenchLayout
        {
            WindowWidth = bounds.Width,
            WindowHeight = bounds.Height,
            WindowMaximized = WindowState == WindowState.Maximized,
            SidebarVisible = _sidebarVisible,
            SidebarWidth = _sidebarVisible && SidebarColumn.ActualWidth > 0 ? SidebarColumn.ActualWidth : _rememberedSidebarWidth,
            PanelVisible = _panelVisible,
            PanelHeight = _panelVisible && PanelRow.ActualHeight > 0 ? PanelRow.ActualHeight : _rememberedPanelHeight,
            AssistantVisible = _assistantVisible,
            AssistantWidth = _assistantVisible && SecondarySidebarColumn.ActualWidth > 0 ? SecondarySidebarColumn.ActualWidth : _rememberedAssistantWidth,
            ActiveView = _activeView,
            ActivePanel = _activePanel
        };
    }

    private void ResetWorkbenchLayout_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Normal;
        var workArea = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(1480, workArea.Width));
        Height = Math.Max(MinHeight, Math.Min(900, workArea.Height));
        Left = Math.Max(0, (SystemParameters.WorkArea.Width - Width) / 2);
        Top = Math.Max(0, (SystemParameters.WorkArea.Height - Height) / 2);
        _rememberedSidebarWidth = 250;
        _rememberedPanelHeight = 230;
        _rememberedAssistantWidth = 330;
        ShowView("Explorer");
        ShowPanel("Terminal");
        _sidebarVisible = true;
        SidebarColumn.Width = new GridLength(_rememberedSidebarWidth);
        GridSplitterColumn(2, true);
        _panelVisible = true;
        PanelRow.Height = new GridLength(_rememberedPanelHeight);
        PanelSplitter.Visibility = Visibility.Visible;
        _assistantVisible = false;
        SecondarySidebarColumn.Width = new GridLength(0);
        try
        {
            _layoutService.Save(CaptureWorkbenchLayout());
            StatusMessage.Text = "Disposition réinitialisée";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage.Text = "Disposition réinitialisée en mémoire, mais impossible à enregistrer : " + ex.Message;
        }
    }
}
