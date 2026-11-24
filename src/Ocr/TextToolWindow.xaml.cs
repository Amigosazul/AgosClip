using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AgosClip;

/// <summary>
/// Text recognised from a picture (editable, so a misread character can be fixed) with a translation pane
/// underneath; also used to translate a text from the clipboard history. Each call opens its own window.
/// </summary>
public partial class TextToolWindow : Window
{
    CancellationTokenSource? _work;
    bool _translatedCurrent;

    static Settings S => App.Current.Settings;
    internal string SourceText => Source.Text;

    TextToolWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => { HideSystemMenu(); Glass.Apply(this); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || e.Key == Key.System && e.SystemKey == Key.F4) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { Translate_Click(this, new RoutedEventArgs()); e.Handled = true; }
        };
        Closed += (_, _) => _work?.Cancel();
        ShowTarget();
        CopyResultBtn.Opacity = 0.5;
        TranslateBtn.Visibility = TargetBtn.Visibility = S.EnableTranslate ? Visibility.Visible : Visibility.Collapsed;
        if (!S.EnableTranslate)
        {
            ResultCard.Visibility = CopyResultBtn.Visibility = Visibility.Collapsed;
            ResultRow.Height = new GridLength(0);
        }
        SetTop(true);
    }

    /// <summary>Opens a window and recognises the text in <paramref name="image"/>.</summary>
    public static TextToolWindow ShowOcr(BitmapSource image, Rect? near = null)
    {
        var w = new TextToolWindow { Title = "识别文字" };
        w.Heading.Text = "识别文字";
        w.Place(near);
        w.Show();
        w.Activate();
        _ = w.RunOcr(image);
        return w;
    }

    /// <summary>Opens a window with <paramref name="text"/> and translates it right away.</summary>
    public static TextToolWindow ShowTranslate(string text, Rect? near = null)
    {
        var w = new TextToolWindow { Title = "翻译" };
        w.Heading.Text = "翻译";
        w.Info.Text = $"{text.Length} 字";
        w.Source.Text = text;
        w.Place(near);
        w.Show();
        w.Activate();
        if (S.EnableTranslate) w.Translate_Click(w, new RoutedEventArgs());
        return w;
    }

    /// <summary>Beside the area the text came from if there is room, else centred on the screen under the mouse.</summary>
    void Place(Rect? near)
    {
        Native.GetCursorPos(out var pt);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pt.X, pt.Y));
        var dpi = VisualTreeHelperDpi();
        var wa = new Rect(screen.WorkingArea.X / dpi, screen.WorkingArea.Y / dpi, screen.WorkingArea.Width / dpi, screen.WorkingArea.Height / dpi);
        Width = Math.Min(Width, wa.Width * 0.9);
        Height = Math.Min(Height, wa.Height * 0.9);
        if (near is { } r)
        {
            var d = new Rect(r.X / dpi, r.Y / dpi, r.Width / dpi, r.Height / dpi);
            double right = d.Right + 12, left = d.Left - 12 - Width;
            if (right + Width <= wa.Right || left >= wa.Left)
            {
                Left = right + Width <= wa.Right ? right : left;
                Top = Math.Clamp(d.Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
                return;
            }
        }
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
    }

    /// <summary>System DPI scale (screen pixels per WPF unit).</summary>
    static double VisualTreeHelperDpi()
    {
        try { using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero); return g.DpiX / 96.0; }
        catch { return 1; }
    }

    async Task RunOcr(BitmapSource image)
    {
        if (!RapidOcr.Available)
        {
            ShowSourceMessage("文字识别插件还没有安装。\n在 设置 → 插件 里安装后就能用。");
            return;
        }
        var cts = Restart();
        ShowSourceMessage("正在识别文字…");
        Source.IsReadOnly = true;
        try
        {
            var r = await RapidOcr.RecognizeAsync(image, cts.Token);
            if (cts.IsCancellationRequested) return;
            Source.IsReadOnly = false;
            if (r.Lines.Count == 0) { ShowSourceMessage("没有识别到文字"); return; }
            HideSourceMessage();
            Source.Text = r.Text;
            Info.Text = $"{r.Lines.Count} 行 · {r.Elapsed.TotalSeconds:0.0} 秒 · 可以直接修改识别结果";
            Status.Text = "";
            Source.Focus();
            App.Log($"ocr: {image.PixelWidth}x{image.PixelHeight} → {r.Lines.Count} lines in {r.Elapsed.TotalMilliseconds:0} ms");
            if (S.OcrAutoCopy) Copy(Source.Text, "已复制识别结果");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Log("ocr failed: " + ex);
            Source.IsReadOnly = false;
            ShowSourceMessage("识别失败：" + ex.Message + "\n\n可以在 设置 → 插件 里重新安装文字识别插件。");
        }
    }

    CancellationTokenSource Restart()
    {
        _work?.Cancel();
        return _work = new CancellationTokenSource();
    }

    void ShowSourceMessage(string msg) { SourceMessage.Text = msg; SourceMessage.Visibility = Visibility.Visible; Source.Text = ""; }
    void HideSourceMessage() => SourceMessage.Visibility = Visibility.Collapsed;

    void Source_TextChanged(object sender, TextChangedEventArgs e)
    {
        _translatedCurrent = false;
        CopySourceBtn.IsEnabled = Source.Text.Length > 0;
        CopySourceBtn.Opacity = CopySourceBtn.IsEnabled ? 1 : 0.5;
    }

    async void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (!S.EnableTranslate) return;
        var text = Source.Text;
        if (string.IsNullOrWhiteSpace(text)) { Status.Text = "没有要翻译的文字"; return; }
        if (text.Length > 20000) { Status.Text = "文字太长，一次最多翻译 20000 字"; return; }
        if (_translatedCurrent && sender is not Button) return;
        var cts = Restart();
        ResultMessage.Text = "正在翻译…";
        ResultMessage.Visibility = Visibility.Visible;
        Result.Text = "";
        CopyResultBtn.IsEnabled = false;
        CopyResultBtn.Opacity = 0.5;
        TranslateBtn.IsEnabled = false;
        try
        {
            var to = Translator.Resolve(S.TranslateTarget, new[] { text });
            var (r, engine) = await Translator.TranslateAsync(new[] { text }, to, cts.Token);
            if (cts.IsCancellationRequested) return;
            Result.Text = r[0];
            ResultMessage.Visibility = Visibility.Collapsed;
            CopyResultBtn.IsEnabled = true;
            CopyResultBtn.Opacity = 1;
            _translatedCurrent = true;
            Status.Text = $"{engine} · 译为{Translator.NameOf(to)}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Log("translate failed: " + ex.Message);
            ResultMessage.Text = "翻译失败：" + ex.Message;
        }
        finally { TranslateBtn.IsEnabled = true; }
    }

    void ShowTarget() => TargetBtn.Content = "译为：" + Translator.NameOf(S.TranslateTarget) + "  ▾";

    void Target_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TargetBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var t in Translator.Targets)
        {
            var mi = new MenuItem { Header = t.Name, IsCheckable = true, IsChecked = t.Code == S.TranslateTarget };
            mi.Click += (_, _) =>
            {
                S.TranslateTarget = t.Code;
                App.Current.ApplySettings();
                ShowTarget();
                if (Source.Text.Length > 0) { _translatedCurrent = false; Translate_Click(TranslateBtn, new RoutedEventArgs()); }
            };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    void CopySource_Click(object sender, RoutedEventArgs e) => Copy(Source.Text, "已复制原文");
    void CopyResult_Click(object sender, RoutedEventArgs e) => Copy(Result.Text, "已复制译文");

    void Copy(string text, string done)
    {
        if (string.IsNullOrEmpty(text)) return;
        Status.Text = ClipboardWriter.Write(new ClipboardWriter.Payload().Text(text)) ? done : "复制失败，请再试一次";
    }

    void SetTop(bool on)
    {
        Topmost = on;
        TopBtn.Content = on ? "" : ""; // pinned / pin
        TopBtn.ToolTip = on ? "已置顶 · 点击取消置顶" : "未置顶 · 点击置顶";
    }

    void Top_Click(object sender, RoutedEventArgs e) => SetTop(!Topmost);
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void HideSystemMenu()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        const long WS_SYSMENU = 0x00080000;
        long style = (long)Native.GetWindowLongPtr(hwnd, -16);
        if ((style & WS_SYSMENU) == 0) return;
        Native.SetWindowLongPtr(hwnd, -16, (IntPtr)(style & ~WS_SYSMENU));
        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
    }
}
