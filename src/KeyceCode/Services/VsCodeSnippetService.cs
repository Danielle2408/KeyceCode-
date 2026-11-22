using System.IO;
using System.Text.Json;

namespace KeyceCode.Services;

public sealed record MonacoUserSnippet(string Label, string Prefix, string Body, string Description, string[] Scopes);
public sealed record SnippetImportResult(int ImportedFiles, int ImportedSnippets, int SkippedFiles, int SkippedSnippets);

/// <summary>Copies and reads VS Code user snippets as inert JSON data. No snippet content is executed.</summary>
public sealed class VsCodeSnippetService
{
    public const int MaximumFiles = 100;
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    public const int MaximumSnippets = 500;
    private const int MaximumBodyLength = 32 * 1024;
    private readonly string _sourceDirectory;
    private readonly string _destinationDirectory;

    public VsCodeSnippetService(string? sourceDirectory = null, string? destinationDirectory = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _sourceDirectory = Path.GetFullPath(sourceDirectory ?? Path.Combine(appData, "Code", "User", "snippets"));
        _destinationDirectory = Path.GetFullPath(destinationDirectory ?? Path.Combine(appData, "KeyceCode", "snippets"));
    }

    public string SourceDirectory => _sourceDirectory;
    public string DestinationDirectory => _destinationDirectory;

    public SnippetImportResult ImportFromVsCode(string? sourceDirectory = null)
    {
        var source = Path.GetFullPath(sourceDirectory ?? _sourceDirectory);
        if (!Directory.Exists(source)) return new(0, 0, 0, 0);
        if (IsSameOrNested(source, _destinationDirectory)) throw new InvalidDataException("Le dossier source ne peut pas être le dossier KeyceCode.");
        Directory.CreateDirectory(_destinationDirectory);
        var files = Directory.EnumerateFiles(source, "*.json", SearchOption.TopDirectoryOnly).Take(MaximumFiles + 1).ToArray();
        if (files.Length > MaximumFiles) files = files.Take(MaximumFiles).ToArray();
        var importedFiles = 0; var importedSnippets = 0; var skippedFiles = 0; var skippedSnippets = 0;
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > MaximumFileBytes) { skippedFiles++; continue; }
                var parsed = ParseFile(file);
                var remaining = MaximumSnippets - importedSnippets;
                if (remaining <= 0) { skippedFiles++; continue; }
                var accepted = parsed.Snippets.Take(remaining).ToArray();
                skippedSnippets += parsed.Skipped + parsed.Snippets.Count - accepted.Length;
                var destination = Path.Combine(_destinationDirectory, Path.GetFileName(file));
                File.WriteAllText(destination, JsonSerializer.Serialize(accepted.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.First().Raw), new JsonSerializerOptions { WriteIndented = true }));
                importedFiles++; importedSnippets += accepted.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            { skippedFiles++; }
        }
        return new(importedFiles, importedSnippets, skippedFiles, skippedSnippets);
    }

    public IReadOnlyList<MonacoUserSnippet> Load()
    {
        if (!Directory.Exists(_destinationDirectory)) return [];
        var result = new List<MonacoUserSnippet>();
        foreach (var file in Directory.EnumerateFiles(_destinationDirectory, "*.json", SearchOption.TopDirectoryOnly).Take(MaximumFiles))
        {
            try
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || new FileInfo(file).Length > MaximumFileBytes) continue;
                var parsed = ParseFile(file, MaximumSnippets - result.Count);
                result.AddRange(parsed.Snippets.Select(s => s.Value));
                if (result.Count >= MaximumSnippets) break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) { }
        }
        return result;
    }

    private static (List<ParsedSnippet> Snippets, int Skipped) ParseFile(string file, int maximum = MaximumSnippets)
    {
        var bytes = File.ReadAllBytes(file);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Fichier de snippets trop volumineux.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Le fichier de snippets doit contenir un objet JSON.");
        var output = new List<ParsedSnippet>(); var skipped = 0;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (output.Count >= maximum) { skipped++; continue; }
            if (property.Value.ValueKind != JsonValueKind.Object || !TryStringArray(property.Value, "prefix", out var prefixes) || !TryBody(property.Value, out var body)) { skipped++; continue; }
            var scope = ReadString(property.Value, "scope");
            var scopes = (scope ?? InferScope(Path.GetFileName(file))).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();
            var description = ReadString(property.Value, "description") ?? property.Name;
            using var rawDocument = JsonDocument.Parse(property.Value.GetRawText(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 32 });
            var raw = rawDocument.RootElement.Clone();
            foreach (var prefix in prefixes.Where(p => p.Length is > 0 and <= 256).Take(32))
            {
                if (output.Count >= maximum) { skipped++; break; }
                output.Add(new(property.Name, new(property.Name, prefix, body, description[..Math.Min(description.Length, 1000)], scopes), raw));
            }
        }
        return (output, skipped);
    }

    private static bool TryStringArray(JsonElement item, string name, out string[] values)
    {
        values = [];
        if (!item.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind == JsonValueKind.String) values = [value.GetString() ?? ""];
        else if (value.ValueKind == JsonValueKind.Array) values = value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Take(32).ToArray();
        return values.Length > 0;
    }

    private static bool TryBody(JsonElement item, out string body)
    {
        body = "";
        if (!item.TryGetProperty("body", out var value)) return false;
        if (value.ValueKind == JsonValueKind.String) body = value.GetString() ?? "";
        else if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)) body = string.Join("\n", value.EnumerateArray().Select(x => x.GetString() ?? ""));
        else return false;
        return body.Length <= MaximumBodyLength;
    }

    private static string? ReadString(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string InferScope(string filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename);
        return name.Equals("global", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".code-snippets", StringComparison.OrdinalIgnoreCase)
            ? "" : name;
    }
    private static bool IsSameOrNested(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private sealed record ParsedSnippet(string Key, MonacoUserSnippet Value, JsonElement Raw);
}
