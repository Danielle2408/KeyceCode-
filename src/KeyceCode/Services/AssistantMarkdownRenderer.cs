using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace KeyceCode.Services;

/// <summary>Renders Gemini Markdown in the KeyceCode chat with editor-like code colors.</summary>
public static partial class AssistantMarkdownRenderer
{
    private sealed record Palette(Brush Text, Brush Muted, Brush CodeBackground, Brush Border,
        Brush Keyword, Brush String, Brush Comment, Brush Number, Brush Type, Brush Function);

    private static readonly Regex InlineMarkdown = new(@"(\*\*.+?\*\*|`[^`]+`|(?<!\*)\*[^*]+\*)", RegexOptions.Compiled);
    private static readonly Regex CodeToken = new(
        @"(?<comment>//.*$|#.*$)|(?<string>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')|(?<number>\b\d+(?:\.\d+)?\b)|(?<keyword>\b(?:abstract|as|async|await|bool|break|case|catch|char|class|const|continue|def|default|do|double|else|enum|export|extends|false|finally|float|for|foreach|from|function|if|implements|import|in|int|interface|let|long|namespace|new|null|private|protected|public|return|static|string|struct|switch|this|throw|true|try|using|var|void|while|yield)\b)|(?<function>\b[A-Za-z_]\w*(?=\s*\())|(?<type>\b[A-Z][A-Za-z0-9_]*\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static FlowDocument Render(string markdown, string themeId, Brush text, Brush editorBackground, Brush border)
    {
        var palette = CreatePalette(themeId, text, editorBackground, border);
        var document = new FlowDocument
        {
            PagePadding = new Thickness(2),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = palette.Text,
            Background = Brushes.Transparent
        };

        var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length;)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var info = line.TrimStart()[3..].Trim();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[i++]);
                if (i < lines.Length) i++;
                AddCodeBlock(document, string.Join("\n", code), info, palette);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }
            var trimmed = line.TrimStart();
            var heading = Regex.Match(trimmed, @"^(#{1,6})\s+(.+)$");
            var bullet = Regex.Match(trimmed, @"^(?:[-*+]\s+|\d+[.)]\s+)(.+)$");
            var quote = trimmed.StartsWith(">", StringComparison.Ordinal);
            var content = heading.Success ? heading.Groups[2].Value : bullet.Success ? bullet.Groups[1].Value : quote ? trimmed.TrimStart('>', ' ') : line;
            var paragraph = new Paragraph { Margin = new Thickness(0, heading.Success ? 8 : 3, 0, heading.Success ? 5 : 5) };
            if (heading.Success)
            {
                paragraph.FontSize = heading.Groups[1].Length switch { 1 => 18, 2 => 16, 3 => 14, _ => 13 };
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Foreground = palette.Text;
            }
            else if (bullet.Success)
            {
                paragraph.Margin = new Thickness(10, 2, 0, 4);
                paragraph.Inlines.Add(new Run("• ") { Foreground = palette.Keyword });
            }
            else if (quote)
            {
                paragraph.Margin = new Thickness(10, 2, 0, 4);
                paragraph.BorderBrush = palette.Border;
                paragraph.BorderThickness = new Thickness(2, 0, 0, 0);
                paragraph.Padding = new Thickness(8, 0, 0, 0);
                paragraph.Foreground = palette.Muted;
            }
            AddInlineMarkdown(paragraph.Inlines, content, palette);
            document.Blocks.Add(paragraph);
            i++;
        }

        return document;
    }

    private static void AddCodeBlock(FlowDocument document, string code, string language, Palette palette)
    {
        var codeText = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 7, 10, 7),
            Foreground = palette.Text
        };
        AddCodeTokens(codeText.Inlines, code, language, palette);
        var frame = new Border
        {
            Background = palette.CodeBackground,
            BorderBrush = palette.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = codeText
        };
        document.Blocks.Add(new BlockUIContainer(frame) { Margin = new Thickness(0, 4, 0, 7) });
    }

    private static void AddCodeTokens(InlineCollection inlines, string code, string language, Palette palette)
    {
        var offset = 0;
        foreach (Match match in CodeToken.Matches(code))
        {
            if (match.Index > offset) inlines.Add(new Run(code[offset..match.Index]));
            var color = match.Groups["comment"].Success ? palette.Comment
                : match.Groups["string"].Success ? palette.String
                : match.Groups["number"].Success ? palette.Number
                : match.Groups["keyword"].Success ? palette.Keyword
                : match.Groups["function"].Success ? palette.Function
                : palette.Type;
            inlines.Add(new Run(match.Value) { Foreground = color });
            offset = match.Index + match.Length;
        }
        if (offset < code.Length) inlines.Add(new Run(code[offset..]));
    }

    private static void AddInlineMarkdown(InlineCollection inlines, string text, Palette palette)
    {
        var offset = 0;
        foreach (Match match in InlineMarkdown.Matches(text))
        {
            if (match.Index > offset) inlines.Add(new Run(text[offset..match.Index]));
            var value = match.Value;
            if (value.StartsWith("**", StringComparison.Ordinal) && value.EndsWith("**", StringComparison.Ordinal))
                inlines.Add(new Bold(new Run(value[2..^2])));
            else if (value.StartsWith('`'))
                inlines.Add(new Run(value[1..^1]) { FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"), Foreground = palette.String, Background = palette.CodeBackground });
            else inlines.Add(new Italic(new Run(value[1..^1])));
            offset = match.Index + match.Length;
        }
        if (offset < text.Length) inlines.Add(new Run(text[offset..]));
    }

    private static Palette CreatePalette(string themeId, Brush text, Brush editorBackground, Brush border)
    {
        var keyce = themeId == "keyce-night";
        var light = themeId.StartsWith("light", StringComparison.OrdinalIgnoreCase);
        if (light)
            return new Palette(text, Brushes.DimGray, editorBackground, border,
                Brush("#0000FF"), Brush("#A31515"), Brush("#008000"), Brush("#098658"), Brush("#267F99"), Brush("#795E26"));
        return keyce
            ? new Palette(text, Brush("#9BAAC7"), editorBackground, border, Brush("#C792EA"), Brush("#F5A97F"), Brush("#8190B3"), Brush("#F9C66D"), Brush("#79D4C8"), Brush("#82AAFF"))
            : new Palette(text, Brush("#858585"), editorBackground, border, Brush("#C586C0"), Brush("#CE9178"), Brush("#6A9955"), Brush("#B5CEA8"), Brush("#4EC9B0"), Brush("#DCDCAA"));
    }

    private static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
}
