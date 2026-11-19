using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace AgosClip;

/// <summary>
/// WPF scrolls a fixed amount (~3 lines) per wheel event, whatever the event's Delta. A mouse sends one
/// event of 120 per notch, but a precision touchpad sends dozens of tiny events (Delta 2…20) per swipe,
/// so lists shot straight to the end. This scrolls in proportion to Delta instead.
/// </summary>
public static class SmoothWheel
{
    /// <summary>Pixels per 120 delta (one mouse notch), following the Windows "lines to scroll" setting.</summary>
    static double PixelsPerNotch => Math.Max(1, SystemParameters.WheelScrollLines) * 19;

    public static void Attach(Control host)
    {
        // ScrollToVerticalOffset only takes effect at the next layout pass, and a touchpad can send several
        // events before that — so accumulate onto the pending target instead of the stale VerticalOffset.
        double? pending = null;
        ScrollViewer? hooked = null;
        host.PreviewMouseWheel += (_, e) =>
        {
            if (FindScrollViewer(host) is not { } sv) return;
            if (sv.ScrollableHeight <= 0) return;
            if (!ReferenceEquals(hooked, sv))
            {
                hooked = sv;
                sv.ScrollChanged += (_, _) => pending = null; // layout caught up (or the user dragged the scrollbar)
            }
            var target = Math.Clamp((pending ?? sv.VerticalOffset) - e.Delta * PixelsPerNotch / 120.0, 0, sv.ScrollableHeight);
            pending = target;
            sv.ScrollToVerticalOffset(target);
            e.Handled = true;
        };
    }

    public static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer s) return s;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    /// <summary>
    /// Two-finger sideways swipes arrive as WM_MOUSEHWHEEL, which WPF ignores. Routes them to a
    /// horizontal scroll handler (used for the date strip).
    /// </summary>
    public static void HookHorizontal(Window w, Func<Point, bool> wants, Action<double> scroll)
    {
        void Install()
        {
            if (PresentationSource.FromVisual(w) is not HwndSource src) return;
            src.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                const int WM_MOUSEHWHEEL = 0x020E;
                if (msg != WM_MOUSEHWHEEL) return IntPtr.Zero;
                int delta = (short)((long)wp >> 16 & 0xFFFF);
                var p = w.PointFromScreen(new Point((short)((long)lp & 0xFFFF), (short)((long)lp >> 16 & 0xFFFF)));
                if (!wants(p)) return IntPtr.Zero;
                scroll(delta);
                handled = true;
                return new IntPtr(1);
            });
        }
        if (PresentationSource.FromVisual(w) != null) Install();
        else w.SourceInitialized += (_, _) => Install();
    }
}
