using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AgosClip;

/// <summary>
/// Long (scrolling) screenshot of a screen region. Each tick grabs the region and stitches the new rows on
/// (see <see cref="Stitcher"/>), then scrolls the content one notch:
/// <list type="number">
/// <item>the app under the region is brought to the front and gets real wheel input (SendInput);</item>
/// <item>if that doesn't move anything, WM_MOUSEWHEEL is posted straight to the window under the region;</item>
/// <item>then the ↓ key (small steps — also used when a wheel notch jumps further than the region is tall);</item>
/// <item>if that doesn't either, it switches to manual mode — the user scrolls with their own wheel and
///   every movement is stitched — and never stops by itself (完成 / 取消).</item>
/// </list>
/// "The end" (auto stop) only counts once the content has actually moved and then stays still.
/// A red frame (click-through, just outside the region so it is never captured) and a status bar that
/// doesn't take focus show what is happening.
/// </summary>
public sealed class LongShot
{
    public enum ScrollMethod { Wheel, PostedWheel, Keys, Manual, KeysUp /* step back after overshooting */ }

    const int MaxHeight = 30000;
    static LongShot? _active;
    public static bool IsActive => _active != null;
    internal static LongShot? Active => _active;

    readonly Int32Rect _region;
    readonly Stitcher _stitch;
    readonly Func<Int32Rect, BitmapSource> _grab;
    readonly Func<ScrollMethod, bool> _scroll;
    readonly Window _frame, _hud;
    readonly TextBlock _status;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(320) };
    readonly IntPtr _target;          // deepest window under the region centre (scroll target)
    bool _canPost;                    // there is a foreign window to post WM_MOUSEWHEEL to
    Native.POINT _cursorBefore;
    int _frames, _idle;               // frames so far; ticks without movement before anything ever moved
    int _lostStreak;                  // consecutive frames that moved too far to overlap
    bool _recovering;                 // overshot: stepping back up until the frame overlaps the last match again
    bool _done;

    public ScrollMethod Method { get; private set; } = ScrollMethod.Wheel;
    internal Stitcher Stitch => _stitch;
    internal string Status => _status.Text;

    /// <summary>Self-test hook: the result instead of the preview window.</summary>
    internal static event Action<BitmapSource>? Finished;

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Native.POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    public static void Start(Int32Rect region)
    {
        if (_active != null || region.Width < 20 || region.Height < 40) return;
        _active = new LongShot(region, ScreenCapture.CaptureRect, null, TimeSpan.FromMilliseconds(320));
    }

    /// <summary>Self-test: run on a fake screen and a fake scroller.</summary>
    internal static LongShot StartForTest(Int32Rect region, Func<Int32Rect, BitmapSource> grab, Func<ScrollMethod, bool> scroll, TimeSpan tick)
    {
        _active = new LongShot(region, grab, scroll, tick) { _canPost = true };
        return _active;
    }

    LongShot(Int32Rect region, Func<Int32Rect, BitmapSource> grab, Func<ScrollMethod, bool>? scroll, TimeSpan tick)
    {
        _region = region;
        _grab = grab;
        _scroll = scroll ?? RealScroll;
        _timer.Interval = tick;
        _stitch = new Stitcher(region.Width, region.Height);

        // what is under the region (looked up before our own windows exist)
        var centre = new Native.POINT { X = region.X + region.Width / 2, Y = region.Y + region.Height / 2 };
        _target = WindowFromPoint(centre);
        var root = _target == IntPtr.Zero ? IntPtr.Zero : GetAncestor(_target, 2 /* GA_ROOT */);
        if (root != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(root, out uint pid);
            if (pid != (uint)Environment.ProcessId) Native.ForceForeground(root); // wheel input then surely reaches it
            else _target = IntPtr.Zero;
        }

        _canPost = _target != IntPtr.Zero;

        // red frame 3 px outside the region, transparent inside → the wheel goes to the app below
        _frame = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)), BorderThickness = new Thickness(2) },
        };
        var fh = new WindowInteropHelper(_frame).EnsureHandle();
        Native.SetWindowLongPtr(fh, -20, (IntPtr)((long)Native.GetWindowLongPtr(fh, -20) | 0x80 | 0x20 | 0x80000 | 0x08000000)); // TOOL|TRANSPARENT|LAYERED|NOACTIVATE
        Native.SetWindowPos(fh, new IntPtr(-1), region.X - 3, region.Y - 3, region.Width + 6, region.Height + 6, 0x0010);
        _frame.Show();

        _status = new TextBlock { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0), Text = "长截图：正在自动滚动…", MaxWidth = 520, TextTrimming = TextTrimming.CharacterEllipsis };
        var done = HudButton("完成", () => Stop(keep: true), accent: true);
        var cancel = HudButton("取消", () => Stop(keep: false), accent: false);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_status); row.Children.Add(done); row.Children.Add(cancel);
        _hud = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"], FontSize = 12.5,
            Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x1E, 0x20)), CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 6, 6, 6), Child = row },
        };
        var hh = new WindowInteropHelper(_hud).EnsureHandle();
        // NOACTIVATE: the status bar must never take the focus (or the wheel) away from the app being captured
        Native.SetWindowLongPtr(hh, -20, (IntPtr)((long)Native.GetWindowLongPtr(hh, -20) | 0x80 | 0x08000000));
        _hud.Show();
        PlaceHud(hh);

        // park the mouse over the region so wheel input lands on that content
        Native.GetCursorPos(out _cursorBefore);
        Native.SetCursorPos(centre.X, centre.Y);

        _timer.Tick += (_, _) => Step();
        _timer.Start();
        App.Log($"long screenshot: start {region.Width}x{region.Height} at {region.X},{region.Y}, target window {(_target == IntPtr.Zero ? "none" : "found")}");
    }

    static Button HudButton(string text, Action click, bool accent)
    {
        var b = new Button
        {
            Content = text, Margin = new Thickness(4, 0, 0, 0), Focusable = false, Cursor = Cursors.Hand,
            Style = (Style)Application.Current.Resources[accent ? "AccentButton" : "PillButton"],
        };
        b.Click += (_, _) => click();
        return b;
    }

    void PlaceHud(IntPtr hud)
    {
        _hud.UpdateLayout();
        Native.GetWindowRect(hud, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var wa = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(_region.X, _region.Y, _region.Width, _region.Height)).WorkingArea;
        int x = Math.Clamp(_region.X + _region.Width - w, wa.Left, Math.Max(wa.Left, wa.Right - w));
        int y = _region.Y + _region.Height + 8;
        if (y + h > wa.Bottom) y = Math.Max(wa.Top, _region.Y - h - 8);
        if (y + h > _region.Y && y < _region.Y + _region.Height) y = wa.Bottom - h; // never over the region
        Native.SetWindowPos(hud, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010);
    }

    /// <summary>One notch down, by the given method. Returns false if the method can't be used.</summary>
    bool RealScroll(ScrollMethod m)
    {
        const int delta = -120; // one notch: stays well inside the region so consecutive frames overlap
        switch (m)
        {
            case ScrollMethod.Wheel:
                Native.SetCursorPos(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);
                var wheel = new Native.INPUT { type = 0 /* INPUT_MOUSE */ };
                wheel.u.mi = new Native.MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = 0x0800 /* WHEEL */, dwExtraInfo = Native.OurInjectionTag };
                Native.Send(wheel);
                return true;
            case ScrollMethod.Keys:
            case ScrollMethod.KeysUp:
                ushort vk = m == ScrollMethod.Keys ? (ushort)0x28 /* VK_DOWN */ : (ushort)0x26 /* VK_UP */;
                Native.Send(Native.Key(vk, false), Native.Key(vk, true), Native.Key(vk, false), Native.Key(vk, true));
                return true;
            case ScrollMethod.PostedWheel:
                if (_target == IntPtr.Zero) return false;
                int x = _region.X + _region.Width / 2, y = _region.Y + _region.Height / 2;
                var w = (IntPtr)(unchecked((int)((uint)(ushort)(short)delta << 16)));
                var l = (IntPtr)((y << 16) | (x & 0xFFFF));
                return PostMessage(_target, 0x020A /* WM_MOUSEWHEEL */, w, l);
            default:
                return false;
        }
    }

    void Step()
    {
        if (_done) return;
        try
        {
            int added = _stitch.Add(_grab(_region));
            _frames++;
            if (_stitch.Height >= MaxHeight) { Stop(keep: true); return; }

            if (added < 0 && Method is ScrollMethod.Wheel or ScrollMethod.PostedWheel && ++_lostStreak >= 2)
            {
                // a wheel notch jumps further than the region is tall: switch to the smaller ↓ steps,
                // first stepping back up until we overlap the last matched frame again (no gap)
                _lostStreak = 0;
                _idle = 0;
                Method = ScrollMethod.Keys;
                _recovering = true;
                App.Log("long screenshot: wheel steps too big for this region, switching to Keys");
            }
            else if (added >= 0) { _lostStreak = 0; if (_recovering) _recovering = false; }

            if (_stitch.Moved)
            {
                // it has scrolled before: standing still now means the bottom was reached (auto modes only)
                if (Method != ScrollMethod.Manual && _stitch.StillFrames >= 3) { Stop(keep: true); return; }
            }
            else if (_frames > 1 && added == 0 && !_recovering && ++_idle >= 2 && Method != ScrollMethod.Manual)
            {
                // this way of scrolling does nothing here: try the next one
                _idle = 0;
                Method = Method switch
                {
                    ScrollMethod.Wheel when _canPost => ScrollMethod.PostedWheel,
                    ScrollMethod.Wheel or ScrollMethod.PostedWheel => ScrollMethod.Keys,
                    _ => ScrollMethod.Manual,
                };
                App.Log($"long screenshot: switching to {Method}");
            }

            _status.Text = (Method == ScrollMethod.Manual
                    ? "请用鼠标滚轮往下滚，滚完点“完成”"
                    : "长截图：正在自动滚动…") + $" · 已拼接 {_stitch.Height} px"
                + (added < 0 ? " · 滚得太快了，往回滚一点" : "");

            if (Method != ScrollMethod.Manual && !_scroll(_recovering ? ScrollMethod.KeysUp : Method))
                Method = ScrollMethod.Manual;
        }
        catch (Exception ex)
        {
            App.Log("long screenshot step failed: " + ex.Message);
            Stop(keep: _stitch.Height > 0);
        }
    }

    internal void Stop(bool keep)
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        Native.SetCursorPos(_cursorBefore.X, _cursorBefore.Y);
        _frame.Close();
        _hud.Close();
        _active = null;
        App.Log($"long screenshot: {(keep ? "done" : "cancelled")} at {_stitch.Height} px (method {Method}, {_stitch.Lost} unmatched frames)");
        if (!keep || _stitch.Height == 0) return;
        var result = _stitch.Result();
        if (Finished != null) { Finished(result); return; }
        LongShotResultWindow.Show(result, _region);
    }
}

