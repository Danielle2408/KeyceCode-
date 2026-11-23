using KeyceCode.Services;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.Json;

namespace KeyceCode;

public partial class MainWindow
{
    private readonly ExtensionGalleryService _extensionGallery = new();
    private readonly VsixExtensionService _vsixExtensions = new();
    private readonly HashSet<string> _activeExtensionIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string ExtensionId, string Command), (string Title, string Description)> _extensionCommands = [];
    private ExtensionHostService? _extensionHost;
    private CancellationTokenSource? _extensionOperationCancellation;

    private void InitializeExtensionList() => RefreshInstalledExtensions();

    private void RefreshInstalledExtensions()
    {
        InstalledExtensions.Items.Clear();
        foreach (var extension in _vsixExtensions.GetInstalled())
            InstalledExtensions.Items.Add($"{(_activeExtensionIds.Contains(extension.Id) ? "● Active" : "○ Inactive")}  {extension.Id}  ·  {extension.Version}");
        InstalledExtensionsStatus.Text = InstalledExtensions.Items.Count == 0
            ? $"Aucune extension installée · {_vsixExtensions.ExtensionRoot}"
            : $"{InstalledExtensions.Items.Count} extension(s) · {_activeExtensionIds.Count} active(s)";
    }

    private async void ActivateInstalledExtension_Click(object sender, RoutedEventArgs e)
    {
        var installed = _vsixExtensions.GetInstalled();
        if (installed.Count == 0)
        {
            ExtensionHostStatus.Text = "Aucune extension installée à activer.";
            return;
        }
        var choices = installed.ToDictionary(extension => $"{extension.Id}@{extension.Version} · {extension.DisplayName}", extension => extension, StringComparer.OrdinalIgnoreCase);
        var selected = QuickPickWindow.Show(this, "Choisir une extension à activer", choices.Keys);
        if (selected is null || !choices.TryGetValue(selected, out var extension)) return;
        if (_activeExtensionIds.Contains(extension.Id))
        {
            ExtensionHostStatus.Text = $"{extension.Id} est déjà active.";
            return;
        }

        var consent = MessageBox.Show(this,
            $"Activer {extension.Id}@{extension.Version} ?\n\nLe code de cette extension sera exécuté par Node.js avec les droits de ton compte Windows. Le processus est séparé de KeyceCode, mais il n’est pas sandboxé et peut accéder aux fichiers et au réseau. N’active que du code auquel tu fais confiance.",
            "Autoriser l’exécution de l’extension", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes) return;

        ActivateExtensionButton.IsEnabled = false;
        ExtensionHostStatus.Text = "Démarrage de l’hôte Node.js…";
        try
        {
            _extensionHost ??= new ExtensionHostService();
            await _extensionHost.StartAsync(QueueExtensionHostMessage);
            ExtensionHostStatus.Text = $"Activation de {extension.Id}…";
            var result = await _extensionHost.ActivateAsync(extension, _workspace?.RootPath);
            if (result.Type == "activationFailed")
            {
                ExtensionHostStatus.Text = $"Échec d’activation : {extension.Id}";
                OutputPanel.AppendText($"[Extension Host] {extension.Id} : {result.Error}{Environment.NewLine}");
                return;
            }
            _activeExtensionIds.Add(extension.Id);
            AddContributedCommands(extension);
            RefreshInstalledExtensions();
            RefreshExtensionCommands();
            ExtensionHostStatus.Text = $"{_activeExtensionIds.Count} extension(s) active(s) · processus Node.js séparé";
            StatusMessage.Text = $"Extension activée : {extension.Id}";
        }
        catch (Exception ex)
        {
            ExtensionHostStatus.Text = "Impossible de démarrer l’hôte d’extensions : " + ex.Message;
            OutputPanel.AppendText("[Extension Host] " + ex + Environment.NewLine);
        }
        finally { ActivateExtensionButton.IsEnabled = true; }
    }

    private void AddContributedCommands(InstalledExtension extension)
    {
        try
        {
            var manifestPath = Path.Combine(extension.Path, "extension", "package.json");
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!manifest.RootElement.TryGetProperty("contributes", out var contributes) ||
                !contributes.TryGetProperty("commands", out var commands) || commands.ValueKind != JsonValueKind.Array) return;
            foreach (var item in commands.EnumerateArray())
            {
                if (!item.TryGetProperty("command", out var idValue) || idValue.ValueKind != JsonValueKind.String) continue;
                var command = idValue.GetString();
                if (string.IsNullOrWhiteSpace(command)) continue;
                var title = item.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String
                    ? titleValue.GetString() ?? command : command;
                var description = item.TryGetProperty("category", out var categoryValue) && categoryValue.ValueKind == JsonValueKind.String
                    ? categoryValue.GetString() ?? string.Empty : string.Empty;
                _extensionCommands[(extension.Id, command)] = (title, description);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            OutputPanel.AppendText($"[Extension Host] Impossible de lire les commandes de {extension.Id} : {ex.Message}{Environment.NewLine}");
        }
    }

    private void QueueExtensionHostMessage(ExtensionHostMessage message)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.InvokeAsync(() => HandleExtensionHostMessage(message));
    }

    private void HandleExtensionHostMessage(ExtensionHostMessage message)
    {
        switch (message.Type)
        {
            case "commandRegistered":
                if (message.ExtensionId is { } extensionId && message.Command is { } command &&
                    !_extensionCommands.ContainsKey((extensionId, command)))
                    _extensionCommands[(extensionId, command)] = (command, string.Empty);
                RefreshExtensionCommands();
                break;
            case "notification":
                StatusMessage.Text = $"[{message.ExtensionId ?? "Extension"}] {message.Message}";
                break;
            case "activationFailed":
                if (message.Error is not null) OutputPanel.AppendText($"[Extension Host] {message.ExtensionId} : {message.Error}{Environment.NewLine}");
                break;
            case "commandFailed":
                OutputPanel.AppendText($"[Extension Host] {message.ExtensionId}/{message.Command} : {message.Error}{Environment.NewLine}");
                StatusMessage.Text = $"La commande d’extension a échoué · {message.Command}";
                break;
            case "commandCompleted":
                StatusMessage.Text = "Commande d’extension exécutée.";
                break;
            case "hostError":
                OutputPanel.AppendText("[Extension Host] " + message.Error + Environment.NewLine);
                break;
        }
    }

    private void RefreshExtensionCommands()
    {
        ExtensionCommands.Items.Clear();
        foreach (var item in _extensionCommands.OrderBy(pair => pair.Key.ExtensionId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(pair => pair.Value.Title, StringComparer.OrdinalIgnoreCase))
        {
            if (!_activeExtensionIds.Contains(item.Key.ExtensionId)) continue;
            var label = string.IsNullOrWhiteSpace(item.Value.Description)
                ? $"{item.Value.Title}  ·  {item.Key.ExtensionId}"
                : $"{item.Value.Description}: {item.Value.Title}  ·  {item.Key.ExtensionId}";
            ExtensionCommands.Items.Add(new ListBoxItem { Content = label, Tag = item.Key, ToolTip = item.Key.Command });
        }
    }

    private async void RunExtensionCommand_Click(object sender, MouseButtonEventArgs e)
    {
        if (ExtensionCommands.SelectedItem is not ListBoxItem { Tag: ValueTuple<string, string> command }) return;
        await InvokeExtensionCommandAsync(command.Item1, command.Item2);
    }

    private async Task InvokeExtensionCommandAsync(string extensionId, string command)
    {
        try
        {
            if (_extensionHost is null) return;
            await _extensionHost.InvokeCommandAsync(extensionId, command);
        }
        catch (Exception ex)
        {
            OutputPanel.AppendText("[Extension Host] " + ex + Environment.NewLine);
            StatusMessage.Text = "Commande d’extension impossible · consulte Sortie";
        }
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
        BeginExtensionDownloadProgress();
        string? packagePath = null;
        try
        {
            var progress = CreateExtensionDownloadProgress();
            packagePath = await _extensionGallery.DownloadAsync(extension, progress, _extensionOperationCancellation!.Token);
            ExtensionGalleryStatus.Text = "VSIX téléchargé. Vérification et installation…";
            var installed = await _vsixExtensions.InstallAsync(packagePath, _extensionOperationCancellation!.Token, CreateVsixInstallProgress());
            RefreshInstalledExtensions();
            ExtensionGalleryStatus.Text = $"{installed.Id}@{installed.Version} installé localement. Son code reste inactif dans KeyceCode.";
            ExtensionDetails.Text += $"\n\nInstallé dans : {installed.Path}";
            StatusMessage.Text = $"Extension téléchargée et installée (inactive) : {installed.Id}";
        }
        catch (OperationCanceledException)
        {
            ExtensionGalleryStatus.Text = "Téléchargement ou installation annulé.";
        }
        catch (Exception ex)
        {
            ExtensionGalleryStatus.Text = "Échec : " + ex.Message;
            ExtensionDetails.Text += "\n\n" + ex.Message;
            OutputPanel.AppendText("[Extensions] " + ex + Environment.NewLine);
        }
        finally
        {
            EndExtensionDownloadProgress();
            UpdateGallerySelectionButtons();
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
            UpdateGallerySelectionButtons();
            return;
        }
        var verification = extension.Verified ? "Éditeur vérifié par Open VSX" : "Éditeur non vérifié";
        var rating = extension.Rating is { } value ? $" · note {value:0.0}/5" : string.Empty;
        ExtensionDetails.Text = $"{extension.DisplayName}\n{extension.Identity}\n{verification}{rating}\n{extension.Description}";
        UpdateGallerySelectionButtons();
    }

    private void UpdateGallerySelectionButtons()
    {
        if (ExtensionResults.SelectedItem is not GalleryExtension extension)
        {
            InstallGalleryExtensionButton.Content = "Télécharger et installer depuis Open VSX";
            InstallGalleryExtensionButton.IsEnabled = false;
            DownloadExtensionButton.IsEnabled = false;
            return;
        }
        var alreadyInstalled = _vsixExtensions.GetInstalled().Any(installed =>
            installed.Id.Equals($"{extension.Namespace}.{extension.Name}", StringComparison.OrdinalIgnoreCase) &&
            installed.Version.Equals(extension.Version, StringComparison.OrdinalIgnoreCase));
        InstallGalleryExtensionButton.Content = alreadyInstalled ? "Déjà installée" : "Télécharger et installer depuis Open VSX";
        InstallGalleryExtensionButton.IsEnabled = !alreadyInstalled;
        DownloadExtensionButton.IsEnabled = true;
    }

    private async void DownloadExtension_Click(object sender, RoutedEventArgs e)
    {
        if (ExtensionResults.SelectedItem is not GalleryExtension extension) return;
        DownloadExtensionButton.IsEnabled = false;
        BeginExtensionDownloadProgress();
        try
        {
            var path = await _extensionGallery.DownloadAsync(extension, CreateExtensionDownloadProgress(), _extensionOperationCancellation!.Token);
            ExtensionGalleryStatus.Text = "Paquet VSIX téléchargé. KeyceCode ne l’a pas installé ni exécuté.";
            ExtensionDetails.Text += $"\n\nFichier : {path}";
            StatusMessage.Text = $"VSIX téléchargé : {extension.Name}";
        }
        catch (OperationCanceledException) { ExtensionGalleryStatus.Text = "Téléchargement annulé."; }
        catch (Exception ex)
        {
            ExtensionGalleryStatus.Text = "Échec du téléchargement : " + ex.Message;
            ExtensionDetails.Text += "\n\n" + ex.Message;
            OutputPanel.AppendText("[Extensions] " + ex + Environment.NewLine);
        }
        finally
        {
            EndExtensionDownloadProgress();
            UpdateGallerySelectionButtons();
        }
    }

    private void BeginExtensionDownloadProgress()
    {
        _extensionOperationCancellation?.Cancel();
        _extensionOperationCancellation?.Dispose();
        _extensionOperationCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        ExtensionDownloadProgress.Value = 0;
        ExtensionDownloadProgress.IsIndeterminate = true;
        ExtensionDownloadProgress.Visibility = Visibility.Visible;
        CancelExtensionOperationButton.Visibility = Visibility.Visible;
        ExtensionGalleryStatus.Text = "Préparation du téléchargement…";
    }

    private IProgress<ExtensionDownloadProgress> CreateExtensionDownloadProgress() => new Progress<ExtensionDownloadProgress>(progress =>
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        ExtensionDownloadProgress.IsIndeterminate = progress.TotalBytes is not > 0;
        if (progress.TotalBytes is > 0)
        {
            ExtensionDownloadProgress.Value = Math.Clamp(progress.BytesReceived * 100d / progress.TotalBytes.Value, 0, 100);
            ExtensionGalleryStatus.Text = $"{progress.Phase} {FormatExtensionBytes(progress.BytesReceived)} / {FormatExtensionBytes(progress.TotalBytes.Value)}";
        }
        else
        {
            ExtensionGalleryStatus.Text = progress.BytesReceived > 0
                ? $"{progress.Phase} {FormatExtensionBytes(progress.BytesReceived)}"
                : progress.Phase;
        }
    });

    private IProgress<VsixInstallProgress> CreateVsixInstallProgress() => new Progress<VsixInstallProgress>(progress =>
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        ExtensionDownloadProgress.IsIndeterminate = progress.TotalBytes <= 0;
        if (progress.TotalBytes > 0)
        {
            ExtensionDownloadProgress.Value = Math.Clamp(progress.BytesProcessed * 100d / progress.TotalBytes, 0, 100);
            ExtensionGalleryStatus.Text = $"{progress.Phase} {FormatExtensionBytes(progress.BytesProcessed)} / {FormatExtensionBytes(progress.TotalBytes)}";
        }
        else ExtensionGalleryStatus.Text = progress.Phase;
    });

    private static string FormatExtensionBytes(long bytes) => bytes < 1024 * 1024
        ? $"{bytes / 1024d:0} Ko" : $"{bytes / (1024d * 1024d):0.0} Mo";

    private void CancelExtensionOperation_Click(object sender, RoutedEventArgs e)
    {
        _extensionOperationCancellation?.Cancel();
        ExtensionGalleryStatus.Text = "Annulation en cours…";
        CancelExtensionOperationButton.IsEnabled = false;
    }

    private void EndExtensionDownloadProgress()
    {
        _extensionOperationCancellation?.Dispose();
        _extensionOperationCancellation = null;
        ExtensionDownloadProgress.IsIndeterminate = false;
        ExtensionDownloadProgress.Visibility = Visibility.Collapsed;
        CancelExtensionOperationButton.Visibility = Visibility.Collapsed;
        CancelExtensionOperationButton.IsEnabled = true;
    }
}
