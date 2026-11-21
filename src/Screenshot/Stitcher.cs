using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgosClip;

/// <summary>
/// Joins successive captures of a scrolling region into one tall image (long screenshot).
/// For each new frame it finds how far the content moved by matching rows against the previous
/// frame, then appends only the rows that are new. Rows are compared by a hash of their middle
/// part, so a scrollbar at the edge doesn't spoil the match.
/// </summary>
public sealed class Stitcher
{
    readonly int _w, _h, _stride;
    readonly List<byte[]> _rows = new();   // accumulated output, one Bgra32 row per entry
    ulong[]? _prevHashes;
    public int Height => _rows.Count;
    public int Width => _w;
    /// <summary>Consecutive frames that brought nothing new (the bottom was reached).</summary>
    public int StillFrames { get; private set; }

    public Stitcher(int width, int height)
    {
        _w = width;
        _h = height;
        _stride = width * 4;
    }

    static byte[] Pixels(BitmapSource frame, out int stride)
    {
        var bgra = frame.Format == PixelFormats.Bgra32 || frame.Format == PixelFormats.Bgr32 || frame.Format == PixelFormats.Pbgra32
            ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        stride = bgra.PixelWidth * 4;
        var px = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(px, stride, 0);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255; // screen grabs (Bgr32) carry no real alpha
        return px;
    }

    ulong[] RowHashes(byte[] px)
    {
        int from = _w / 10 * 4, to = (_w - _w / 10) * 4; // middle 80%: ignore scrollbars / edges
        var h = new ulong[_h];
        for (int y = 0; y < _h; y++)
        {
            ulong x = 1469598103934665603UL;
            int o = y * _stride;
            for (int i = from; i < to; i += 4)
            {
                x = (x ^ px[o + i]) * 1099511628211UL;
                x = (x ^ px[o + i + 1]) * 1099511628211UL;
                x = (x ^ px[o + i + 2]) * 1099511628211UL;
            }
            h[y] = x;
        }
        return h;
    }

    /// <summary>
    /// Adds a frame. Returns how many new rows were appended (0 = nothing moved).
    /// </summary>
    public int Add(BitmapSource frame)
    {
        if (frame.PixelWidth != _w || frame.PixelHeight != _h) throw new ArgumentException("frame size changed");
        var px = Pixels(frame, out _);
        var hashes = RowHashes(px);
        if (_prevHashes == null)
        {
            for (int y = 0; y < _h; y++) _rows.Add(Row(px, y));
            _prevHashes = hashes;
            return _h;
        }
        int shift = FindShift(_prevHashes, hashes);
        _prevHashes = hashes;
        if (shift <= 0) { StillFrames++; return 0; }
        StillFrames = 0;
        for (int y = _h - shift; y < _h; y++) _rows.Add(Row(px, y));
        return shift;
    }

    byte[] Row(byte[] px, int y)
    {
        var r = new byte[_stride];
        Buffer.BlockCopy(px, y * _stride, r, 0, _stride);
        return r;
    }

    /// <summary>
    /// Content moved up by d rows when cur[i] == prev[i + d]. Picks the smallest d whose overlap matches
    /// at least 90% of the compared rows (so a fixed header/footer doesn't break it); 0 if unmoved,
    /// -1 if no plausible shift was found.
    /// </summary>
    internal static int FindShift(ulong[] prev, ulong[] cur)
    {
        int h = prev.Length;
        if (Same(prev, cur)) return 0;
        int minOverlap = Math.Max(8, h / 8);
        int best = -1;
        double bestScore = 0;
        for (int d = 1; d <= h - minOverlap; d++)
        {
            int n = h - d, match = 0, informative = 0;
            for (int i = 0; i < n; i++)
            {
                // rows identical in both frames at the same place (sticky bars) say nothing about the shift
                if (cur[i] == prev[i]) continue;
                informative++;
                if (cur[i] == prev[i + d]) match++;
            }
            if (informative < minOverlap / 2) continue;
            double score = (double)match / informative;
            if (score >= 0.9 && score > bestScore + 1e-9)
            {
                best = d;
                bestScore = score;
                if (score >= 0.999) break; // perfect: the smallest such shift wins
            }
        }
        return best;
    }

    static bool Same(ulong[] a, ulong[] b)
    {
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    public BitmapSource Result()
    {
        var px = new byte[_stride * _rows.Count];
        for (int y = 0; y < _rows.Count; y++) Buffer.BlockCopy(_rows[y], 0, px, y * _stride, _stride);
        var bmp = BitmapSource.Create(_w, Math.Max(1, _rows.Count), 96, 96, PixelFormats.Bgra32, null, px, _stride);
        bmp.Freeze();
        return bmp;
    }
}
