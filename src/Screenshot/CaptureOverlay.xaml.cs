using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
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
        BuildOptions();
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
        var at = e.GetPosition(Stage);
        if (IsInside(Toolbar, e) || (_editing != null && IsInside(_editing, e))) return;
        if (_tool != Tool.None && HasSelection && _sel.Contains(at))
        {
            if (_tool == Tool.Text) BeginText(at);
            else BeginMosaic(at);
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2 && HasSelection && _sel.Contains(at)) { Copy(); return; }
        CommitText();
        if (HasAnnotations) return; // the area is fixed once it has been marked up (undo or cancel to start over)
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
        if (_stroke != null) { PaintMosaic(e.GetPosition(Stage)); return; }
        if (!_dragging) return;
        var p = e.GetPosition(Stage);
        p = new Point(Math.Clamp(p.X, 0, Stage.ActualWidth), Math.Clamp(p.Y, 0, Stage.ActualHeight));
        _sel = new Rect(_start, p);
        UpdateSelectionVisuals();
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_stroke != null) { EndMosaic(); return; }
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
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (_editing != null)
        {
            // typing goes to the text box; Esc / Ctrl+Enter finish this text
            if (e.Key == Key.Escape || (e.Key == Key.Enter && ctrl)) { CommitText(); e.Handled = true; }
            return;
        }
        if (e.Key == Key.Z && ctrl) { Undo(); e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Escape:
                if (PinMenu.IsVisible) PinMenu.Visibility = Visibility.Collapsed;
                else if (_tool != Tool.None) SetTool(Tool.None);
                else _session.Cancel();
                break;
            case Key.Enter when HasSelection: Copy(); break;
            case Key.C when HasSelection && (Keyboard.Modifiers & ModifierKeys.Control) != 0: Copy(); break;
            default: return;
        }
        e.Handled = true;
    }

    internal void Copy() { if (HasSelection) { CommitText(); _session.Finish(this, CaptureAction.Copy); } }
    void Copy_Click(object sender, RoutedEventArgs e) => Copy();
    void Pin_Click(object sender, RoutedEventArgs e)
    {
        PinMenu.Visibility = PinMenu.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        UpdateSelectionVisuals(); // re-place: the toolbar grew
    }
    void PinTop_Click(object sender, RoutedEventArgs e) { CommitText(); _session.Finish(this, CaptureAction.PinOnTop); }
    void PinNormal_Click(object sender, RoutedEventArgs e) { CommitText(); _session.Finish(this, CaptureAction.PinNormal); }
    void Cancel_Click(object sender, RoutedEventArgs e) => _session.Cancel();

    // ───────────── editing: text, mosaic, undo ─────────────

    internal enum Tool { None, Text, Mosaic }
    Tool _tool;
    internal Tool CurrentTool => _tool;

    // last choices survive between screenshots
    static double _textSize = 24;
    static Color _textColor = Color.FromRgb(0xFF, 0x3B, 0x30);
    static int _mosaicBlock = 12;
    static readonly double[] TextSizeChoices = { 14, 18, 24, 32, 48 };
    static readonly string[] ColorChoices = { "#FF3B30", "#FF9500", "#FFCC00", "#34C759", "#007AFF", "#AF52DE", "#000000", "#FFFFFF" };
    static readonly (string Label, int Px)[] MosaicChoices = { ("小", 6), ("中", 12), ("大", 20) };

    abstract record Anno(UIElement Element);
    sealed record TextAnno(UIElement Element, string Text, Point At, double Size, Color Color) : Anno(Element);
    sealed record MosaicAnno(UIElement Element, int Block, Dictionary<(int, int), Color> Cells) : Anno(Element);

    readonly List<Anno> _annos = new();
    internal bool HasAnnotations => _annos.Count > 0;
    internal int AnnotationCount => _annos.Count;

    void BuildOptions()
    {
        foreach (var size in TextSizeChoices)
        {
            var b = new Button { Content = size.ToString("0"), Style = (Style)Resources["ToolBtn"], Padding = new Thickness(8, 3, 8, 3), Tag = size };
            b.Click += (_, _) => { _textSize = size; RefreshOptions(); if (_editing != null) _editing.FontSize = size; };
            TextSizes.Children.Add(b);
        }
        foreach (var hex in ColorChoices)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new Button { Style = (Style)Resources["Swatch"], Background = new SolidColorBrush(c), ToolTip = hex };
            b.Click += (_, _) => { _textColor = c; RefreshOptions(); if (_editing != null) _editing.Foreground = new SolidColorBrush(c); };
            TextColors.Children.Add(b);
        }
        foreach (var (label, px) in MosaicChoices)
        {
            var b = new Button { Content = label, Style = (Style)Resources["ToolBtn"], Padding = new Thickness(10, 3, 10, 3), Tag = px };
            b.Click += (_, _) => { _mosaicBlock = px; RefreshOptions(); };
            MosaicSizes.Children.Add(b);
        }
        RefreshOptions();
    }

    void RefreshOptions()
    {
        void Mark(Button b, bool on)
        {
            if (on) { b.SetResourceReference(BackgroundProperty, "SelBg"); b.SetResourceReference(ForegroundProperty, "Accent"); }
            else { b.ClearValue(BackgroundProperty); b.ClearValue(ForegroundProperty); }
        }
        foreach (Button b in TextSizes.Children) Mark(b, (double)b.Tag == _textSize);
        foreach (Button b in TextColors.Children) b.Tag = ((SolidColorBrush)b.Background).Color == _textColor ? "on" : null;
        foreach (Button b in MosaicSizes.Children) Mark(b, (int)b.Tag == _mosaicBlock);
        Mark(TextToolBtn, _tool == Tool.Text);
        Mark(MosaicToolBtn, _tool == Tool.Mosaic);
        TextOptions.Visibility = _tool == Tool.Text ? Visibility.Visible : Visibility.Collapsed;
        MosaicOptions.Visibility = _tool == Tool.Mosaic ? Visibility.Visible : Visibility.Collapsed;
        UndoBtn.IsEnabled = HasAnnotations || _editing != null;
        LongBtn.IsEnabled = !HasAnnotations; // a long shot re-captures the live screen, marks can't follow
        Cursor = _tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
    }

    internal void SetTool(Tool t)
    {
        CommitText();
        _tool = _tool == t ? Tool.None : t;
        PinMenu.Visibility = Visibility.Collapsed;
        RefreshOptions();
        UpdateSelectionVisuals();
    }

    void TextTool_Click(object sender, RoutedEventArgs e) => SetTool(Tool.Text);
    void MosaicTool_Click(object sender, RoutedEventArgs e) => SetTool(Tool.Mosaic);
    void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    internal void Undo()
    {
        if (_editing != null) { AnnoLayer.Children.Remove(_editing); _editing = null; RefreshOptions(); return; }
        if (_annos.Count == 0) return;
        var last = _annos[^1];
        _annos.RemoveAt(_annos.Count - 1);
        AnnoLayer.Children.Remove(last.Element);
        RefreshOptions();
    }

    // text

    TextBox? _editing;
    const double TextPad = 2;

    internal void BeginText(Point at)
    {
        CommitText();
        var box = new TextBox
        {
            FontSize = _textSize, Foreground = new SolidColorBrush(_textColor), Background = Brushes.Transparent,
            BorderThickness = new Thickness(1), Padding = new Thickness(TextPad - 1), MinWidth = 24, AcceptsReturn = true,
            FontFamily = (FontFamily)FindResource("UiFont"), CaretBrush = new SolidColorBrush(_textColor),
        };
        box.SetResourceReference(Control.BorderBrushProperty, "Accent");
        Canvas.SetLeft(box, at.X);
        Canvas.SetTop(box, at.Y - _textSize * 0.6);
        AnnoLayer.Children.Add(box);
        _editing = box;
        box.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (_editing == box) CommitText(); }));
        box.Loaded += (_, _) => { box.Focus(); Keyboard.Focus(box); };
        RefreshOptions();
    }

    /// <summary>Self-test hook: type into the open text box.</summary>
    internal void TypeForTest(string text) { if (_editing != null) _editing.Text = text; }

    internal void CommitText()
    {
        var box = _editing;
        if (box == null) return;
        _editing = null;
        AnnoLayer.Children.Remove(box);
        if (!string.IsNullOrWhiteSpace(box.Text))
        {
            var at = new Point(Canvas.GetLeft(box) + TextPad, Canvas.GetTop(box) + TextPad);
            var color = ((SolidColorBrush)box.Foreground).Color;
            var tb = new TextBlock { Text = box.Text, FontSize = box.FontSize, Foreground = new SolidColorBrush(color), FontFamily = box.FontFamily, IsHitTestVisible = false };
            Canvas.SetLeft(tb, at.X);
            Canvas.SetTop(tb, at.Y);
            AnnoLayer.Children.Add(tb);
            _annos.Add(new TextAnno(tb, box.Text, at, box.FontSize, color));
        }
        RefreshOptions();
        Focus();
    }

    // mosaic: a brush that turns the cells it touches into their average colour

    MosaicAnno? _stroke;
    DrawingGroup? _strokeDrawing;
    byte[]? _px; int _pxStride;

    void EnsurePixels()
    {
        if (_px != null) return;
        var src = new FormatConvertedBitmap((BitmapSource)Shot.Source, PixelFormats.Bgra32, null, 0);
        _pxStride = src.PixelWidth * 4;
        _px = new byte[_pxStride * src.PixelHeight];
        src.CopyPixels(_px, _pxStride, 0);
    }

    Color Average(int x0, int y0, int w, int h)
    {
        long r = 0, g = 0, b = 0, n = 0;
        int x1 = Math.Min(_monitor.Width, x0 + w), y1 = Math.Min(_monitor.Height, y0 + h);
        for (int y = Math.Max(0, y0); y < y1; y++)
            for (int x = Math.Max(0, x0); x < x1; x++)
            {
                int o = y * _pxStride + x * 4;
                b += _px![o]; g += _px[o + 1]; r += _px[o + 2]; n++;
            }
        return n == 0 ? Colors.Gray : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    internal void BeginMosaic(Point at)
    {
        CommitText();
        EnsurePixels();
        // Outer group: a transparent full-stage rect anchors the drawing at (0,0) so the Image needs no
        // offset. The cells go in an inner group clipped to the selection — clipping the outer group
        // would also clip the anchor and shift everything by the selection's top-left.
        var outer = new DrawingGroup();
        outer.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, Stage.ActualWidth, Stage.ActualHeight))));
        _strokeDrawing = new DrawingGroup { ClipGeometry = new RectangleGeometry(_sel) };
        outer.Children.Add(_strokeDrawing);
        var img = new Image { Source = new DrawingImage(outer), Stretch = Stretch.None, IsHitTestVisible = false };
        AnnoLayer.Children.Add(img);
        _stroke = new MosaicAnno(img, _mosaicBlock, new Dictionary<(int, int), Color>());
        CaptureMouse();
        PaintMosaic(at);
    }

    internal void PaintMosaic(Point at)
    {
        if (_stroke == null || _strokeDrawing == null) return;
        double s = Scale;
        int B = _stroke.Block;
        double px = at.X * s, py = at.Y * s, R = B * 1.6;
        var selP = PhysicalSelection();
        for (int cx = (int)Math.Floor((px - R) / B); cx <= (int)Math.Floor((px + R) / B); cx++)
            for (int cy = (int)Math.Floor((py - R) / B); cy <= (int)Math.Floor((py + R) / B); cy++)
            {
                double ccx = (cx + 0.5) * B, ccy = (cy + 0.5) * B;
                if ((ccx - px) * (ccx - px) + (ccy - py) * (ccy - py) > R * R) continue;
                if ((cx + 1) * B <= selP.X || cx * B >= selP.X + selP.Width || (cy + 1) * B <= selP.Y || cy * B >= selP.Y + selP.Height) continue;
                if (_stroke.Cells.ContainsKey((cx, cy))) continue;
                var c = Average(cx * B, cy * B, B, B);
                _stroke.Cells[(cx, cy)] = c;
                var brush = new SolidColorBrush(c); brush.Freeze();
                _strokeDrawing.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(cx * B / s, cy * B / s, B / s + 0.5, B / s + 0.5))));
            }
    }

    internal void EndMosaic()
    {
        if (_stroke == null) return;
        ReleaseMouseCapture();
        if (_stroke.Cells.Count > 0) _annos.Add(_stroke);
        else AnnoLayer.Children.Remove(_stroke.Element);
        _stroke = null;
        _strokeDrawing = null;
        RefreshOptions();
    }

    /// <summary>
    /// The final picture: the cropped screenshot with every mosaic cell and text drawn on top at full
    /// physical resolution (what you see on screen, pixel for pixel).
    /// </summary>
    internal BitmapSource Compose(BitmapSource crop)
    {
        if (!HasAnnotations) return crop;
        double s = Scale;
        var selP = PhysicalSelection();
        int w = crop.PixelWidth, h = crop.PixelHeight;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(crop, new Rect(0, 0, w, h));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            foreach (var a in _annos)
            {
                switch (a)
                {
                    case MosaicAnno m:
                        foreach (var ((cx, cy), c) in m.Cells)
                            dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(cx * m.Block - selP.X, cy * m.Block - selP.Y, m.Block, m.Block));
                        break;
                    case TextAnno t:
                        var ft = new FormattedText(t.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                            new Typeface((FontFamily)FindResource("UiFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                            t.Size * s, new SolidColorBrush(t.Color), 1.0);
                        dc.DrawText(ft, new Point(t.At.X * s - selP.X, t.At.Y * s - selP.Y));
                        break;
                }
            }
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    // long screenshot

    void Long_Click(object sender, RoutedEventArgs e)
    {
        if (!HasSelection || HasAnnotations) return;
        _session.StartLong(this);
    }
}
