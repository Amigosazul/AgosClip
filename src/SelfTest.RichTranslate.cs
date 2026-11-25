using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;

namespace AgosClip;

internal static partial class SelfTest
{
    static readonly Regex FakeMarker = new(@"<a i=(\d+)>(.*?)</a>");

    /// <summary>"Translates" by upper-casing in brackets; marked pieces come back in reverse order (like a real reordering).</summary>
    static string FakeLine(string line)
    {
        var ms = FakeMarker.Matches(line);
        if (ms.Count == 0) return "[" + line.ToUpperInvariant() + "]";
        return string.Join(" ", ms.Reverse().Select(m => $"<a i={m.Groups[1].Value}>[{m.Groups[2].Value.ToUpperInvariant()}]</a>"));
    }

    static Task<(string[], string)> FakeTranslate(IReadOnlyList<string> texts, string to, CancellationToken ct) =>
        Task.FromResult((texts.Select(t => string.Join("\n", t.Split('\n').Select(FakeLine))).ToArray(), "fake"));

    /// <summary>A service that drops the markers.</summary>
    static Task<(string[], string)> FakeNoMarkers(IReadOnlyList<string> texts, string to, CancellationToken ct) =>
        Task.FromResult((texts.Select(t => string.Join("\n", t.Split('\n').Select(l => "[" + Regex.Replace(l, "<[^>]+>", "").ToUpperInvariant() + "]"))).ToArray(), "fake"));

    /// <summary>A service that merges the lines of a batch (forces the one-by-one retry).</summary>
    static Task<(string[], string)> FakeMergesLines(IReadOnlyList<string> texts, string to, CancellationToken ct) =>
        Task.FromResult((texts.Select(t => string.Join(" ", t.Split('\n').Select(FakeLine))).ToArray(), "fake"));

    /// <summary>CF_HTML the way Chrome writes it: header with byte offsets, page, fragment comments.</summary>
    internal static byte[] CfHtml(string fragment, string sourceUrl = "https://example.com/页面")
    {
        string pre = "<html><head><style>p{color:red}</style><title>标题 Title</title></head><body>\r\n<!--StartFragment-->";
        string post = "<!--EndFragment-->\r\n</body>\r\n</html>";
        const string Fmt = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\nSourceURL:{4}\r\n";
        int h = Encoding.UTF8.GetByteCount(string.Format(Fmt, 0, 0, 0, 0, sourceUrl));
        int sf = h + Encoding.UTF8.GetByteCount(pre), ef = sf + Encoding.UTF8.GetByteCount(fragment);
        int eh = ef + Encoding.UTF8.GetByteCount(post);
        return Encoding.UTF8.GetBytes(string.Format(Fmt, h, eh, sf, ef, sourceUrl) + pre + fragment + post);
    }

    static int HeaderValue(string s, string key) => int.Parse(Regex.Match(s, "^" + key + @":(\d+)", RegexOptions.Multiline).Groups[1].Value);

