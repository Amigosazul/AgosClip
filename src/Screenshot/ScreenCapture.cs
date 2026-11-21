using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AgosClip;

/// <summary>Grabs the whole virtual desktop (all monitors) at physical resolution.</summary>
public static class ScreenCapture
{
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    /// <summary>Physical-pixel rectangle of the virtual screen (may start at negative coordinates).</summary>
    public static Int32Rect VirtualBounds()
    {
        var all = System.Windows.Forms.Screen.AllScreens.Select(s => s.Bounds).ToArray();
        int l = all.Min(b => b.Left), t = all.Min(b => b.Top), r = all.Max(b => b.Right), btm = all.Max(b => b.Bottom);
        return new Int32Rect(l, t, r - l, btm - t);
    }

    public static (BitmapSource Image, Int32Rect Bounds) CaptureAll()
    {
        var vb = VirtualBounds();
        using var bmp = new System.Drawing.Bitmap(vb.Width, vb.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(vb.X, vb.Y, 0, 0, new System.Drawing.Size(vb.Width, vb.Height), System.Drawing.CopyPixelOperation.SourceCopy);
        var h = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return (src, vb);
        }
        finally { DeleteObject(h); }
    }

    /// <summary>Grabs one screen rectangle (physical pixels).</summary>
    public static BitmapSource CaptureRect(Int32Rect r)
    {
        using var bmp = new System.Drawing.Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(r.X, r.Y, 0, 0, new System.Drawing.Size(r.Width, r.Height), System.Drawing.CopyPixelOperation.SourceCopy);
        var h = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DeleteObject(h); }
    }

    public static BitmapSource Crop(BitmapSource full, Int32Rect r)
    {
        var c = new CroppedBitmap(full, r);
        c.Freeze();
        return c;
    }

    public static byte[] Png(BitmapSource img)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Puts a picture on the clipboard (CF_DIB + PNG, fully rendered) — it then shows up in the history too.</summary>
    public static bool CopyToClipboard(BitmapSource img) =>
        ClipboardWriter.Write(new ClipboardWriter.Payload().Dib(img).Add("PNG", Png(img)));
}
