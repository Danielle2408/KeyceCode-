using System.IO;
using System.Text.Json;

namespace KeyceCode.Services.Lsp;

public sealed record LspDocumentEdits(string Path, IReadOnlyList<JsonElement> Edits);

/// <summary>Validates LSP WorkspaceEdit targets and applies UTF-16 text edits without side effects.</summary>
public static class LspWorkspaceEditService
{
    private const int MaximumDocuments = 500;
    private const int MaximumEditsPerDocument = 20_000;

    public static IReadOnlyList<LspDocumentEdits> Parse(JsonElement workspaceEdit, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        if (workspaceEdit.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Le serveur LSP n’a pas renvoyé un WorkspaceEdit valide.");
        var workspace = new WorkspaceService(workspaceRoot);
        var byPath = new Dictionary<string, List<JsonElement>>(StringComparer.OrdinalIgnoreCase);

        if (workspaceEdit.TryGetProperty("changes", out var changes))
        {
            if (changes.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Le champ changes du WorkspaceEdit est invalide.");
            foreach (var change in changes.EnumerateObject())
                AddDocumentEdits(change.Name, change.Value, workspace, byPath);
        }

        if (workspaceEdit.TryGetProperty("documentChanges", out var documentChanges))
        {
            if (documentChanges.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Le champ documentChanges du WorkspaceEdit est invalide.");
            foreach (var change in documentChanges.EnumerateArray())
            {
                if (change.ValueKind != JsonValueKind.Object ||
                    !change.TryGetProperty("textDocument", out var textDocument) ||
                    !textDocument.TryGetProperty("uri", out var uriElement) ||
                    uriElement.ValueKind != JsonValueKind.String ||
                    !change.TryGetProperty("edits", out var textEdits))
                    throw new InvalidDataException("Les opérations de création, déplacement ou suppression de fichiers LSP ne sont pas prises en charge.");
                AddDocumentEdits(uriElement.GetString()!, textEdits, workspace, byPath);
            }
        }

        return byPath.Select(pair => new LspDocumentEdits(pair.Key, pair.Value.ToArray())).ToArray();
    }

    public static string ApplyTextEdits(string source, IReadOnlyList<JsonElement> edits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count > MaximumEditsPerDocument)
            throw new InvalidDataException("Le serveur LSP a renvoyé trop d’éditions pour un document.");

        var parsed = new List<(int Start, int End, string Text)>(edits.Count);
        foreach (var edit in edits)
        {
            if (edit.ValueKind != JsonValueKind.Object || !edit.TryGetProperty("range", out var range) ||
                !TryGetOffset(source, range, "start", out var start) || !TryGetOffset(source, range, "end", out var end) ||
                start > end || !edit.TryGetProperty("newText", out var newText) || newText.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Le serveur LSP a renvoyé une édition de texte invalide.");
            parsed.Add((start, end, newText.GetString() ?? string.Empty));
        }

        var ascending = parsed.OrderBy(edit => edit.Start).ThenBy(edit => edit.End).ToArray();
        for (var index = 1; index < ascending.Length; index++)
            if (ascending[index].Start < ascending[index - 1].End || ascending[index].Start == ascending[index - 1].Start)
                throw new InvalidDataException("Le serveur LSP a renvoyé des éditions qui se chevauchent ou sont ambiguës.");

        var result = new System.Text.StringBuilder(source);
        foreach (var edit in ascending.OrderByDescending(edit => edit.Start))
        {
            result.Remove(edit.Start, edit.End - edit.Start);
            result.Insert(edit.Start, edit.Text);
        }
        return result.ToString();
    }

    private static void AddDocumentEdits(string uriText, JsonElement edits, WorkspaceService workspace,
        Dictionary<string, List<JsonElement>> byPath)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || !uri.IsFile)
            throw new UnauthorizedAccessException("Le serveur LSP a ciblé une URI qui n’est pas un fichier local.");
        var path = workspace.ResolvePath(uri.LocalPath);
        if (!File.Exists(path)) throw new FileNotFoundException("Le serveur LSP a ciblé un fichier absent du workspace.", path);
        if (edits.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("La liste d’éditions LSP est invalide.");
        if (!byPath.TryGetValue(path, out var collected))
        {
            if (byPath.Count >= MaximumDocuments)
                throw new InvalidDataException("Le WorkspaceEdit cible un nombre excessif de fichiers.");
            collected = [];
            byPath.Add(path, collected);
        }
        foreach (var edit in edits.EnumerateArray()) collected.Add(edit.Clone());
        if (collected.Count > MaximumEditsPerDocument)
            throw new InvalidDataException("Le serveur LSP a renvoyé trop d’éditions pour un document.");
    }

    private static bool TryGetOffset(string text, JsonElement range, string point, out int offset)
    {
        offset = 0;
        if (range.ValueKind != JsonValueKind.Object || !range.TryGetProperty(point, out var position) ||
            !position.TryGetProperty("line", out var lineJson) || !lineJson.TryGetInt32(out var line) || line < 0 ||
            !position.TryGetProperty("character", out var characterJson) || !characterJson.TryGetInt32(out var character) || character < 0)
            return false;
        for (var currentLine = 0; currentLine < line; currentLine++)
        {
            var newline = text.IndexOf('\n', offset);
            if (newline < 0) return false;
            offset = newline + 1;
        }
        var lineEnd = text.IndexOf('\n', offset);
        if (lineEnd < 0) lineEnd = text.Length;
        var lineLength = lineEnd - offset;
        if (lineLength > 0 && text[lineEnd - 1] == '\r') lineLength--;
        if (character > lineLength) return false;
        offset += character;
        return true;
    }
}
