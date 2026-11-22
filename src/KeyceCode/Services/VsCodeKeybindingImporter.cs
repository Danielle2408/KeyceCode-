using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KeyceCode.Services;

/// <summary>A supported, editor-scoped VS Code keybinding mapped to a KeyceCode action.</summary>
public sealed record ImportedKeybinding(string Key, string Command, string? When);

public sealed record VsCodeKeybindingImportResult(
    IReadOnlyList<ImportedKeybinding> Keybindings,
    int SkippedCount,
    string SourcePath,
    int OverriddenCount = 0);

/// <summary>
/// Reads only a bounded subset of VS Code keybindings and maps explicit command IDs to
/// KeyceCode operations. It never evaluates VS Code's `when` expression language.
/// </summary>
public static partial class VsCodeKeybindingImporter
{
    public const int MaximumFileBytes = 1_048_576;
    public const int MaximumEntries = 5_000;

    private static readonly IReadOnlyDictionary<string, string> SupportedCommands =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workbench.action.files.save"] = "save",
            ["workbench.action.quickOpen"] = "quickOpen",
            ["workbench.action.showCommands"] = "commandPalette",
            ["actions.find"] = "find",
            ["editor.action.startFindReplaceAction"] = "replace",
            ["editor.action.formatDocument"] = "formatDocument",
            ["editor.action.formatSelection"] = "formatSelection",
            ["editor.action.commentLine"] = "commentLine",
            ["editor.action.removeCommentLine"] = "uncommentLine",
            ["editor.action.insertCursorBelow"] = "insertCursorBelow",
            ["editor.action.insertCursorAbove"] = "insertCursorAbove",
            ["editor.action.codeAction"] = "codeActions",
            ["editor.action.rename"] = "rename",
            ["undo"] = "undo",
            ["redo"] = "redo",
            ["editor.action.selectAll"] = "selectAll",
            ["editor.fold"] = "fold",
            ["editor.unfold"] = "unfold"
        };

    private static readonly HashSet<string> SupportedWhenContexts = new(StringComparer.Ordinal)
    {
        "editorTextFocus", "textInputFocus", "editorFocus"
    };

    [GeneratedRegex("^(?:(?:ctrl|shift|alt|win|meta|cmd|option)\\+)*(?:[a-z0-9]|f(?:[1-9]|1[0-2])|space|enter|escape|tab|backspace|delete|insert|home|end|pageup|pagedown|up|down|left|right|numpad[0-9]|numpad_add|numpad_subtract|numpad_multiply|numpad_divide|numpad_decimal|numpad_enter|[;,./\\\\'`\\[\\]\\-=])(?:\\s+(?:(?:ctrl|shift|alt|win|meta|cmd|option)\\+)*(?:[a-z0-9]|f(?:[1-9]|1[0-2])|space|enter|escape|tab|backspace|delete|insert|home|end|pageup|pagedown|up|down|left|right|numpad[0-9]|numpad_add|numpad_subtract|numpad_multiply|numpad_divide|numpad_decimal|numpad_enter|[;,./\\\\'`\\[\\]\\-=])){0,1}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex KeyChordPattern();

    public static string DefaultKeybindingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "keybindings.json");

    public static string DefaultStoragePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyceCode", "keybindings.json");

    public static void SaveImported(VsCodeKeybindingImportResult result, string? storagePath = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        storagePath ??= DefaultStoragePath;
        var fullPath = Path.GetFullPath(storagePath);
        var folder = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException("Le chemin de stockage des raccourcis est invalide.");
        Directory.CreateDirectory(folder);
        var tempPath = fullPath + ".tmp";
        try
        {
            var safeBindings = result.Keybindings
                .Where(binding => IsSupportedMappedCommand(binding.Command) && TryNormalizeKey(binding.Key, out _) &&
                    (binding.When is null || SupportedWhenContexts.Contains(binding.When)))
                .Take(MaximumEntries)
                .ToArray();
            var json = JsonSerializer.Serialize(safeBindings, new JsonSerializerOptions { WriteIndented = true });
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
                throw new InvalidDataException("Les raccourcis importés dépassent la limite de stockage.");
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (IOException) { }
        }
    }

    public static IReadOnlyList<ImportedKeybinding> LoadSaved(string? storagePath = null)
    {
        storagePath ??= DefaultStoragePath;
        var fullPath = Path.GetFullPath(storagePath);
        if (!File.Exists(fullPath)) return [];
        if (new FileInfo(fullPath).Length > MaximumFileBytes) return [];
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > MaximumEntries) return [];
            var result = new List<ImportedKeybinding>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !TryGetBoundedString(entry, "Key", 80, out var key) ||
                    !TryGetBoundedString(entry, "Command", 80, out var command) ||
                    !IsSupportedMappedCommand(command) ||
                    !TryNormalizeKey(key, out var normalizedKey) ||
                    !TryGetSupportedWhen(entry, out var when)) continue;
                result.Add(new ImportedKeybinding(normalizedKey, command, when));
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return [];
        }
    }

    public static VsCodeKeybindingImportResult Import(string keybindingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keybindingsPath);
        var fullPath = Path.GetFullPath(keybindingsPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Fichier keybindings.json introuvable.", fullPath);
        if (info.Length > MaximumFileBytes) throw new InvalidDataException("keybindings.json dépasse la limite de 1 Mio.");

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 24
        });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("keybindings.json doit contenir un tableau JSON.");

        var result = new List<ImportedKeybinding>();
        var skipped = 0;
        var overridden = 0;
        var examined = 0;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (++examined > MaximumEntries)
                throw new InvalidDataException($"keybindings.json contient plus de {MaximumEntries} entrées.");
            if (entry.ValueKind != JsonValueKind.Object ||
                !TryGetBoundedString(entry, "key", 80, out var key) ||
                !TryGetBoundedString(entry, "command", 200, out var command) ||
                !SupportedCommands.TryGetValue(command, out var mappedCommand) ||
                !TryNormalizeKey(key, out var normalizedKey) ||
                !TryGetSupportedWhen(entry, out var when))
            {
                skipped++;
                continue;
            }

            var collisionIndex = result.FindIndex(existing =>
                string.Equals(existing.Key, normalizedKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.When, when, StringComparison.Ordinal));
            if (collisionIndex >= 0)
            {
                result.RemoveAt(collisionIndex);
                overridden++;
            }
            result.Add(new ImportedKeybinding(normalizedKey, mappedCommand, when));
        }

        return new VsCodeKeybindingImportResult(result, skipped, fullPath, overridden);
    }

    private static bool TryGetBoundedString(JsonElement element, string property, int maxLength, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var jsonValue) || jsonValue.ValueKind != JsonValueKind.String) return false;
        value = jsonValue.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0 && value.Length <= maxLength;
    }

    private static bool TryNormalizeKey(string key, out string normalized)
    {
        normalized = string.Empty;
        var candidate = Regex.Replace(key.Trim().ToLowerInvariant(), @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!KeyChordPattern().IsMatch(candidate)) return false;
        normalized = Regex.Replace(candidate, @"\bcmd\b|\boption\b", token => token.Value == "cmd" ? "ctrl" : "alt", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return true;
    }

    private static bool IsSupportedMappedCommand(string command) => SupportedCommands.Values.Contains(command, StringComparer.Ordinal);

    private static bool TryGetSupportedWhen(JsonElement entry, out string? when)
    {
        when = null;
        if (!entry.TryGetProperty("when", out var whenElement) || whenElement.ValueKind == JsonValueKind.Null) return true;
        if (whenElement.ValueKind != JsonValueKind.String) return false;
        var expression = whenElement.GetString()?.Trim();
        if (string.IsNullOrEmpty(expression)) return true;
        if (!SupportedWhenContexts.Contains(expression)) return false;
        when = expression;
        return true;
    }
}
