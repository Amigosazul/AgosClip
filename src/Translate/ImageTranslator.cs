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
        return Math.Abs(l.Box.X - b.Bounds.X) < h * 3;                      // left edges roughly aligned
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
            var typeface = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            for (int i = 0; i < blocks.Count; i++)
            {
                var text = translated[i]?.Trim() ?? "";
                if (text.Length == 0) continue;
                var block = blocks[i];
                var (bg, fg) = PickColors(px, w, h, block.Lines[0].Box);
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
                double wide = Math.Max(area.Width, right - area.X);
                double full = Math.Max(9, block.LineHeight * 0.72), min = Math.Max(9, full * 0.6);
                FormattedText Layout(double size, double width)
                {
                    var f = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, fgBrush, 1.0)
                    { MaxTextWidth = Math.Max(8, width), Trimming = TextTrimming.None };
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
                // cover whatever the new text spills onto beyond the original lines
                double textW = Math.Min(wide, ft.WidthIncludingTrailingWhitespace + 2);
                if (textW > area.Width || ft.Height > area.Height + 1)
                    dc.DrawRectangle(bgBrush, null, new Rect(area.X, area.Y, Math.Max(area.Width, textW), Math.Min(h - area.Y, Math.Max(area.Height, ft.Height))));
                // a paragraph starts at its top like the original; a single line is centred on it
                double y = block.Lines.Count > 1 ? area.Y : area.Y + Math.Max(0, (area.Height - ft.Height) / 2);
                dc.DrawText(ft, new Point(area.X, y));
            }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

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
