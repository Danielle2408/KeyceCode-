using System.IO;
using System.Text.Json;

namespace KeyceCode.Services.Lsp;

public sealed record LspWorkspaceSymbol(string Name, int Kind, string? ContainerName, string Path, int Line, int Character)
{
    public string Label(string workspaceRoot)
    {
        var relative = System.IO.Path.GetRelativePath(workspaceRoot, Path).Replace('\\', '/');
        var container = string.IsNullOrWhiteSpace(ContainerName) ? string.Empty : $" · {ContainerName}";
        return $"{Name}{container} · {KindName(Kind)} · {relative}:{Line + 1}";
    }

    private static string KindName(int kind) => kind switch
    {
        1 => "Fichier", 2 => "Module", 3 => "Namespace", 4 => "Package", 5 => "Classe",
        6 => "Méthode", 7 => "Propriété", 8 => "Champ", 9 => "Constructeur", 10 => "Énumération",
        11 => "Interface", 12 => "Fonction", 13 => "Variable", 14 => "Constante", 15 => "Chaîne",
        16 => "Nombre", 17 => "Booléen", 18 => "Tableau", 19 => "Objet", 20 => "Clé",
        21 => "Null", 22 => "Énumération", 23 => "Membre", 24 => "Événement", 25 => "Opérateur", 26 => "Type",
        _ => "Symbole"
    };
}

/// <summary>Parses bounded LSP workspace/symbol results and keeps only symbols in the workspace.</summary>
public static class LspWorkspaceSymbolService
{
    public const int MaximumResults = 2_000;

    public static IReadOnlyList<LspWorkspaceSymbol> Parse(JsonElement response, string workspaceRoot)
    {
        if (response.ValueKind != JsonValueKind.Array) return [];
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var results = new List<LspWorkspaceSymbol>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in response.EnumerateArray())
        {
            if (results.Count >= MaximumResults) break;
            if (symbol.ValueKind != JsonValueKind.Object || !ReadString(symbol, "name", out var name) || name.Length > 512 ||
                !symbol.TryGetProperty("location", out var location) || location.ValueKind != JsonValueKind.Object)
                continue;

            var uriText = ReadString(location, "uri", out var uriValue) ? uriValue
                : ReadString(location, "targetUri", out var targetUri) ? targetUri : null;
            if (uriText is null || !Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || !uri.IsFile) continue;

            string fullPath;
            try { fullPath = Path.GetFullPath(uri.LocalPath); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            var relativePath = Path.GetRelativePath(root, fullPath);
            if (Path.IsPathRooted(relativePath) || relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(fullPath)) continue;

            var range = location.TryGetProperty("range", out var locationRange) ? locationRange
                : symbol.TryGetProperty("range", out var symbolRange) ? symbolRange : default;
            var line = 0;
            var character = 0;
            if (range.ValueKind == JsonValueKind.Object && range.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.Object)
            {
                if (start.TryGetProperty("line", out var lineElement) && (!lineElement.TryGetInt32(out line) || line < 0)) continue;
                if (start.TryGetProperty("character", out var characterElement) && (!characterElement.TryGetInt32(out character) || character < 0)) continue;
            }
            if (line > 1_000_000 || character > 1_000_000) continue;

            var kind = symbol.TryGetProperty("kind", out var kindElement) && kindElement.TryGetInt32(out var kindNumber) ? kindNumber : 0;
            var container = ReadString(symbol, "containerName", out var containerName) ? containerName : null;
            var identity = $"{fullPath}|{line}|{character}|{name}|{container}";
            if (!seen.Add(identity)) continue;
            results.Add(new LspWorkspaceSymbol(name, kind, container, fullPath, line, character));
        }
        return results;
    }

    private static bool ReadString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var candidate) || candidate.ValueKind != JsonValueKind.String) return false;
        value = candidate.GetString() ?? string.Empty;
        return value.Length > 0;
    }
}
