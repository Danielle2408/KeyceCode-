using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KeyceCode.Services;

/// <summary>
/// Small Gemini client used by KeyceIA. Supply a key from secure application
/// settings, or set GEMINI_API_KEY in the process environment. Never commit keys.
/// </summary>
public sealed class GeminiService : IDisposable
{
    private static readonly Uri ApiRoot = new("https://generativelanguage.googleapis.com/v1beta/");
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string? _explicitApiKey;
    private readonly GeminiCredentialStore _credentialStore = new();
    private readonly string _model;

    public GeminiService(string? apiKey = null, string? model = null, HttpClient? httpClient = null)
    {
        _explicitApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _model = string.IsNullOrWhiteSpace(model)
            ? Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-3.8-flash"
            : model.Trim();
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
        _ownsClient = httpClient is null;
    }

    public Task<string> AskAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("La demande ne peut pas être vide.", nameof(prompt));

        return GenerateAsync(
            "Tu es KeyceIA, l'assistant de programmation de KeyceCode. Réponds en français par défaut. " +
            "Explique clairement et fournis du code prêt à adapter lorsque c'est utile.\n\n" + prompt,
            cancellationToken);
    }

    /// <summary>Requests a short continuation for the provided source context.</summary>
    public Task<string> CompleteAsync(string source, string? language = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Le contexte de code ne peut pas être vide.", nameof(source));

        var languageHint = string.IsNullOrWhiteSpace(language) ? "" : $"Langage : {language.Trim()}.\n";
        return GenerateAsync(
            "Tu es le moteur d'autocomplétion de KeyceCode. Continue le code fourni à l'endroit où il s'arrête. " +
            "Retourne uniquement le texte à insérer, sans bloc Markdown ni explication. " +
            "Respecte le langage, le style et le contexte existants.\n" + languageHint +
            "Contexte de code :\n" + source,
            cancellationToken, preserveWhitespace: true);
    }

    private async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken, bool preserveWhitespace = false)
    {
        var apiKey = _explicitApiKey ?? _credentialStore.Read() ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Clé Gemini absente. Définissez GEMINI_API_KEY ou fournissez une clé depuis les paramètres sécurisés de l'application.");

        var relativeUrl = $"models/{Uri.EscapeDataString(_model)}:generateContent";
        var payload = new Dictionary<string, object>
        {
            ["contents"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["parts"] = new[] { new Dictionary<string, string> { ["text"] = prompt } }
                }
            },
            ["generationConfig"] = new Dictionary<string, object>
            {
                ["temperature"] = 0.2,
                ["maxOutputTokens"] = 2048
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoot, relativeUrl))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-goog-api-key", apiKey);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = TryReadApiError(body);
            throw new HttpRequestException(
                $"Gemini a répondu {(int)response.StatusCode} ({response.ReasonPhrase}). {detail}".Trim());
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("candidates", out var candidates) &&
                candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                {
                    var result = new StringBuilder();
                    foreach (var part in parts.EnumerateArray())
                        if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                            result.Append(text.GetString());
                    if (result.Length > 0)
                        return preserveWhitespace ? result.ToString() : result.ToString().Trim();
                }
            }

            var reason = document.RootElement.TryGetProperty("promptFeedback", out var feedback) &&
                         feedback.TryGetProperty("blockReason", out var blockReason)
                ? $"La demande a été bloquée : {blockReason.GetString()}"
                : "Gemini n'a renvoyé aucun texte.";
            throw new InvalidOperationException(reason);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("La réponse de Gemini n'est pas un JSON valide.", ex);
        }
    }

    private static string TryReadApiError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
                return message.GetString() ?? string.Empty;
        }
        catch (JsonException) { }
        return "Vérifiez la clé, le modèle et la connexion réseau.";
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
