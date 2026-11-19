using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;

namespace AgosClip;

internal readonly record struct PanelAnchor(Native.POINT Cursor, Native.RECT? Field = null, Native.RECT? Caret = null)
{
    public static PanelAnchor AtCursor()
    {
        Native.GetCursorPos(out var cursor);
        return new(cursor);
    }
}

internal static class PanelPlacement
{
    const uint NearestMonitor = 2;
    // UI Automation calls cross process boundaries and may outlive the time budget (80 ms, run in
    // parallel with the list rebuild; past it the panel opens by the caret/mouse instead of waiting). Allow only
    // one outstanding query, so an unresponsive accessibility provider cannot pile up workers.
    static int _queryInFlight;

    public static async Task<PanelAnchor> CaptureAsync(IntPtr target)
    {
        var anchor = PanelAnchor.AtCursor();
        if (target == IntPtr.Zero || Native.GetForegroundWindow() != target) return anchor;
        uint thread = Native.GetWindowThreadProcessId(target, out _);
        var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (thread == 0 || !Native.GetGUIThreadInfo(thread, ref info) || info.hwndFocus == IntPtr.Zero) return anchor;

        var focus = info.hwndFocus;
        var caret = ReadCaret(info);
        var className = new StringBuilder(256);
        Native.GetClassName(focus, className, className.Capacity);
        string name = className.ToString();
        if (name.Equals("Edit", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase))
        {
            const long readOnly = 0x0800; // ES_READONLY
            if ((Native.GetWindowLongPtr(focus, -16).ToInt64() & readOnly) != 0 || !Native.IsWindowEnabled(focus))
                return anchor;
            if (Native.GetWindowRect(focus, out var field) && HasArea(field))
                return anchor with { Field = field, Caret = caret };
        }

        if (Native.IsHungAppWindow(target) || Interlocked.CompareExchange(ref _queryInFlight, 1, 0) != 0)
            return anchor with { Caret = caret };
        var query = Task.Run(() =>
        {
            try { return ReadAutomationField(target); }
            catch (Exception) { return (null, false); } // provider disappeared, declined access or failed
            finally { Volatile.Write(ref _queryInFlight, 0); }
        });
        try
        {
            var (field, nonEditable) = await query.WaitAsync(TimeSpan.FromMilliseconds(80));
            return anchor with { Field = field, Caret = nonEditable ? null : caret };
        }
        catch (TimeoutException) { return anchor with { Caret = caret }; }
    }

    static Native.RECT? ReadCaret(Native.GUITHREADINFO info)
    {
        // A caret from another HWND in this thread may belong to a previously focused control.
        if (info.hwndCaret != info.hwndFocus || info.rcCaret.Bottom <= info.rcCaret.Top) return null;
        var top = new Native.POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
        var bottom = new Native.POINT { X = info.rcCaret.Right, Y = info.rcCaret.Bottom };
        // GUITHREADINFO reports the caret in the owning window's logical client coordinates.
        var context = Native.SetThreadDpiAwarenessContext(Native.GetWindowDpiAwarenessContext(info.hwndCaret));
        try
        {
            if (!Native.ClientToScreen(info.hwndCaret, ref top) || !Native.ClientToScreen(info.hwndCaret, ref bottom)) return null;
        }
        finally { if (context != IntPtr.Zero) Native.SetThreadDpiAwarenessContext(context); }
        if (!Native.LogicalToPhysicalPointForPerMonitorDPI(info.hwndCaret, ref top) ||
            !Native.LogicalToPhysicalPointForPerMonitorDPI(info.hwndCaret, ref bottom)) return null;
        return new Native.RECT { Left = top.X, Top = top.Y, Right = Math.Max(top.X + 1, bottom.X), Bottom = bottom.Y };
    }

