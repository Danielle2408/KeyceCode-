using System.Text;
using System.Text.RegularExpressions;

namespace KeyceCode.Services;

public sealed record BuildOutputDiagnostic(string Severity, string Message, string File, int Line, int Column, string? Code);

/// <summary>Extracts MSBuild, Roslyn, GCC, Clang, and MSVC diagnostics from streamed terminal text.</summary>
public sealed class BuildOutputDiagnosticParser
{
    private const int MaximumLineLength = 8192;
    private const int MaximumDiagnosticsPerAppend = 1000;
    private static readonly Regex AnsiEscape = new("\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DiagnosticLine = new(
        @"^(?<file>.+?)\((?<line>[1-9]\d*)(?:,(?<column>[1-9]\d*))?(?:,\d+,\d+)?\):\s*(?<severity>error|warning|info)\s*(?<code>[A-Za-z]+\d+)?\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GccDiagnosticLine = new(
        @"^(?<file>(?:[A-Za-z]:)?[^:]+):(?<line>\d+)(?::(?<column>\d+))?:\s*(?<severity>fatal error|error|warning|note):\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly StringBuilder _line = new();
    private bool _droppingOversizeLine;

    /// <summary>Appends a terminal chunk and returns diagnostics completed by this chunk.</summary>
    public IReadOnlyList<BuildOutputDiagnostic> Append(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length == 0) return Array.Empty<BuildOutputDiagnostic>();

        var diagnostics = new List<BuildOutputDiagnostic>();
        foreach (var character in chunk)
        {
            if (character is '\r' or '\n')
            {
                if (!_droppingOversizeLine && _line.Length > 0 && diagnostics.Count < MaximumDiagnosticsPerAppend)
                {
                    var parsed = ParseLine(_line.ToString());
                    if (parsed is not null) diagnostics.Add(parsed);
                }
                _line.Clear();
                _droppingOversizeLine = false;
                continue;
            }

            if (_droppingOversizeLine) continue;
            if (_line.Length >= MaximumLineLength)
            {
                _line.Clear();
                _droppingOversizeLine = true;
                continue;
            }
            _line.Append(character);
        }
        return diagnostics;
    }

    /// <summary>Discards an incomplete line when starting a new build.</summary>
    public void Clear()
    {
        _line.Clear();
        _droppingOversizeLine = false;
    }

    private static BuildOutputDiagnostic? ParseLine(string rawLine)
    {
        var line = AnsiEscape.Replace(rawLine, string.Empty).Trim();
        var match = DiagnosticLine.Match(line);
        if (!match.Success) match = GccDiagnosticLine.Match(line);
        if (!match.Success ||
            !int.TryParse(match.Groups["line"].Value, out var lineNumber) ||
            !int.TryParse(match.Groups["column"].Success ? match.Groups["column"].Value : "1", out var column))
            return null;

        var severity = match.Groups["severity"].Value.ToLowerInvariant() switch
        {
            "error" or "fatal error" => "Error",
            "warning" => "Warning",
            _ => "Info"
        };
        var message = match.Groups["message"].Value.Trim();
        if (message.Length == 0) return null;
        var code = match.Groups["code"].Success ? match.Groups["code"].Value : null;
        return new BuildOutputDiagnostic(severity, message, match.Groups["file"].Value.Trim(), lineNumber, column, code);
    }
}
