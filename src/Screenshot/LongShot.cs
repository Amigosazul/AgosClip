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
/// region with their own mouse wheel; every 120 ms the region is grabbed and the new rows are stitched on
/// (see <see cref="Stitcher"/>). AgosClip never scrolls, moves the mouse, presses keys or switches windows.
///
/// Around the region (never on it, so nothing of ours is captured):
/// a red click-through frame; a hint at the top-left (滚动页面截取更多内容 · 已拼接 N px); a live preview
/// strip beside the region showing everything stitched so far; and a toolbar at the bottom-right:
/// ✎ 预览全图 · 📌 钉图 (置顶/普通) · ⤓ 保存 · ✕ 取消 · ✓ 完成 (copy). None of them takes the focus.
/// </summary>
public sealed class LongShot
{
    const int MaxHeight = 30000;
    const double PreviewWidthDip = 150;
    static LongShot? _active;
    public static bool IsActive => _active != null;
    internal static LongShot? Active => _active;

    readonly Int32Rect _region;
    readonly Stitcher _stitch;
    readonly Func<Int32Rect, BitmapSource> _grab;
    readonly Window _frame, _hint, _bar;
    readonly Window? _preview;
    readonly TextBlock _hintText;
    readonly StackPanel _pinChoice;
    readonly DispatcherTimer _timer = new();
    readonly double _dpi;               // physical px per DIP on the region's monitor

    // live preview: a downscaled copy built row by row as rows are stitched
    readonly WriteableBitmap? _thumb;
    readonly Image? _thumbImage;
    readonly ScrollViewer? _thumbScroll;
    readonly double _thumbScale;        // preview px per stitched px
    int _thumbRows;                     // preview rows written so far
    bool _done;

    internal Stitcher Stitch => _stitch;
    internal string Status => _hintText.Text;
    internal int PreviewRows => _thumbRows;
    internal bool HasPreview => _preview != null;
    internal Window Toolbar => _bar;
    internal Window? PreviewWindow => _preview;

    /// <summary>Self-test hook: the result instead of the chosen action.</summary>
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

