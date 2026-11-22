using System.IO;
using System.Text.Json;

namespace KeyceCode.Services.Dap;

public sealed record VsCodeLaunchConfiguration(string Name, string Type, string Request, JsonElement Value);
public sealed record DebugAdapterAvailability(string Type, bool IsAvailable, string Message, string? ExecutablePath = null, IReadOnlyList<string>? Arguments = null, IReadOnlyDictionary<string, string?>? Environment = null);

/// <summary>Reads VS Code launch.json and resolves only debug adapters KeyceCode can actually start.</summary>
public static class VsCodeLaunchConfigurationService
{
    private const int MaximumFileBytes = 1_000_000;
    private const int MaximumConfigurations = 100;

    public static IReadOnlyList<VsCodeLaunchConfiguration> Read(string workspaceRoot)
    {
        var path = Path.Combine(Path.GetFullPath(workspaceRoot), ".vscode", "launch.json");
        if (!File.Exists(path)) return [];
        var info = new FileInfo(path);
        if (info.Length > MaximumFileBytes) throw new InvalidDataException(".vscode/launch.json dépasse la limite de 1 Mo.");
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("configurations", out var configs) || configs.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("launch.json doit contenir un tableau « configurations ».");
        if (configs.GetArrayLength() > MaximumConfigurations) throw new InvalidDataException("launch.json contient plus de 100 configurations.");
        var result = new List<VsCodeLaunchConfiguration>();
        foreach (var config in configs.EnumerateArray())
        {
            if (config.ValueKind != JsonValueKind.Object) continue;
            var name = String(config, "name");
            var type = String(config, "type");
            var request = String(config, "request");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type) || request is not ("launch" or "attach")) continue;
            result.Add(new VsCodeLaunchConfiguration(name, type, request, config.Clone()));
        }
        return result;
    }

    public static DebugAdapterAvailability ResolveAdapter(string type, string? pythonExecutable = null, string? userProfile = null)
    {
        if (type is "python" or "debugpy")
        {
            var python = pythonExecutable ?? FindOnPath("python.exe");
            var root = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var extension = FindLatestExtension(Path.Combine(root, ".vscode", "extensions"), "ms-python.debugpy-");
            var libraryRoot = extension is null ? null : Path.Combine(extension, "bundled", "libs");
            if (python is null) return new(type, false, "Python n’est pas installé ou absent du PATH. Installez Python 3.");
            if (libraryRoot is null || !Directory.Exists(Path.Combine(libraryRoot, "debugpy", "adapter")))
                return new(type, false, "L’adaptateur MIT debugpy n’est pas installé. Installez l’extension Python Debugger (ms-python.debugpy) ou un environnement Python contenant debugpy.");
            if (!HasMitDebugpyManifest(extension!))
                return new(type, false, "Le manifeste de l’adaptateur Python est absent ou ne confirme pas la licence MIT; KeyceCode ne le lancera pas automatiquement.");
            return new(type, true, $"debugpy prêt depuis {Path.GetFileName(extension)}; l’adaptateur sera lancé avec Python système.", python,
                ["-m", "debugpy.adapter"], new Dictionary<string, string?> { ["PYTHONPATH"] = libraryRoot });
        }

        if (type is "coreclr" or "clr")
            return new(type, false, "Le profil VS Code contient peut-être vsdbg, mais sa licence limite son usage aux produits Microsoft Visual Studio. KeyceCode ne le lance pas. Configurez un adaptateur redistribuable compatible DAP.");
        if (type is "cppdbg" or "cppvsdbg")
            return new(type, false, "La configuration C/C++ est détectée, mais KeyceCode ne lance pas l’adaptateur fourni par l’extension Microsoft sans intégration de licence et de dépendances.");
        if (type is "node" or "pwa-node" or "extensionHost")
            return new(type, false, "Aucun adaptateur Node DAP indépendant n’est configuré. Installez et configurez un adaptateur compatible DAP.");
        return new(type, false, $"Aucun adaptateur DAP configuré pour le type « {type} ». Les adaptateurs d’extensions VS Code ne sont pas exécutés automatiquement.");
    }

    public static JsonElement ExpandWorkspaceVariables(JsonElement configuration, string workspaceRoot, string? activeFile = null)
    {
        var root = Path.GetFullPath(workspaceRoot);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) WriteExpanded(writer, configuration, root, activeFile);
        using var result = JsonDocument.Parse(output.ToArray());
        return result.RootElement.Clone();
    }

    private static void WriteExpanded(Utf8JsonWriter writer, JsonElement element, string root, string? activeFile)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()) { writer.WritePropertyName(property.Name); WriteExpanded(writer, property.Value, root, activeFile); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var child in element.EnumerateArray()) WriteExpanded(writer, child, root, activeFile); writer.WriteEndArray(); break;
            case JsonValueKind.String:
                var value = element.GetString() ?? "";
                value = value.Replace("${workspaceFolder}", root, StringComparison.Ordinal).Replace("${workspaceFolderBasename}", Path.GetFileName(Path.TrimEndingDirectorySeparator(root)), StringComparison.Ordinal);
                if (activeFile is not null) value = value.Replace("${file}", Path.GetFullPath(activeFile), StringComparison.Ordinal).Replace("${fileBasename}", Path.GetFileName(activeFile), StringComparison.Ordinal).Replace("${fileDirname}", Path.GetDirectoryName(Path.GetFullPath(activeFile)) ?? root, StringComparison.Ordinal);
                writer.WriteStringValue(value); break;
            case JsonValueKind.Number: element.WriteTo(writer); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new InvalidDataException("Valeur JSON de launch.json non prise en charge.");
        }
    }

    private static string? String(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var candidate = Path.Combine(directory.Trim('"'), executable); if (File.Exists(candidate)) return candidate; }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static string? FindLatestExtension(string extensionDirectory, string prefix)
    {
        if (!Directory.Exists(extensionDirectory)) return null;
        return Directory.EnumerateDirectories(extensionDirectory, prefix + "*", SearchOption.TopDirectoryOnly)
            .Where(path => Directory.Exists(Path.Combine(path, "bundled", "libs", "debugpy", "adapter")))
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool HasMitDebugpyManifest(string extensionPath)
    {
        try
        {
            var manifestPath = Path.Combine(extensionPath, "package.json");
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 256_000) return false;
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath), new JsonDocumentOptions { MaxDepth = 16 });
            var root = manifest.RootElement;
            return String(root, "name") == "debugpy" && String(root, "publisher") == "ms-python" &&
                string.Equals(String(root, "license"), "MIT", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }
}
