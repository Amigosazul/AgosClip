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

/// <summary>
/// Rich-text translation: translates the text inside an item's HTML Format (or, when there is no HTML, its
/// Rich Text Format) and leaves everything else (tags, styles, tables, lists, links, colours) exactly as it was.
///
/// Text is translated a "segment" at a time: the run of text between two block boundaries (paragraph, cell,
/// list item, line break…), so sentences stay whole. When a segment mixes formatting ("Click <b>Save</b> now")
/// its pieces are sent wrapped in &lt;a i=N&gt; markers, which the translation services keep around the matching
/// words; each translated piece goes back into its own run. If the markers come back damaged, the whole
/// sentence goes into the segment's longest run (the sentence keeps its main formatting).
///
/// Code (pre/code/kbd/samp), scripts and styles are never translated. The FlowDocument used for RTF is a
/// DispatcherObject: call this on an STA thread (the UI thread).
/// </summary>
public static class RichTranslator
{
    public sealed record Result(string Text, IReadOnlyList<ClipFormat> Formats, int Segments, string Engine, string Target);

    /// <summary>Translates a list of texts; Translator.TranslateAsync in the app, a fake one in the self-test.</summary>
    internal delegate Task<(string[] Texts, string Engine)> TranslateFn(IReadOnlyList<string> texts, string to, CancellationToken ct);

    public const int MaxChars = 30000;

    /// <summary>True if the item carries a format this class can translate.</summary>
    public static bool Supports(IReadOnlyList<string> formatNames) =>
        formatNames.Contains(DataFormats.Html) || formatNames.Contains(DataFormats.Rtf);

    public static Task<Result> TranslateAsync(IReadOnlyList<ClipFormat> formats, string target, CancellationToken ct = default)
        => TranslateAsync(formats, target, Translator.TranslateAsync, ct);

    internal static async Task<Result> TranslateAsync(IReadOnlyList<ClipFormat> formats, string target, TranslateFn translate, CancellationToken ct)
    {
        var html = formats.FirstOrDefault(f => f.Name == DataFormats.Html);
        var rtf = formats.FirstOrDefault(f => f.Name == DataFormats.Rtf);
        if (html != null)
        {
            // HTML wins: every app that pastes rich text reads it, and a translated RTF next to it would
            // mean translating everything twice. The old RTF / spreadsheet formats are dropped (stale text).
            var doc = HtmlDoc.Parse(html.Data);
            var segs = doc.Segments();
            var (engine, to) = await Fill(segs, target, translate, ct);
            var bytes = doc.Build();
            return new Result(doc.PlainText(), new[] { new ClipFormat(DataFormats.Html, bytes, true) }, segs.Count, engine, to);
        }
        if (rtf != null)
        {
            var fd = LoadRtf(rtf.Data);
            var segs = RtfSegments(fd);
            var (engine, to) = await Fill(segs, target, translate, ct);
            var range = new TextRange(fd.ContentStart, fd.ContentEnd);
            using var ms = new MemoryStream();
            range.Save(ms, DataFormats.Rtf);
            var text = range.Text.TrimEnd('\r', '\n');
            return new Result(text, new[] { new ClipFormat(DataFormats.Rtf, ms.ToArray(), true) }, segs.Count, engine, to);
        }
        throw new InvalidOperationException("这条记录没有可以翻译的富文本格式");
    }

    // ───────────── segments ─────────────

    /// <summary>A piece of text that can be read and replaced: an HTML text node or a WPF Run.</summary>
    internal sealed class Slot
    {
        public string Text = "";          // decoded text
        public Action<string> Set = _ => { };
    }

    /// <summary>Slots that read as one sentence or paragraph.</summary>
    internal sealed class Segment
    {
        public readonly List<Slot> Slots = new();
    }

