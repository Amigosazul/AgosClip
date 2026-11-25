using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgosClip;

internal static partial class SelfTest
{
    /// <summary>Draws lines of text (at the given y, font size, colours) onto a w×h picture.</summary>
    static BitmapSource TextPicture(int w, int h, Color bg, IEnumerable<(string Text, double X, double Y, double Size, Color Fg)> lines, Func<string, bool>? bold = null)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, w, h));
            var tf = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var tfBold = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            foreach (var l in lines)
                dc.DrawText(new FormattedText(l.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold?.Invoke(l.Text) == true ? tfBold : tf, l.Size, new SolidColorBrush(l.Fg), 1.0), new Point(l.X, l.Y));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    static T Wait<T>(Task<T> t, int timeoutMs = 60000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!t.IsCompleted && sw.ElapsedMilliseconds < timeoutMs) Pump(50);
        if (!t.IsCompleted) throw new TimeoutException("task did not finish");
        return t.GetAwaiter().GetResult();
    }

    static string Squash(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    static void OcrTests(string outDir)
    {
        // translation helpers (offline)
        Check(Translator.Resolve(Translator.Auto, new[] { "你好，世界" }) == "en", "translate auto: Chinese → English");
        Check(Translator.Resolve(Translator.Auto, new[] { "Hello world" }) == "zh-Hans", "translate auto: English → Chinese");
        Check(Translator.Resolve(Translator.Auto, new[] { "用 git rebase 整理提交" }) == "en", "translate auto: Chinese with some English words counts as Chinese");
        Check(Translator.Resolve("ja", new[] { "Hello" }) == "ja", "translate: explicit target kept");
        var pieces = Translator.SplitLong(string.Join("\n", Enumerable.Repeat(new string('a', 300), 20)), 1000).ToList();
        Check(pieces.All(p => p.Length <= 1000) && string.Concat(pieces).Replace("\n", "").Length == 6000, $"translate: long text split into {pieces.Count} pieces without losing characters");

        // paragraph grouping + rendering (offline, synthetic OCR lines)
        var fake = new List<OcrLine>
        {
            new(new Int32Rect(20, 20, 300, 24), "The quick brown fox jumps over", 0.9f),
            new(new Int32Rect(20, 50, 220, 24), "the lazy dog.", 0.9f),
            new(new Int32Rect(420, 20, 120, 24), "Price", 0.9f),     // another column on the same row
            new(new Int32Rect(20, 140, 200, 24), "Second paragraph", 0.9f), // far below
        };
        var blocks = ImageTranslator.Group(fake);
        Check(blocks.Count == 3 && blocks[0].Lines.Count == 2 && blocks[0].Text == "The quick brown fox jumps over the lazy dog.",
            $"image translate: lines grouped into paragraphs ({blocks.Count} blocks, first = \"{blocks.FirstOrDefault()?.Text}\")");
        var page = TextPicture(600, 200, Color.FromRgb(0xF4, 0xF1, 0xE8), fake.Select(l => (l.Text, (double)l.Box.X, (double)l.Box.Y, 18.0, Colors.Black)));
        var rendered = ImageTranslator.Render(page, blocks, new[] { "敏捷的棕色狐狸跳过了懒狗。", "价格", "第二段" });
        Check(rendered.PixelWidth == 600 && rendered.PixelHeight == 200, "image translate: output keeps the picture size");
        File.WriteAllBytes(Path.Combine(outDir, "translate-render.png"), ScreenCapture.Png(rendered));
        {
            // the background colour of the paragraph is reused for the cover-up (cream, not white)
            var px = new byte[4];
            new CroppedBitmap(rendered, new Int32Rect(310, 60, 1, 1)).CopyPixels(px, 4, 0);
            Check(Math.Abs(px[2] - 0xF4) < 6 && Math.Abs(px[1] - 0xF1) < 6 && Math.Abs(px[0] - 0xE8) < 6, $"image translate: cover-up uses the sampled background ({px[2]:X2}{px[1]:X2}{px[0]:X2})");
        }

        // formatting: list items and short lines keep their own line, centred paragraphs stay centred, bold stays bold
        {
            var layout = new List<OcrLine>
            {
                new(new Int32Rect(20, 20, 100, 24), "Settings", 0.9f),
                new(new Int32Rect(20, 50, 200, 24), "1. Open the panel", 0.9f),
                new(new Int32Rect(20, 80, 180, 24), "2. Press Ctrl+T", 0.9f),
                new(new Int32Rect(20, 130, 120, 24), "Short menu line", 0.9f),
                new(new Int32Rect(20, 160, 320, 24), "A much longer line right under it", 0.9f),
                new(new Int32Rect(200, 230, 160, 24), "Welcome to the", 0.9f),
                new(new Int32Rect(220, 260, 120, 24), "new release", 0.9f),
            };
            var lb = ImageTranslator.Group(layout);
            Check(lb.Count == 6 && lb[5].Lines.Count == 2, $"image translate: list items and hard line breaks stay separate lines ({lb.Count} blocks: {string.Join(" | ", lb.Select(b => b.Text))})");
            Check(lb.Count == 6 && ImageTranslator.AlignOf(lb[5]) == ImageTranslator.Align.Center && ImageTranslator.AlignOf(lb[1]) == ImageTranslator.Align.Left,
                "image translate: centred paragraph detected as centred");
            Check(ImageTranslator.StartsItem("• item") && ImageTranslator.StartsItem("3) step") && ImageTranslator.StartsItem("二、背景")
                && !ImageTranslator.StartsItem("3.14 is pi") && !ImageTranslator.StartsItem("the lazy dog"), "image translate: list item markers");

            // bold vs regular, the same size, English and Chinese
            var texts = new[] { ("Bold Title Text", true), ("regular body text line here", false), ("粗体标题文字", true), ("普通正文的一行文字", false) };
            var specs = texts.Select((t, i) => (t.Item1, 20.0, 20.0 + i * 50, 20.0, Color.FromRgb(0x22, 0x22, 0x22))).ToList();
            var bpic = TextPicture(600, 220, Colors.White, specs, t => texts.First(x => x.Item1 == t).Item2);
            var bpx = new byte[600 * 220 * 4];
            new FormatConvertedBitmap(bpic, PixelFormats.Bgra32, null, 0).CopyPixels(bpx, 600 * 4, 0);
            var ratios = specs.Select(sp =>
            {
                var ft = new FormattedText(sp.Item1, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 20, Brushes.Black, 1.0);
                var box = new Int32Rect(16, (int)sp.Item3 - 2, (int)ft.Width + 8, (int)ft.Height + 4);
                return ImageTranslator.StrokeRatio(bpx, 600, 220, box, Colors.White);
            }).ToList();
            var bolds = ImageTranslator.Bolds(ratios);
            Check(bolds.SequenceEqual(texts.Select(t => t.Item2)), $"image translate: bold detected (stroke ratios {string.Join(", ", ratios.Select(r => r.ToString("0.000")))})");
            Check(ImageTranslator.Bolds(new[] { ratios[1] }).SequenceEqual(new[] { false }) && ImageTranslator.Bolds(new[] { ratios[1], ratios[3] }).All(b => !b)
                && ImageTranslator.Bolds(new[] { ratios[0] }).SequenceEqual(new[] { true }),
                "image translate: regular text alone (or regular Chinese next to English) is not bold; bold alone still is");
            // render: OCR-like boxes measured around the drawn text; nothing in this picture is bold
            var lpic = TextPicture(600, 300, Colors.White, layout.Select(l => (l.Text, (double)l.Box.X, (double)l.Box.Y, 17.0, Colors.Black)));
            var lpx = new byte[600 * 300 * 4];
            new FormatConvertedBitmap(lpic, PixelFormats.Bgra32, null, 0).CopyPixels(lpx, 600 * 4, 0);
            var measured = layout.Select(l =>
            {
                var ft = new FormattedText(l.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 17, Brushes.Black, 1.0);
                return new OcrLine(new Int32Rect(l.Box.X - 2, l.Box.Y - 1, (int)Math.Ceiling(ft.Width) + 4, (int)Math.Ceiling(ft.Height) + 2), l.Text, l.Score);
            }).ToList();
            var mb = ImageTranslator.Group(measured);
            var plain = mb.Select(b => b.Lines.Average(l => ImageTranslator.StrokeRatio(lpx, 600, 300, l.Box, Colors.White))).ToList();
            Check(ImageTranslator.Bolds(plain).All(b => !b), $"image translate: regular text of one picture is not taken for bold (ratios {string.Join(", ", plain.Select(r => r.ToString("0.000")))})");
            var lr = ImageTranslator.Render(lpic, mb, mb.Select((b, i) => b.Text.StartsWith("Welcome") ? "欢迎使用新版本" : "译文 " + i).ToList());
            File.WriteAllBytes(Path.Combine(outDir, "translate-layout.png"), ScreenCapture.Png(lr));

            // the pin's translation status sits outside the picture, with a spinner while it works
            var pin = PinWindow.Open(lpic, new Int32Rect(200, 200, 600, 300), onTop: false);
            pin.TranslateInPlace();
            Pump(100);
            Check(RapidOcr.Available ? pin.StatusBusy && pin.StatusMessage.Contains("翻译") : !pin.StatusBusy && pin.StatusMessage.Contains("插件"),
                $"pin translate: status pill shown (busy={pin.StatusBusy}, \"{pin.StatusMessage}\")");
            pin.Close();
            Pump(100);
            Check(!pin.StatusBusy && pin.StatusMessage == "", "pin translate: status pill goes away with the pin");
        }

        if (!RapidOcr.Available)
        {
            Report.AppendLine("SKIP OCR recognition (set AGOSCLIP_OCR_DIR to an unpacked OCR plugin)");
            return;
        }

        // 1. plain screenshot-like picture: Chinese, English, numbers, a dark row
        var img = TextPicture(720, 260, Colors.White, new (string, double, double, double, Color)[]
        {
            ("剪贴板历史记录", 24, 20, 28, Colors.Black),
            ("Hello AgosClip 2026", 24, 80, 24, Color.FromRgb(0x20, 0x20, 0x20)),
            ("截图以后可以识别文字", 24, 140, 20, Color.FromRgb(0x33, 0x33, 0x33)),
        });
        {
            // dark strip at the bottom with light text
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(img, new Rect(0, 0, 720, 260));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x20)), null, new Rect(0, 200, 720, 60));
                dc.DrawText(new FormattedText("Dark mode text 42", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 22, Brushes.White, 1.0), new Point(24, 214));
            }
            var rtb = new RenderTargetBitmap(720, 260, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            img = rtb;
        }
        File.WriteAllBytes(Path.Combine(outDir, "ocr-input.png"), ScreenCapture.Png(img));
        var r = Wait(RapidOcr.RecognizeAsync(img));
        Report.AppendLine($"     ocr text ({r.Elapsed.TotalMilliseconds:0} ms): {r.Text.Replace("\n", " | ")}");
        var flat = Squash(r.Text);
        Check(flat.Contains("剪贴板历史记录"), "OCR: Chinese line");
        Check(flat.Contains("HelloAgosClip2026"), "OCR: English + digits line");
        Check(flat.Contains("截图以后可以识别文字"), "OCR: smaller Chinese line");
        Check(flat.Contains("Darkmodetext42"), "OCR: light text on a dark background");
        var order = new[] { "剪贴板", "Hello", "截图", "Dark" }.Select(k => r.Text.IndexOf(k, StringComparison.Ordinal)).ToList();
        Check(order.All(i => i >= 0) && order.SequenceEqual(order.OrderBy(i => i)), "OCR: lines come out top to bottom");
        Check(r.Lines.All(l => l.Box.X >= 0 && l.Box.Y >= 0 && l.Box.X + l.Box.Width <= 720 && l.Box.Y + l.Box.Height <= 260), "OCR: boxes inside the picture");

        // 2. a single flat line (exercises the letterbox padding)
        var flatLine = TextPicture(900, 36, Colors.White, new[] { ("A single wide line of text for padding", 8.0, 4.0, 20.0, Colors.Black) });
        var r2 = Wait(RapidOcr.RecognizeAsync(flatLine));
        Check(Squash(r2.Text).Contains("Asinglewidelineoftext"), $"OCR: one flat line ({r2.Text})");

        // 3. a long screenshot: cut into strips, a line on a strip boundary must appear exactly once
        var longLines = Enumerable.Range(0, 40).Select(i => ($"Line number {i:00} of the long page", 30.0, 30.0 + i * 120, 22.0, Colors.Black)).ToList();
        var tall = TextPicture(800, 4900, Colors.White, longLines);
        var r3 = Wait(RapidOcr.RecognizeAsync(tall), 120000);
        var found = Enumerable.Range(0, 40).Count(i => Squash(r3.Text).Contains($"Linenumber{i:00}"));
        var dupes = Enumerable.Range(0, 40).Count(i => System.Text.RegularExpressions.Regex.Matches(Squash(r3.Text), $"Linenumber{i:00}").Count > 1);
        Check(found >= 38 && dupes == 0, $"OCR: long picture read in strips ({found}/40 lines, {dupes} duplicated, {r3.Elapsed.TotalSeconds:0.0} s)");

        // 4. picture translation end to end needs the network: opt-in
        if (Environment.GetEnvironmentVariable("AGOSCLIP_NET_TESTS") == "1")
        {
            try
            {
                var (t, engine) = Wait(Translator.TranslateAsync(new[] { "Hello world", "" }, "zh-Hans"));
                Check(t.Length == 2 && t[0].Contains("世界") && t[1] == "", $"translate online via {engine}: {t[0]}");
                var tr = Wait(ImageTranslator.TranslateAsync(img, Translator.Auto));
                File.WriteAllBytes(Path.Combine(outDir, "translate-online.png"), ScreenCapture.Png(tr.Image));
                Check(tr.Blocks >= 3, $"image translate online: {tr.Blocks} blocks via {tr.Engine}");
            }
            catch (Exception ex) { Check(false, "translate online: " + ex.Message); }
        }

        // 5. the result window
        var w = TextToolWindow.ShowOcr(img);
        for (int i = 0; i < 100 && w.SourceText.Length == 0; i++) Pump(100);
        Check(Squash(w.SourceText).Contains("剪贴板历史记录"), "OCR window shows the recognised text");
        RenderOnGlass((FrameworkElement)w.Content, Path.Combine(outDir, "ocr-window.png"), false, false);
        w.Close();
        RapidOcr.Unload();
    }
}
