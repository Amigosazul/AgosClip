using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgosClip;

/// <summary>
/// Picture translation: OCR the picture, group the lines into paragraphs, translate each paragraph, then
/// paint over the original lines with their own background colour and write the translation in their place.
/// </summary>
public static class ImageTranslator
{
    public sealed record Result(BitmapSource Image, int Blocks, string Engine, string Target);

    /// <summary>A paragraph: lines that sit under one another with similar height and overlapping columns.</summary>
    internal sealed class Block
    {
        public readonly List<OcrLine> Lines = new();
        public Int32Rect Bounds;
        public double LineHeight => Lines.Average(l => l.Box.Height);

        public string Text
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < Lines.Count; i++)
                {
                    var t = Lines[i].Text;
                    if (i > 0)
                    {
                        // a wrapped English line needs a space; Chinese doesn't; a hyphenated word joins
                        char a = sb[^1], b = t[0];
                        if (a == '-' && char.IsLetter(b)) sb.Length--;
                        else if (!(IsCjk(a) || IsCjk(b))) sb.Append(' ');
                    }
                    sb.Append(t);
                }
                return sb.ToString();
            }
        }
    }

    static bool IsCjk(char c) => c >= 0x2E80 && c <= 0x9FFF || c >= 0xFF00 && c <= 0xFFEF || c >= 0x3000 && c <= 0x303F;

    public static async Task<Result> TranslateAsync(BitmapSource image, string target, CancellationToken ct = default)
    {
        var ocr = await RapidOcr.RecognizeAsync(image, ct);
        if (ocr.Lines.Count == 0) throw new InvalidOperationException("图片里没有识别到文字");
        var blocks = Group(ocr.Lines);
        var texts = blocks.Select(b => b.Text).ToList();
        var to = Translator.Resolve(target, texts);
        var (translated, engine) = await Translator.TranslateAsync(texts, to, ct);
        return new Result(Render(image, blocks, translated), blocks.Count, engine, to);
    }

    internal static List<Block> Group(IReadOnlyList<OcrLine> lines)
    {
        var blocks = new List<Block>();
        foreach (var l in lines.OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X))
        {
            var b = blocks.LastOrDefault(b => Continues(b, l));
            if (b == null) { b = new Block(); blocks.Add(b); b.Bounds = l.Box; }
            else b.Bounds = Union(b.Bounds, l.Box);
            b.Lines.Add(l);
        }
        return blocks;
    }

    static bool Continues(Block b, OcrLine l)
    {
        var last = b.Lines[^1];
        double h = b.LineHeight;
        if (l.Box.Height < h * 0.7 || l.Box.Height > h * 1.4) return false;
        int gap = l.Box.Y - (last.Box.Y + last.Box.Height);
        if (gap < -h * 0.3 || gap > h * 0.9) return false;                 // same row, or too far below
        if (RapidOcr.SameRow(last.Box, l.Box)) return false;                // side by side = another column/cell
        int x0 = Math.Max(b.Bounds.X, l.Box.X), x1 = Math.Min(b.Bounds.X + b.Bounds.Width, l.Box.X + l.Box.Width);
        if (x1 - x0 < Math.Min(b.Bounds.Width, l.Box.Width) * 0.3) return false; // columns must overlap
        if (StartsItem(l.Text)) return false;                                // "1." "•" "-" … = a list item of its own
        double lastCx = last.Box.X + last.Box.Width / 2.0, cx = l.Box.X + l.Box.Width / 2.0;
        bool centred = Math.Abs(lastCx - cx) < h * 0.8 && Math.Abs(last.Box.X - l.Box.X) > h;
        if (centred) return true;                                            // a centred paragraph (title, card text)
        // the line before ended well short of the paragraph's right edge: that was a hard line break
        // (end of a paragraph, a heading, a list/menu line), so the new line starts a block of its own
        int right = Math.Max(b.Bounds.X + b.Bounds.Width, l.Box.X + l.Box.Width);
        if (last.Box.X + last.Box.Width < right - h * 2.5) return false;
        return Math.Abs(l.Box.X - b.Bounds.X) < h * 3;                      // left edges roughly aligned
    }

    static readonly System.Text.RegularExpressions.Regex ItemStart = new(
        @"^\s*([•·●○■□▪◦▶►✓✔☐☑*\-–—]|\d{1,3}(?:[.．](?!\d)|[)、])|[（(]\d{1,3}[)）]|[a-zA-Z][.)]\s|[一二三四五六七八九十]{1,3}[、.．])");

    internal static bool StartsItem(string text) => ItemStart.IsMatch(text);

    internal enum Align { Left, Center, Right }

    /// <summary>How a paragraph's lines line up: a single line or ragged lines count as left-aligned.</summary>
    internal static Align AlignOf(Block b)
    {
        if (b.Lines.Count < 2) return Align.Left;
        double h = b.LineHeight, tol = h * 0.6;
        double Spread(Func<Int32Rect, double> f) => b.Lines.Max(l => f(l.Box)) - b.Lines.Min(l => f(l.Box));
        if (Spread(r => r.X) <= tol) return Align.Left;
        if (Spread(r => r.X + r.Width / 2.0) <= tol) return Align.Center;
        if (Spread(r => r.X + r.Width) <= tol) return Align.Right;
        return Align.Left;
    }

    /// <summary>
    /// Typical stroke thickness of the text in a line, relative to the line height: the horizontal runs of
    /// text-coloured pixels, each measured with sub-pixel precision (an anti-aliased edge pixel counts as part
    /// ink), averaged over the middle half so long horizontal strokes don't count. Bold text has clearly
    /// thicker strokes; whole-pixel run lengths would be too coarse for small text (2 px vs 3 px = 1.5×).
    /// </summary>
    internal static double StrokeRatio(byte[] px, int w, int h, Int32Rect r, Color bg)
    {
        int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y);
        int y1 = Math.Min(h, r.Y + r.Height), x1 = Math.Min(w, r.X + r.Width);
        if (x1 <= x0 || y1 <= y0) return 0;
        int Dist(int x, int y)
        {
            int o = (y * w + x) * 4;
            return Math.Abs(px[o + 2] - bg.R) + Math.Abs(px[o + 1] - bg.G) + Math.Abs(px[o] - bg.B);
        }
        // full ink = the text colour's distance from the background (a high percentile, not the single max)
        var all = new List<int>();
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int d = Dist(x, y); if (d >= 60) all.Add(d); }
        if (all.Count < 20) return 0;
        all.Sort();
        double full = all[(int)(all.Count * 0.9)];
        double edge = full * 0.2;
        var runs = new List<double>();
        for (int y = y0; y < y1; y++)
        {
            double run = 0;
            for (int x = x0; x <= x1; x++)
            {
                double d = x < x1 ? Dist(x, y) : 0;
                if (d >= edge) run += Math.Min(1, d / full);
                else if (run > 0) { runs.Add(run); run = 0; }
            }
        }
        if (runs.Count < 8) return 0;
        runs.Sort();
        int q1 = runs.Count / 4, q3 = Math.Max(q1 + 1, runs.Count * 3 / 4);
        double mean = 0;
        for (int i = q1; i < q3; i++) mean += runs[i];
        return mean / (q3 - q1) / r.Height;
    }

    static Int32Rect Union(Int32Rect a, Int32Rect b)
    {
        int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        int x1 = Math.Max(a.X + a.Width, b.X + b.Width), y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }

    internal static BitmapSource Render(BitmapSource image, IReadOnlyList<Block> blocks, IReadOnlyList<string> translated)
    {
        var src = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        src.CopyPixels(px, stride, 0);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(src, new Rect(0, 0, w, h)); // pixel units: rendered at 96 dpi below
            var family = new FontFamily("Microsoft YaHei UI, Segoe UI");
            var regular = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var bold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            // colours and stroke thickness of every paragraph first: "bold" is judged against the picture's body text
            var colors = blocks.Select(b => PickColors(px, w, h, b.Lines[0].Box)).ToList();
            var strokes = blocks.Select((b, i) => b.Lines.Average(l => StrokeRatio(px, w, h, l.Box, colors[i].Bg))).ToList();
            var bolds = Bolds(strokes);
            for (int i = 0; i < blocks.Count; i++)
            {
                var text = translated[i]?.Trim() ?? "";
                if (text.Length == 0) continue;
                var block = blocks[i];
                var (bg, fg) = colors[i];
                var typeface = bolds[i] ? bold : regular;
                var align = AlignOf(block);
                var bgBrush = new SolidColorBrush(bg); bgBrush.Freeze();
                var fgBrush = new SolidColorBrush(fg); fgBrush.Freeze();
                foreach (var l in block.Lines) dc.DrawRectangle(bgBrush, null, Inflate(l.Box, 2, w, h));

                // translations are often longer than the original: first let the text use free space to the
                // right (up to the next paragraph on the same rows, or the picture's edge), then shrink the font
                // down to ~60% of the original; below that let it overflow downwards rather than become unreadable
                var area = block.Bounds;
                int right = w - 4;
                foreach (var other in blocks)
                    if (other != block && other.Bounds.X >= area.X + area.Width
                        && other.Bounds.Y < area.Y + area.Height && other.Bounds.Y + other.Bounds.Height > area.Y)
                        right = Math.Min(right, other.Bounds.X - 6);
                // centred text may grow evenly both ways, right-aligned text keeps its right edge
                double cx = area.X + area.Width / 2.0, areaRight = area.X + area.Width;
                double wide = align switch
                {
                    Align.Center => Math.Max(area.Width, 2 * Math.Min(cx - 4, right - cx)),
                    Align.Right => area.Width,
                    _ => Math.Max(area.Width, right - area.X),
                };
                double full = Math.Max(9, block.LineHeight * 0.72), min = Math.Max(9, full * 0.6);
                var textAlign = align switch { Align.Center => TextAlignment.Center, Align.Right => TextAlignment.Right, _ => TextAlignment.Left };
                double width = area.Width;
                FormattedText Layout(double size, double wd)
                {
                    width = Math.Max(8, wd);
                    var f = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, fgBrush, 1.0)
                    { MaxTextWidth = width, Trimming = TextTrimming.None, TextAlignment = textAlign };
                    f.LineHeight = size * 1.25;
                    return f;
                }
                double size = full;
                var ft = Layout(size, area.Width);
                if (ft.Height > area.Height + 1) ft = Layout(size, wide);
                while (ft.Height > area.Height + 1 && size > min)
                {
                    size = Math.Max(min, size * 0.92);
                    ft = Layout(size, wide);
                }
                double x = align switch { Align.Center => cx - width / 2, Align.Right => areaRight - width, _ => area.X };
                // cover whatever the new text spills onto beyond the original lines
                double textW = Math.Min(width, ft.WidthIncludingTrailingWhitespace + 2);
                double textX = align switch { Align.Center => cx - textW / 2, Align.Right => areaRight - textW, _ => area.X };
                if (textW > area.Width || ft.Height > area.Height + 1)
                {
                    double l0 = Math.Max(0, Math.Min(area.X, textX)), r0 = Math.Min(w, Math.Max(areaRight, textX + textW));
                    dc.DrawRectangle(bgBrush, null, new Rect(l0, area.Y, r0 - l0, Math.Min(h - area.Y, Math.Max(area.Height, ft.Height))));
                }
                // a paragraph starts at its top like the original; a single line is centred on it
                double y = block.Lines.Count > 1 ? area.Y : area.Y + Math.Max(0, (area.Height - ft.Height) / 2);
                dc.DrawText(ft, new Point(x, y));
            }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// Which paragraphs are bold, from their stroke ratios: clearly thicker strokes than the picture's body text
    /// (the lower median, so with two paragraphs the thinner one), or thick in absolute terms.
    /// </summary>
    internal static List<bool> Bolds(IReadOnlyList<double> strokes)
    {
        var known = strokes.Where(s => s > 0).OrderBy(s => s).ToList();
        double body = known.Count > 0 ? known[(known.Count - 1) / 2] : 0;
        return strokes.Select(s => s >= BoldAbsolute || known.Count >= 2 && s >= body * 1.4 && s >= BoldRelativeMin).ToList();
    }

    internal const double BoldAbsolute = 0.11, BoldRelativeMin = 0.08;

    static Rect Inflate(Int32Rect r, int by, int w, int h)
    {
        double x0 = Math.Max(0, r.X - by), y0 = Math.Max(0, r.Y - by);
        double x1 = Math.Min(w, r.X + r.Width + by), y1 = Math.Min(h, r.Y + r.Height + by);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Background = the most common colour on the box's border; text colour = the average of the pixels inside
    /// that differ clearly from it (else black or white, whichever contrasts).
    /// </summary>
    static (Color Bg, Color Fg) PickColors(byte[] px, int w, int h, Int32Rect r)
    {
        // 5-bit buckets: count, and sum the real colours so the fill matches exactly
        var counts = new Dictionary<int, (int N, long R, long G, long B)>();
        void Count(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int o = (y * w + x) * 4;
            int key = (px[o + 2] >> 3) << 10 | (px[o + 1] >> 3) << 5 | px[o] >> 3;
            var c = counts.GetValueOrDefault(key);
            counts[key] = (c.N + 1, c.R + px[o + 2], c.G + px[o + 1], c.B + px[o]);
        }
        for (int x = r.X; x < r.X + r.Width; x++) { Count(x, r.Y); Count(x, r.Y + r.Height - 1); }
        for (int y = r.Y; y < r.Y + r.Height; y++) { Count(r.X, y); Count(r.X + r.Width - 1, y); }
        var bg = System.Windows.Media.Colors.White;
        if (counts.Count > 0)
        {
            var top = counts.Values.MaxBy(c => c.N);
            bg = Color.FromRgb((byte)(top.R / top.N), (byte)(top.G / top.N), (byte)(top.B / top.N));
        }

        long sr = 0, sg = 0, sb = 0, n = 0;
        for (int y = r.Y; y < Math.Min(h, r.Y + r.Height); y++)
            for (int x = r.X; x < Math.Min(w, r.X + r.Width); x++)
            {
                int o = (y * w + x) * 4;
                int d = Math.Abs(px[o + 2] - bg.R) + Math.Abs(px[o + 1] - bg.G) + Math.Abs(px[o] - bg.B);
                if (d < 150) continue;
                sr += px[o + 2]; sg += px[o + 1]; sb += px[o]; n++;
            }
        double lum = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B;
        var fg = n > 0 ? Color.FromRgb((byte)(sr / n), (byte)(sg / n), (byte)(sb / n)) : lum > 140 ? System.Windows.Media.Colors.Black : System.Windows.Media.Colors.White;
        return (bg, fg);
    }
}
