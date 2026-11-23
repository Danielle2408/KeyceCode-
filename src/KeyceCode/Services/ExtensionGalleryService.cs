using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KeyceCode.Services;

public sealed record GalleryExtension(
    string DisplayName,
    string Namespace,
    string Name,
    string Version,
    string Description,
    long DownloadCount,
    double? Rating,
    bool Verified,
    Uri DetailsUri)
{
    public string Identity => $"{Namespace}.{Name}@{Version}";
    public override string ToString() => $"{DisplayName}  ·  {Namespace}.{Name}  ·  {DownloadCount:N0} téléchargements";
}

public sealed record ExtensionDownloadProgress(string Phase, long BytesReceived, long? TotalBytes);

/// <summary>Queries Open VSX and downloads VSIX packages without executing them.</summary>
public sealed class ExtensionGalleryService : IDisposable
{
    private static readonly Uri RegistryRoot = new("https://open-vsx.org/");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private const long MaximumPackageBytes = 100L * 1024 * 1024;

    public async Task<IReadOnlyList<GalleryExtension>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var uri = new Uri(RegistryRoot, "api/-/search?query=" + Uri.EscapeDataString(query.Trim()) + "&size=50");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("extensions", out var extensions) || extensions.ValueKind != JsonValueKind.Array)
            return Array.Empty<GalleryExtension>();

        var results = new List<GalleryExtension>();
        foreach (var item in extensions.EnumerateArray())
        {
            var details = ReadString(item, "url");
            if (!TryRegistryUri(details, out var detailsUri)) continue;
            var publisher = ReadString(item, "namespace");
            var name = ReadString(item, "name");
            var version = ReadString(item, "version");
            if (publisher is null || name is null || version is null) continue;
            results.Add(new GalleryExtension(
                ReadString(item, "displayName") ?? name,
                publisher,
                name,
                version,
                ReadString(item, "description") ?? "Aucune description fournie.",
                ReadInt64(item, "downloadCount"),
                ReadDouble(item, "averageRating"),
                item.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True,
                detailsUri));
        }
        return results;
    }

    public async Task<string> DownloadAsync(GalleryExtension extension, IProgress<ExtensionDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extension);
        progress?.Report(new("Recherche du paquet Windows…", 0, null));
        var targetPlatform = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win32-x64",
            Architecture.Arm64 => "win32-arm64",
            Architecture.X86 => "win32-ia32",
            _ => throw new PlatformNotSupportedException("Cette architecture Windows n’est pas prise en charge par l’installation de VSIX.")
        };
        JsonDocument? metadata = null;
        string? publishedPlatform = null;
        Uri? downloadUri = null;
        foreach (var platform in new[] { targetPlatform, "universal" }.Distinct(StringComparer.Ordinal))
        {
            var metadataUri = new Uri(RegistryRoot,
                $"api/{Uri.EscapeDataString(extension.Namespace)}/{Uri.EscapeDataString(extension.Name)}/{platform}/{Uri.EscapeDataString(extension.Version)}");
            using var metadataResponse = await _http.GetAsync(metadataUri, cancellationToken);
            if (metadataResponse.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
            metadataResponse.EnsureSuccessStatusCode();
            var candidate = await JsonDocument.ParseAsync(await metadataResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (candidate.RootElement.TryGetProperty("files", out var files) && TryDownloadUri(ReadString(files, "download"), out var candidateUri))
            {
                metadata = candidate;
                downloadUri = candidateUri;
                publishedPlatform = ReadString(candidate.RootElement, "targetPlatform");
                break;
            }
            candidate.Dispose();
        }
        using (metadata)
        {
        if (metadata is null || downloadUri is null)
            throw new InvalidOperationException("Open VSX ne propose pas de paquet VSIX pour Windows ou une version universelle de cette extension.");
        if (publishedPlatform is not ("win32-x64" or "win32-arm64" or "win32-ia32" or "universal"))
            throw new InvalidOperationException($"Cette version est publiée pour {publishedPlatform ?? "une autre plateforme"}, pas pour {targetPlatform}.");
        if (!downloadUri.AbsolutePath.EndsWith(".vsix", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La galerie a renvoyé une adresse VSIX invalide.");

        using var response = await _http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri || !IsAllowedDownloadUri(finalUri))
            throw new InvalidOperationException("Le téléchargement a été redirigé vers un domaine non autorisé.");
        if (response.Content.Headers.ContentLength is > MaximumPackageBytes)
            throw new InvalidDataException("Le paquet dépasse la limite de téléchargement de 100 Mo.");
        var totalBytes = response.Content.Headers.ContentLength;
        progress?.Report(new("Téléchargement du VSIX…", 0, totalBytes));

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyceCode", "extensions", "downloads");
        Directory.CreateDirectory(directory);
        var safeName = string.Concat($"{extension.Namespace}.{extension.Name}-{extension.Version}.vsix".Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var destination = Path.Combine(directory, safeName);
        var temporary = destination + ".download";
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long written = 0;
                var lastProgress = Environment.TickCount64;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    written += read;
                    if (written > MaximumPackageBytes) throw new InvalidDataException("Le paquet dépasse la limite de téléchargement de 100 Mo.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (Environment.TickCount64 - lastProgress >= 100 || written == totalBytes)
                    {
                        progress?.Report(new("Téléchargement du VSIX…", written, totalBytes));
                        lastProgress = Environment.TickCount64;
                    }
                }
            }
            progress?.Report(new("VSIX téléchargé · vérification…", totalBytes ?? 0, totalBytes));
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { }
            throw;
        }
        }
    }

    private static bool TryRegistryUri(string? value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!) && IsRegistryUri(uri);
    private static bool TryDownloadUri(string? value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!) && IsAllowedDownloadUri(uri);
    private static bool IsRegistryUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals(RegistryRoot.Host, StringComparison.OrdinalIgnoreCase);
    private static bool IsAllowedDownloadUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals(RegistryRoot.Host, StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("openvsx.eclipsecontent.org", StringComparison.OrdinalIgnoreCase));
    private static string? ReadString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() : null;
    private static long ReadInt64(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.TryGetInt64(out var number) ? number : 0;
    private static double? ReadDouble(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.TryGetDouble(out var number) ? number : null;

    public void Dispose() => _http.Dispose();
}
