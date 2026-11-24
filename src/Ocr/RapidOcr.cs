using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;

namespace AgosClip;

/// <summary>One recognised line: where it is in the picture (pixels) and what it says.</summary>
public sealed record OcrLine(Int32Rect Box, string Text, float Score);

public sealed record OcrResult(IReadOnlyList<OcrLine> Lines, string Text, TimeSpan Elapsed);

/// <summary>
/// RapidOCR (PaddleOCR PP-OCRv4 det + rec as ONNX) on ONNX Runtime, ported from rapidocr_onnxruntime 1.4.4
/// without OpenCV. The models and onnxruntime.dll come from the OCR plugin (see <see cref="Plugins"/>);
/// the app itself only carries the managed ONNX Runtime wrapper.
/// <para>Differences from the Python original, all aimed at screenshots: boxes are axis-aligned (connected
/// components instead of minAreaRect contours), there is no angle classifier, and tall pictures (long
/// screenshots) are cut into overlapping strips instead of being shrunk to 2000 px.</para>
/// </summary>
public static class RapidOcr
{
    const int DetLimitSide = 736;   // limit_type "min": the short side is scaled up to this
    const int DetMaxSide = 2000;    // max_side_len: larger pictures are scaled down for detection only
    const int DetHardCap = 4096;    // never feed the detector more than this per side
    const float DetThresh = 0.3f, BoxThresh = 0.5f, UnclipRatio = 1.6f;
    const float TextScore = 0.5f;
    const int RecHeight = 48, RecBatch = 6;

    static readonly object Gate = new();
    static InferenceSession? _det, _rec;
    static string[] _chars = Array.Empty<string>();
    static string? _loadedFrom;
    static string? _nativeDir;
    static bool _resolverSet;
    static Timer? _idle;
    // created after the DLL resolver is in place (a static initialiser would load onnxruntime.dll too early)
    static RunOptions? _runOpts;
    static RunOptions RunOpts => _runOpts ??= new RunOptions();

    /// <summary>Folder that holds onnxruntime.dll, det.onnx and rec.onnx; null when the plugin is missing.</summary>
    public static Func<string?> ModelDir { get; set; } = () => Plugins.ActiveDir(Plugins.Ocr);

    public static bool Available => ModelDir() is { } d && Plugins.HasFiles(Plugins.Ocr, d);

    /// <summary>Recognises the text in a picture on a background thread.</summary>
    public static Task<OcrResult> RecognizeAsync(BitmapSource image, CancellationToken ct = default)
    {
        var img = Bgr.From(image); // read the pixels on the calling (UI) thread
        return Task.Run(() => Recognize(img, ct), ct);
    }

    /// <summary>Drops the loaded models (≈100 MB); the next call loads them again.</summary>
    public static void Unload()
    {
        lock (Gate)
        {
            _det?.Dispose(); _rec?.Dispose();
            _det = _rec = null;
            _loadedFrom = null;
        }
    }