/// <summary>Preview of a finished long screenshot with 复制 / 钉图 (置顶·普通) / 保存 / 关闭.</summary>
public static class LongShotResultWindow
{
    public static Window Show(BitmapSource image, Int32Rect region)
    {
        var w = new Window
        {
            Title = "AgosClip 长截图", Width = 520, Height = Math.Min(720, SystemParameters.WorkArea.Height - 80),
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true,
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/AgosClip.ico")),
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"], FontSize = 13,
        };
        w.SetResourceReference(Window.BackgroundProperty, "WindowBg");
        w.SetResourceReference(Window.ForegroundProperty, "Fg");
        var img = new Image { Source = image, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var scroll = new ScrollViewer { Content = img, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12, 12, 12, 0) };
        SmoothWheel.Attach(scroll);

        Button B(string text, string style, Action a)
        {
            var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Style = (Style)Application.Current.Resources[style] };
            b.Click += (_, _) => a();
            return b;
        }
        var info = new TextBlock { Text = $"{image.PixelWidth} × {image.PixelHeight}", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        var bar = new DockPanel { Margin = new Thickness(12, 10, 12, 12), LastChildFill = false };
        bar.Children.Add(info);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        buttons.Children.Add(B("复制", "AccentButton", () => { ScreenCapture.CopyToClipboard(image); w.Close(); }));
        buttons.Children.Add(B("置顶钉图", "PillButton", () => { PinWindow.Open(image, PinSpot(image, region), onTop: true, fitScreen: true); w.Close(); }));
        buttons.Children.Add(B("普通钉图", "PillButton", () => { PinWindow.Open(image, PinSpot(image, region), onTop: false, fitScreen: true); w.Close(); }));
        buttons.Children.Add(B("保存…", "PillButton", () => { if (PinWindow.SaveImage(w, image)) w.Close(); }));
        buttons.Children.Add(B("关闭", "PillButton", w.Close));
        bar.Children.Add(buttons);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(scroll);
        w.Content = root;
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.Show();
        w.Activate();
        return w;
    }

    static Int32Rect PinSpot(BitmapSource image, Int32Rect region) => new(region.X, region.Y, image.PixelWidth, image.PixelHeight);
}
