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
    private string _model;

    public GeminiService(string? apiKey = null, string? model = null, HttpClient? httpClient = null)
    {
        _explicitApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var configuredModel = string.IsNullOrWhiteSpace(model) ? Environment.GetEnvironmentVariable("GEMINI_MODEL") : model;
        _model = string.IsNullOrWhiteSpace(configuredModel) ? "gemini-3.5-flash-lite" : configuredModel.Trim();
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
        _ownsClient = httpClient is null;
    }

    /// <summary>Gemini Developer API model ID, without the "models/" prefix.</summary>
    public string Model
    {
        get => _model;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("L’identifiant du modèle Gemini ne peut pas être vide.", nameof(value));
            _model = value.Trim();
        }
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

        var relativeUrl = "interactions";
        var payload = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["input"] = prompt,
            // Do not persist code prompts on Google's side; each request is stateless.
            ["store"] = false,
            ["generation_config"] = new Dictionary<string, object>
            {
                ["temperature"] = 0.2,
                ["max_output_tokens"] = 2048
            }
        };

        string body;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoot, relativeUrl))
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-goog-api-key", apiKey);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) break;

            var transient = response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or
                System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.GatewayTimeout;
            var rateLimited = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
            var retryLimit = transient ? 2 : rateLimited ? 1 : 0;
            if (attempt < retryLimit)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(retryAfter.TotalMilliseconds, 250, 10_000)), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var detail = TryReadApiError(body);
            var diagnosis = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "La clé Gemini est invalide ou n’a pas accès à l’API.",
                System.Net.HttpStatusCode.NotFound => "Le modèle Gemini demandé est introuvable ou indisponible pour cette clé.",
                System.Net.HttpStatusCode.TooManyRequests => "Quota ou limite de requêtes Gemini atteint. Réessayez plus tard ou vérifiez les quotas du projet.",
                System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.GatewayTimeout =>
                    $"Le service ou le modèle Gemini est temporairement saturé/indisponible après plusieurs essais. Détail Google : {detail}",
                _ => detail
            };
            throw new HttpRequestException(
                $"Gemini HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {diagnosis}".Trim(), null, response.StatusCode);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            {
                var result = new StringBuilder();
                foreach (var step in steps.EnumerateArray())
                {
                    if (!step.TryGetProperty("type", out var type) || type.GetString() != "model_output" ||
                        !step.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var part in content.EnumerateArray())
                        if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                            result.Append(text.GetString());
                }
                if (result.Length > 0)
                    return preserveWhitespace ? result.ToString() : result.ToString().Trim();
            }

            // Keep accepting the legacy response shape while existing installations migrate.
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

            var reason = document.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                ? $"Gemini n’a renvoyé aucun texte (état : {status.GetString()})."
                : document.RootElement.TryGetProperty("promptFeedback", out var feedback) &&
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
