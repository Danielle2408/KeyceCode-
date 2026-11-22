using KeyceCode.Services.Lsp;
using System.IO;
using System.Windows;

namespace KeyceCode;

public partial class MainWindow
{
    private CancellationTokenSource? _workspaceSymbolsCancellation;

    private async void WorkspaceSymbols_Click(object sender, RoutedEventArgs e) => await GoToWorkspaceSymbolAsync();

    private async Task GoToWorkspaceSymbolAsync()
    {
        if (_workspace is null) { StatusMessage.Text = "Ouvrez un dossier avant de chercher ses symboles."; return; }
        if (_languageServer is not { IsInitialized: true } server || !HasLspCapability(server.ServerCapabilities, "workspaceSymbolProvider"))
        {
            StatusMessage.Text = "Le serveur LSP actif ne fournit pas la recherche de symboles workspace.";
            return;
        }

        var query = QuickPickWindow.Prompt(this, "Symbole dans l’espace de travail", "Nom de symbole (vide = tout)");
        if (query is null) return;
        _workspaceSymbolsCancellation?.Cancel();
        _workspaceSymbolsCancellation?.Dispose();
        _workspaceSymbolsCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            StatusMessage.Text = "Recherche des symboles LSP…";
            var response = await server.RequestAsync("workspace/symbol", new { query = query.Trim() }, _workspaceSymbolsCancellation.Token);
            var symbols = LspWorkspaceSymbolService.Parse(response, _workspace.RootPath);
            if (symbols.Count == 0)
            {
                StatusMessage.Text = "Aucun symbole workspace retourné par le serveur LSP.";
                return;
            }
            var choices = symbols.Select((symbol, index) => $"{symbol.Label(_workspace.RootPath)}  ·  #{index + 1}").ToArray();
            var selected = QuickPickWindow.Show(this, $"Symboles de l’espace de travail · {symbols.Count:N0}", choices);
            if (selected is null) return;
            var indexText = selected[(selected.LastIndexOf(" · #", StringComparison.Ordinal) + 4)..];
            if (!int.TryParse(indexText, out var index) || index < 1 || index > symbols.Count) return;
            var symbol = symbols[index - 1];
            await OpenDocumentAsync(symbol.Path, pin: true);
            await PostEditorMessageAsync(new { type = "executeCommand", command = "editor.action.gotoLine", args = symbol.Line + 1 });
            StatusMessage.Text = $"Symbole ouvert : {symbol.Name}";
        }
        catch (OperationCanceledException) { StatusMessage.Text = "Recherche de symboles annulée."; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            StatusMessage.Text = "Recherche LSP indisponible : " + ex.Message;
            OutputPanel.AppendText("[LSP] Symboles workspace : " + ex.Message + Environment.NewLine);
        }
        finally
        {
            _workspaceSymbolsCancellation?.Dispose();
            _workspaceSymbolsCancellation = null;
        }
    }
}
