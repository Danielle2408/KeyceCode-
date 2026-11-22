using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KeyceCode;

/// <summary>A non-destructive choice dialog for an external edit to an open document.</summary>
internal sealed class ExternalFileChangeWindow : Window
{
    public ExternalFileChangeWindow(string name, string path, string editorText, string diskText, bool isDirty)
    {
        Title = "Fichier modifié à l’extérieur";
        Width = 960;
        Height = 600;
        MinWidth = 700;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        Foreground = Brushes.WhiteSmoke;

        var layout = new DockPanel { Margin = new Thickness(14) };
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        heading.Children.Add(new TextBlock
        {
            Text = $"« {name} » a changé sur le disque. {(isDirty ? "L’éditeur contient des modifications non enregistrées." : "Le buffer est propre, mais son contenu diffère du disque.")}",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        });
        heading.Children.Add(new TextBlock { Text = path, Foreground = Brushes.LightGray, TextTrimming = TextTrimming.CharacterEllipsis });
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var compare = new Button { Content = "Comparer", MinWidth = 100, Margin = new Thickness(5), Padding = new Thickness(10, 6, 10, 6) };
        compare.Click += (_, _) => new ExternalFileCompareWindow(name, editorText, diskText) { Owner = this }.ShowDialog();
        var reload = new Button { Content = "Recharger", MinWidth = 100, Margin = new Thickness(5), Padding = new Thickness(10, 6, 10, 6), IsDefault = true };
        reload.Click += (_, _) => DialogResult = true;
        var keep = new Button { Content = "Garder l’éditeur", MinWidth = 130, Margin = new Thickness(5), Padding = new Thickness(10, 6, 10, 6), IsCancel = true };
        keep.Click += (_, _) => DialogResult = false;
        footer.Children.Add(compare);
        footer.Children.Add(reload);
        footer.Children.Add(keep);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);

        var note = new TextBlock
        {
            Text = "Aucun contenu ne sera remplacé sans votre choix. Garder l’éditeur marque le document comme modifié; un enregistrement ultérieur remplacera la version sur disque.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.LightGray,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(note, Dock.Bottom);
        layout.Children.Add(note);
        Content = layout;
    }
}

internal sealed class ExternalFileCompareWindow : Window
{
    public ExternalFileCompareWindow(string name, string editorText, string diskText)
    {
        Title = $"Comparer — {name}";
        Width = 1100;
        Height = 700;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        Foreground = Brushes.WhiteSmoke;

        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var leftLabel = new TextBlock { Text = "ÉDITEUR", Margin = new Thickness(0, 0, 6, 6), FontWeight = FontWeights.SemiBold };
        var rightLabel = new TextBlock { Text = "DISQUE", Margin = new Thickness(6, 0, 0, 6), FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(rightLabel, 1);
        grid.Children.Add(leftLabel);
        grid.Children.Add(rightLabel);
        var left = CreateTextPane(editorText);
        var right = CreateTextPane(diskText);
        Grid.SetRow(left, 1);
        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        Content = grid;
    }

    private static TextBox CreateTextPane(string text) => new()
    {
        Text = text,
        IsReadOnly = true,
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 13,
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 24)),
        Foreground = Brushes.WhiteSmoke,
        Margin = new Thickness(0, 0, 6, 0)
    };
}