    static Window Floating(UIElement content, bool clickThrough)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, Content = content,
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"], FontSize = 13,
        };
        var h = new WindowInteropHelper(w).EnsureHandle();
        long ex = (long)Native.GetWindowLongPtr(h, -20) | 0x80 /* TOOL */ | 0x08000000 /* NOACTIVATE */;
        if (clickThrough) ex |= 0x20 /* TRANSPARENT */ | 0x80000 /* LAYERED */;
        Native.SetWindowLongPtr(h, -20, (IntPtr)ex);
        return w;
    }

    static void PlaceAt(Window w, int x, int y) =>
        Native.SetWindowPos(new WindowInteropHelper(w).Handle, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010);

    static (int W, int H) SizeOf(Window w)
    {
        w.UpdateLayout();
        Native.GetWindowRect(new WindowInteropHelper(w).Handle, out var r);
        return (r.Right - r.Left, r.Bottom - r.Top);
    }

    LongShot(Int32Rect region, Func<Int32Rect, BitmapSource> grab, TimeSpan tick)
    {
        _region = region;
        _grab = grab;
        _timer.Interval = tick; // often enough that ordinary wheel scrolling still leaves overlapping frames
        _stitch = new Stitcher(region.Width, region.Height);
        var screen = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(region.X, region.Y, region.Width, region.Height));
        var wa = screen.WorkingArea;
        var mon = Native.MonitorFromPoint(new Native.POINT { X = region.X + 1, Y = region.Y + 1 }, 2);
        _dpi = Native.GetDpiForMonitor(mon, 0, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1;

        // red frame 3 px outside the region, transparent inside → the user's wheel goes to the app below
        _frame = Floating(new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)), BorderThickness = new Thickness(2) }, clickThrough: true);
        _frame.SizeToContent = SizeToContent.Manual;
        _frame.Show();
        Native.SetWindowPos(new WindowInteropHelper(_frame).Handle, new IntPtr(-1), region.X - 3, region.Y - 3, region.Width + 6, region.Height + 6, 0x0010);

        // hint at the top-left, above the region
        _hintText = new TextBlock { Foreground = Brushes.White, FontSize = 14 };
        var hintBox = new Border { Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x1E, 0x1E, 0x20)), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 10, 4), Child = _hintText };
        _hint = Floating(hintBox, clickThrough: true);
        UpdateHint(0);
        _hint.Show();
        var (hw, hh) = SizeOf(_hint);
        int hy = region.Y - hh - 8 >= wa.Top ? region.Y - hh - 8 : region.Y + region.Height + 8;
        PlaceAt(_hint, Math.Clamp(region.X, wa.Left, Math.Max(wa.Left, wa.Right - hw)), hy);

        // live preview strip beside the region (right, else left; skipped if neither side has room)
        int pw = (int)Math.Round(PreviewWidthDip * _dpi), gap = (int)Math.Round(12 * _dpi);
        int? px = region.X + region.Width + gap + pw <= wa.Right ? region.X + region.Width + gap
                : region.X - gap - pw >= wa.Left ? region.X - gap - pw : null;
        if (px is int previewX)
        {
            _thumbScale = (double)pw / region.Width;
            int maxRows = (int)Math.Ceiling(MaxHeight * _thumbScale) + 2;
            _thumb = new WriteableBitmap(pw, maxRows, 96 * _dpi, 96 * _dpi, PixelFormats.Bgra32, null);
            _thumbImage = new Image { Source = _thumb, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Height = 0 };
            RenderOptions.SetBitmapScalingMode(_thumbImage, BitmapScalingMode.HighQuality);
            _thumbScroll = new ScrollViewer
            {
                Content = _thumbImage, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Width = pw / _dpi, Height = region.Height / _dpi,
            };
            var card = new Border
            {
                Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), BorderThickness = new Thickness(1),
                Child = _thumbScroll,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = 0.3 },
            };
            _preview = Floating(card, clickThrough: true);
            _preview.Show();
            PlaceAt(_preview, previewX, region.Y);
        }

        // toolbar at the bottom-right: ✎ 预览全图 · 📌 钉图 · ⤓ 保存 · ✕ 取消 · ✓ 完成
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(IconButton("", "预览全图（复制、钉图、保存都可以在里面做）", () => Finish(Outcome.Preview)));
        tools.Children.Add(IconButton("", "钉图", TogglePinChoice));
        tools.Children.Add(IconButton("", "另存为图片…", () => Finish(Outcome.Save)));
        tools.Children.Add(IconButton("", "取消", () => Stop(keep: false), Color.FromRgb(0xFF, 0x3B, 0x30)));
        tools.Children.Add(IconButton("", "完成：复制到剪贴板", () => Finish(Outcome.Copy), Color.FromRgb(0x34, 0xC7, 0x59)));
        _pinChoice = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        _pinChoice.Children.Add(TextButton("置顶钉图", () => Finish(Outcome.PinTop)));
        _pinChoice.Children.Add(TextButton("普通钉图", () => Finish(Outcome.PinNormal)));
        var barBox = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(6), BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { tools, _pinChoice } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.3 },
        };
        barBox.SetResourceReference(Border.BackgroundProperty, "OverlayBg");
        barBox.SetResourceReference(Border.BorderBrushProperty, "Hairline");
        _bar = Floating(barBox, clickThrough: false);
        _bar.Show();
        PlaceBar();

        _timer.Tick += (_, _) => Step();
        _timer.Start();
        App.Log($"long screenshot: start {region.Width}x{region.Height} at {region.X},{region.Y} (user scrolls, preview {(HasPreview ? "on" : "off")})");
    }

    void PlaceBar()
    {
        var wa = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(_region.X, _region.Y, _region.Width, _region.Height)).WorkingArea;
        var (bw, bh) = SizeOf(_bar);
        int x = Math.Clamp(_region.X + _region.Width - bw, wa.Left, Math.Max(wa.Left, wa.Right - bw));
        int y = _region.Y + _region.Height + 10;
        if (y + bh > wa.Bottom) y = Math.Max(wa.Top, _region.Y - bh - 10);
        if (y + bh > _region.Y && y < _region.Y + _region.Height) y = wa.Bottom - bh; // never over the region
        PlaceAt(_bar, x, y);
    }

    Button IconButton(string glyph, string tip, System.Action click, Color? color = null)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 17 },
            ToolTip = tip, Width = 44, Height = 38, Focusable = false, Cursor = Cursors.Hand,
            Style = (Style)Application.Current.Resources["IconButton"], Margin = new Thickness(2, 0, 2, 0),
        };
        if (color is Color c) b.Foreground = new SolidColorBrush(c);
        else b.SetResourceReference(Control.ForegroundProperty, "Fg");
        b.Click += (_, _) => click();
        return b;
    }

    static Button TextButton(string text, System.Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(2), Focusable = false, Style = (Style)Application.Current.Resources["PillButton"], HorizontalContentAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => click();
        return b;
    }

    void TogglePinChoice()
    {
        _pinChoice.Visibility = _pinChoice.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        PlaceBar(); // it grew or shrank
    }

    void UpdateHint(int added) =>
        _hintText.Text = "滚动页面截取更多内容"
                         + (_stitch.Height > 0 ? $" · 已拼接 {_stitch.Height} px" : "")
                         + (added < 0 ? " · 没对上，往回滚一点" : "");

    void Step()
    {
        if (_done) return;
        try
        {
            int added = _stitch.Add(_grab(_region));
            UpdateHint(added);
            if (added > 0) GrowPreview();
            if (_stitch.Height >= MaxHeight) Finish(Outcome.Preview);
        }
        catch (Exception ex)
        {
            App.Log("long screenshot step failed: " + ex.Message);
            if (_stitch.Height > 0) Finish(Outcome.Preview); else Stop(keep: false);
        }
    }

    /// <summary>Downscale only the newly stitched rows into the preview (nearest neighbour) and follow the bottom.</summary>
    void GrowPreview()
    {
        if (_thumb == null || _thumbImage == null || _thumbScroll == null) return;
        int pw = _thumb.PixelWidth, target = Math.Min(_thumb.PixelHeight, (int)Math.Floor(_stitch.Height * _thumbScale));
        if (target <= _thumbRows) return;
        var row = new byte[pw * 4];
        var srcX = new int[pw];
        for (int x = 0; x < pw; x++) srcX[x] = Math.Min(_region.Width - 1, (int)(x / _thumbScale));
        for (int py = _thumbRows; py < target; py++)
        {
            var src = _stitch.RowAt(Math.Min(_stitch.Height - 1, (int)(py / _thumbScale)));
            for (int x = 0; x < pw; x++) Buffer.BlockCopy(src, srcX[x] * 4, row, x * 4, 4);
            _thumb.WritePixels(new Int32Rect(0, py, pw, 1), row, pw * 4, 0);
        }
        _thumbRows = target;
        _thumbImage.Height = _thumbRows / _dpi;
        _thumbScroll.UpdateLayout();
        _thumbScroll.ScrollToEnd();
    }

    internal enum Outcome { Copy, Preview, PinTop, PinNormal, Save }

    internal void Finish(Outcome action)
    {
        if (_done || _stitch.Height == 0) { Stop(keep: false); return; }
        var result = _stitch.Result();
        if (action == Outcome.Save)
        {
            // keep everything open if the dialog is cancelled
            _timer.Stop();
            if (Finished == null && !PinWindow.SaveImage(_bar, result)) { _timer.Start(); return; }
        }
        Close();
        App.Log($"long screenshot: {action} at {_stitch.Height} px ({_stitch.Lost} frames didn't overlap)");
        if (Finished != null) { Finished(result); return; }
        switch (action)
        {
            case Outcome.Copy: ScreenCapture.CopyToClipboard(result); break;
            case Outcome.Preview: LongShotResultWindow.Show(result, _region); break;
            case Outcome.PinTop: PinWindow.Open(result, new Int32Rect(_region.X, _region.Y, result.PixelWidth, result.PixelHeight), onTop: true, fitScreen: true); break;
            case Outcome.PinNormal: PinWindow.Open(result, new Int32Rect(_region.X, _region.Y, result.PixelWidth, result.PixelHeight), onTop: false, fitScreen: true); break;
        }
    }

    /// <summary>取消 (keep=false) or, for tests, hand the result to <see cref="Finished"/> (keep=true).</summary>
    internal void Stop(bool keep)
    {
        if (_done) return;
        if (keep && _stitch.Height > 0) { Finish(Outcome.Copy); return; }
        Close();
        App.Log($"long screenshot: cancelled at {_stitch.Height} px");
    }

    void Close()
    {
        _done = true;
        _timer.Stop();
        _frame.Close();
        _hint.Close();
        _preview?.Close();
        _bar.Close();
        _active = null;
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
