using KeyceCode.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace KeyceCode;

public partial class MainWindow
{
    private readonly WorkbenchLayoutService _layoutService = new();
    private double _rememberedSidebarWidth = 250;
    private double _rememberedPanelHeight = 230;
    private double _rememberedPanelWidth = 320;
    private double _rememberedAssistantWidth = 330;
    private string _panelPosition = "Bottom";

    private void InitializeEditorPanelHost()
    {
        var outerGrid = (Grid)EditorRegion.Parent;
        outerGrid.Children.Remove(EditorRegion);
        outerGrid.Children.Remove(PanelSplitter);
        outerGrid.Children.Remove(PanelRegion);
        EditorPanelHost.Children.Add(EditorRegion);
        EditorPanelHost.Children.Add(PanelSplitter);
        EditorPanelHost.Children.Add(PanelRegion);
        ApplyPanelPlacement();
    }

    private void RememberPanelSize()
    {
        if (!_panelVisible) return;
        if (_panelPosition == "Bottom" && PanelRow.ActualHeight >= 100)
            _rememberedPanelHeight = PanelRow.ActualHeight;
        else if (_panelPosition != "Bottom" && PanelRegion.ActualWidth >= 220)
            _rememberedPanelWidth = PanelRegion.ActualWidth;
    }

    private void ApplyPanelPlacement()
    {
        var visible = _panelVisible;
        PanelRegion.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PanelSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PanelPositionBottomMenuItem.IsChecked = _panelPosition == "Bottom";
        PanelPositionLeftMenuItem.IsChecked = _panelPosition == "Left";
        PanelPositionRightMenuItem.IsChecked = _panelPosition == "Right";

        if (_panelPosition == "Bottom")
        {
            PanelLeftColumn.Width = new GridLength(0);
            PanelVerticalSplitterColumn.Width = new GridLength(0);
            EditorRightColumn.Width = new GridLength(1, GridUnitType.Star);
            EditorTopRow.Height = new GridLength(1, GridUnitType.Star);
            PanelHorizontalSplitterRow.Height = new GridLength(visible ? 5 : 0);
            PanelRow.Height = new GridLength(visible ? _rememberedPanelHeight : 0);
            Grid.SetColumn(EditorRegion, 2); Grid.SetRow(EditorRegion, 0); Grid.SetRowSpan(EditorRegion, 1);
            Grid.SetColumn(PanelSplitter, 2); Grid.SetRow(PanelSplitter, 1); Grid.SetRowSpan(PanelSplitter, 1);
            Grid.SetColumn(PanelRegion, 2); Grid.SetRow(PanelRegion, 2); Grid.SetRowSpan(PanelRegion, 1);
            PanelSplitter.Width = double.NaN; PanelSplitter.Height = 5;
            PanelSplitter.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            PanelSplitter.VerticalAlignment = System.Windows.VerticalAlignment.Center;
            PanelSplitter.ResizeDirection = GridResizeDirection.Rows;
        }
        else
        {
            PanelHorizontalSplitterRow.Height = new GridLength(0);
            PanelRow.Height = new GridLength(0);
            EditorTopRow.Height = new GridLength(1, GridUnitType.Star);
            if (_panelPosition == "Left")
            {
                PanelLeftColumn.Width = new GridLength(visible ? _rememberedPanelWidth : 0);
                PanelVerticalSplitterColumn.Width = new GridLength(visible ? 5 : 0);
                EditorRightColumn.Width = new GridLength(1, GridUnitType.Star);
                Grid.SetColumn(EditorRegion, 2); Grid.SetColumn(PanelRegion, 0);
            }
            else
            {
                PanelLeftColumn.Width = new GridLength(1, GridUnitType.Star);
                PanelVerticalSplitterColumn.Width = new GridLength(visible ? 5 : 0);
                EditorRightColumn.Width = new GridLength(visible ? _rememberedPanelWidth : 0);
                Grid.SetColumn(EditorRegion, 0); Grid.SetColumn(PanelRegion, 2);
            }
            Grid.SetRow(EditorRegion, 0); Grid.SetRowSpan(EditorRegion, 3);
            Grid.SetRow(PanelSplitter, 0); Grid.SetColumn(PanelSplitter, 1); Grid.SetRowSpan(PanelSplitter, 3);
            Grid.SetRow(PanelRegion, 0); Grid.SetRowSpan(PanelRegion, 3);
            PanelSplitter.Width = 5; PanelSplitter.Height = double.NaN;
            PanelSplitter.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            PanelSplitter.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
            PanelSplitter.ResizeDirection = GridResizeDirection.Columns;
        }
        PanelSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
    }

    private void RestoreWorkbenchLayout()
    {
        var layout = _layoutService.Load();
        _rememberedSidebarWidth = layout.SidebarWidth;
        _rememberedPanelHeight = layout.PanelHeight;
        _rememberedPanelWidth = layout.PanelWidth;
        _rememberedAssistantWidth = layout.AssistantWidth;
        _panelPosition = layout.PanelPosition;
        _panelVisible = layout.PanelVisible;
        ApplyPanelPlacement();
        Width = Math.Max(MinWidth, Math.Min(layout.WindowWidth, SystemParameters.WorkArea.Width));
        Height = Math.Max(MinHeight, Math.Min(layout.WindowHeight, SystemParameters.WorkArea.Height));
        ShowView(layout.ActiveView);
        ShowPanel(layout.ActivePanel);
        _sidebarVisible = layout.SidebarVisible;
        SidebarColumn.Width = layout.SidebarVisible ? new GridLength(_rememberedSidebarWidth) : new GridLength(0);
        GridSplitterColumn(2, layout.SidebarVisible);
        _panelVisible = layout.PanelVisible;
        ApplyPanelPlacement();
        _assistantVisible = layout.AssistantVisible;
        AssistantSplitterColumn.Width = layout.AssistantVisible ? new GridLength(5) : new GridLength(0);
        SecondarySidebarColumn.Width = layout.AssistantVisible ? new GridLength(_rememberedAssistantWidth) : new GridLength(0);
        AssistantGridSplitter.Visibility = layout.AssistantVisible ? Visibility.Visible : Visibility.Collapsed;
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
            PanelHeight = _panelVisible && _panelPosition == "Bottom" && PanelRow.ActualHeight > 0 ? PanelRow.ActualHeight : _rememberedPanelHeight,
            PanelPosition = _panelPosition,
            PanelWidth = _panelVisible && _panelPosition != "Bottom" && PanelRegion.ActualWidth > 0 ? PanelRegion.ActualWidth : _rememberedPanelWidth,
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
        _rememberedPanelWidth = 320;
        _panelPosition = "Bottom";
        _rememberedAssistantWidth = 330;
        ShowView("Explorer");
        ShowPanel("Terminal");
        _sidebarVisible = true;
        SidebarColumn.Width = new GridLength(_rememberedSidebarWidth);
        GridSplitterColumn(2, true);
        _panelVisible = true;
        ApplyPanelPlacement();
        _assistantVisible = false;
        AssistantSplitterColumn.Width = new GridLength(0);
        SecondarySidebarColumn.Width = new GridLength(0);
        AssistantGridSplitter.Visibility = Visibility.Collapsed;
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
