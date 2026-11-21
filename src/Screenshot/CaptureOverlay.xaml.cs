using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AgosClip;

/// <summary>
/// Full-screen selection layer for one monitor. Shows that monitor's part of the frozen screenshot,
/// lets the user drag a region, and offers 复制 / 钉图 (置顶 or 普通) at the selection's bottom-right.
/// All overlays of a capture share one <see cref="CaptureSession"/>.
/// </summary>
public partial class CaptureOverlay : Window
{
    readonly CaptureSession _session;
    readonly Int32Rect _monitor;   // physical rect of this monitor
    Point _start;
    bool _dragging;
    Rect _sel = Rect.Empty;        // selection in this window's DIPs

    internal CaptureOverlay(CaptureSession session, BitmapSource monitorImage, Int32Rect monitorPhysical)
    {
        InitializeComponent();
        _session = session;
        _monitor = monitorPhysical;
        Shot.Source = monitorImage;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowLongPtr(hwnd, -20, (IntPtr)((long)Native.GetWindowLongPtr(hwnd, -20) | 0x80)); // tool window
        Native.SetWindowPos(hwnd, new IntPtr(-1), _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height, 0x0010);
        SizeChanged += (_, _) => Layout();
        Loaded += (_, _) => Layout();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, e) => { if (_sel.IsEmpty || _dragging) _session.Cancel(); else ClearSelection(); e.Handled = true; };
        PreviewKeyDown += OnKey;
    }

    internal Int32Rect MonitorRect => _monitor;

    /// <summary>Physical pixels per DIP on this monitor (exact, from the real window size).</summary>
    double Scale => ActualWidth > 0 ? _monitor.Width / ActualWidth : 1;

    void Layout()
    {
        Shot.Width = Stage.ActualWidth;
        Shot.Height = Stage.ActualHeight;
        DimAll.Rect = new Rect(0, 0, Stage.ActualWidth, Stage.ActualHeight);
        Canvas.SetLeft(Hint, Math.Max(0, (Stage.ActualWidth - Hint.ActualWidth) / 2));
        Canvas.SetTop(Hint, 24);
        UpdateSelectionVisuals();
    }

    internal bool HasSelection => !_sel.IsEmpty && _sel.Width >= 2 && _sel.Height >= 2;

    internal void ClearSelection()
    {
        _sel = Rect.Empty;
        _dragging = false;
        UpdateSelectionVisuals();
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && HasSelection && _sel.Contains(e.GetPosition(Stage))) { Copy(); return; }
        if (IsInside(Toolbar, e)) return;
        _session.SelectionStarted(this); // only one monitor may hold a selection
        _start = e.GetPosition(Stage);
        _sel = new Rect(_start, _start);
        _dragging = true;
        CaptureMouse();
        PinMenu.Visibility = Visibility.Collapsed;
        UpdateSelectionVisuals();
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(Stage);
        p = new Point(Math.Clamp(p.X, 0, Stage.ActualWidth), Math.Clamp(p.Y, 0, Stage.ActualHeight));
        _sel = new Rect(_start, p);
        UpdateSelectionVisuals();
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (!HasSelection) ClearSelection();
        UpdateSelectionVisuals();
    }

    static bool IsInside(FrameworkElement el, MouseEventArgs e) =>
        el.IsVisible && new Rect(el.RenderSize).Contains(e.GetPosition(el));

    /// <summary>Self-test hook: select a DIP rectangle as if dragged.</summary>
    internal void SelectForTest(Rect r)
    {
        _session.SelectionStarted(this);
        _sel = r;
        UpdateSelectionVisuals();
    }

    void UpdateSelectionVisuals()
    {
        bool has = HasSelection;
        Hint.Visibility = has || _dragging ? Visibility.Collapsed : Visibility.Visible;
        DimHole.Rect = has ? _sel : Rect.Empty;
        SelBorder.Visibility = SizeLabel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.Visibility = has && !_dragging ? Visibility.Visible : Visibility.Collapsed;
        if (!has) return;

        Canvas.SetLeft(SelBorder, _sel.X); Canvas.SetTop(SelBorder, _sel.Y);
        SelBorder.Width = _sel.Width; SelBorder.Height = _sel.Height;

        var phys = PhysicalSelection();
        SizeText.Text = $"{phys.Width} × {phys.Height}";
        SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(SizeLabel, _sel.X);
        Canvas.SetTop(SizeLabel, _sel.Y - SizeLabel.DesiredSize.Height - 4 >= 0 ? _sel.Y - SizeLabel.DesiredSize.Height - 4 : _sel.Y + 4);

        // toolbar under the bottom-right corner, flipped inside the screen when there's no room
        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tb = Toolbar.DesiredSize;
        double x = Math.Clamp(_sel.Right - tb.Width, 4, Math.Max(4, Stage.ActualWidth - tb.Width - 4));
        double y = _sel.Bottom + 6;
        if (y + tb.Height > Stage.ActualHeight - 4) y = _sel.Bottom - tb.Height - 6;            // inside, bottom-right
        if (y < 4) y = 4;
        Canvas.SetLeft(Toolbar, x);
        Canvas.SetTop(Toolbar, y);
    }

    /// <summary>The selection in physical pixels relative to this monitor's image.</summary>
    internal Int32Rect PhysicalSelection()
    {
        double s = Scale;
        int x = (int)Math.Round(_sel.X * s), y = (int)Math.Round(_sel.Y * s);
        int r = (int)Math.Round(_sel.Right * s), b = (int)Math.Round(_sel.Bottom * s);
        x = Math.Clamp(x, 0, _monitor.Width); y = Math.Clamp(y, 0, _monitor.Height);
        r = Math.Clamp(r, x, _monitor.Width); b = Math.Clamp(b, y, _monitor.Height);
        return new Int32Rect(x, y, Math.Max(1, r - x), Math.Max(1, b - y));
    }

    /// <summary>Screen position (physical) of the selection — where a pin should appear.</summary>
    internal Int32Rect ScreenSelection()
    {
        var p = PhysicalSelection();
        return new Int32Rect(_monitor.X + p.X, _monitor.Y + p.Y, p.Width, p.Height);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (PinMenu.IsVisible) PinMenu.Visibility = Visibility.Collapsed;
                else _session.Cancel();
                break;
            case Key.Enter when HasSelection: Copy(); break;
            case Key.C when HasSelection && (Keyboard.Modifiers & ModifierKeys.Control) != 0: Copy(); break;
            default: return;
        }
        e.Handled = true;
    }

    internal void Copy() { if (HasSelection) _session.Finish(this, CaptureAction.Copy); }
    void Copy_Click(object sender, RoutedEventArgs e) => Copy();
    void Pin_Click(object sender, RoutedEventArgs e)
    {
        PinMenu.Visibility = PinMenu.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        UpdateSelectionVisuals(); // re-place: the toolbar grew
    }
    void PinTop_Click(object sender, RoutedEventArgs e) => _session.Finish(this, CaptureAction.PinOnTop);
    void PinNormal_Click(object sender, RoutedEventArgs e) => _session.Finish(this, CaptureAction.PinNormal);
    void Cancel_Click(object sender, RoutedEventArgs e) => _session.Cancel();
}
