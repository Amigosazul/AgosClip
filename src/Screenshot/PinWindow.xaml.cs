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

    readonly BitmapSource _image;
    readonly IntPtr _hwnd;
    double _zoom = 1;
    int _x, _y;
    readonly DispatcherTimer _badgeTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    public bool OnTop { get; private set; }
    public double Zoom => _zoom;

    PinWindow(BitmapSource image, Int32Rect where, bool onTop)
    {
        InitializeComponent();
        _image = image;
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
        Closed += (_, _) => Open_.Remove(this);
    }

    public static PinWindow Open(BitmapSource image, Int32Rect where, bool onTop)
    {
        var w = new PinWindow(image, where, onTop);
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
        m.Items.Add(Item("关闭", Close));
        m.Items.Add(Item("关闭所有钉图", CloseAll));
        return m;
    }

    static MenuItem Item(string header, Action a)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => a();
        return mi;
    }

    void Save()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存钉图",
            FileName = $"截图 {DateTime.Now:yyyy-MM-dd HHmmss}.png",
            Filter = "PNG 图片|*.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dlg.ShowDialog(this) == true)
            try { File.WriteAllBytes(dlg.FileName, ScreenCapture.Png(_image)); }
            catch (Exception ex) { MessageBox.Show(this, "保存失败：" + ex.Message, "AgosClip"); }
    }

    void Top_Click(object sender, RoutedEventArgs e) => SetOnTop(!OnTop);
    void Copy_Click(object sender, RoutedEventArgs e) => ScreenCapture.CopyToClipboard(_image);
    void Save_Click(object sender, RoutedEventArgs e) => Save();
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
