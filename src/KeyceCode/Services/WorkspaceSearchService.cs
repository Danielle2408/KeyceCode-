using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KeyceCode.Services;

public sealed record WorkspaceSearchOptions(
    string Query,
    string? IncludePattern = null,
    string? ExcludePattern = null,
    bool UseRegex = false,
    bool MatchCase = false,
    bool WholeWord = false);

public sealed record WorkspaceSearchHit(string Path, string RelativePath, int Line, int Column, string Text, string Match);
public sealed record WorkspaceSearchResult(IReadOnlyList<WorkspaceSearchHit> Hits, int FilesSearched, int FilesSkipped, bool Truncated);
public sealed record WorkspaceReplacePreviewFile(string Path, string RelativePath, int MatchCount, string Preview);
public sealed record WorkspaceReplacePreview(IReadOnlyList<WorkspaceReplacePreviewFile> Files, int TotalMatches, int FilesSearched, int FilesSkipped, bool Truncated, string Query, string Replacement);
public sealed record WorkspaceReplaceApplyResult(int FilesChanged, int MatchesReplaced);

/// <summary>Bounded workspace search and preview-first multi-file replacement.</summary>
public sealed class WorkspaceSearchService
{
    public const int MaximumFiles = 10_000;
    public const int MaximumResults = 5_000;
    public const int MaximumMatches = 20_000;
    public const long MaximumFileBytes = 2_000_000;
    public const long MaximumTotalReadBytes = 128_000_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private sealed record PlannedFile(string FullPath, string RelativePath, byte[] OriginalHash, byte[] NewBytes, int MatchCount);
    private readonly Dictionary<WorkspaceReplacePreview, IReadOnlyList<PlannedFile>> _plans = new(ReferenceEqualityComparer.Instance);

