using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AgosClip;

/// <summary>Listens for clipboard changes (no polling) and records text and images into the store.</summary>
public sealed class ClipboardMonitor : IDisposable
{
    const int MaxTextChars = 2_000_000;
    const int MaxImagePixels = 60_000_000;

    readonly Store _store;
    readonly Func<Settings> _settings;
    readonly HwndSource _wnd;
    readonly DispatcherTimer _debounce;
    readonly int _myPid = Environment.ProcessId;

    public ClipboardMonitor(Store store, Func<Settings> settings)
    {
        _store = store;
        _settings = settings;
        // message-only window (HWND_MESSAGE = -3)
        _wnd = new HwndSource(new HwndSourceParameters("AgosClipMonitor") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _wnd.AddHook(WndProc);
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Capture(); };
        if (!Native.AddClipboardFormatListener(_wnd.Handle))
            throw new InvalidOperationException("AddClipboardFormatListener failed: " + Marshal.GetLastWin32Error());
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            _debounce.Stop();
            _debounce.Start();
            handled = true;
        }
        return IntPtr.Zero;
    }

    static IDataObject? GetData()
    {
        for (int i = 0; i < 8; i++)
        {
            try { return Clipboard.GetDataObject(); }
            catch (COMException) { Thread.Sleep(25); }
            catch (ExternalException) { Thread.Sleep(25); }
        }
        return null;
    }

    static bool Has(IDataObject d, string fmt)
    {
        try { return d.GetDataPresent(fmt, false); } catch { return false; }
    }

    static object? Get(IDataObject d, string fmt)
    {
        try { return d.GetData(fmt, false); } catch { return null; }
    }

    void Capture()
    {
        try { CaptureCore(); }
        catch (Exception ex) { App.Log("capture failed: " + ex); }
    }

    void CaptureCore() => CaptureCore(retry: true);

    /// <summary>
    /// One diagnostic line per clipboard event: format names, owner app and what was done. Never content.
    /// </summary>
    static void Diag(string? source, string[] formats, string what) =>
        App.Log($"clip [{source ?? "?"}] {{{string.Join(", ", formats)}}} → {what}");

    void CaptureCore(bool retry)
    {
        if (App.Paused) return;
        var data = GetData();
        string? source = SourceApp(out bool fromUs);
        string who = fromUs ? "self" : source ?? "?";
        if (data == null) { App.Log($"clip [{source ?? "?"}] → clipboard locked, skipped"); return; }
        string[] formats;
        try { formats = data.GetFormats(false); } catch { formats = Array.Empty<string>(); }
        if (formats.Length == 0) return; // clipboard was emptied (apps do this right before writing)

        // Respect apps (password managers etc.) that ask clipboard history to skip their content.
        foreach (var flag in new[] { "ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore" })
            if (Has(data, flag)) { Diag(who, formats, "skipped: " + flag); return; }
        if (Get(data, "CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
        {
            var b = new byte[4];
            ms.Read(b, 0, 4);
            if (BitConverter.ToInt32(b, 0) == 0) { Diag(who, formats, "skipped: CanIncludeInClipboardHistory=0"); return; }
        }

        bool hasImage = formats.Any(f => f is "PNG" or "image/png" or "DeviceIndependentBitmap" or "Format17" or "Bitmap" or "System.Drawing.Bitmap");
        var text = Has(data, DataFormats.UnicodeText) ? Get(data, DataFormats.UnicodeText) as string : null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (text.Length > MaxTextChars) { Diag(who, formats, $"skipped: text too long ({text.Length})"); return; }
            _store.Upsert(ClipKind.Text, Store.TextHash(text), text, null, null, 0, 0, fromUs ? null : source);
            Diag(who, formats, $"text ({text.Length} chars)");
            return;
        }
        // Empty/whitespace text next to a picture (some screenshot tools do this) → record the picture.

        if (!hasImage) { Diag(who, formats, "nothing to record (no text or image)"); return; }
        if (!_settings().RecordImages) { Diag(who, formats, "skipped: 记录图片 is off"); return; }
        var img = ReadImage(data, out var why);
        if (img == null)
        {
            if (retry)
            {
                // delayed rendering: the owner may still be producing the bitmap; try once more shortly
                Diag(who, formats, "image not readable yet (" + why + "), retrying");
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                t.Tick += (_, _) => { t.Stop(); try { CaptureCore(retry: false); } catch (Exception ex) { App.Log("capture retry: " + ex); } };
                t.Start();
            }
            else Diag(who, formats, "image FAILED: " + why);
            return;
        }
        if ((long)img.PixelWidth * img.PixelHeight > MaxImagePixels) { Diag(who, formats, $"skipped: image too large {img.PixelWidth}x{img.PixelHeight}"); return; }
        img.Freeze();
        var src = fromUs ? null : source;
        var disp = Dispatcher.CurrentDispatcher;
        Diag(who, formats, $"image {img.PixelWidth}x{img.PixelHeight}");
        Task.Run(() =>
        {
            try
            {
                var (hash, rel, thumb) = SaveImage(img);
                disp.BeginInvoke(() =>
                {
                    try { _store.Upsert(ClipKind.Image, hash, null, rel, thumb, img.PixelWidth, img.PixelHeight, src); }
                    catch (Exception ex) { App.Log("store image failed: " + ex); }
                });
            }
            catch (Exception ex) { App.Log("save image failed: " + ex); }
        });
    }

    internal (string hash, string? rel, string? thumb) SaveImage(BitmapSource img)
    {
        var bgra = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var px = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(px, stride, 0);
        var head = BitConverter.GetBytes(((long)bgra.PixelWidth << 32) | (uint)bgra.PixelHeight);
        using var sha = SHA256.Create();
        sha.TransformBlock(head, 0, head.Length, null, 0);
        sha.TransformFinalBlock(px, 0, px.Length);
        var hash = "I" + Convert.ToHexString(sha.Hash!).ToLowerInvariant();

        if (_store.Exists(hash)) return (hash, null, null); // upsert keeps the stored paths

        var day = DateTime.Now.ToString("yyyy-MM-dd");
        var dir = Path.Combine(Store.ImagesDir, day);
        Directory.CreateDirectory(dir);
        var name = hash.Substring(1, 20);
        var rel = Path.Combine("images", day, name + ".png");
        WritePng(bgra, Path.Combine(Settings.DataDir, rel));

        string? thumbRel = null;
        double scale = Math.Min(1.0, Math.Min(680.0 / bgra.PixelWidth, 360.0 / bgra.PixelHeight));
        if (scale < 0.999)
        {
            var t = new TransformedBitmap(bgra, new ScaleTransform(scale, scale));
            thumbRel = Path.Combine("images", day, name + "_t.png");
            WritePng(t, Path.Combine(Settings.DataDir, thumbRel));
        }
        return (hash, rel, thumbRel ?? rel);
    }

    static void WritePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) enc.Save(fs);
        File.Move(tmp, path, true);
    }

    static Stream? AsStream(object? o) => o switch
    {
        MemoryStream m => m,
        byte[] bytes => new MemoryStream(bytes),
        Stream st => st,
        _ => null,
    };

    /// <summary>Tries every common clipboard picture format; <paramref name="why"/> says what failed.</summary>
    static BitmapSource? ReadImage(IDataObject data, out string why)
    {
        var errors = new System.Collections.Generic.List<string>();
        foreach (var fmt in new[] { "PNG", "image/png" })
        {
            try
            {
                if (Has(data, fmt) && AsStream(Get(data, fmt)) is { } png)
                {
                    png.Position = 0;
                    var dec = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    why = "";
                    return dec.Frames[0];
                }
            }
            catch (Exception ex) { errors.Add($"{fmt}: {ex.GetType().Name} {ex.Message}"); }
        }
        foreach (var fmt in new[] { DataFormats.Dib, "Format17" /* CF_DIBV5 */ })
        {
            try
            {
                if (Has(data, fmt) && AsStream(Get(data, fmt)) is MemoryStream dib)
                {
                    var bmp = DecodeDib(dib.ToArray());
                    if (bmp != null) { why = ""; return bmp; }
                    errors.Add(fmt + ": too short");
                }
            }
            catch (Exception ex) { errors.Add($"{fmt}: {ex.GetType().Name} {ex.Message}"); }
        }
        try
        {
            if (Has(data, DataFormats.Bitmap) && Get(data, DataFormats.Bitmap) is BitmapSource bs) { why = ""; return bs; }
        }
        catch (Exception ex) { errors.Add($"Bitmap: {ex.GetType().Name} {ex.Message}"); }
        try
        {
            // GDI+ path: understands DIB variants the WPF BMP decoder rejects
            if (System.Windows.Forms.Clipboard.GetImage() is System.Drawing.Bitmap gdi)
            {
                using (gdi)
                {
                    var h = gdi.GetHbitmap();
                    try
                    {
                        why = "";
                        return Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    }
                    finally { DeleteObject(h); }
                }
            }
            errors.Add("GDI+: no image");
        }
        catch (Exception ex) { errors.Add($"GDI+: {ex.GetType().Name} {ex.Message}"); }
        why = string.Join("; ", errors);
        return null;
    }

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    /// <summary>Turns a CF_DIB blob into a bitmap by prepending a BITMAPFILEHEADER.</summary>
    static BitmapSource? DecodeDib(byte[] dib)
    {
        if (dib.Length < 40) return null;
        int biSize = BitConverter.ToInt32(dib, 0);
        int bitCount = BitConverter.ToInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int clrUsed = BitConverter.ToInt32(dib, 32);
        int colors = clrUsed != 0 ? clrUsed : (bitCount <= 8 ? 1 << bitCount : 0);
        int masks = biSize == 40 && compression == 3 ? 12 : biSize == 40 && compression == 6 ? 16 : 0;
        int offset = 14 + biSize + masks + colors * 4;

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B'; file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(offset).CopyTo(file, 10);
        dib.CopyTo(file, 14);
        var dec = new BmpBitmapDecoder(new MemoryStream(file), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return dec.Frames[0];
    }

    string? SourceApp(out bool fromUs)
    {
        fromUs = false;
        try
        {
            var owner = Native.GetClipboardOwner();
            if (owner == IntPtr.Zero) owner = Native.GetForegroundWindow();
            if (owner == IntPtr.Zero) return null;
            Native.GetWindowThreadProcessId(owner, out uint pid);
            if (pid == 0) return null;
            if (pid == _myPid) { fromUs = true; return null; }
            using var p = Process.GetProcessById((int)pid);
            try
            {
                var desc = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
            }
            catch { /* access denied for elevated processes */ }
            return p.ProcessName;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        Native.RemoveClipboardFormatListener(_wnd.Handle);
        _wnd.Dispose();
    }
}
