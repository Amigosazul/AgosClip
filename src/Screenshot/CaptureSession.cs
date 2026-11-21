using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AgosClip;

public enum CaptureAction { Copy, PinOnTop, PinNormal }

/// <summary>One screenshot: freeze the screen, show a selection overlay per monitor, then copy or pin.</summary>
public sealed class CaptureSession
{
    static CaptureSession? _active;
    readonly List<CaptureOverlay> _overlays = new();
    BitmapSource _full = null!;
    Int32Rect _bounds;

    public static bool IsActive => _active != null;

    /// <summary>Raised after a copy/pin with the cropped image (self-test hook).</summary>
    internal static event Action<CaptureAction, BitmapSource, Int32Rect>? Completed;

    /// <summary>Starts a capture (ignored while one is already open).</summary>
    public static void Start()
    {
        if (_active != null) return;
        var s = new CaptureSession();
        _active = s;
        try { s.Open(); }
        catch (Exception ex)
        {
            App.Log("screenshot failed: " + ex);
            s.CloseAll();
        }
    }

    /// <summary>Self-test: run the whole flow on a given picture instead of a real screen grab.</summary>
    internal static CaptureSession StartWith(BitmapSource full, Int32Rect bounds)
    {
        var s = new CaptureSession();
        _active = s;
        s.Show(full, bounds);
        return s;
    }

    void Open() => Show(ScreenCapture.CaptureAll().Image, ScreenCapture.VirtualBounds()); // grab before any overlay exists

    void Show(BitmapSource full, Int32Rect bounds)
    {
        (_full, _bounds) = (full, bounds);
        App.Log($"screenshot: {_bounds.Width}x{_bounds.Height} over {System.Windows.Forms.Screen.AllScreens.Length} monitor(s)");
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var b = screen.Bounds;
            var mon = new Int32Rect(b.X, b.Y, b.Width, b.Height);
            var part = ScreenCapture.Crop(_full, new Int32Rect(b.X - _bounds.X, b.Y - _bounds.Y, b.Width, b.Height));
            var o = new CaptureOverlay(this, part, mon);
            _overlays.Add(o);
            o.Show();
        }
        // focus the monitor under the mouse so Esc/Enter work at once
        Native.GetCursorPos(out var pt);
        var target = _overlays.FirstOrDefault(o =>
        {
            var r = o.MonitorRect;
            return pt.X >= r.X && pt.X < r.X + r.Width && pt.Y >= r.Y && pt.Y < r.Y + r.Height;
        }) ?? _overlays.FirstOrDefault();
        if (target != null)
        {
            target.Activate();
            Native.ForceForeground(new System.Windows.Interop.WindowInteropHelper(target).Handle);
        }
    }

    internal IReadOnlyList<CaptureOverlay> Overlays => _overlays;
    internal static CaptureSession? Active => _active;

    internal void SelectionStarted(CaptureOverlay owner)
    {
        foreach (var o in _overlays) if (o != owner) o.ClearSelection();
    }

    internal void Finish(CaptureOverlay owner, CaptureAction action)
    {
        var where = owner.ScreenSelection();
        var crop = ScreenCapture.Crop(_full, new Int32Rect(where.X - _bounds.X, where.Y - _bounds.Y, where.Width, where.Height));
        CloseAll();
        switch (action)
        {
            case CaptureAction.Copy:
                if (!ScreenCapture.CopyToClipboard(crop)) App.Log("screenshot: copy failed");
                break;
            default:
                PinWindow.Open(crop, where, onTop: action == CaptureAction.PinOnTop);
                break;
        }
        App.Log($"screenshot: {action} {where.Width}x{where.Height}");
        Completed?.Invoke(action, crop, where);
    }

    public void Cancel() => CloseAll();

    void CloseAll()
    {
        foreach (var o in _overlays) { try { o.Close(); } catch { } }
        _overlays.Clear();
        _active = null;
    }
}
