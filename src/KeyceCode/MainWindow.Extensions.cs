using KeyceCode.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KeyceCode;

public partial class MainWindow
{
    private readonly ExtensionGalleryService _extensionGallery = new();

    private async void ExtensionSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        await SearchExtensionsAsync();
        e.Handled = true;
    }

    private async void SearchMarketplace_Click(object sender, RoutedEventArgs e) => await SearchExtensionsAsync();

    private async Task SearchExtensionsAsync()
    {
        var query = ExtensionSearch.Text.Trim();
        if (query.Length == 0) { ExtensionGalleryStatus.Text = "Entrez un nom ou une catégorie."; return; }
        ExtensionResults.Items.Clear();
        ExtensionDetails.Text = "Chargement du catalogue Open VSX…";
        DownloadExtensionButton.IsEnabled = false;
        ExtensionGalleryStatus.Text = "Recherche…";
        try
        {
            var results = await _extensionGallery.SearchAsync(query);
            foreach (var extension in results) ExtensionResults.Items.Add(extension);
            ExtensionGalleryStatus.Text = $"{results.Count} résultat(s) affiché(s). Les extensions sont téléchargées comme VSIX; elles ne sont pas exécutées par KeyceCode.";
            ExtensionDetails.Text = results.Count == 0 ? "Aucun résultat pour cette recherche." : "Sélectionnez une extension pour consulter sa description et ses informations.";
            if (results.Count > 0) ExtensionResults.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            ExtensionGalleryStatus.Text = "Impossible de joindre Open VSX.";
            ExtensionDetails.Text = ex.Message;
        }
    }

    private void ExtensionResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExtensionResults.SelectedItem is not GalleryExtension extension)
        {
            DownloadExtensionButton.IsEnabled = false;
            return;
        }
        var verification = extension.Verified ? "Éditeur vérifié par Open VSX" : "Éditeur non vérifié";
        var rating = extension.Rating is { } value ? $" · note {value:0.0}/5" : string.Empty;
        ExtensionDetails.Text = $"{extension.DisplayName}\n{extension.Identity}\n{verification}{rating}\n{extension.Description}";
        DownloadExtensionButton.IsEnabled = true;
    }

    private async void DownloadExtension_Click(object sender, RoutedEventArgs e)
    {
        if (ExtensionResults.SelectedItem is not GalleryExtension extension) return;
        DownloadExtensionButton.IsEnabled = false;
        ExtensionGalleryStatus.Text = $"Téléchargement de {extension.Identity}…";
        try
        {
            var path = await _extensionGallery.DownloadAsync(extension);
            ExtensionGalleryStatus.Text = "Paquet VSIX téléchargé. KeyceCode ne l’a pas installé ni exécuté.";
            ExtensionDetails.Text += $"\n\nFichier : {path}";
            StatusMessage.Text = $"VSIX téléchargé : {extension.Name}";
        }
        catch (Exception ex)
        {
            ExtensionGalleryStatus.Text = "Échec du téléchargement.";
            ExtensionDetails.Text += "\n\n" + ex.Message;
        }
        finally { DownloadExtensionButton.IsEnabled = ExtensionResults.SelectedItem is GalleryExtension; }
    }
}
