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
/// Long (scrolling) screenshot of a screen region, driven by the user: they scroll the content under the
/// region with their own mouse wheel, and every few hundredths of a second the region is grabbed and the
/// new rows are stitched on (see <see cref="Stitcher"/>). AgosClip never scrolls, moves the mouse, presses
/// keys or switches windows by itself. It ends only on 完成 / 取消 (or at a size cap).
/// A red frame (click-through, just outside the region so it is never captured) and a status bar that
/// never takes the focus show what is happening.
/// </summary>
public sealed class LongShot
{
    const int MaxHeight = 30000;
    static LongShot? _active;
    public static bool IsActive => _active != null;
    internal static LongShot? Active => _active;

    readonly Int32Rect _region;
    readonly Stitcher _stitch;
    readonly Func<Int32Rect, BitmapSource> _grab;
    readonly Window _frame, _hud;
    readonly TextBlock _status;
    readonly DispatcherTimer _timer = new();
    bool _done;

    internal Stitcher Stitch => _stitch;
    internal string Status => _status.Text;

    /// <summary>Self-test hook: the result instead of the preview window.</summary>
    internal static event Action<BitmapSource>? Finished;

    public static void Start(Int32Rect region)
    {
        if (_active != null || region.Width < 20 || region.Height < 40) return;
        _active = new LongShot(region, ScreenCapture.CaptureRect, TimeSpan.FromMilliseconds(120));
    }

    /// <summary>Self-test: run on a fake screen.</summary>
    internal static LongShot StartForTest(Int32Rect region, Func<Int32Rect, BitmapSource> grab, TimeSpan tick)
    {
        _active = new LongShot(region, grab, tick);
        return _active;
    }

    LongShot(Int32Rect region, Func<Int32Rect, BitmapSource> grab, TimeSpan tick)
    {
        _region = region;
        _grab = grab;
        _timer.Interval = tick; // often enough that ordinary wheel scrolling still leaves overlapping frames
        _stitch = new Stitcher(region.Width, region.Height);

        // red frame 3 px outside the region, transparent inside → the user's wheel goes to the app below
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

        _status = new TextBlock { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0), MaxWidth = 560, TextTrimming = TextTrimming.CharacterEllipsis };
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
        UpdateStatus(0);
        var hh = new WindowInteropHelper(_hud).EnsureHandle();
        // NOACTIVATE: the status bar must never take the focus (or the wheel) away from the app being captured
        Native.SetWindowLongPtr(hh, -20, (IntPtr)((long)Native.GetWindowLongPtr(hh, -20) | 0x80 | 0x08000000));
        _hud.Show();
        PlaceHud(hh);

        _timer.Tick += (_, _) => Step();
        _timer.Start();
        App.Log($"long screenshot: start {region.Width}x{region.Height} at {region.X},{region.Y} (user scrolls)");
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

    void UpdateStatus(int added) =>
        _status.Text = "在红框里用鼠标滚轮往下滚，滚完点“完成”"
                       + (_stitch.Height > 0 ? $" · 已拼接 {_stitch.Height} px" : "")
                       + (added < 0 ? " · 这一屏没对上（滚得太快或往回滚了），往回滚一点" : "");

    void Step()
    {
        if (_done) return;
        try
        {
            int added = _stitch.Add(_grab(_region));
            UpdateStatus(added);
            if (_stitch.Height >= MaxHeight) Stop(keep: true);
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
        _frame.Close();
        _hud.Close();
        _active = null;
        App.Log($"long screenshot: {(keep ? "done" : "cancelled")} at {_stitch.Height} px ({_stitch.Lost} frames didn't overlap)");
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