    static (Native.RECT? Field, bool NonEditable) ReadAutomationField(IntPtr target)
    {
        if (Native.GetForegroundWindow() != target) return (null, true);
        var element = AutomationElement.FocusedElement;
        if (element == null) return (null, false);

        // Only inspect focus, geometry and editability. Never read the field's text or value.
        var info = element.Current;
        if (!info.HasKeyboardFocus || !info.IsEnabled || info.IsOffscreen) return (null, true);
        var type = info.ControlType;
        bool editable = false;
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
        {
            if (((ValuePattern)value).Current.IsReadOnly) return (null, true);
            editable = type == ControlType.Edit || type == ControlType.Document || type == ControlType.ComboBox;
        }
        if (!editable && element.TryGetCurrentPattern(TextPattern.Pattern, out var text))
        {
            var attribute = ((TextPattern)text).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
            if (attribute is true) return (null, true);
            editable = attribute is false;
        }
        if (!editable && type != ControlType.Edit) return (null, false);

        Rect bounds = info.BoundingRectangle;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 || !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom))
            return (null, false);
        var field = new Native.RECT
        {
            Left = (int)Math.Floor(bounds.Left), Top = (int)Math.Floor(bounds.Top),
            Right = (int)Math.Ceiling(bounds.Right), Bottom = (int)Math.Ceiling(bounds.Bottom)
        };
        // UIA can briefly report stale focus, or expose a focused browser document on another window.
        if (Native.GetForegroundWindow() != target || !Native.GetWindowRect(target, out var window) || !Intersects(field, window))
            return (null, true);
        return (field, false);
    }

    public static (int X, int Y, int Width, int Height, bool ExpandDown) Calculate(PanelAnchor anchor, double widthDip, double heightDip)
    {
        Native.RECT? bounds = anchor.Field ?? anchor.Caret;
        var monitor = bounds is { } rect ? Native.MonitorFromRect(ref rect, NearestMonitor)
            : Native.MonitorFromPoint(anchor.Cursor, NearestMonitor);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref info);
        double scale = Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;
        return Calculate(anchor, info.rcWork, (int)Math.Round(widthDip * scale), (int)Math.Round(heightDip * scale), (int)Math.Round(6 * scale));
    }

    internal static (int X, int Y, int Width, int Height, bool ExpandDown) Calculate(
        PanelAnchor anchor, Native.RECT work, int width, int height, int gap)
    {
        int ClampX(int x) => Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        int ClampY(int y) => Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));

        if (anchor.Field is { } field)
        {
            // Keep the editing surface clear whenever the panel fits on one of its edges.
            int below = field.Bottom + gap;
            int above = field.Top - gap - height;
            if (below + height <= work.Bottom) return (ClampX(field.Left), ClampY(below), width, height, true);
            if (above >= work.Top) return (ClampX(field.Left), ClampY(above), width, height, false);
            if (field.Right + gap + width <= work.Right) return (field.Right + gap, ClampY(field.Top), width, height, true);
            if (field.Left - gap - width >= work.Left) return (field.Left - gap - width, ClampY(field.Top), width, height, true);
        }

        // A large document may leave no space around its field. Anchor to the caret when exposed.
        var point = anchor.Caret is { } caret && Intersects(caret, work) ? caret
            : anchor.Field is { } oversized ? new Native.RECT { Left = oversized.Left, Top = oversized.Top, Bottom = oversized.Top }
            : new Native.RECT { Left = anchor.Cursor.X, Top = anchor.Cursor.Y, Bottom = anchor.Cursor.Y };
        int bottom = point.Bottom + gap;
        bool expandDown = bottom + height <= work.Bottom || work.Bottom - bottom >= point.Top - gap - work.Top;
        int y = expandDown ? bottom : point.Top - gap - height;
        return (ClampX(point.Left), ClampY(y), width, height, expandDown);
    }

    static bool HasArea(Native.RECT r) => r.Right > r.Left && r.Bottom > r.Top;
    static bool Intersects(Native.RECT a, Native.RECT b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}
