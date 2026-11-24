using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace AgosClip;

/// <summary>Something that fills a top-down 32-bit frame buffer (the screen, or a fake one in self-tests).</summary>
public interface IFrameSource : IDisposable
{
    bool Grab(byte[] dest);
}

/// <summary>
/// Copies one screen rectangle per call into a top-down 32-bit buffer with GDI (BitBlt into a DIB section),
/// optionally drawing the mouse pointer on top. Create, use and dispose it on the same (capture) thread.
/// </summary>
public sealed class FrameGrabber : IFrameSource
{
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public Native.POINT pt; }
    [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, uint step, IntPtr brush, uint flags);

    const uint SRCCOPY = 0x00CC0020, DI_NORMAL = 3;
    const int CURSOR_SHOWING = 1;

    readonly Int32Rect _r;
    readonly bool _cursor;
    readonly IntPtr _screen, _mem, _bmp, _old, _bits;
    readonly Dictionary<IntPtr, (int X, int Y)> _hotspots = new();

    public FrameGrabber(Int32Rect region, bool drawCursor)
    {
        _r = region;
        _cursor = drawCursor;
        _screen = GetDC(IntPtr.Zero);
        _mem = CreateCompatibleDC(_screen);
        var bi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = region.Width, biHeight = -region.Height, // negative = top-down
            biPlanes = 1, biBitCount = 32,
        };
        _bmp = CreateDIBSection(_mem, ref bi, 0, out _bits, IntPtr.Zero, 0);
        if (_bmp == IntPtr.Zero) { Dispose(); throw new InvalidOperationException("CreateDIBSection failed"); }
        _old = SelectObject(_mem, _bmp);
    }

    /// <summary>Fills <paramref name="dest"/> (width*height*4 bytes) with the current screen content.</summary>
    public bool Grab(byte[] dest)
    {
        // With DWM the screen DC is the composed desktop, layered windows included. No CAPTUREBLT:
        // it makes the mouse pointer flicker on some machines.
        if (!BitBlt(_mem, 0, 0, _r.Width, _r.Height, _screen, _r.X, _r.Y, SRCCOPY)) return false;
        if (_cursor) DrawCursor();
        GdiFlush();
        Marshal.Copy(_bits, dest, 0, _r.Width * _r.Height * 4);
        return true;
    }

    void DrawCursor()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
        if (!_hotspots.TryGetValue(ci.hCursor, out var hs))
        {
            if (GetIconInfo(ci.hCursor, out var ii))
            {
                hs = (ii.xHotspot, ii.yHotspot);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
            _hotspots[ci.hCursor] = hs;
        }
        int x = ci.pt.X - hs.X - _r.X, y = ci.pt.Y - hs.Y - _r.Y;
        if (x > _r.Width || y > _r.Height || x < -64 || y < -64) return; // pointer is elsewhere
        DrawIconEx(_mem, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
    }

    public void Dispose()
    {
        if (_old != IntPtr.Zero) SelectObject(_mem, _old);
        if (_bmp != IntPtr.Zero) DeleteObject(_bmp);
        if (_mem != IntPtr.Zero) DeleteDC(_mem);
        if (_screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screen);
    }
}