    static readonly Regex Ws = new(@"[ \t\r\n\f]+");
    static readonly Regex Marker = new(@"<\s*a\s+i\s*=\s*[""']?(\d+)[""']?\s*>(.*?)<\s*/\s*a\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex AnyTag = new(@"<[^<>]*>");

    static bool Worth(string s) => s.Any(char.IsLetter); // numbers, punctuation and symbols stay as they are
    static bool IsCjkTarget(string to) => to.StartsWith("zh") || to == "ja" || to == "ko";

    static string Lead(string s) => s[..(s.Length - s.TrimStart().Length)];
    static string Trail(string s) => s[s.TrimEnd().Length..];
    static string Collapse(string s) => Ws.Replace(s, " ").Trim();
    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    static string Unesc(string s) => System.Net.WebUtility.HtmlDecode(s);

    /// <summary>One translation request per segment: the text itself, or marked pieces for mixed formatting.</summary>
    sealed class Unit
    {
        public required Segment Seg;
        public required List<Slot> Live;  // slots with letters, in order
        public required string Source;
        public bool Marked => Live.Count > 1;
    }

    static Unit? MakeUnit(Segment seg)
    {
        // runs without letters (bullets, numbers, "·", "→") stay exactly as they are and get no marker
        var live = seg.Slots.Where(s => Worth(s.Text)).ToList();
        if (live.Count == 0) return null;
        if (live.Count == 1) return new Unit { Seg = seg, Live = live, Source = Collapse(live[0].Text) };
        var sb = new StringBuilder();
        for (int i = 0; i < live.Count; i++)
        {
            // keep the original word gaps between pieces ("Click" + " " + "Save"), none for "点击" + "保存"
            if (i > 0 && (Trail(live[i - 1].Text).Length > 0 || Lead(live[i].Text).Length > 0)) sb.Append(' ');
            sb.Append("<a i=").Append(i).Append('>').Append(Esc(Collapse(live[i].Text))).Append("</a>");
        }
        return new Unit { Seg = seg, Live = live, Source = sb.ToString() };
    }

    /// <summary>Writes a translated unit back into its slots. Returns false if the markers had to be given up.</summary>
    static bool Apply(Unit u, string translated, string to)
    {
        bool cjk = IsCjkTarget(to);
        void Put(Slot s, string t, bool first, bool last)
        {
            // keep the whitespace around a run; inside a Chinese/Japanese sentence the gaps between runs go
            string lead = Lead(s.Text), trail = Trail(s.Text);
            if (cjk) { if (!first) lead = ""; if (!last) trail = ""; }
            s.Set(lead + t + trail);
        }
        translated = translated.Trim();
        if (!u.Marked) { Put(u.Live[0], translated, true, true); return true; }

        var pieces = new string?[u.Live.Count];
        bool ok = true;
        int lastIdx = -1, pos = 0;
        var loose = new StringBuilder(); // text the service put outside every marker
        foreach (Match m in Marker.Matches(translated))
        {
            loose.Append(translated, pos, m.Index - pos);
            pos = m.Index + m.Length;
            int i = int.Parse(m.Groups[1].Value);
            if (i >= pieces.Length || pieces[i] != null) { ok = false; break; }
            var t = Unesc(m.Groups[2].Value);
            if (AnyTag.IsMatch(t)) { ok = false; break; } // nested or broken markers
            var extra = loose.ToString().Trim();
            loose.Clear();
            if (extra.Length > 0)
            {
                // outside text before this marker belongs to the previous piece (or this one, if first)
                if (lastIdx >= 0) pieces[lastIdx] += (cjk ? "" : " ") + Unesc(extra);
                else t = Unesc(extra) + (cjk ? "" : " ") + t;
            }
            pieces[i] = t;
            lastIdx = i;
        }
        if (ok)
        {
            var tail = Unesc(translated[pos..]).Trim();
            if (pieces.Any(p => p == null)) ok = false;
            else if (tail.Length > 0) pieces[lastIdx] += (cjk ? "" : " ") + tail;
        }
        if (ok)
        {
            for (int i = 0; i < pieces.Length; i++) Put(u.Live[i], pieces[i]!.Trim(), i == 0, i == pieces.Length - 1);
            return true;
        }
        // markers lost: the whole sentence goes into the longest run, the other runs are emptied
        var all = Collapse(Unesc(AnyTag.Replace(translated, " ")));
        var main = u.Live.OrderByDescending(s => s.Text.Trim().Length).First();
        string outerLead = Lead(u.Live[0].Text), outerTrail = Trail(u.Live[^1].Text);
        foreach (var s in u.Live) s.Set(s == main ? outerLead + all + outerTrail : "");
        return false;
    }

    /// <summary>Translates every segment, several per request (one per line), and writes the results back.</summary>
    static async Task<(string Engine, string To)> Fill(List<Segment> segs, string target, TranslateFn translate, CancellationToken ct)
    {
        var units = segs.Select(MakeUnit).Where(u => u != null).Select(u => u!).ToList();
        if (units.Count == 0) throw new InvalidOperationException("没有找到要翻译的文字");
        int total = units.Sum(u => u.Source.Length);
        if (total > MaxChars) throw new InvalidOperationException($"内容太长（约 {total} 字），富文本一次最多翻译 {MaxChars} 字");
        var to = Translator.Resolve(target, units.Select(u => AnyTag.Replace(u.Source, "")));

        // batches of whole lines, ~1500 characters each: far fewer requests than one per paragraph
        var batches = new List<List<Unit>>();
        int len = 0;
        foreach (var u in units)
        {
            if (batches.Count == 0 || len + u.Source.Length > 1500 && batches[^1].Count > 0) { batches.Add(new()); len = 0; }
            batches[^1].Add(u);
            len += u.Source.Length + 1;
        }
        var (joined, engine) = await translate(batches.Select(b => string.Join("\n", b.Select(u => u.Source))).ToList(), to, ct);

        int lost = 0;
        var retry = new List<Unit>();
        for (int b = 0; b < batches.Count; b++)
        {
            var lines = (joined[b] ?? "").Replace("\r\n", "\n").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            if (lines.Length != batches[b].Count) { retry.AddRange(batches[b]); continue; } // lines merged or split
            for (int i = 0; i < lines.Length; i++) if (!Apply(batches[b][i], lines[i], to)) lost++;
        }
        if (retry.Count > 0)
        {
            App.Log($"rich translate: {retry.Count} segments retried one by one (line count changed)");
            var (single, e2) = await translate(retry.Select(u => u.Source).ToList(), to, ct);
            for (int i = 0; i < retry.Count; i++) if (!Apply(retry[i], (single[i] ?? "").Replace('\n', ' '), to)) lost++;
            engine = e2;
        }
        App.Log($"rich translate: {units.Count} segments, {total} chars, {batches.Count} batches, {retry.Count} retried, {lost} lost markers via {engine}");
        return (engine, to);
    }

    // ───────────── HTML ─────────────

    /// <summary>CF_HTML: header with byte offsets, then the page; only the fragment (what was selected) is translated.</summary>
    internal sealed class HtmlDoc
    {
        string _sourceUrl = "";
        string _pre = "", _post = "";
        readonly List<Token> _tokens = new();

        internal sealed class Token
        {
            public string Raw = "";
            public bool IsText;
            public string Name = "";      // tags: lower-case name
            public bool End, SelfClose;
        }

        static readonly HashSet<string> Inline = new()
        {
            "a", "abbr", "b", "bdi", "bdo", "big", "cite", "data", "dfn", "em", "font", "i", "ins", "del", "label", "mark", "q",
            "s", "small", "span", "strike", "strong", "sub", "sup", "time", "tt", "u", "var", "nobr", "wbr", "o:p", "w:sdt",
        };
        /// <summary>Never translated (and they end a segment).</summary>
        static readonly HashSet<string> Skip = new() { "script", "style", "pre", "code", "kbd", "samp", "textarea", "svg", "math", "head", "title", "xml", "noscript" };
        /// <summary>Raw-text elements: their content is not markup.</summary>
        static readonly HashSet<string> RawText = new() { "script", "style", "textarea", "title", "xml" };
        static readonly HashSet<string> Void = new() { "br", "img", "hr", "meta", "link", "input", "col", "area", "base", "embed", "param", "source", "track", "wbr" };
        static readonly HashSet<string> Block = new()
        {
            "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "ul", "ol", "dl", "dt", "dd", "table", "tr", "thead", "tbody", "tfoot",
            "caption", "blockquote", "section", "article", "header", "footer", "nav", "aside", "main", "figure", "figcaption", "pre", "hr",
            "address", "details", "summary", "body", "html",
        };

        public static HtmlDoc Parse(byte[] cfHtml)
        {
            var d = new HtmlDoc();
            int n = cfHtml.Length;
            while (n > 0 && cfHtml[n - 1] == 0) n--;
            var all = Encoding.UTF8.GetString(cfHtml, 0, n);
            int Header(string key)
            {
                var m = Regex.Match(all, @"^" + key + @":\s*(-?\d+)", RegexOptions.Multiline);
                return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : -1;
            }
            var url = Regex.Match(all, @"^SourceURL:(.*)$", RegexOptions.Multiline);
            if (url.Success && url.Index < 400) d._sourceUrl = url.Groups[1].Value.Trim();

            int sh = Header("StartHTML"), sf = Header("StartFragment"), ef = Header("EndFragment");
            bool header = sh >= 0 && sf >= sh && ef >= sf && ef <= n;
            int first = all.IndexOf('<');
            string page = header ? Encoding.UTF8.GetString(cfHtml, sh, n - sh) : first >= 0 ? all[first..] : all;
            int fs, fe; // the fragment, in chars of page
            // the comments are exact even when an app got the byte offsets wrong; then the header; then <body>
            int a = page.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
            int b = a < 0 ? -1 : page.IndexOf("<!--EndFragment-->", a, StringComparison.OrdinalIgnoreCase);
            if (b > a && a >= 0) { fs = a + "<!--StartFragment-->".Length; fe = b; }
            else if (header)
            {
                fs = Encoding.UTF8.GetCharCount(cfHtml, sh, sf - sh);
                fe = Math.Min(page.Length, fs + Encoding.UTF8.GetCharCount(cfHtml, sf, ef - sf));
            }
            else
            {
                var body = Regex.Match(page, @"<body[^>]*>", RegexOptions.IgnoreCase);
                fs = body.Success ? body.Index + body.Length : 0;
                int close = page.LastIndexOf("</body", StringComparison.OrdinalIgnoreCase);
                fe = close >= fs ? close : page.Length;
            }
            d._pre = page[..fs];
            d._post = page[fe..];
            d.Tokenize(page[fs..fe], SkipDepthAt(d._pre));
            return d;
        }

        /// <summary>A fragment can start inside &lt;pre&gt; or &lt;code&gt; opened before it.</summary>
        static int SkipDepthAt(string pre)
        {
            int depth = 0;
            foreach (Match m in Regex.Matches(pre, @"<(/?)(pre|code|kbd|samp)\b[^>]*>", RegexOptions.IgnoreCase))
                depth = Math.Max(0, depth + (m.Groups[1].Value == "/" ? -1 : 1));
            return depth;
        }

        int _initialSkip;

        void Tokenize(string s, int initialSkip)
        {
            _initialSkip = initialSkip;
            int i = 0;
            while (i < s.Length)
            {
                int lt = s.IndexOf('<', i);
                if (lt < 0) { AddText(s[i..]); break; }
                if (lt > i) AddText(s[i..lt]);
                if (s.AsSpan(lt).StartsWith("<!--"))
                {
                    int e = s.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                    e = e < 0 ? s.Length : e + 3;
                    _tokens.Add(new Token { Raw = s[lt..e], Name = "!" });
                    i = e;
                    continue;
                }
                var m = Regex.Match(s[lt..Math.Min(s.Length, lt + 64)], @"^<(/?)([A-Za-z][\w:.-]*)");
                if (!m.Success)
                {
                    // "<" that isn't a tag (e.g. "a < b" written unescaped), or <!DOCTYPE>/<?xml?>
                    if (lt + 1 < s.Length && (s[lt + 1] == '!' || s[lt + 1] == '?'))
                    {
                        int e2 = s.IndexOf('>', lt);
                        e2 = e2 < 0 ? s.Length : e2 + 1;
                        _tokens.Add(new Token { Raw = s[lt..e2], Name = "!" });
                        i = e2;
                    }
                    else { AddText("<"); i = lt + 1; }
                    continue;
                }
                int end = TagEnd(s, lt);
                var tok = new Token
                {
                    Raw = s[lt..end],
                    End = m.Groups[1].Value == "/",
                    Name = m.Groups[2].Value.ToLowerInvariant(),
                };
                tok.SelfClose = tok.Raw.EndsWith("/>") || Void.Contains(tok.Name);
                _tokens.Add(tok);
                i = end;
                if (!tok.End && !tok.SelfClose && RawText.Contains(tok.Name))
                {
                    // everything up to the closing tag is text that must not be parsed or translated
                    var close = Regex.Match(s[i..], @"</" + Regex.Escape(tok.Name) + @"\s*>", RegexOptions.IgnoreCase);
                    int ce = close.Success ? i + close.Index : s.Length;
                    if (ce > i) _tokens.Add(new Token { Raw = s[i..ce], Name = "#raw" });
                    i = ce;
                }
            }
        }

        /// <summary>End of a tag, skipping quoted attribute values (they may contain '&gt;').</summary>
        static int TagEnd(string s, int lt)
        {
            char q = '\0';
            for (int i = lt + 1; i < s.Length; i++)
            {
                char c = s[i];
                if (q != '\0') { if (c == q) q = '\0'; }
                else if (c == '"' || c == '\'') q = c;
                else if (c == '>') return i + 1;
            }
            return s.Length;
        }

        void AddText(string raw)
        {
            if (raw.Length == 0) return;
            _tokens.Add(new Token { Raw = raw, IsText = true });
        }

        /// <summary>Cuts the fragment into segments; each text token becomes a slot that rewrites its Raw.</summary>
        public List<Segment> Segments()
        {
            var segs = new List<Segment>();
            var cur = new Segment();
            void Flush() { if (cur.Slots.Count > 0) { segs.Add(cur); cur = new Segment(); } }
            int skip = _initialSkip;
            foreach (var t in _tokens)
            {
                if (t.IsText)
                {
                    if (skip > 0) continue;
                    var tok = t;
                    cur.Slots.Add(new Slot { Text = Unesc(t.Raw), Set = v => tok.Raw = EscText(v) });
                    continue;
                }
                if (t.Name is "!" or "#raw") continue;
                if (Skip.Contains(t.Name) && !t.SelfClose)
                {
                    Flush();
                    skip = Math.Max(0, skip + (t.End ? -1 : 1));
                    continue;
                }
                if (Inline.Contains(t.Name)) continue;
                Flush(); // block, cell, br, img…
            }
            Flush();
            return segs;
        }

        /// <summary>Text to HTML: &amp; &lt; &gt; escaped, no-break spaces kept visible.</summary>
        static string EscText(string v) => Esc(v).Replace(" ", "&nbsp;");

        /// <summary>The page with the translated fragment, under a fresh CF_HTML header.</summary>
        public byte[] Build()
        {
            var frag = string.Concat(_tokens.Select(t => t.Raw));
            const string Fmt = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            string url = _sourceUrl.Length > 0 ? "SourceURL:" + _sourceUrl + "\r\n" : "";
            int headerLen = Encoding.UTF8.GetByteCount(string.Format(Fmt, 0, 0, 0, 0) + url);
            int sh = headerLen;
            int sf = sh + Encoding.UTF8.GetByteCount(_pre);
            int ef = sf + Encoding.UTF8.GetByteCount(frag);
            int eh = ef + Encoding.UTF8.GetByteCount(_post);
            return Encoding.UTF8.GetBytes(string.Format(Fmt, sh, eh, sf, ef) + url + _pre + frag + _post);
        }

        /// <summary>Plain text of the (translated) fragment: blocks and rows on their own lines, cells split by tabs.</summary>
        public string PlainText()
        {
            var sb = new StringBuilder();
            void NewLine()
            {
                while (sb.Length > 0 && (sb[^1] == ' ' || sb[^1] == '\t')) sb.Length--;
                if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            }
            int pre = _initialSkip > 0 ? 1 : 0, hidden = 0;
            bool cellOpen = false;
            foreach (var t in _tokens)
            {
                if (t.IsText)
                {
                    if (hidden > 0) continue;
                    var s = Unesc(t.Raw).Replace(" ", " ");
                    if (pre > 0) { sb.Append(s.Replace("\r\n", "\n")); continue; }
                    s = Ws.Replace(s, " ");
                    if (s == " " && (sb.Length == 0 || sb[^1] is ' ' or '\n' or '\t')) continue;
                    if (s.StartsWith(' ') && sb.Length > 0 && sb[^1] is ' ' or '\n' or '\t') s = s[1..];
                    sb.Append(s);
                    continue;
                }
                switch (t.Name)
                {
                    case "pre": pre = Math.Max(0, pre + (t.End ? -1 : 1)); NewLine(); break;
                    case "script" or "style" or "head" or "title" or "xml" or "noscript": if (!t.SelfClose) hidden = Math.Max(0, hidden + (t.End ? -1 : 1)); break;
                    case "br": sb.Append('\n'); break;
                    case "td" or "th":
                        if (!t.End && cellOpen) { while (sb.Length > 0 && sb[^1] == ' ') sb.Length--; sb.Append('\t'); }
                        if (!t.End) cellOpen = true;
                        break;
                    case "tr": cellOpen = false; NewLine(); break;
                    default: if (Block.Contains(t.Name)) NewLine(); break;
                }
            }
            return Regex.Replace(sb.ToString(), @"[ \t]+\n", "\n").Trim('\n', ' ').Replace("\n", "\r\n");
        }
    }

    // ───────────── RTF (via a WPF FlowDocument) ─────────────

    internal static FlowDocument LoadRtf(byte[] data)
    {
        int n = data.Length;
        while (n > 0 && data[n - 1] == 0) n--;
        var fd = new FlowDocument();
        var range = new TextRange(fd.ContentStart, fd.ContentEnd);
        using var ms = new MemoryStream(data, 0, n);
        range.Load(ms, DataFormats.Rtf);
        return fd;
    }

    internal static List<Segment> RtfSegments(FlowDocument fd)
    {
        var segs = new List<Segment>();
        Segment cur = new();
        void Flush() { if (cur.Slots.Count > 0) { segs.Add(cur); cur = new Segment(); } }

        void Inlines(InlineCollection items)
        {
            foreach (var inline in items.ToList())
            {
                switch (inline)
                {
                    case Run r:
                        if (IsCode(r)) { Flush(); break; }
                        cur.Slots.Add(new Slot { Text = r.Text, Set = v => r.Text = v });
                        break;
                    case Span sp:
                        if (IsCode(sp)) { Flush(); break; }
                        Inlines(sp.Inlines);
                        break;
                    default: // LineBreak, pictures
                        Flush();
                        break;
                }
            }
        }
        void Blocks(BlockCollection blocks)
        {
            foreach (var b in blocks.ToList())
            {
                switch (b)
                {
                    case Paragraph p: if (!IsCode(p)) Inlines(p.Inlines); Flush(); break;
                    case List l: foreach (var li in l.ListItems) Blocks(li.Blocks); break;
                    case Table t:
                        foreach (var g in t.RowGroups) foreach (var row in g.Rows) foreach (var cell in row.Cells) Blocks(cell.Blocks);
                        break;
                    case Section s: Blocks(s.Blocks); break;
                }
            }
        }
        Blocks(fd.Blocks);
        return segs;
    }

    /// <summary>Text set in a monospaced font of its own is code: left alone.</summary>
    static bool IsCode(TextElement e) =>
        e.ReadLocalValue(TextElement.FontFamilyProperty) is System.Windows.Media.FontFamily { Source: { } f }
        && (f.Contains("Consolas", StringComparison.OrdinalIgnoreCase) || f.Contains("Courier", StringComparison.OrdinalIgnoreCase)
            || f.Contains("Mono", StringComparison.OrdinalIgnoreCase));
}