    static void EnsureLoaded()
    {
        var dir = ModelDir() ?? throw new InvalidOperationException("文字识别插件还没有安装");
        if (_det != null && _loadedFrom == dir) return;
        Unload();
        SetResolver(dir);
        var opt = new SessionOptions
        {
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _det = new InferenceSession(Path.Combine(dir, "det.onnx"), opt);
        _rec = new InferenceSession(Path.Combine(dir, "rec.onnx"), opt);
        _chars = LoadCharacters(_rec, dir);
        _loadedFrom = dir;
        App.Log($"ocr: models loaded from {dir} ({_chars.Length} characters)");
    }

    /// <summary>
    /// onnxruntime.dll lives in the plugin folder, not next to the exe. Load it by full path: Windows 11 has an
    /// older onnxruntime.dll in System32 that a plain name lookup could pick up.
    /// </summary>
    static void SetResolver(string dir)
    {
        _nativeDir = dir;
        if (_resolverSet) return;
        _resolverSet = true;
        NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, asm, path) =>
        {
            if (_nativeDir != null && name.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase))
            {
                var file = Path.Combine(_nativeDir, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll");
                if (File.Exists(file)) return NativeLibrary.Load(file);
            }
            return IntPtr.Zero;
        });
    }

    /// <summary>PP-OCR dictionary: RapidOCR stores it in the rec model's metadata ("character"), else keys.txt.</summary>
    static string[] LoadCharacters(InferenceSession rec, string dir)
    {
        string? raw = null;
        try { rec.ModelMetadata.CustomMetadataMap.TryGetValue("character", out raw); } catch { }
        var keys = Path.Combine(dir, "keys.txt");
        if (string.IsNullOrEmpty(raw) && File.Exists(keys)) raw = File.ReadAllText(keys, Encoding.UTF8);
        if (string.IsNullOrEmpty(raw)) throw new InvalidDataException("识别模型缺少字符表");
        var list = raw.Replace("\r\n", "\n").Split('\n').ToList();
        if (list.Count > 0 && list[^1].Length == 0) list.RemoveAt(list.Count - 1);
        list.Add(" ");         // use_space_char
        list.Insert(0, "");    // CTC blank
        return list.ToArray();
    }

    static void TouchIdle()
    {
        _idle ??= new Timer(_ => { lock (Gate) { if (_det != null) { Unload(); App.Log("ocr: models unloaded (idle)"); } } });
        _idle.Change(TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);
    }

    static OcrResult Recognize(Bgr img, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lock (Gate)
        {
            EnsureLoaded();
            try
            {
                var boxes = new List<Int32Rect>();
                foreach (var (top, height) in Strips(img.W, img.H))
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var b in Detect(img, top, height))
                    {
                        var g = new Int32Rect(b.X, b.Y + top, b.Width, b.Height);
                        // strips overlap: keep one copy of a line found in both
                        if (!boxes.Any(o => Overlap(o, g) > 0.5)) boxes.Add(g);
                    }
                }
                ct.ThrowIfCancellationRequested();
                var lines = RecognizeBoxes(img, boxes, ct);
                var ordered = Order(lines);
                return new OcrResult(ordered, JoinText(ordered), sw.Elapsed);
            }
            finally { TouchIdle(); }
        }
    }

    /// <summary>Tall pictures (long screenshots) → overlapping horizontal strips, each read at full resolution.</summary>
    static IEnumerable<(int Top, int Height)> Strips(int w, int h)
    {
        int strip = Math.Max(1200, Math.Min(2000, w));
        if (h <= strip * 1.5) { yield return (0, h); yield break; }
        const int overlap = 120;
        for (int top = 0; ; top += strip - overlap)
        {
            int hh = Math.Min(strip, h - top);
            yield return (top, hh);
            if (top + hh >= h) yield break;
        }
    }

    static double Overlap(Int32Rect a, Int32Rect b)
    {
        int x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
        int x1 = Math.Min(a.X + a.Width, b.X + b.Width), y1 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (x1 <= x0 || y1 <= y0) return 0;
        double inter = (double)(x1 - x0) * (y1 - y0);
        return inter / Math.Min((double)a.Width * a.Height, (double)b.Width * b.Height);
    }

    // ───────────── detection (DBNet) ─────────────

    /// <summary>Boxes in the strip's coordinates.</summary>
    static List<Int32Rect> Detect(Bgr img, int top, int height)
    {
        int w = img.W, h = height;
        // scale for detection: big pictures down to 2000 px, then the short side up to 736 (multiples of 32)
        double s0 = Math.Max(w, h) > DetMaxSide ? (double)DetMaxSide / Math.Max(w, h) : 1;
        double vw = w * s0, vh = h * s0;
        // very flat strips (one line of text): pad top and bottom with black like RapidOCR's letterbox
        double pad = 0;
        if (vh <= 30 || vw / vh > 8)
        {
            double newH = Math.Max(vw / 8, 30) * 2;
            pad = Math.Max(0, (newH - vh) / 2);
        }
        double ph = vh + 2 * pad;
        double r = Math.Min(vw, ph) < DetLimitSide ? DetLimitSide / Math.Min(vw, ph) : 1;
        int rw = Math.Max(32, (int)Math.Round(vw * r / 32) * 32), rh = Math.Max(32, (int)Math.Round(ph * r / 32) * 32);
        if (Math.Max(rw, rh) > DetHardCap)
        {
            double k = (double)DetHardCap / Math.Max(rw, rh);
            rw = Math.Max(32, (int)(rw * k) / 32 * 32);
            rh = Math.Max(32, (int)(rh * k) / 32 * 32);
        }

        // input tensor: BGR, (x/255 − 0.5)/0.5, sampled bilinearly from the strip (+ black padding)
        var input = new float[3 * rw * rh];
        int plane = rw * rh;
        double sx = vw / rw / s0, sy = ph / rh; // tensor px → padded-scaled px
        for (int y = 0; y < rh; y++)
        {
            double py = (y + 0.5) * sy - 0.5 - pad; // in scaled strip px
            double oy = (py + 0.5) / s0 - 0.5;      // in original strip px
            bool outside = py < -0.5 || py > vh - 0.5;
            for (int x = 0; x < rw; x++)
            {
                int o = y * rw + x;
                if (outside) { input[o] = input[plane + o] = input[2 * plane + o] = -1f; continue; }
                double ox = (x + 0.5) * sx - 0.5;
                img.Sample(ox, top + oy, top, top + h - 1, out float b, out float g, out float rr);
                input[o] = b / 127.5f - 1f;
                input[plane + o] = g / 127.5f - 1f;
                input[2 * plane + o] = rr / 127.5f - 1f;
            }
        }

        float[] pred;
        using (var t = OrtValue.CreateTensorValueFromMemory(input, new long[] { 1, 3, rh, rw }))
        using (var outs = _det!.Run(RunOpts, new[] { _det.InputMetadata.Keys.First() }, new[] { t }, new[] { _det.OutputMetadata.Keys.First() }))
            pred = outs[0].GetTensorDataAsSpan<float>().ToArray();

        // binarise + 2×2 dilation (cv2.dilate, anchor at the kernel centre → x-1..x, y-1..y)
        var bin = new bool[plane];
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                bool on = pred[y * rw + x] > DetThresh
                          || x > 0 && pred[y * rw + x - 1] > DetThresh
                          || y > 0 && (pred[(y - 1) * rw + x] > DetThresh || x > 0 && pred[(y - 1) * rw + x - 1] > DetThresh);
                bin[y * rw + x] = on;
            }

        var boxes = new List<Int32Rect>();
        var seen = new bool[plane];
        var stack = new Stack<int>();
        int found = 0;
        for (int start = 0; start < plane && found < 1000; start++)
        {
            if (!bin[start] || seen[start]) continue;
            found++;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % rw, py = p / rw;
                if (px < minX) minX = px; if (px > maxX) maxX = px;
                if (py < minY) minY = py; if (py > maxY) maxY = py;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx, ny = py + dy;
                        if (nx < 0 || ny < 0 || nx >= rw || ny >= rh) continue;
                        int q = ny * rw + nx;
                        if (bin[q] && !seen[q]) { seen[q] = true; stack.Push(q); }
                    }
            }
            double bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (Math.Min(bw, bh) < 3) continue;
            double score = 0;
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++) score += pred[y * rw + x];
            score /= bw * bh;
            if (score < BoxThresh) continue;
            double d = bw * bh * UnclipRatio / (2 * (bw + bh)); // unclip distance
            double x0 = minX - d, y0 = minY - d, x1 = maxX + 1 + d, y1 = maxY + 1 + d;
            if (Math.Min(x1 - x0, y1 - y0) < 5) continue;
            // tensor px → original strip px
            double fx = vw / rw / s0, fy = ph / rh;
            int X0 = Math.Clamp((int)Math.Round(x0 * fx), 0, w), X1 = Math.Clamp((int)Math.Round(x1 * fx), 0, w);
            int Y0 = Math.Clamp((int)Math.Round((y0 * fy - pad) / s0), 0, h), Y1 = Math.Clamp((int)Math.Round((y1 * fy - pad) / s0), 0, h);
            if (X1 - X0 <= 3 || Y1 - Y0 <= 3) continue;
            boxes.Add(new Int32Rect(X0, Y0, X1 - X0, Y1 - Y0));
        }
        return boxes;
    }

    // ───────────── recognition (CRNN + CTC) ─────────────

    static List<OcrLine> RecognizeBoxes(Bgr img, List<Int32Rect> boxes, CancellationToken ct)
    {
        var result = new List<OcrLine>();
        // RapidOCR batches by aspect ratio so little padding is wasted
        var order = boxes.Select((b, i) => (b, i, ratio: Ratio(b))).OrderBy(t => t.ratio).ToList();
        for (int start = 0; start < order.Count; start += RecBatch)
        {
            ct.ThrowIfCancellationRequested();
            var batch = order.Skip(start).Take(RecBatch).ToList();
            double maxRatio = Math.Max(320.0 / RecHeight, batch.Max(t => t.ratio));
            int imgW = (int)(RecHeight * maxRatio);
            int n = batch.Count, plane = RecHeight * imgW;
            var input = new float[n * 3 * plane]; // zero = padding
            for (int k = 0; k < n; k++)
            {
                var b = batch[k].b;
                bool rotate = b.Height >= b.Width * 1.5;
                int resizedW = Math.Min(imgW, (int)Math.Ceiling(RecHeight * batch[k].ratio));
                int baseO = k * 3 * plane;
                for (int y = 0; y < RecHeight; y++)
                    for (int x = 0; x < resizedW; x++)
                    {
                        // crop coordinates (rotated 90° counter-clockwise for vertical text, like np.rot90)
                        double u = (x + 0.5) * (rotate ? b.Height : b.Width) / resizedW - 0.5;
                        double v = (y + 0.5) * (rotate ? b.Width : b.Height) / RecHeight - 0.5;
                        double cx = rotate ? b.Width - 1 - v : u, cy = rotate ? u : v;
                        img.Sample(b.X + cx, b.Y + cy, b.Y, b.Y + b.Height - 1, out float bb, out float gg, out float rr, b.X, b.X + b.Width - 1);
                        int o = y * imgW + x;
                        input[baseO + o] = bb / 127.5f - 1f;
                        input[baseO + plane + o] = gg / 127.5f - 1f;
                        input[baseO + 2 * plane + o] = rr / 127.5f - 1f;
                    }
            }
            using var t = OrtValue.CreateTensorValueFromMemory(input, new long[] { n, 3, RecHeight, imgW });
            using var outs = _rec!.Run(RunOpts, new[] { _rec.InputMetadata.Keys.First() }, new[] { t }, new[] { _rec.OutputMetadata.Keys.First() });
            var shape = outs[0].GetTensorTypeAndShape().Shape; // [n, T, C]
            var data = outs[0].GetTensorDataAsSpan<float>();
            int steps = (int)shape[1], classes = (int)shape[2];
            for (int k = 0; k < n; k++)
            {
                var (text, score) = Decode(data.Slice(k * steps * classes, steps * classes), steps, classes);
                text = text.Trim();
                if (text.Length > 0 && score >= TextScore) result.Add(new OcrLine(batch[k].b, text, score));
            }
        }
        return result;
    }

    static double Ratio(Int32Rect b) => b.Height >= b.Width * 1.5 ? (double)b.Height / b.Width : (double)b.Width / b.Height;

    static (string, float) Decode(ReadOnlySpan<float> probs, int steps, int classes)
    {
        var sb = new StringBuilder();
        double sum = 0;
        int count = 0, last = -1;
        for (int t = 0; t < steps; t++)
        {
            int best = 0;
            float bp = probs[t * classes];
            for (int c = 1; c < classes; c++)
            {
                float p = probs[t * classes + c];
                if (p > bp) { bp = p; best = c; }
            }
            if (best != 0 && best != last && best < _chars.Length)
            {
                sb.Append(_chars[best]);
                sum += bp;
                count++;
            }
            last = best;
        }
        return (sb.ToString(), count == 0 ? 0 : (float)(sum / count));
    }

    // ───────────── reading order ─────────────

    /// <summary>Rows top to bottom (boxes whose vertical spans mostly overlap share a row), left to right inside a row.</summary>
    internal static List<OcrLine> Order(List<OcrLine> lines)
    {
        var rows = new List<List<OcrLine>>();
        foreach (var l in lines.OrderBy(l => l.Box.Y + l.Box.Height / 2.0))
        {
            var row = rows.LastOrDefault();
            if (row != null && row.Any(o => SameRow(o.Box, l.Box))) row.Add(l);
            else rows.Add(new List<OcrLine> { l });
        }
        return rows.SelectMany(r => r.OrderBy(l => l.Box.X)).ToList();
    }

    internal static bool SameRow(Int32Rect a, Int32Rect b)
    {
        int y0 = Math.Max(a.Y, b.Y), y1 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return y1 - y0 >= 0.5 * Math.Min(a.Height, b.Height);
    }

    /// <summary>Lines in reading order → text; pieces on the same row are joined with spaces.</summary>
    internal static string JoinText(List<OcrLine> ordered)
    {
        var sb = new StringBuilder();
        OcrLine? prev = null;
        foreach (var l in ordered)
        {
            if (prev != null) sb.Append(SameRow(prev.Box, l.Box) && l.Box.X >= prev.Box.X ? (l.Box.X - (prev.Box.X + prev.Box.Width) > 2 * l.Box.Height ? "    " : " ") : "\n");
            sb.Append(l.Text);
            prev = l;
        }
        return sb.ToString();
    }

    // ───────────── pixels ─────────────

    /// <summary>A picture as BGR bytes (3 per pixel), sampled bilinearly with edge clamping.</summary>
    internal sealed class Bgr
    {
        public readonly int W, H;
        readonly byte[] _p;
        Bgr(int w, int h, byte[] p) { W = w; H = h; _p = p; }

        public static Bgr From(BitmapSource src)
        {
            var conv = src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Bgr32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var raw = new byte[stride * h];
            conv.CopyPixels(raw, stride, 0);
            var p = new byte[w * h * 3];
            bool alpha = conv.Format == PixelFormats.Bgra32;
            for (int i = 0, j = 0; i < raw.Length; i += 4, j += 3)
            {
                if (alpha && raw[i + 3] < 255)
                {
                    // transparent parts (copied PNGs) over white, the way a viewer would show them
                    int a = raw[i + 3];
                    p[j] = (byte)((raw[i] * a + 255 * (255 - a)) / 255);
                    p[j + 1] = (byte)((raw[i + 1] * a + 255 * (255 - a)) / 255);
                    p[j + 2] = (byte)((raw[i + 2] * a + 255 * (255 - a)) / 255);
                }
                else { p[j] = raw[i]; p[j + 1] = raw[i + 1]; p[j + 2] = raw[i + 2]; }
            }
            return new Bgr(w, h, p);
        }

        public void Sample(double x, double y, int yMin, int yMax, out float b, out float g, out float r, int xMin = 0, int xMax = int.MaxValue)
        {
            xMax = Math.Min(xMax, W - 1);
            yMax = Math.Min(yMax, H - 1);
            x = Math.Clamp(x, xMin, xMax);
            y = Math.Clamp(y, yMin, yMax);
            int x0 = (int)x, y0 = (int)y;
            int x1 = Math.Min(x0 + 1, xMax), y1 = Math.Min(y0 + 1, yMax);
            float fx = (float)(x - x0), fy = (float)(y - y0);
            int a = (y0 * W + x0) * 3, bI = (y0 * W + x1) * 3, c = (y1 * W + x0) * 3, d = (y1 * W + x1) * 3;
            float w00 = (1 - fx) * (1 - fy), w01 = fx * (1 - fy), w10 = (1 - fx) * fy, w11 = fx * fy;
            b = _p[a] * w00 + _p[bI] * w01 + _p[c] * w10 + _p[d] * w11;
            g = _p[a + 1] * w00 + _p[bI + 1] * w01 + _p[c + 1] * w10 + _p[d + 1] * w11;
            r = _p[a + 2] * w00 + _p[bI + 2] * w01 + _p[c + 2] * w10 + _p[d + 2] * w11;
        }
    }
}
