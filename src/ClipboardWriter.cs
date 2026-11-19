using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgosClip;

/// <summary>
/// Writes the clipboard with plain Win32 calls, every format fully rendered up front.
///
/// WPF's Clipboard.SetDataObject goes through OLE, which publishes formats as *delayed rendering*:
/// anyone who reads the clipboard right after (our own reader thread, remote-desktop and sync tools…)
/// makes Windows call back into our UI thread for the data while that thread is still inside
/// OleFlushClipboard. The two wait on each other until Windows gives up after ~10 s — the
/// "paste: put text on clipboard" hang. With real HGLOBALs there is no call-back, so a reader can
/// never block us.
/// </summary>
public static class ClipboardWriter
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalFree(IntPtr mem);

    public const uint CF_UNICODETEXT = 13, CF_DIB = 8, CF_HDROP = 15;
    const uint GMEM_MOVEABLE = 0x0002;

    static HwndSource? _owner;

    /// <summary>A message-only window owns what we put on the clipboard (lets the reader recognise "self").</summary>
    static IntPtr Owner
    {
        get
        {
            _owner ??= new HwndSource(new HwndSourceParameters("AgosClipWriter") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            return _owner.Handle;
        }
    }

    public static uint Format(string name) => name switch
    {
        "UnicodeText" => CF_UNICODETEXT,
        "DeviceIndependentBitmap" => CF_DIB,
        "FileDrop" => CF_HDROP,
        _ => RegisterClipboardFormat(name),
    };

    /// <summary>Formats to publish together: (clipboard format id, complete bytes).</summary>
    public sealed class Payload
    {
        internal readonly List<(uint Format, byte[] Data)> Items = new();
        public Payload Add(uint format, byte[] data) { Items.Add((format, data)); return this; }
        public Payload Add(string format, byte[] data) => Add(Format(format), data);

        /// <summary>UTF-16 text with terminator (CF_UNICODETEXT).</summary>
        public Payload Text(string text) => Add(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));

        /// <summary>Byte-oriented text formats (HTML Format is UTF-8 by definition; RTF/CSV are 8-bit) with a NUL.</summary>
        public Payload Bytes8(string format, byte[] data)
        {
            var b = new byte[data.Length + 1];
            data.CopyTo(b, 0);
            return Add(format, b);
        }

        /// <summary>CF_HDROP: DROPFILES header + double-NUL-terminated UTF-16 path list.</summary>
        public Payload Files(IReadOnlyList<string> paths)
        {
            var list = Encoding.Unicode.GetBytes(string.Join("\0", paths) + "\0\0");
            var b = new byte[20 + list.Length];
            BitConverter.GetBytes(20).CopyTo(b, 0);  // pFiles: offset of the list
            BitConverter.GetBytes(1).CopyTo(b, 16);  // fWide = TRUE
            list.CopyTo(b, 20);
            return Add(CF_HDROP, b);
        }

        /// <summary>CF_DIB (32-bit BI_RGB, bottom-up) from any bitmap.</summary>
        public Payload Dib(BitmapSource image)
        {
            var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            bgra.CopyPixels(px, stride, 0);
            var b = new byte[40 + px.Length];
            BitConverter.GetBytes(40).CopyTo(b, 0);              // biSize
            BitConverter.GetBytes(w).CopyTo(b, 4);               // biWidth
            BitConverter.GetBytes(h).CopyTo(b, 8);               // biHeight > 0 → bottom-up
            BitConverter.GetBytes((short)1).CopyTo(b, 12);       // biPlanes
            BitConverter.GetBytes((short)32).CopyTo(b, 14);      // biBitCount
            BitConverter.GetBytes(px.Length).CopyTo(b, 20);      // biSizeImage (biCompression 0 = BI_RGB)
            for (int y = 0; y < h; y++) Buffer.BlockCopy(px, (h - 1 - y) * stride, b, 40 + y * stride, stride);
            return Add(CF_DIB, b);
        }
    }

    /// <summary>
    /// Replaces the clipboard with <paramref name="payload"/>. Only waits for the clipboard itself
    /// (someone else holding it open) — briefly, with retries — never on another program's response.
    /// </summary>
    public static bool Write(Payload payload)
    {
        var owner = Owner;
        bool open = false;
        for (int i = 0; i < 25 && !(open = OpenClipboard(owner)); i++) Thread.Sleep(15);
        if (!open) { App.Log("clipboard write: could not open the clipboard (held by another program)"); return false; }
        try
        {
            if (!EmptyClipboard()) return false;
            bool any = false;
            foreach (var (format, data) in payload.Items)
            {
                if (format == 0) continue;
                var mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)Math.Max(1, data.Length));
                if (mem == IntPtr.Zero) continue;
                var p = GlobalLock(mem);
                if (p == IntPtr.Zero) { GlobalFree(mem); continue; }
                Marshal.Copy(data, 0, p, data.Length);
                GlobalUnlock(mem);
                if (SetClipboardData(format, mem) == IntPtr.Zero) GlobalFree(mem); // on success the system owns it
                else any = true;
            }
            return any;
        }
        finally { CloseClipboard(); }
    }
}
