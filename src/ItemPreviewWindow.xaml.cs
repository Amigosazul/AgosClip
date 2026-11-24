using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace AgosClip;

/// <summary>
/// Issue #10 preview, in its own window (not inside the clipboard panel): full text, images with
/// fit / 100% / zoom, text and image files, a clear message for missing files. Never touches the
/// clipboard. One instance is reused; Esc / Space close it and hand the focus back to the panel.
/// </summary>
public partial class ItemPreviewWindow : Window
{
    static ItemPreviewWindow? _current;
    /// <summary>The shared preview window (created on first use).</summary>
    public static ItemPreviewWindow Current => _current ??= new ItemPreviewWindow();
    internal static bool IsOpen => _current is { IsVisible: true };

    ItemPreviewWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => { HideSystemCaptionButtons(); Glass.Apply(this); };
        StateChanged += (_, _) => { HideSystemCaptionButtons(); MaxBtn.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922"; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.System && e.SystemKey == Key.F4) { ClosePreview(); e.Handled = true; return; }
            if (HandlePreviewKey(e.Key, (Keyboard.Modifiers & ModifierKeys.Control) != 0)) e.Handled = true;
        };
        Closing += (_, e) => { e.Cancel = true; ClosePreview(); }; // keep the instance; just hide it
    }

    /// <summary>Opens next to the panel (right, else left), else centred on its screen; 900×700 at most 85% of the screen.</summary>
    internal void PlaceBeside(Window? panel)
    {
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(900, wa.Width * 0.85);
        Height = Math.Min(700, wa.Height * 0.85);
        if (panel is { IsVisible: true })
        {
            double right = panel.Left + panel.ActualWidth + 12, left = panel.Left - 12 - Width;
            Left = right + Width <= wa.Right ? right : left >= wa.Left ? left : wa.Left + (wa.Width - Width) / 2;
            Top = Math.Clamp(panel.Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        }
        else
        {
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
        }
    }

    // ───────────── window chrome ─────────────

    void HideSystemCaptionButtons()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        const long WS_SYSMENU = 0x00080000;
        long style = (long)Native.GetWindowLongPtr(hwnd, -16);
        if ((style & WS_SYSMENU) == 0) return;
        Native.SetWindowLongPtr(hwnd, -16, (IntPtr)(style & ~WS_SYSMENU));
        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
    }

    void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ───────────── preview (issue #10) ─────────────

    internal bool PreviewOpen => IsVisible;
    /// <summary>Image zoom; null = fit the viewport (never upscaled).</summary>
    double? _zoom;
    internal string PreviewMode { get; private set; } = "";

    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp" };
    static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".log", ".csv", ".tsv", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".toml",
        ".html", ".htm", ".css", ".js", ".ts", ".jsx", ".tsx", ".cs", ".py", ".java", ".c", ".h", ".cpp", ".go", ".rs", ".sql",
        ".sh", ".ps1", ".bat", ".cmd", ".srt", ".properties", ".gitignore",
    };
    const long MaxPreviewText = 2 * 1024 * 1024;

    internal void OpenPreview(ItemVm vm)
    {
        var item = vm.Item;
        PreviewTitle.Text = item.Kind switch { ClipKind.Image => "图片", ClipKind.Files => vm.FilesTitle, _ => "文本" };
        PreviewInfo.Text = vm.Meta;
        switch (item.Kind)
        {
            case ClipKind.Image:
                var path = item.ImagePath == null ? null : System.IO.Path.Combine(Settings.DataDir, item.ImagePath);
                if (!ShowPreviewImage(path)) ShowPreviewMessage("图片文件已不存在，无法预览");
                break;
            case ClipKind.Files:
                PreviewFiles(item.Paths);
                break;
            default:
                ShowPreviewText(item.Text ?? "");
                int len = (item.Text ?? "").Length; // the meta line already shows the length past 300
                PreviewInfo.Text = $"{vm.Meta}{(len > 300 ? "" : $"  ·  {len} 字")}  ·  {LineCount(item.Text)} 行";
                break;
        }
        ShowPluginButtons();
        Title = $"预览 · {PreviewTitle.Text}";
        if (!IsVisible) { Show(); }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Native.ForceForeground(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        if (PreviewMode == "text") { PreviewText.Focus(); Keyboard.Focus(PreviewText); PreviewText.CaretIndex = 0; PreviewText.ScrollToHome(); }
        else { Focus(); Keyboard.Focus(this); }
        if (PreviewMode == "image") Dispatcher.BeginInvoke(ApplyZoom, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>识别文字 for a picture (OCR plugin installed), 翻译 for text (translation on).</summary>
    void ShowPluginButtons()
    {
        OcrBtn.Visibility = PreviewMode == "image" && RapidOcr.Available ? Visibility.Visible : Visibility.Collapsed;
        TranslateBtn.Visibility = PreviewMode == "text" && App.Current.Settings.EnableTranslate && PreviewText.Text.Trim().Length > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    void Ocr_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source is System.Windows.Media.Imaging.BitmapSource img) TextToolWindow.ShowOcr(img);
    }

    void Translate_Click(object sender, RoutedEventArgs e)
    {
        // a selection translates just that part
        var text = PreviewText.SelectionLength > 0 ? PreviewText.SelectedText : PreviewText.Text;
        if (!string.IsNullOrWhiteSpace(text)) TextToolWindow.ShowTranslate(text);
    }

    static int LineCount(string? s) => string.IsNullOrEmpty(s) ? 0 : s.Count(c => c == '\n') + (s.EndsWith('\n') ? 0 : 1);

    void PreviewFiles(string[] paths)
    {
        if (paths.Length == 1)
        {
            var p = paths[0];
            if (!System.IO.File.Exists(p) && !System.IO.Directory.Exists(p)) { ShowPreviewMessage($"文件已被移动或删除：\n{p}"); return; }
            var ext = System.IO.Path.GetExtension(p);
            if (System.IO.File.Exists(p) && ImageExt.Contains(ext))
            {
                if (!ShowPreviewImage(p)) ShowPreviewMessage($"无法读取这张图片：\n{p}");
                return;
            }
            if (System.IO.File.Exists(p) && TextExt.Contains(ext))
            {
                try
                {
                    var info = new System.IO.FileInfo(p);
                    if (info.Length > MaxPreviewText) { ShowPreviewMessage($"文件太大（{info.Length / 1048576.0:0.#} MB），只预览 2 MB 以内的文本文件"); return; }
                    var text = ReadTextFile(p);
                    ShowPreviewText(text);
                    PreviewInfo.Text = $"{p}  ·  {LineCount(text)} 行";
                }
                catch (Exception ex) { ShowPreviewMessage($"无法读取这个文件：{ex.Message}"); }
                return;
            }
        }
        // anything else: list the entries with their state
        var sb = new System.Text.StringBuilder();
        foreach (var p in paths)
        {
            bool dir = System.IO.Directory.Exists(p), file = System.IO.File.Exists(p);
            string state = dir ? "文件夹" : file ? FormatSize(new System.IO.FileInfo(p).Length) : "已不存在";
            sb.Append(p).Append("    (").Append(state).Append(")\n");
        }
        ShowPreviewText(sb.ToString().TrimEnd());
        PreviewInfo.Text = $"{paths.Length} 项 · 这类文件不能直接预览，下面是路径";
    }

    static string FormatSize(long n) => n < 1024 ? $"{n} B" : n < 1048576 ? $"{n / 1024.0:0.#} KB" : $"{n / 1048576.0:0.#} MB";

    /// <summary>UTF-8 (with or without BOM) / UTF-16 by BOM; text that isn't valid UTF-8 is read as GBK.</summary>
    internal static string ReadTextFile(string path)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF))
            return new System.IO.StreamReader(new System.IO.MemoryStream(bytes), true).ReadToEnd();
        try
        {
            var s = new System.Text.UTF8Encoding(false, true).GetString(bytes);
            return s.Length > 0 && s[0] == '﻿' ? s[1..] : s;
        }
        catch (System.Text.DecoderFallbackException)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(936).GetString(bytes);
        }
    }

    void SetPreviewMode(string mode)
    {
        PreviewMode = mode;
        PreviewImageScroller.Visibility = mode == "image" ? Visibility.Visible : Visibility.Collapsed;
        PreviewText.Visibility = mode == "text" ? Visibility.Visible : Visibility.Collapsed;
        PreviewMessage.Visibility = mode == "message" ? Visibility.Visible : Visibility.Collapsed;
        PreviewZoomIn.Visibility = PreviewZoomOut.Visibility = PreviewZoomText.Visibility =
            mode == "image" ? Visibility.Visible : Visibility.Collapsed;
        if (mode != "image") PreviewImage.Source = null; // let a large bitmap go
        if (mode != "text") PreviewText.Clear();
    }

    void ShowPreviewText(string text) { SetPreviewMode("text"); PreviewText.Text = text; }
    void ShowPreviewMessage(string msg) { SetPreviewMode("message"); PreviewMessage.Text = msg; }

    bool ShowPreviewImage(string? path)
    {
        if (path == null || !System.IO.File.Exists(path)) return false;
        try
        {
            var b = new System.Windows.Media.Imaging.BitmapImage();
            b.BeginInit();
            b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            b.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            SetPreviewMode("image");
            PreviewImage.Source = b;
            // show pixels 1:1 in physical pixels at 100% (the bitmap's DPI must not shrink or grow it)
            PreviewImage.Width = b.PixelWidth / DpiScale;
            PreviewImage.Height = b.PixelHeight / DpiScale;
            _zoom = null;
            ApplyZoom();
            return true;
        }
        catch { return false; }
    }

    double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    double FitZoom
    {
        get
        {
            if (PreviewImage.Source == null) return 1;
            double vw = Math.Max(1, PreviewImageScroller.ActualWidth - 4), vh = Math.Max(1, PreviewImageScroller.ActualHeight - 4);
            if (PreviewImageScroller.ActualWidth <= 0) { vw = Math.Max(1, Width - 40); vh = Math.Max(1, Height - 110); }
            return Math.Min(1, Math.Min(vw / PreviewImage.Width, vh / PreviewImage.Height));
        }
    }

    internal double CurrentZoom => _zoom ?? FitZoom;

    void ApplyZoom()
    {
        double z = CurrentZoom;
        PreviewScale.ScaleX = PreviewScale.ScaleY = z;
        PreviewZoomText.Content = _zoom == null ? "适应" : $"{Math.Round(z * 100)}%";
    }

    void Zoom(double factor)
    {
        _zoom = Math.Clamp(CurrentZoom * factor, 0.05, 8);
        ApplyZoom();
    }

    void ToggleZoom() { _zoom = _zoom == null ? 1 : null; ApplyZoom(); }

    void PreviewZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(1.25);
    void PreviewZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(0.8);
    void PreviewZoomToggle_Click(object sender, RoutedEventArgs e) => ToggleZoom();
    void PreviewImage_Click(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) { ToggleZoom(); e.Handled = true; } }
    void PreviewScroller_SizeChanged(object sender, SizeChangedEventArgs e) { if (_zoom == null) ApplyZoom(); }

    void PreviewImage_Wheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return; // plain wheel scrolls
        Zoom(e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    void PreviewClose_Click(object sender, RoutedEventArgs e) => ClosePreview();

    /// <summary>Hides the window (kept for reuse) and gives the focus back to the clipboard panel if it is open.</summary>
    internal void ClosePreview()
    {
        if (!PreviewOpen) return;
        Hide();
        SetPreviewMode("");
        Closed_?.Invoke();
    }

    /// <summary>Raised after the preview is hidden (the panel takes the focus back).</summary>
    internal event Action? Closed_;

    /// <summary>Esc / Space close. Image: + − 0 zoom. Everything else goes to the read-only text (select, Ctrl+C, scroll).</summary>
    internal bool HandlePreviewKey(Key key, bool ctrl)
    {
        switch (key)
        {
            case Key.Escape: ClosePreview(); return true;
            case Key.Space: ClosePreview(); return true; // like Quick Look: Space opens and closes
            case Key.OemPlus or Key.Add when PreviewMode == "image": Zoom(1.25); return true;
            case Key.OemMinus or Key.Subtract when PreviewMode == "image": Zoom(0.8); return true;
            case Key.D0 or Key.NumPad0 when PreviewMode == "image": ToggleZoom(); return true;
            case Key.T when !ctrl && TranslateBtn.IsVisible: Translate_Click(this, new RoutedEventArgs()); return true;
            case Key.Enter or Key.Delete or Key.F2: return true; // never paste/delete/edit underneath
            case Key.P or Key.F when ctrl: return true;
            default: return false;
        }
    }

}
