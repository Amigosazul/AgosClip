using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AgosClip;

/// <summary>
/// A screenshot pinned on the desktop at 1:1, where it was taken. Drag to move, wheel to zoom,
/// double-click or Esc to close. "置顶" keeps it above every window; "普通" behaves like a normal window
/// (it then shows in the taskbar so a covered pin can always be found again).
/// </summary>
public partial class PinWindow : Window
{
    static readonly List<PinWindow> Open_ = new();
    public static IReadOnlyList<PinWindow> All => Open_;

    BitmapSource _image;              // what is shown (the translation once there is one)
    readonly BitmapSource _original;
    BitmapSource? _translated;
    System.Threading.CancellationTokenSource? _translating;
    readonly IntPtr _hwnd;
    double _zoom = 1;
    int _x, _y;
    readonly DispatcherTimer _badgeTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    public bool OnTop { get; private set; }
    public double Zoom => _zoom;

    PinWindow(BitmapSource image, Int32Rect where, bool onTop)
    {
        InitializeComponent();
        _image = _original = image;
        Picture.Source = image;
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        (_x, _y) = (where.X, where.Y);
        int pref = 3; // DWMWCP_ROUNDSMALL
        Native.DwmSetWindowAttribute(_hwnd, 33, ref pref, 4);
        SetOnTop(onTop);
        ApplySize();

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Close(); return; }
            try { DragMove(); } catch (InvalidOperationException) { }
            if (Native.GetWindowRect(_hwnd, out var r)) (_x, _y) = (r.Left, r.Top);
        };
        MouseWheel += (_, e) => ZoomBy(e.Delta > 0 ? 1.1 : 1 / 1.1, e.GetPosition(this));
        MouseEnter += (_, _) => Tools.Visibility = Visibility.Visible;
        MouseLeave += (_, _) => Tools.Visibility = Visibility.Collapsed;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0) ScreenCapture.CopyToClipboard(_image);
        };
        _badgeTimer.Tick += (_, _) => { _badgeTimer.Stop(); ZoomBadge.Visibility = Visibility.Collapsed; };
        ContextMenu = BuildMenu();
        Closed += (_, _) => { Open_.Remove(this); _translating?.Cancel(); };
        ContextMenuOpening += (_, _) => RefreshMenu();
    }

    /// <param name="fitScreen">Long screenshots: start zoomed out so the whole picture fits on the screen.</param>
    public static PinWindow Open(BitmapSource image, Int32Rect where, bool onTop, bool fitScreen = false)
    {
        var w = new PinWindow(image, where, onTop);
        if (fitScreen)
        {
            var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(where.X + 10, where.Y + 10)).WorkingArea;
            w._zoom = Math.Min(1, Math.Min((wa.Height * 0.9) / image.PixelHeight, (wa.Width * 0.9) / image.PixelWidth));
            int ww = (int)(image.PixelWidth * w._zoom), hh = (int)(image.PixelHeight * w._zoom);
            w._x = Math.Clamp(where.X, wa.Left, Math.Max(wa.Left, wa.Right - ww));
            w._y = Math.Clamp(where.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - hh));
            w.ApplySize();
        }
        Open_.Add(w);
        w.Show();
        w.Activate();
        return w;
    }

    public static void CloseAll()
    {
        foreach (var w in Open_.ToList()) w.Close();
    }

    public void SetOnTop(bool on)
    {
        OnTop = on;
        Topmost = on;
        ShowInTaskbar = !on; // a normal pin can end up behind other windows — keep it reachable
        TopBtn.Content = on ? "" : "";
        TopBtn.ToolTip = on ? "已置顶 · 点击取消置顶" : "未置顶 · 点击置顶";
        TopBtn.SetValue(ForegroundProperty, on ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.LightGray);
        if (ContextMenu?.Items[0] is MenuItem mi) mi.IsChecked = on;
    }

    void ApplySize()
    {
        int w = Math.Max(16, (int)Math.Round(_image.PixelWidth * _zoom));
        int h = Math.Max(16, (int)Math.Round(_image.PixelHeight * _zoom));
        Native.SetWindowPos(_hwnd, IntPtr.Zero, _x, _y, w, h, 0x0004 | 0x0010); // NOZORDER | NOACTIVATE
    }

    /// <summary>Zoom around the mouse, 10% … 500%.</summary>
    internal void ZoomBy(double factor, Point around)
    {
        double old = _zoom;
        _zoom = Math.Clamp(_zoom * factor, 0.1, 5);
        if (Math.Abs(_zoom - old) < 1e-6) return;
        // keep the point under the mouse in place (physical px)
        double scale = ActualWidth > 0 ? _image.PixelWidth * old / ActualWidth : 1;
        _x += (int)Math.Round(around.X * scale * (1 - _zoom / old));
        _y += (int)Math.Round(around.Y * scale * (1 - _zoom / old));
        ApplySize();
        ZoomText.Text = $"{_zoom * 100:0}%";
        ZoomBadge.Visibility = Visibility.Visible;
        _badgeTimer.Stop();
        _badgeTimer.Interval = TimeSpan.FromMilliseconds(900);
        _badgeTimer.Start();
    }

    ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        var top = new MenuItem { Header = "置顶", IsCheckable = true, IsChecked = OnTop };
        top.Click += (_, _) => SetOnTop(top.IsChecked);
        m.Items.Add(top);
        m.Items.Add(Item("复制", () => ScreenCapture.CopyToClipboard(_image)));
        m.Items.Add(Item("另存为…", Save));
        m.Items.Add(Item("原始大小", () => { _zoom = 1; ApplySize(); }));
        m.Items.Add(new Separator());
        _ocrItem = Item("识别文字", () => TextToolWindow.ShowOcr(_image, ScreenRect()));
        _translateItem = Item("翻译图片", TranslateInPlace);
        _swapItem = Item("显示原图", () => ShowImage(_image == _original ? _translated : _original));
        m.Items.Add(_ocrItem);
        m.Items.Add(_translateItem);
        m.Items.Add(_swapItem);
        m.Items.Add(new Separator());
        m.Items.Add(Item("关闭", Close));
        m.Items.Add(Item("关闭所有钉图", CloseAll));
        return m;
    }

    MenuItem? _ocrItem, _translateItem, _swapItem;

    void RefreshMenu()
    {
        bool ocr = RapidOcr.Available;
        if (_ocrItem != null) _ocrItem.Visibility = ocr ? Visibility.Visible : Visibility.Collapsed;
        if (_translateItem != null)
        {
            _translateItem.Visibility = ocr && App.Current.Settings.EnableTranslate && _translated == null ? Visibility.Visible : Visibility.Collapsed;
            _translateItem.IsEnabled = _translating == null;
        }
        if (_swapItem != null)
        {
            _swapItem.Visibility = _translated != null ? Visibility.Visible : Visibility.Collapsed;
            _swapItem.Header = _image == _original ? "显示译文" : "显示原图";
        }
    }

    /// <summary>Where the pin is on screen (physical px), so a result window can open beside it.</summary>
    Rect ScreenRect() => Native.GetWindowRect(_hwnd, out var r) ? new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : Rect.Empty;

    /// <summary>Swaps the picture (same pixel size), keeping position and zoom.</summary>
    void ShowImage(BitmapSource? img)
    {
        if (img == null) return;
        _image = img;
        Picture.Source = img;
    }

    void Badge(string text, bool stay)
    {
        ZoomText.Text = text;
        ZoomBadge.Visibility = Visibility.Visible;
        _badgeTimer.Stop();
        if (!stay) { _badgeTimer.Interval = TimeSpan.FromSeconds(4); _badgeTimer.Start(); }
    }

    /// <summary>Recognises the text, translates it and shows the picture with the translation written in.</summary>
    public async void TranslateInPlace()
    {
        if (_translating != null) return;
        if (!RapidOcr.Available) { Badge("请先在 设置 → 插件 里安装文字识别", false); return; }
        _translating = new System.Threading.CancellationTokenSource();
        Badge("正在翻译…", true);
        try
        {
            var r = await ImageTranslator.TranslateAsync(_original, App.Current.Settings.TranslateTarget, _translating.Token);
            _translated = r.Image;
            ShowImage(_translated);
            Badge($"{r.Engine} · 译为{Translator.NameOf(r.Target)} · 右键可看原图", false);
            App.Log($"pin translate: {r.Blocks} blocks via {r.Engine}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Log("pin translate failed: " + ex.Message);
            Badge("翻译失败：" + ex.Message, false);
        }
        finally { _translating = null; }
    }

    static MenuItem Item(string header, Action a)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => a();
        return mi;
    }

    void Save() => SaveImage(this, _image);

    /// <summary>Save-as dialog for a picture (PNG). Returns true if saved.</summary>
    public static bool SaveImage(Window owner, BitmapSource image)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存钉图",
            FileName = $"截图 {DateTime.Now:yyyy-MM-dd HHmmss}.png",
            Filter = "PNG 图片|*.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dlg.ShowDialog(owner) != true) return false;
        try { File.WriteAllBytes(dlg.FileName, ScreenCapture.Png(image)); return true; }
        catch (Exception ex) { MessageBox.Show(owner, "保存失败：" + ex.Message, "AgosClip"); return false; }
    }

    void Top_Click(object sender, RoutedEventArgs e) => SetOnTop(!OnTop);
    void Copy_Click(object sender, RoutedEventArgs e) => ScreenCapture.CopyToClipboard(_image);
    void Save_Click(object sender, RoutedEventArgs e) => Save();
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
