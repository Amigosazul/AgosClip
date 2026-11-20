using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AgosClip;

/// <summary>
/// Issue #9: renders release notes (GitHub-flavoured Markdown subset) as themed WPF text.
/// Blocks: # headings, paragraphs (a newline is a line break, as on GitHub releases), - * + and 1. lists
/// (nested by indentation), &gt; quotes, ``` code blocks, --- rules. Inline: **bold**, *italic*, ~~strike~~,
/// `code`, [links](https://…) and bare URLs. Colours are resource references, so light/dark follow the theme.
/// Links open in the default browser (http/https only).
/// </summary>
public static class MarkdownView
{
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Microsoft YaHei UI");
    static readonly Regex ListItem = new(@"^(\s*)([-*+]|\d{1,3}[.)])\s+(.*)$");
    static readonly Regex Heading = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
    static readonly Regex Rule = new(@"^\s*([-*_])(\s*\1){2,}\s*$");
    static readonly Regex InlineRx = new(
        @"(?<code>`[^`]+`)|(?<bold>\*\*(?=\S)(.+?)(?<=\S)\*\*|__(?=\S)(.+?)(?<=\S)__)|(?<strike>~~(?=\S)(.+?)(?<=\S)~~)" +
        @"|(?<link>\[(?<lt>[^\]]+)\]\((?<lu>[^)\s]+)\))|(?<url>https?://[^\s<>()（）\]]+[^\s<>()（）\].,;:!?。，；：！？、])" +
        @"|(?<em>(?<![\w*])\*(?=[^\s*])(.+?)(?<=[^\s*])\*(?![\w*]))");

    public static void Render(Panel target, string? markdown)
    {
        target.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown)) return;
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var para = new List<string>();

        void FlushPara()
        {
            if (para.Count == 0) return;
            var tb = Text(12.5, "Fg");
            for (int i = 0; i < para.Count; i++)
            {
                if (i > 0) tb.Inlines.Add(new LineBreak());
                AddInlines(tb.Inlines, para[i].Trim());
            }
            tb.Margin = new Thickness(0, 2, 0, 6);
            target.Children.Add(tb);
            para.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```"))
            {
                FlushPara();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++) code.Add(lines[i]);
                target.Children.Add(CodeBlock(string.Join("\n", code)));
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) { FlushPara(); continue; }
            if (Heading.Match(line) is { Success: true } h)
            {
                FlushPara();
                int level = h.Groups[1].Length;
                var tb = Text(level == 1 ? 17 : level == 2 ? 15 : 13.5, "Fg");
                tb.FontWeight = FontWeights.SemiBold;
                tb.Margin = new Thickness(0, target.Children.Count == 0 ? 0 : 8, 0, 4);
                AddInlines(tb.Inlines, h.Groups[2].Value);
                target.Children.Add(tb);
                continue;
            }
            if (Rule.IsMatch(line) && para.Count == 0)
            {
                var r = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 8) };
                r.SetResourceReference(Border.BackgroundProperty, "CardBorder");
                target.Children.Add(r);
                continue;
            }
            if (ListItem.Match(line) is { Success: true } li)
            {
                FlushPara();
                int depth = li.Groups[1].Value.Replace("\t", "    ").Length / 2;
                string marker = li.Groups[2].Value;
                string body = li.Groups[3].Value;
                // continuation lines (indented, not a new item) belong to this item
                while (i + 1 < lines.Length && lines[i + 1].Length > 0 && char.IsWhiteSpace(lines[i + 1][0])
                       && !ListItem.IsMatch(lines[i + 1]) && !string.IsNullOrWhiteSpace(lines[i + 1]))
                    body += "\n" + lines[++i].Trim();
                target.Children.Add(Bullet(char.IsDigit(marker[0]) ? marker.TrimEnd(')').TrimEnd('.') + "." : "•", body, depth));
                continue;
            }
            if (line.TrimStart().StartsWith('>'))
            {
                FlushPara();
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('>'); i++) quote.Add(lines[i].TrimStart()[1..].TrimStart());
                i--;
                var tb = Text(12.5, "SubFg");
                for (int q = 0; q < quote.Count; q++) { if (q > 0) tb.Inlines.Add(new LineBreak()); AddInlines(tb.Inlines, quote[q]); }
                var bar = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2), Margin = new Thickness(0, 2, 0, 6), Child = tb };
                bar.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
                target.Children.Add(bar);
                continue;
            }
            para.Add(line);
        }
        FlushPara();
    }

    static TextBlock Text(double size, string brush)
    {
        var tb = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.55, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return tb;
    }

    static FrameworkElement Bullet(string marker, string body, int depth)
    {
        var g = new Grid { Margin = new Thickness(4 + depth * 16, 1, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(marker == "•" ? 14 : 20) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var m = Text(12.5, "SubFg");
        m.Text = marker;
        var tb = Text(12.5, "Fg");
        var parts = body.Split('\n');
        for (int i = 0; i < parts.Length; i++) { if (i > 0) tb.Inlines.Add(new LineBreak()); AddInlines(tb.Inlines, parts[i]); }
        Grid.SetColumn(tb, 1);
        g.Children.Add(m);
        g.Children.Add(tb);
        return g;
    }

    static FrameworkElement CodeBlock(string code)
    {
        var tb = new TextBlock { Text = code, FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 2, 0, 8), Child = tb };
        b.SetResourceReference(Border.BackgroundProperty, "Track");
        return b;
    }

    static void AddInlines(InlineCollection into, string text)
    {
        int pos = 0;
        foreach (Match m in InlineRx.Matches(text))
        {
            if (m.Index > pos) into.Add(new Run(text[pos..m.Index]));
            pos = m.Index + m.Length;
            if (m.Groups["code"].Success)
            {
                var r = new Run(m.Value[1..^1]) { FontFamily = Mono, FontSize = 11.5 };
                r.SetResourceReference(TextElement.BackgroundProperty, "Track");
                into.Add(r);
            }
            else if (m.Groups["bold"].Success)
            {
                var span = new Bold();
                AddInlines(span.Inlines, m.Value[2..^2]);
                into.Add(span);
            }
            else if (m.Groups["strike"].Success)
            {
                var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                AddInlines(span.Inlines, m.Value[2..^2]);
                into.Add(span);
            }
            else if (m.Groups["em"].Success)
            {
                var span = new Italic();
                AddInlines(span.Inlines, m.Value[1..^1]);
                into.Add(span);
            }
            else if (m.Groups["link"].Success) into.Add(Link(m.Groups["lt"].Value, m.Groups["lu"].Value));
            else if (m.Groups["url"].Success) into.Add(Link(m.Value, m.Value));
        }
        if (pos < text.Length) into.Add(new Run(text[pos..]));
    }

    static Inline Link(string label, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return new Run(label);
        var h = new Hyperlink(new Run(label)) { NavigateUri = uri, ToolTip = url, TextDecorations = null };
        h.SetResourceReference(TextElement.ForegroundProperty, "Accent");
        h.MouseEnter += (_, _) => h.TextDecorations = TextDecorations.Underline;
        h.MouseLeave += (_, _) => h.TextDecorations = null;
        h.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { App.Log("open link: " + ex.Message); }
            e.Handled = true;
        };
        return h;
    }
}
