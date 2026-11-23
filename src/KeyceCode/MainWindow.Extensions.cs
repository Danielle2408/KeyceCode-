using KeyceCode.Services;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KeyceCode;

public partial class MainWindow
{
    private readonly ExtensionGalleryService _extensionGallery = new();
    private readonly VsixExtensionService _vsixExtensions = new();

    private void InitializeExtensionList() => RefreshInstalledExtensions();

    private void RefreshInstalledExtensions()
    {
        InstalledExtensions.Items.Clear();
        foreach (var extension in _vsixExtensions.GetInstalled())
            InstalledExtensions.Items.Add($"{extension.Id}  ·  {extension.Version}");
        InstalledExtensionsStatus.Text = InstalledExtensions.Items.Count == 0
            ? $"Aucune extension installée · {_vsixExtensions.ExtensionRoot}"
            : $"{InstalledExtensions.Items.Count} extension(s) · {_vsixExtensions.ExtensionRoot}";
    }

    private async void InstallVsix_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Installer un VSIX téléchargé depuis Open VSX",
            Filter = "Paquets VSIX (*.vsix)|*.vsix",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        InstallExtensionButton.IsEnabled = false;
        ExtensionGalleryStatus.Text = "Validation et installation du paquet…";
        try
        {
            var installed = await _vsixExtensions.InstallAsync(dialog.FileName);
            RefreshInstalledExtensions();
            ExtensionGalleryStatus.Text = $"{installed.Id}@{installed.Version} installé. Le code de l’extension reste inactif.";
            StatusMessage.Text = $"Extension installée : {installed.Id}";
        }
        catch (Exception ex)
        {
            ExtensionGalleryStatus.Text = "Installation VSIX refusée ou impossible : " + ex.Message;
        }
        finally { InstallExtensionButton.IsEnabled = true; }
    }

    private async void InstallGalleryExtension_Click(object sender, RoutedEventArgs e)
    {
        if (ExtensionResults.SelectedItem is not GalleryExtension extension) return;
        InstallGalleryExtensionButton.IsEnabled = false;
        DownloadExtensionButton.IsEnabled = false;
        ExtensionGalleryStatus.Text = $"Téléchargement de {extension.Identity}…";
        string? packagePath = null;
        try
        {
            packagePath = await _extensionGallery.DownloadAsync(extension);
            ExtensionGalleryStatus.Text = "VSIX téléchargé. Vérification et installation…";
            var installed = await _vsixExtensions.InstallAsync(packagePath);
            RefreshInstalledExtensions();
            ExtensionGalleryStatus.Text = $"{installed.Id}@{installed.Version} installé localement. Son code reste inactif dans KeyceCode.";
            ExtensionDetails.Text += $"\n\nInstallé dans : {installed.Path}";
            StatusMessage.Text = $"Extension téléchargée et installée (inactive) : {installed.Id}";
        }
        catch (Exception ex)
        {
            ExtensionGalleryStatus.Text = "Téléchargement ou installation impossible.";
            ExtensionDetails.Text += "\n\n" + ex.Message;
        }
        finally
        {
            InstallGalleryExtensionButton.IsEnabled = ExtensionResults.SelectedItem is GalleryExtension;
            DownloadExtensionButton.IsEnabled = ExtensionResults.SelectedItem is GalleryExtension;
            if (packagePath is not null) ExtensionDetails.Text += $"\nPaquet : {packagePath}";
        }
    }

    private async void ImportVsCodeExtensions_Click(object sender, RoutedEventArgs e)
    {
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        if (!Directory.Exists(source))
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choisir le dossier extensions de Visual Studio Code",
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;
            source = dialog.FolderName;
        }

        InstallExtensionButton.IsEnabled = false;
        ExtensionGalleryStatus.Text = "Import sécurisé des paquets VS Code…";
        try
        {
            var result = await _vsixExtensions.ImportFromVsCodeAsync(source);
            RefreshInstalledExtensions();
            ExtensionGalleryStatus.Text = $"{result.Imported.Count} paquet(s) importé(s) comme inactifs; {result.Skipped.Count} ignoré(s).";
            ExtensionDetails.Text = result.Skipped.Count == 0
                ? "Les extensions importées sont stockées localement, mais leur code n’est pas exécuté par KeyceCode."
                : string.Join(Environment.NewLine, result.Skipped.Take(5));
            StatusMessage.Text = $"Import VS Code : {result.Imported.Count} extension(s)";
        }
        catch (Exception ex)
        {
            ExtensionGalleryStatus.Text = "Import impossible : " + ex.Message;
        }
        finally { InstallExtensionButton.IsEnabled = true; }
    }

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
            InstallGalleryExtensionButton.IsEnabled = false;
            DownloadExtensionButton.IsEnabled = false;
            return;
        }
        var verification = extension.Verified ? "Éditeur vérifié par Open VSX" : "Éditeur non vérifié";
        var rating = extension.Rating is { } value ? $" · note {value:0.0}/5" : string.Empty;
        ExtensionDetails.Text = $"{extension.DisplayName}\n{extension.Identity}\n{verification}{rating}\n{extension.Description}";
        DownloadExtensionButton.IsEnabled = true;
        InstallGalleryExtensionButton.IsEnabled = true;
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