    static void RichTranslateTests(string outDir)
    {
        const string frag =
            "<p style=\"font-family:'Segoe UI'\">Click <b>Save</b> to keep\r\n your changes.</p>" +
            "<ul><li>First item</li><li>价格 100</li></ul>" +
            "<pre>var x = \"do not translate\";</pre>" +
            "<p>Run <code>git status</code> first</p>" +
            "<table><tr><td>Name</td><td>Price</td></tr><tr><td>Apple</td><td>3</td></tr></table>" +
            "<p><a href=\"https://x.test/?a=1&amp;b=2\" title=\"a > b\">Read the docs</a> &amp; more&nbsp;text</p>" +
            "<script>var hello = 'world';</script>";
        var html = new ClipFormat(DataFormats.Html, CfHtml(frag), true);

        // 1. formatting, structure and code survive; only text changes
        var r = Wait(RichTranslator.TranslateAsync(new[] { html, new ClipFormat(DataFormats.Rtf, Encoding.ASCII.GetBytes(@"{\rtf1 old}"), true) },
            "zh-Hans", FakeTranslate, CancellationToken.None));
        var outHtml = Encoding.UTF8.GetString(r.Formats[0].Data);
        Check(r.Formats.Count == 1 && r.Formats[0].Name == DataFormats.Html, "rich translate: HTML out, the stale RTF dropped");
        Check(outHtml.Contains("<b>[SAVE]</b>") && outHtml.Contains("[CLICK]") && outHtml.Contains("[TO KEEP YOUR CHANGES.]"),
            "rich translate: a bold word keeps its own translation (markers)");
        Check(outHtml.Contains("<pre>var x = \"do not translate\";</pre>") && outHtml.Contains("<code>git status</code>")
              && outHtml.Contains("var hello = 'world';") && outHtml.Contains("<style>p{color:red}</style>"),
            "rich translate: pre / code / script / style untouched");
        Check(outHtml.Contains("href=\"https://x.test/?a=1&amp;b=2\" title=\"a > b\">[READ THE DOCS]</a>") && outHtml.Contains("&amp;"),
            "rich translate: link attributes kept, link text translated");
        Check(outHtml.Contains("<td>[NAME]</td>") && outHtml.Contains("<td>3</td>") && outHtml.Contains("<li>[FIRST ITEM]</li>"),
            "rich translate: table cells and list items translated in place; numbers left alone");
        Check(outHtml.Contains("<title>标题 Title</title>") && !outHtml.Contains("<a i="), "rich translate: no markers left; head untouched");

        // the CF_HTML header must point at the right bytes (UTF-8 offsets, Chinese in the page)
        var bytes = r.Formats[0].Data;
        int sh = HeaderValue(outHtml, "StartHTML"), eh = HeaderValue(outHtml, "EndHTML"), sf = HeaderValue(outHtml, "StartFragment"), ef = HeaderValue(outHtml, "EndFragment");
        var page = Encoding.UTF8.GetString(bytes, sh, eh - sh);
        var fragOut = Encoding.UTF8.GetString(bytes, sf, ef - sf);
        Check(eh == bytes.Length && page.StartsWith("<html>") && Encoding.UTF8.GetString(bytes, 0, sf).EndsWith("<!--StartFragment-->")
              && Encoding.UTF8.GetString(bytes, ef, bytes.Length - ef).StartsWith("<!--EndFragment-->") && fragOut.StartsWith("<p style")
              && outHtml.Contains("SourceURL:https://example.com/页面"),
            $"rich translate: CF_HTML offsets match the new content (fragment {ef - sf} bytes)");
        Check(r.Text.Contains("[NAME]\t[PRICE]") && r.Text.Contains("[CLICK][SAVE]") && !r.Text.Contains("var hello")
              && r.Text.Contains("var x = \"do not translate\";") && r.Text.Contains("\r\n"),
            "rich translate: plain-text version has lines and tab-separated cells");
        File.WriteAllText(Path.Combine(outDir, "rich-translate.html"), outHtml, new UTF8Encoding(false));

        // 2. Chinese target: no stray spaces between runs; Chinese source with English runs
        var r2 = Wait(RichTranslator.TranslateAsync(new[] { new ClipFormat(DataFormats.Html, CfHtml("<p>点击 <b>保存</b> 按钮</p>"), true) },
            "en", FakeTranslate, CancellationToken.None));
        Check(Encoding.UTF8.GetString(r2.Formats[0].Data).Contains("[点击] <b>[保存]</b> [按钮]"), "rich translate: spaces between runs kept for a Latin target");
        var r2b = Wait(RichTranslator.TranslateAsync(new[] { html }, "zh-Hans", FakeTranslate, CancellationToken.None));
        Check(Encoding.UTF8.GetString(r2b.Formats[0].Data).Contains("[CLICK]<b>[SAVE]</b>[TO KEEP YOUR CHANGES.]</p>"),
            "rich translate: no gaps between runs for a Chinese target");

        // 3. a service that loses the markers: the sentence goes into its longest run, nothing is lost
        var r3 = Wait(RichTranslator.TranslateAsync(new[] { html }, "zh-Hans", FakeNoMarkers, CancellationToken.None));
        var h3 = Encoding.UTF8.GetString(r3.Formats[0].Data);
        Check(h3.Contains("'Segoe UI'\"><b></b>[CLICK SAVE TO KEEP YOUR CHANGES.]</p>") && !h3.Contains("<a i="),
            "rich translate: markers lost → whole sentence in the main run");

        // 4. a service that merges the batch's lines: each segment is retried alone
        var r4 = Wait(RichTranslator.TranslateAsync(new[] { html }, "zh-Hans", FakeMergesLines, CancellationToken.None));
        var h4 = Encoding.UTF8.GetString(r4.Formats[0].Data);
        Check(h4.Contains("<b>[SAVE]</b>") && h4.Contains("<td>[NAME]</td>"), "rich translate: merged lines → segments retried one by one");

        // 5. an app with a wrong header but correct comments, and a fragment with no header at all
        var bad = Encoding.UTF8.GetBytes("Version:0.9\r\nStartHTML:0000000105\r\nEndHTML:0000000999\r\nStartFragment:0000000300\r\nEndFragment:0000000400\r\n" +
            "<html><body><!--StartFragment--><p>Hello <i>world</i></p><!--EndFragment--></body></html>");
        var r5 = Wait(RichTranslator.TranslateAsync(new[] { new ClipFormat(DataFormats.Html, bad, true) }, "zh-Hans", FakeTranslate, CancellationToken.None));
        Check(Encoding.UTF8.GetString(r5.Formats[0].Data).Contains("<p>[HELLO]<i>[WORLD]</i></p>"), "rich translate: wrong byte offsets → fragment comments used");
        var bare = Encoding.UTF8.GetBytes("<div>Plain <u>page</u></div>");
        var r6 = Wait(RichTranslator.TranslateAsync(new[] { new ClipFormat(DataFormats.Html, bare, true) }, "zh-Hans", FakeTranslate, CancellationToken.None));
        Check(Encoding.UTF8.GetString(r6.Formats[0].Data).Contains("<div>[PLAIN]<u>[PAGE]</u></div>") && r6.Text == "[PLAIN][PAGE]",
            "rich translate: HTML without a header");
        try
        {
            Wait(RichTranslator.TranslateAsync(new[] { new ClipFormat(DataFormats.Html, CfHtml("<p>123 · 456</p>"), true) }, "zh-Hans", FakeTranslate, CancellationToken.None));
            Check(false, "rich translate: nothing to translate is reported");
        }
        catch (InvalidOperationException) { Check(true, "rich translate: nothing to translate is reported"); }

        // 6. RTF (WordPad / Outlook without HTML) through a FlowDocument
        var fd = new FlowDocument();
        var p = new Paragraph();
        p.Inlines.Add(new Run("Click "));
        p.Inlines.Add(new Bold(new Run("Save")));
        p.Inlines.Add(new Run(" now."));
        fd.Blocks.Add(p);
        fd.Blocks.Add(new Paragraph(new Run("Second paragraph")));
        fd.Blocks.Add(new Paragraph(new Run("Console.WriteLine();") { FontFamily = new System.Windows.Media.FontFamily("Consolas") }));
        byte[] rtf;
        using (var ms = new MemoryStream()) { new TextRange(fd.ContentStart, fd.ContentEnd).Save(ms, DataFormats.Rtf); rtf = ms.ToArray(); }
        var r7 = Wait(RichTranslator.TranslateAsync(new[] { new ClipFormat(DataFormats.Rtf, rtf, true) }, "zh-Hans", FakeTranslate, CancellationToken.None));
        var back = RichTranslator.LoadRtf(r7.Formats[0].Data);
        var bolds = new List<string>();
        void Walk(InlineCollection items)
        {
            foreach (var i in items)
                if (i is Run run && run.FontWeight == FontWeights.Bold) bolds.Add(run.Text);
                else if (i is Span sp) Walk(sp.Inlines);
        }
        foreach (var para in back.Blocks.OfType<Paragraph>()) Walk(para.Inlines);
        Check(r7.Formats[0].Name == DataFormats.Rtf && bolds.Contains("[SAVE]") && r7.Text.Contains("[SECOND PARAGRAPH]")
              && r7.Text.Contains("Console.WriteLine();"),
            $"rich translate: RTF keeps bold and paragraphs, monospace left alone ({r7.Text.Replace("\r\n", " | ")})");

        // 7. the panel offers it: rich items show 翻译，保留格式
        var vm = new ItemVm(new ClipItem { Kind = ClipKind.Text, Text = "x", Formats = new[] { DataFormats.Html } }, false);
        Check(vm.CanTranslate == App.Current.Settings.EnableTranslate && vm.TranslateTip.Contains("保留格式"), "rich translate: panel button for rich text");

        // 8. online, end to end through the window (opt-in)
        if (Environment.GetEnvironmentVariable("AGOSCLIP_NET_TESTS") == "1")
        {
            try
            {
                var online = Wait(RichTranslator.TranslateAsync(new[] { html }, "zh-Hans"));
                var oh = Encoding.UTF8.GetString(online.Formats[0].Data);
                File.WriteAllText(Path.Combine(outDir, "rich-translate-online.html"), oh, new UTF8Encoding(false));
                var bold = Regex.Match(oh, "<b>(.*?)</b>").Groups[1].Value;
                Check(bold.Length > 0 && bold.Any(c => c >= 0x4E00 && c <= 0x9FFF) && oh.Contains("<pre>var x") && !oh.Contains("<a i="),
                    $"rich translate online via {online.Engine}: bold = {bold}; text = {online.Text.Replace("\r\n", " | ")}");

                var w = TextToolWindow.ShowTranslate("Click Save to keep your changes.", null, new[] { html });
                for (int i = 0; i < 300 && (w.Busy || w.RichResult == null) && w.ResultText.Length == 0; i++) Pump(100);
                Check(w.RichResult != null && w.ResultText.Length > 0, "rich translate window: formatted result ready");
                RenderOnGlass((FrameworkElement)w.Content, Path.Combine(outDir, "rich-translate-window.png"), false, false);
                w.Close();
            }
            catch (Exception ex) { Check(false, "rich translate online: " + ex.Message); }
        }
    }
}
