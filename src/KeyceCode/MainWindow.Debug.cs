using KeyceCode.Services.Dap;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace KeyceCode;

public partial class MainWindow
{
    private DapClient? _debugClient;
    private readonly Dictionary<string, HashSet<int>> _editorBreakpoints = new(StringComparer.OrdinalIgnoreCase);
    private VsCodeLaunchConfiguration? _selectedDebugConfiguration;
    private string? _debugProgramPath;
    private string? _debugConfigurationRoot;
    private int _debugThreadId = 1;
    private TaskCompletionSource _debugInitialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async void ToggleBreakpoint_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is null) { StatusMessage.Text = "Ouvrez un fichier avant de poser un point d’arrêt."; return; }
        await PostEditorMessageAsync(new { type = "toggleBreakpoint" });
    }

    private void DebugConfigurations_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedDebugConfiguration = (DebugConfigurations.SelectedItem as ListBoxItem)?.Tag as VsCodeLaunchConfiguration;
        if (_selectedDebugConfiguration is null) return;
        var adapter = VsCodeLaunchConfigurationService.ResolveAdapter(_selectedDebugConfiguration.Type);
        DebugStatusText.Text = adapter.Message;
    }

    private void RefreshDebugConfigurations_Click(object sender, RoutedEventArgs e) => RefreshDebugConfigurations();

    private void RefreshDebugConfigurations()
    {
        DebugConfigurations.Items.Clear();
        _selectedDebugConfiguration = null;
        if (_workspace is null) { DebugStatusText.Text = "Ouvrez un dossier avant de charger launch.json."; return; }
        _debugConfigurationRoot = Path.GetFullPath(_workspace.RootPath);
        try
        {
            var configs = VsCodeLaunchConfigurationService.Read(_workspace.RootPath);
            foreach (var config in configs)
                DebugConfigurations.Items.Add(new ListBoxItem { Content = $"{config.Name}  ·  {config.Type}", Tag = config, ToolTip = $"Requête {config.Request}" });
            if (DebugConfigurations.Items.Count > 0)
            {
                DebugConfigurations.SelectedIndex = 0;
                DebugStatusText.Text = $"{configs.Count} configuration(s) chargée(s) depuis .vscode/launch.json.";
            }
            else DebugStatusText.Text = "Aucune configuration launch/attach valide dans .vscode/launch.json.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            DebugStatusText.Text = ".vscode/launch.json invalide : " + ex.Message;
        }
    }

    private async void CreateDebugConfig_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null) { DebugStatusText.Text = "Ouvrez un dossier avant de créer launch.json."; return; }
        var activeFile = _activeDocument?.Path;
        if (string.IsNullOrWhiteSpace(activeFile) || !string.Equals(Path.GetExtension(activeFile), ".py", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Ouvrez d’abord un fichier Python (.py) pour créer une configuration de lancement.", "Configuration de débogage", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var directory = Path.Combine(_workspace.RootPath, ".vscode");
        var path = Path.Combine(directory, "launch.json");
        if (File.Exists(path))
        {
            if (MessageBox.Show(this, ".vscode/launch.json existe déjà. Voulez-vous l’ouvrir sans l’écraser ?", "Configuration de débogage", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await OpenDocumentAsync(path, pin: true);
            return;
        }
        Directory.CreateDirectory(directory);
        var content = """
            {
              "version": "0.2.0",
              "configurations": [
                {
                  "name": "Python: Fichier actuel",
                  "type": "debugpy",
                  "request": "launch",
                  "program": "${file}",
                  "console": "internalConsole",
                  "justMyCode": true
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(path, content + Environment.NewLine);
        await OpenDocumentAsync(path, pin: true);
        RefreshDebugConfigurations();
    }

    private async void OpenDebugConfig_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null) { DebugStatusText.Text = "Ouvrez un dossier avant d’ouvrir launch.json."; return; }
        var path = Path.Combine(_workspace.RootPath, ".vscode", "launch.json");
        if (!File.Exists(path)) { DebugStatusText.Text = "Aucun .vscode/launch.json. Créez une configuration Python après avoir ouvert un fichier .py."; return; }
        await OpenDocumentAsync(path, pin: true);
    }

    private async void DebugStart_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null) { DebugStatusText.Text = "Ouvrez un dossier avant de démarrer le débogage."; return; }
        if (_debugClient is not null) { DebugStatusText.Text = "Une session de débogage est déjà active."; return; }
        if (_debugConfigurationRoot != Path.GetFullPath(_workspace.RootPath) || DebugConfigurations.Items.Count == 0) RefreshDebugConfigurations();
        var config = _selectedDebugConfiguration;
        if (config is null) { DebugStatusText.Text = "Choisissez une configuration launch.json."; return; }
        var adapter = VsCodeLaunchConfigurationService.ResolveAdapter(config.Type);
        if (!adapter.IsAvailable || adapter.ExecutablePath is null)
        {
            DebugStatusText.Text = adapter.Message;
            MessageBox.Show(this, adapter.Message, "Adaptateur DAP indisponible", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var activeFile = _activeDocument?.Path;
            var expanded = VsCodeLaunchConfigurationService.ExpandWorkspaceVariables(config.Value, _workspace.RootPath, activeFile);
            var unresolved = FindUnresolvedVariable(expanded);
            if (unresolved is not null)
            {
                DebugStatusText.Text = $"Variable non prise en charge : {unresolved}";
                return;
            }
            var arguments = ToAdapterArguments(expanded);
            _debugProgramPath = arguments.TryGetValue("program", out var program) && program.ValueKind == JsonValueKind.String
                ? Path.GetFullPath(program.GetString()!, _workspace.RootPath)
                : activeFile;
            var cwd = arguments.TryGetValue("cwd", out var workingDirectory) && workingDirectory.ValueKind == JsonValueKind.String
                ? workingDirectory.GetString()! : _workspace.RootPath;
            var confirmation = $"Configuration : {config.Name}\nType : {config.Type} · {config.Request}\nRépertoire : {cwd}\nFichier cible : {_debugProgramPath ?? "défini par l’adaptateur"}\n\nKeyceCode va démarrer l’adaptateur debugpy local et lui transmettre la configuration. Confirmez-vous l’exécution ?";
            if (MessageBox.Show(this, confirmation, "Confirmer le débogage", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            ShowPanel("DebugConsole");
            DebugConsolePanel.Clear();
            _debugInitialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new DapClient();
            _debugClient = client;
            client.OutputReceived += (_, line) => Dispatcher.BeginInvoke(() => AppendDebugText("[debugpy] " + line));
            client.TransportError += (_, error) => Dispatcher.BeginInvoke(() => AppendDebugText("[DAP transport] " + error.Message));
            client.EventReceived += (_, debugEvent) => Dispatcher.BeginInvoke(() => _ = HandleDebugEventAsync(client, debugEvent));
            await client.StartAsync(new DapAdapterOptions
            {
                ExecutablePath = adapter.ExecutablePath,
                Arguments = adapter.Arguments ?? [],
                WorkingDirectory = _workspace.RootPath,
                Environment = adapter.Environment ?? new Dictionary<string, string?>()
            });
            await client.InitializeAsync(config.Type == "python" ? "debugpy" : config.Type, "fr-FR");
            if (config.Request == "attach") await client.AttachAsync(arguments);
            else await client.LaunchAsync(arguments);
            await _debugInitialized.Task.WaitAsync(TimeSpan.FromSeconds(15));
            DebugStatusText.Text = $"Session active · {config.Name}";
        }
        catch (Exception ex)
        {
            DebugStatusText.Text = "Le débogage n’a pas démarré : " + ex.Message;
            AppendDebugText("[Erreur] " + ex.Message);
            await StopDebugSessionAsync(terminateDebuggee: true);
        }
    }

    private async Task HandleDebugEventAsync(DapClient client, DapEventArgs debugEvent)
    {
        if (!ReferenceEquals(client, _debugClient)) return;
        try
        {
            switch (debugEvent.EventName)
            {
                case "initialized":
                    await ApplyDebugBreakpointsAsync(client);
                    await client.ConfigurationDoneAsync();
                    _debugInitialized.TrySetResult();
                    AppendDebugText("[DAP] Configuration terminée; adaptateur initialisé.");
                    break;
                case "output":
                    var category = debugEvent.Body.TryGetProperty("category", out var cat) ? cat.GetString() : "console";
                    var output = debugEvent.Body.TryGetProperty("output", out var outputElement) ? outputElement.GetString() : null;
                    if (!string.IsNullOrEmpty(output)) AppendDebugText($"[{category}] {output.TrimEnd()} ");
                    break;
                case "stopped":
                    if (debugEvent.Body.TryGetProperty("threadId", out var thread) && thread.TryGetInt32(out var threadId)) _debugThreadId = threadId;
                    var reason = debugEvent.Body.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : "pause";
                    AppendDebugText($"[DAP] Exécution suspendue ({reason}).");
                    await ShowDebugStackAsync(client);
                    break;
                case "continued": AppendDebugText("[DAP] Exécution reprise."); break;
                case "exited":
                    if (debugEvent.Body.TryGetProperty("exitCode", out var code)) AppendDebugText($"[DAP] Processus terminé · code {code}.");
                    break;
                case "terminated":
                    AppendDebugText("[DAP] Session terminée.");
                    DebugStatusText.Text = "Session de débogage terminée.";
                    break;
                case "breakpoint": AppendDebugText("[DAP] État d’un point d’arrêt mis à jour."); break;
            }
        }
        catch (Exception ex)
        {
            AppendDebugText("[DAP event] " + ex.Message);
            if (debugEvent.EventName == "initialized") _debugInitialized.TrySetException(ex);
        }
    }

    private async Task ApplyDebugBreakpointsAsync(DapClient client)
    {
        if (string.IsNullOrWhiteSpace(_debugProgramPath))
        {
            AppendDebugText("[DAP] Aucun chemin de fichier cible; points d’arrêt ignorés.");
            return;
        }
        var sources = _editorBreakpoints.ToDictionary(pair => pair.Key, pair => new HashSet<int>(pair.Value), StringComparer.OrdinalIgnoreCase);
        var targetPath = Path.GetFullPath(_debugProgramPath);
        if (!sources.TryGetValue(targetPath, out var targetLines)) sources[targetPath] = targetLines = [];
        foreach (var line in ParseDebugBreakpointLines(DebugBreakpointLines.Text)) targetLines.Add(line);
        foreach (var (path, lines) in sources)
            await SendDebugBreakpointsAsync(client, path, lines);
        AppendDebugText($"[DAP] Points d’arrêt chargés pour {sources.Count} fichier(s).");
    }

    private async Task SendDebugBreakpointsAsync(DapClient client, string path, IEnumerable<int> lines)
    {
        var orderedLines = lines.Where(line => line > 0).Distinct().Order().ToArray();
        var result = await client.RequestAsync("setBreakpoints", new
        {
            source = new { name = Path.GetFileName(path), path },
            breakpoints = orderedLines.Select(line => new { line }).ToArray(),
            sourceModified = false
        });
        var verified = result.TryGetProperty("breakpoints", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().Count(item => item.TryGetProperty("verified", out var value) && value.ValueKind == JsonValueKind.True) : 0;
        AppendDebugText($"[DAP] {verified}/{orderedLines.Length} point(s) d’arrêt vérifié(s) sur {Path.GetFileName(path)}.");
    }

    private async Task HandleEditorBreakpointToggledAsync(JsonElement message)
    {
        var documentId = ReadString(message, "id");
        var document = _documents.FirstOrDefault(item => item.Id == documentId);
        var path = document?.Path;
        var line = ReadInt(message, "line", 0);
        var enabled = ReadBool(message, "enabled") == true;
        if (string.IsNullOrWhiteSpace(path) || line <= 0) return;

        path = Path.GetFullPath(path);
        if (!_editorBreakpoints.TryGetValue(path, out var lines)) _editorBreakpoints[path] = lines = [];
        if (enabled) lines.Add(line);
        else lines.Remove(line);

        var action = enabled ? "activé" : "retiré";
        StatusMessage.Text = $"Point d’arrêt {action} · {Path.GetFileName(path)}:{line} (F9)";
        if (_debugClient is { } client && _debugInitialized.Task.IsCompletedSuccessfully)
        {
            try { await SendDebugBreakpointsAsync(client, path, lines); }
            catch (Exception ex) { AppendDebugText($"[DAP breakpoint] {ex.Message}"); }
        }
    }

    private int[] GetEditorBreakpointLines(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        return _editorBreakpoints.TryGetValue(Path.GetFullPath(path), out var lines) ? lines.Order().ToArray() : [];
    }

    private static IEnumerable<int> ParseDebugBreakpointLines(string text) =>
        text.Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.TryParse(value, out var line) ? line : 0)
            .Where(line => line > 0).Distinct();

    private async Task ShowDebugStackAsync(DapClient client)
    {
        var stack = await client.RequestAsync("stackTrace", new { threadId = _debugThreadId, startFrame = 0, levels = 20 });
        if (!stack.TryGetProperty("stackFrames", out var frames) || frames.ValueKind != JsonValueKind.Array) return;
        foreach (var frame in frames.EnumerateArray())
        {
            var name = frame.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : "frame";
            var line = frame.TryGetProperty("line", out var lineValue) ? lineValue.GetInt32() : 0;
            var file = frame.TryGetProperty("source", out var source) && source.TryGetProperty("path", out var path) ? path.GetString() : "";
            AppendDebugText($"  {name} · {Path.GetFileName(file)}:{line}");
        }
        if (frames.GetArrayLength() == 0 || !frames[0].TryGetProperty("id", out var frameId)) return;
        var scopes = await client.RequestAsync("scopes", new { frameId = frameId.GetInt32() });
        if (!scopes.TryGetProperty("scopes", out var scopeArray) || scopeArray.ValueKind != JsonValueKind.Array) return;
        foreach (var scope in scopeArray.EnumerateArray().Take(3))
        {
            if (!scope.TryGetProperty("variablesReference", out var reference) || reference.GetInt32() == 0) continue;
            var variables = await client.RequestAsync("variables", new { variablesReference = reference.GetInt32(), filter = "named", count = 30 });
            if (!variables.TryGetProperty("variables", out var variableArray) || variableArray.ValueKind != JsonValueKind.Array) continue;
            foreach (var variable in variableArray.EnumerateArray().Take(30))
            {
                var variableName = variable.TryGetProperty("name", out var n) ? n.GetString() : "?";
                var value = variable.TryGetProperty("value", out var v) ? v.GetString() : "";
                AppendDebugText($"    {variableName} = {value}");
            }
        }
    }

    private async void DebugContinue_Click(object sender, RoutedEventArgs e) => await DebugRequestAsync("continue", new { threadId = _debugThreadId, singleThread = false });
    private async void DebugPause_Click(object sender, RoutedEventArgs e) => await DebugRequestAsync("pause", new { threadId = _debugThreadId });
    private async void DebugNext_Click(object sender, RoutedEventArgs e) => await DebugRequestAsync("next", new { threadId = _debugThreadId, singleThread = false });
    private async void DebugStepIn_Click(object sender, RoutedEventArgs e) => await DebugRequestAsync("stepIn", new { threadId = _debugThreadId, singleThread = false });
    private async void DebugStepOut_Click(object sender, RoutedEventArgs e) => await DebugRequestAsync("stepOut", new { threadId = _debugThreadId, singleThread = false });
    private async void DebugRestart_Click(object sender, RoutedEventArgs e)
    {
        var config = _selectedDebugConfiguration;
        await StopDebugSessionAsync(terminateDebuggee: true);
        if (config is not null) await Dispatcher.InvokeAsync(() => DebugConfigurations.SelectedItem = DebugConfigurations.Items.OfType<ListBoxItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, config)));
        DebugStart_Click(sender, e);
    }
    private async void DebugStop_Click(object sender, RoutedEventArgs e) => await StopDebugSessionAsync(terminateDebuggee: true);

    private async Task DebugRequestAsync(string command, object arguments)
    {
        var client = _debugClient;
        if (client is null) { DebugStatusText.Text = "Aucune session DAP active."; return; }
        try { await client.RequestAsync(command, arguments); }
        catch (Exception ex) { AppendDebugText($"[DAP {command}] {ex.Message}"); }
    }

    private async Task StopDebugSessionAsync(bool terminateDebuggee)
    {
        var client = _debugClient;
        _debugClient = null;
        _debugInitialized.TrySetCanceled();
        if (client is null) return;
        try { await client.DisconnectAsync(terminateDebuggee); }
        catch (Exception ex) { AppendDebugText("[DAP disconnect] " + ex.Message); }
        finally { await client.DisposeAsync(); }
        DebugStatusText.Text = "Session de débogage arrêtée.";
    }

    private void AppendDebugText(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppendDebugText(text)); return; }
        DebugConsolePanel.AppendText(text + Environment.NewLine);
        const int maxCharacters = 250_000;
        if (DebugConsolePanel.Text.Length > maxCharacters) DebugConsolePanel.Text = DebugConsolePanel.Text[^maxCharacters..];
        DebugConsolePanel.ScrollToEnd();
    }

    private static Dictionary<string, JsonElement> ToAdapterArguments(JsonElement configuration)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in configuration.EnumerateObject())
            if (property.Name is not ("name" or "type" or "request")) result[property.Name] = property.Value.Clone();
        return result;
    }

    private static string? FindUnresolvedVariable(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString() ?? "";
            var start = value.IndexOf("${", StringComparison.Ordinal);
            if (start >= 0) { var end = value.IndexOf('}', start + 2); return end > start ? value[start..(end + 1)] : value[start..]; }
        }
        else if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject()) if (FindUnresolvedVariable(property.Value) is { } unresolvedProperty) return unresolvedProperty;
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (FindUnresolvedVariable(item) is { } unresolvedItem) return unresolvedItem;
        return null;
    }
}
