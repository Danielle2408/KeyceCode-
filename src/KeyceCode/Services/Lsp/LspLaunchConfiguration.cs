using System.Text.Json;

namespace KeyceCode.Services.Lsp;

public sealed record LspLaunchConfiguration(string ExecutablePath, IReadOnlyList<string> Arguments)
{
    /// <summary>Resolves saved settings first, then development environment overrides.</summary>
    public static LspLaunchConfiguration Resolve(string? savedExecutable, string? savedArgumentsJson,
        string? environmentExecutable = null, string? environmentArgumentsJson = null)
    {
        var executable = string.IsNullOrWhiteSpace(savedExecutable) ? environmentExecutable?.Trim() : savedExecutable.Trim();
        if (string.IsNullOrWhiteSpace(executable))
            return new LspLaunchConfiguration(string.Empty, Array.Empty<string>());

        var json = string.IsNullOrWhiteSpace(savedArgumentsJson) ? environmentArgumentsJson : savedArgumentsJson;
        if (string.IsNullOrWhiteSpace(json)) return new LspLaunchConfiguration(executable, Array.Empty<string>());

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 128)
            throw new JsonException("Les arguments LSP doivent être un tableau JSON de 128 chaînes maximum.");

        var arguments = new List<string>();
        foreach (var value in document.RootElement.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
                throw new JsonException("Chaque argument LSP doit être une chaîne JSON.");
            var argument = value.GetString() ?? string.Empty;
            if (argument.Length > 16_384) throw new JsonException("Un argument LSP dépasse 16 Kio.");
            arguments.Add(argument);
        }
        return new LspLaunchConfiguration(executable, arguments);
    }
}