    public async Task<WorkspaceSearchResult> SearchAsync(WorkspaceService workspace, WorkspaceSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var regex = CreateRegex(options);
        var hits = new List<WorkspaceSearchHit>();
        var filesSearched = 0;
        var filesSkipped = 0;
        var truncated = false;
        long totalBytes = 0;

        foreach (var (path, relativePath) in EnumerateFiles(await workspace.GetEntriesAsync(cancellationToken).ConfigureAwait(false), workspace.RootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesPatterns(relativePath, options.IncludePattern, options.ExcludePattern)) continue;
            if (filesSearched >= MaximumFiles || hits.Count >= MaximumResults || totalBytes >= MaximumTotalReadBytes)
            {
                truncated = true;
                break;
            }
            try
            {
                var bytes = await ReadCandidateAsync(workspace, path, totalBytes, cancellationToken).ConfigureAwait(false);
                if (bytes is null) { filesSkipped++; continue; }
                totalBytes += bytes.Length;
                filesSearched++;
                var text = DecodeText(bytes, out _, out _);
                foreach (var match in FindMatches(regex, text))
                {
                    var (line, column, lineText) = GetLine(text, match.Index);
                    hits.Add(new WorkspaceSearchHit(path, relativePath, line, column, lineText.Trim(), match.Value));
                    if (hits.Count >= MaximumResults) { truncated = true; break; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or RegexMatchTimeoutException)
            {
                filesSkipped++;
            }
        }

        return new WorkspaceSearchResult(hits, filesSearched, filesSkipped, truncated);
    }

    public async Task<WorkspaceReplacePreview> PreviewReplaceAsync(WorkspaceService workspace, WorkspaceSearchOptions options, string replacement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(replacement);
        var regex = CreateRegex(options);
        var files = new List<WorkspaceReplacePreviewFile>();
        var plans = new List<PlannedFile>();
        var totalMatches = 0;
        var filesSearched = 0;
        var filesSkipped = 0;
        var truncated = false;
        long totalBytes = 0;

        foreach (var (path, relativePath) in EnumerateFiles(await workspace.GetEntriesAsync(cancellationToken).ConfigureAwait(false), workspace.RootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesPatterns(relativePath, options.IncludePattern, options.ExcludePattern)) continue;
            if (filesSearched >= MaximumFiles || totalMatches >= MaximumMatches || totalBytes >= MaximumTotalReadBytes)
            {
                truncated = true;
                break;
            }
            try
            {
                var bytes = await ReadCandidateAsync(workspace, path, totalBytes, cancellationToken).ConfigureAwait(false);
                if (bytes is null) { filesSkipped++; continue; }
                totalBytes += bytes.Length;
                filesSearched++;
                var text = DecodeText(bytes, out var encoding, out var preamble);
                var matches = FindMatches(regex, text).Take(MaximumMatches - totalMatches + 1).ToList();
                if (matches.Count == 0) continue;
                if (totalMatches + matches.Count > MaximumMatches)
                {
                    matches = matches.Take(MaximumMatches - totalMatches).ToList();
                    truncated = true;
                }
                var newText = ReplaceMatches(regex, text, replacement);
                var newBytes = Concat(preamble, encoding.GetBytes(newText));
                var count = matches.Count;
                totalMatches += count;
                plans.Add(new PlannedFile(path, relativePath, SHA256.HashData(bytes), newBytes, count));
                files.Add(new WorkspaceReplacePreviewFile(path, relativePath, count, BuildChangePreview(text, matches, replacement)));
                if (truncated) break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or RegexMatchTimeoutException)
            {
                filesSkipped++;
            }
        }

        var preview = new WorkspaceReplacePreview(files, totalMatches, filesSearched, filesSkipped, truncated, options.Query, replacement);
        lock (_plans)
        {
            _plans.Clear();
            _plans[preview] = plans;
        }
        return preview;
    }

    /// <summary>Applies only the exact preview object returned above and aborts if any source file changed since preview.</summary>
    public async Task<WorkspaceReplaceApplyResult> ApplyPreviewAsync(WorkspaceService workspace, WorkspaceReplacePreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(preview);
        IReadOnlyList<PlannedFile> plans;
        lock (_plans)
        {
            if (!_plans.Remove(preview, out plans!)) throw new InvalidOperationException("Cette prévisualisation est expirée ou a déjà été appliquée.");
        }
        if (plans.Count == 0) return new(0, 0);

        var staged = new List<(PlannedFile Plan, string Temporary, string Backup)>();
        var replaced = new List<(PlannedFile Plan, string Backup)>();
        try
        {
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var safePath = workspace.ResolvePath(plan.RelativePath);
                if (!string.Equals(Path.GetFullPath(safePath), Path.GetFullPath(plan.FullPath), StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("Le chemin d’un fichier a changé depuis la prévisualisation.");
                var info = new FileInfo(safePath);
                if (!info.Exists || info.Length > MaximumFileBytes || (info.Attributes & FileAttributes.ReadOnly) != 0)
                    throw new IOException($"Le fichier {plan.RelativePath} a été supprimé, agrandi ou est en lecture seule.");
                var originalBytes = await File.ReadAllBytesAsync(safePath, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(originalBytes), plan.OriginalHash))
                    throw new IOException($"Le fichier {plan.RelativePath} a changé depuis la prévisualisation. Relancez la recherche.");
                if (plan.NewBytes.LongLength > MaximumFileBytes)
                    throw new IOException($"Le résultat de remplacement de {plan.RelativePath} dépasse la limite de {MaximumFileBytes / 1_000_000} Mo.");
                var directory = Path.GetDirectoryName(safePath)!;
                var nonce = Guid.NewGuid().ToString("N");
                var temporary = Path.Combine(directory, $".keycecode-replace-{nonce}.tmp");
                var backup = Path.Combine(directory, $".keycecode-replace-{nonce}.bak");
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await stream.WriteAsync(plan.NewBytes, cancellationToken).ConfigureAwait(false);
                staged.Add((plan, temporary, backup));
            }

            foreach (var item in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var safePath = workspace.ResolvePath(item.Plan.RelativePath);
                var current = await File.ReadAllBytesAsync(safePath, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(current), item.Plan.OriginalHash))
                    throw new IOException($"Le fichier {item.Plan.RelativePath} a changé pendant l’application. Aucun changement restant n’est appliqué.");
                File.Replace(item.Temporary, safePath, item.Backup, ignoreMetadataErrors: true);
                replaced.Add((item.Plan, item.Backup));
            }

            foreach (var item in staged)
            {
                TryDelete(item.Temporary);
                TryDelete(item.Backup);
            }
            return new(replaced.Count, replaced.Sum(item => item.Plan.MatchCount));
        }
        catch
        {
            foreach (var item in replaced.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(item.Backup)) File.Replace(item.Backup, item.Plan.FullPath, null, ignoreMetadataErrors: true);
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    // Keep the backup for manual recovery when rollback itself is blocked.
                }
            }
            foreach (var item in staged) TryDelete(item.Temporary);
            throw;
        }
    }

    private static IEnumerable<(string FullPath, string RelativePath)> EnumerateFiles(IReadOnlyList<WorkspaceEntry> entries, string root)
    {
        var pending = new Stack<WorkspaceEntry>(entries.Reverse());
        while (pending.TryPop(out var entry))
        {
            if (entry.IsDirectory)
            {
                for (var index = entry.Children.Count - 1; index >= 0; index--) pending.Push(entry.Children[index]);
            }
            else yield return (entry.FullPath, entry.RelativePath.Replace('\\', '/'));
        }
    }

    private static async Task<byte[]?> ReadCandidateAsync(WorkspaceService workspace, string path, long totalBytes, CancellationToken cancellationToken)
    {
        var safePath = workspace.ResolvePath(path);
        var info = new FileInfo(safePath);
        if (!info.Exists || info.Length > MaximumFileBytes || totalBytes + info.Length > MaximumTotalReadBytes) return null;
        var bytes = await File.ReadAllBytesAsync(safePath, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MaximumFileBytes || bytes.AsSpan().IndexOf((byte)0) >= 0 && !HasUtf16Bom(bytes)) return null;
        return bytes;
    }

    private static bool HasUtf16Bom(ReadOnlySpan<byte> bytes) => bytes.StartsWith(new byte[] { 0xFF, 0xFE }) || bytes.StartsWith(new byte[] { 0xFE, 0xFF });

    private static string DecodeText(byte[] bytes, out Encoding encoding, out byte[] preamble)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = StrictUtf8; preamble = [0xEF, 0xBB, 0xBF]; return StrictUtf8.GetString(bytes, 3, bytes.Length - 3); }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); preamble = [0xFF, 0xFE]; return encoding.GetString(bytes, 2, bytes.Length - 2); }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); preamble = [0xFE, 0xFF]; return encoding.GetString(bytes, 2, bytes.Length - 2); }
        encoding = StrictUtf8;
        preamble = [];
        return StrictUtf8.GetString(bytes);
    }

    private static Regex CreateRegex(WorkspaceSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Query);
        var pattern = options.UseRegex ? options.Query : Regex.Escape(options.Query);
        if (options.WholeWord) pattern = $"(?<![\\p{{L}}\\p{{N}}_])(?:{pattern})(?![\\p{{L}}\\p{{N}}_])";
        return new Regex(pattern, (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, RegexTimeout);
    }

    private static IEnumerable<Match> FindMatches(Regex regex, string text)
    {
        var match = regex.Match(text);
        while (match.Success)
        {
            yield return match;
            match = match.NextMatch();
        }
    }

    private static string ReplaceMatches(Regex regex, string text, string replacement) => regex.Replace(text, replacement);

    private static bool MatchesPatterns(string path, string? includePattern, string? excludePattern)
    {
        var include = SplitPatterns(includePattern);
        var exclude = SplitPatterns(excludePattern);
        if (include.Count > 0 && !include.Any(pattern => GlobMatches(path, pattern))) return false;
        return !exclude.Any(pattern => GlobMatches(path, pattern));
    }

    private static List<string> SplitPatterns(string? patterns) => string.IsNullOrWhiteSpace(patterns)
        ? []
        : patterns.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(50).ToList();

    private static bool GlobMatches(string path, string glob)
    {
        var normalized = path.Replace('\\', '/');
        var value = glob.Trim().Replace('\\', '/');
        if (value.Length == 0) return false;
        var escaped = Regex.Escape(value).Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", "[^/]", StringComparison.Ordinal);
        var regex = "^" + escaped + "$";
        return Regex.IsMatch(normalized, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ||
               (!value.Contains('/') && Regex.IsMatch(Path.GetFileName(normalized), regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
    }

    private static (int Line, int Column, string Text) GetLine(string text, int index)
    {
        var line = 1;
        var start = 0;
        for (var i = 0; i < Math.Min(index, text.Length); i++) if (text[i] == '\n') { line++; start = i + 1; }
        var end = text.IndexOf('\n', start);
        if (end < 0) end = text.Length;
        return (line, index - start + 1, text[start..end].TrimEnd('\r'));
    }

    private static string BuildChangePreview(string oldText, IReadOnlyList<Match> matches, string replacement)
    {
        var output = new StringBuilder();
        foreach (var match in matches.Take(80))
        {
            var (line, column, oldLine) = GetLine(oldText, match.Index);
            var lineStart = match.Index - column + 1;
            var localStart = Math.Clamp(match.Index - lineStart, 0, oldLine.Length);
            var localEnd = Math.Clamp(match.Index + match.Length - lineStart, localStart, oldLine.Length);
            var before = oldLine[..localStart];
            var after = oldLine[localEnd..];
            var oldPreview = EscapePreviewText(oldLine);
            var newPreview = EscapePreviewText(before + match.Result(replacement) + after);
            output.AppendLine($"@@ ligne {line} @@");
            output.AppendLine("- " + oldPreview);
            output.AppendLine("+ " + newPreview);
            if (output.Length > 24_000) { output.AppendLine("… aperçu tronqué"); break; }
        }
        return output.ToString();
    }

    private static string EscapePreviewText(string value) => value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static byte[] Concat(byte[] prefix, byte[] content)
    {
        var result = new byte[prefix.Length + content.Length];
        Buffer.BlockCopy(prefix, 0, result, 0, prefix.Length);
        Buffer.BlockCopy(content, 0, result, prefix.Length, content.Length);
        return result;
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
