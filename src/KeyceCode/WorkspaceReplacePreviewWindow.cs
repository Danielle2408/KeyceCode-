using KeyceCode.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KeyceCode;

internal sealed class WorkspaceReplacePreviewWindow : Window
{
    private WorkspaceReplacePreviewWindow(WorkspaceReplacePreview preview)
    {
        Title = "Prévisualiser le remplacement";
        Width = 900;
        Height = 680;
        MinWidth = 620;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "ForegroundBrush");

        var root = new DockPanel { Margin = new Thickness(16) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        var cancel = new Button { Content = "Annuler", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        var apply = new Button { Content = preview.Truncated ? "Aperçu incomplet" : $"Appliquer {preview.TotalMatches:N0} remplacement(s)", MinWidth = 190, IsEnabled = !preview.Truncated };
        apply.SetResourceReference(BackgroundProperty, "AccentBrush");
        apply.Click += (_, _) => DialogResult = true;
        footer.Children.Add(cancel);
        footer.Children.Add(apply);
        root.Children.Add(footer);

        var summary = new TextBlock
        {
            Text = $"{preview.TotalMatches:N0} remplacement(s) · {preview.Files.Count:N0} fichier(s) · {preview.FilesSearched:N0} parcouru(s) · {preview.FilesSkipped:N0} ignoré(s)\nRechercher : {preview.Query}\nRemplacer par : {preview.Replacement}" +
                (preview.Truncated ? "\nAperçu tronqué : ajoutez un filtre et relancez. Aucun remplacement ne peut être appliqué à cet aperçu." : string.Empty) +
                (preview.Files.Count > 300 ? "\nSeuls les 300 premiers fichiers sont affichés ici; le total complet est indiqué ci-dessus." : string.Empty),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        summary.SetResourceReference(ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(summary, Dock.Top);
        root.Children.Add(summary);

        var list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        foreach (var file in preview.Files.Take(300))
        {
            var text = new TextBlock
            {
                Text = $"{file.RelativePath} · {file.MatchCount:N0} remplacement(s)\n{file.Preview}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8)
            };
            var item = new ListBoxItem { Content = text, Padding = new Thickness(4), BorderThickness = new Thickness(0, 0, 0, 1) };
            item.SetResourceReference(BorderBrushProperty, "BorderBrush");
            list.Items.Add(item);
        }
        root.Children.Add(list);
        Content = root;
    }

    public static bool Show(Window owner, WorkspaceReplacePreview preview)
    {
        var window = new WorkspaceReplacePreviewWindow(preview) { Owner = owner };
        return window.ShowDialog() == true;
    }
}
