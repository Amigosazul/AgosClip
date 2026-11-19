using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AgosClip;

/// <summary>
/// `AgosClip.exe --selftest &lt;outDir&gt;` with AGOSCLIP_DATA pointing at a scratch folder.
/// Exercises storage, real clipboard capture and paste-back, then renders the UI to PNGs.
/// </summary>
internal static class SelfTest
{
    static readonly StringBuilder Report = new();
    static int _fails;

    static void Check(bool ok, string name)
    {
        Report.AppendLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _fails++;
    }

    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        Dispatcher.PushFrame(frame);
    }

    static long At(int daysAgo, int h, int m) =>
        new DateTimeOffset(DateTime.Today.AddDays(-daysAgo).AddHours(h).AddMinutes(m)).ToUnixTimeMilliseconds();

    static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public static int Run(App app, string outDir, bool animationOnly = false)
    {
        // Self-tests must use an explicitly configured data directory, never the normal default.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGOSCLIP_DATA")))
            throw new InvalidOperationException("Self-test requires AGOSCLIP_DATA to point at an isolated test folder. Use tools/selftest.ps1.");
        Directory.CreateDirectory(outDir);
        Report.AppendLine("data dir: " + Settings.DataDir);
        try
        {
            app.Settings = new Settings();
            app.Store = new Store();
            Theme.Apply(false);
            if (!animationOnly)
            {
                StoreTests(app.Store);
                ClipboardTests(app);
                MoveTests(app);
                FeedbackTests(app);
            }
            Seed(app);
            AnimationTests(app, outDir);
            Snapshots(outDir);
            if (!animationOnly && Environment.GetEnvironmentVariable("AGOSCLIP_UPDATE_TEST") == "1") UpdateTest();
        }
        catch (Exception ex)
        {
            Check(false, "exception: " + ex);
        }
        Report.AppendLine(_fails == 0 ? "ALL PASSED" : $"{_fails} FAILED");
        File.WriteAllText(Path.Combine(outDir, "results.txt"), Report.ToString(), new UTF8Encoding(false));
        return _fails == 0 ? 0 : 1;
    }

    static void StoreTests(Store s)
    {
        s.ClearAll(false);
        s.Upsert(ClipKind.Text, "Ta", "hello", null, null, 0, 0, "记事本", At(3, 9, 0));
        Check(s.DayCounts().GetValueOrDefault(Today.AddDays(-3)) == 1, "item lands on its day");
        s.Upsert(ClipKind.Text, "Ta", "hello", null, null, 0, 0, null, At(0, 0, 1));
        var counts = s.DayCounts();
        Check(counts.Count == 1 && counts.GetValueOrDefault(Today) == 1, "identical content kept once and moved to today");
        var it = s.ItemsOnDay(Today).Single();
        Check(it.CopyCount == 2 && it.SourceApp == "记事本", "copy count bumped, source app kept");

        s.Upsert(ClipKind.Text, "Told", "old", null, null, 0, 0, null, At(40, 8, 0));
        s.Upsert(ClipKind.Text, "Tpin", "old pinned", null, null, 0, 0, null, At(40, 8, 5));
        var pin = s.ItemsOnDay(Today.AddDays(-40)).First(x => x.Text == "old pinned");
        s.SetPinned(pin.Id, true);
        s.Upsert(ClipKind.Text, "Tedge", "29 days ago", null, null, 0, 0, null, At(29, 0, 0));
        int removed = s.Cleanup(30);
        Check(removed == 1, $"cleanup(30) removes only the unpinned 40-day-old item (removed {removed})");
        Check(s.PinnedItems().Count == 1 && s.ItemsOnDay(Today.AddDays(-29)).Count == 1, "pinned and in-window items survive cleanup");
        Check(s.Cleanup(0) == 0, "retention 0 keeps forever");

        s.Upsert(ClipKind.Text, "Tpct", "打折 50% off", null, null, 0, 0, null, At(1, 9, 0));
        Check(s.Search("50%").Count == 1 && s.Search("5_%").Count == 0, "search escapes % and _");
        Check(s.Search("记事本").Count == 1, "search matches source app");
    }

    static BitmapSource MakeImage(int w, int h, string label, Color a, Color b)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(a, b, 30), null, new Rect(0, 0, w, h));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), null, new Rect(w * 0.08, h * 0.18, w * 0.6, h * 0.5), 12, 12);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(200, 255, 210, 80)), null, new Point(w * 0.8, h * 0.3), h * 0.14, h * 0.14);
            var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), h * 0.11, Brushes.Black, 1.0);
            dc.DrawText(ft, new Point(w * 0.12, h * 0.35));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    static ClipboardMonitor? _mon;

    static void ClipboardTests(App app)
    {
        var s = app.Store;
        s.ClearAll(false);
        _mon = new ClipboardMonitor(s, () => app.Settings);

        // A slow clipboard owner must not freeze the UI: block the reader thread for 3 s and
        // check the UI thread keeps answering.
        _mon.ReaderForTest.BeginInvoke(() => System.Threading.Thread.Sleep(3000));
        var sw0 = System.Diagnostics.Stopwatch.StartNew();
        double worst = 0;
        for (int i = 0; i < 10; i++)
        {
            var t0 = sw0.Elapsed.TotalMilliseconds;
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.Normal);
            Pump(100);
            worst = Math.Max(worst, sw0.Elapsed.TotalMilliseconds - t0 - 100);
        }
        Check(worst < 300, $"UI stays responsive while clipboard reading is blocked (worst lag {worst:0} ms)");
        Pump(2500); // let the reader finish its 3 s block

        var text = "AgosClip 剪贴板测试 " + Guid.NewGuid().ToString("N")[..8];
        Clipboard.SetText(text);
        Pump(700);
        Check(s.Search(text).Count == 1, "real clipboard text is captured");
        Clipboard.SetText(text);
        Pump(700);
        var hits = s.Search(text);
        Check(hits.Count == 1 && hits[0].CopyCount == 2, "copying the same text again keeps one entry");

        var secret = new DataObject();
        secret.SetText("password-should-not-be-saved");
        secret.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
        Clipboard.SetDataObject(secret, true);
        Pump(700);
        Check(s.Search("password-should-not-be-saved").Count == 0, "content flagged by password managers is skipped");

        Clipboard.SetImage(MakeImage(1200, 700, "截图测试", Colors.SteelBlue, Colors.MediumPurple));
        Pump(2500);
        var imgs = s.ItemsOnDay(Today).Where(i => i.Kind == ClipKind.Image).ToList();
        Check(imgs.Count == 1, "real clipboard image is captured");
        if (imgs.Count == 1)
        {
            var img = imgs[0];
            Check(File.Exists(Path.Combine(Settings.DataDir, img.ImagePath!)) && img.Width == 1200 && img.Height == 700,
                $"image file saved at full size ({img.Width}x{img.Height})");
            Check(img.ThumbPath != img.ImagePath && File.Exists(Path.Combine(Settings.DataDir, img.ThumbPath!)), "thumbnail saved");

            Clipboard.SetText("something else");
            Pump(500);
            Check(Paster.SetClipboard(img) && Clipboard.ContainsImage(), "paste-back puts the image on the clipboard");
            Pump(2500);
            Check(s.ItemsOnDay(Today).Count(i => i.Kind == ClipKind.Image) == 1, "re-copied image is not duplicated");

            var textItem = s.Search(text)[0];
            Check(Paster.SetClipboard(textItem) && Clipboard.GetText() == text, "paste-back puts text on the clipboard");
            Pump(700);
        }

        // Screenshot-tool style clipboards: DIB only (no PNG), plus an empty text format next to it.
        static MemoryStream Dib(BitmapSource b)
        {
            var enc = new BmpBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(b));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return new MemoryStream(ms.ToArray()[14..]); // strip BITMAPFILEHEADER → CF_DIB
        }
        int before = s.ItemsOnDay(Today).Count(i => i.Kind == ClipKind.Image);
        var shot = new DataObject();
        shot.SetData(DataFormats.Dib, Dib(MakeImage(640, 360, "DIB 截图", Colors.Teal, Colors.Navy)));
        Clipboard.SetDataObject(shot, true);
        Pump(2500);
        Check(s.ItemsOnDay(Today).Count(i => i.Kind == ClipKind.Image) == before + 1, "DIB-only screenshot is captured");
        var shot2 = new DataObject();
        shot2.SetData(DataFormats.Dib, Dib(MakeImage(500, 300, "带空文本的截图", Colors.Crimson, Colors.Gold)));
        shot2.SetData(DataFormats.UnicodeText, "");
        Clipboard.SetDataObject(shot2, true);
        Pump(2500);
        Check(s.ItemsOnDay(Today).Count(i => i.Kind == ClipKind.Image) == before + 2, "image next to an empty text format is captured");
        var log = Feedback.LogFile;
        Check(File.Exists(log) && File.ReadAllText(log).Contains("→ image 500x300"), "capture decisions are logged");

        app.Settings.RecordImages = false;
        Clipboard.SetImage(MakeImage(300, 200, "不记录", Colors.Orange, Colors.Red));
        Pump(1500);
        Check(s.ItemsOnDay(Today).Count(i => i.Kind == ClipKind.Image) == before + 2, "images ignored when 记录图片 is off");
        app.Settings.RecordImages = true;
    }

    static void Seed(App app)
    {
        _mon ??= new ClipboardMonitor(app.Store, () => app.Settings);
        var s = app.Store;
        s.ClearAll(false);
        var rows = new List<(int d, int h, int m, string text, string app)>
        {
            (0, 9, 12, "https://learn.microsoft.com/zh-cn/windows/win32/dataxchg/clipboard", "Microsoft Edge"),
            (0, 10, 3, "明天上午 10:00 产品评审会，地点：3 楼大会议室。\n请提前看一下需求文档第 2、3 章。", "微信"),
            (0, 11, 40, "git log --oneline --graph --decorate -n 20", "Windows Terminal"),
            (0, 14, 25, "public static string DataDir { get; } =\n    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), \"AgosClip\");", "Visual Studio Code"),
            (0, 15, 8, "快递单号 SF1234567890123", "Microsoft Edge"),
            (1, 8, 30, "周报：\n1. 完成剪贴板历史的存储层\n2. 接管 Win+V 的方案验证\n3. 设置页：保留天数、开机启动", "记事本"),
            (1, 13, 2, "D:\\Projects\\AgosClip\\src", "文件资源管理器"),
            (1, 16, 45, "SELECT date(created_at/1000,'unixepoch','localtime') d, count(*) FROM items GROUP BY d", "DBeaver"),
            (2, 19, 20, "番茄炒蛋：番茄 2 个、鸡蛋 3 个、糖半勺、盐少许。先炒蛋盛出，再炒番茄出汁后回锅。", "小红书"),
            (4, 10, 0, "Thanks for the update — let's sync on Friday.", "Outlook"),
            (6, 9, 15, "滴滴出行 行程单 2026-11-12", "微信"),
            (9, 17, 30, "黄浦江夜景 摄影参数：f/8，ISO 100，30s", "Lightroom"),
            (12, 11, 11, "Ctrl+Shift+Esc 打开任务管理器", "记事本"),
        };
        foreach (var (d, h, m, text, src) in rows)
            s.Upsert(ClipKind.Text, Store.TextHash(text), text, null, null, 0, 0, src, At(d, h, m));

        var img1 = MakeImage(1280, 720, "设计稿 v3", Color.FromRgb(70, 120, 230), Color.FromRgb(150, 90, 220));
        var (h1, r1, t1) = _mon!.SaveImage(img1);
        s.Upsert(ClipKind.Image, h1, null, r1, t1, 1280, 720, "Figma", At(0, 16, 2));
        var img2 = MakeImage(900, 600, "报表截图", Color.FromRgb(40, 160, 120), Color.FromRgb(30, 90, 160));
        var (h2, r2, t2) = _mon.SaveImage(img2);
        s.Upsert(ClipKind.Image, h2, null, r2, t2, 900, 600, "截图工具", At(1, 10, 20));

        s.Upsert(ClipKind.Text, Store.TextHash("git log --oneline --graph --decorate -n 20"), "git log --oneline --graph --decorate -n 20", null, null, 0, 0, null, At(0, 16, 30));
        foreach (var it in s.ItemsOnDay(Today).Where(i => i.Text?.StartsWith("快递") == true)) s.SetPinned(it.Id, true);
        foreach (var it in s.ItemsOnDay(Today.AddDays(-12))) s.SetPinned(it.Id, true);
        _mon.Dispose();
    }

    static void Render(Visual v, double w, double h, string path, double scale = 1.5)
    {
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(v);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
        Report.AppendLine("wrote " + path);
    }

    /// <summary>A busy fake desktop (wallpaper + window shapes + text) to judge the glass against.</summary>
    static FrameworkElement FakeDesktop(double w, double h, bool dark)
    {
        var c = new System.Windows.Controls.Canvas { Width = w, Height = h };
        var wall = new LinearGradientBrush(dark ? Color.FromRgb(20, 30, 70) : Color.FromRgb(255, 170, 120),
                                           dark ? Color.FromRgb(80, 20, 90) : Color.FromRgb(90, 150, 255), 35);
        c.Children.Add(new System.Windows.Shapes.Rectangle { Width = w, Height = h, Fill = wall });
        void Blob(double x, double y, double r, Color col)
        {
            var e = new System.Windows.Shapes.Ellipse { Width = r, Height = r, Fill = new SolidColorBrush(col) };
            System.Windows.Controls.Canvas.SetLeft(e, x); System.Windows.Controls.Canvas.SetTop(e, y);
            c.Children.Add(e);
        }
        Blob(-60, 300, 260, Color.FromRgb(255, 90, 140));
        Blob(220, -40, 220, Color.FromRgb(80, 220, 200));
        Blob(260, 380, 200, Color.FromRgb(255, 220, 80));
        var win = new System.Windows.Controls.Border
        {
            Width = 300, Height = 260, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(dark ? Color.FromRgb(40, 40, 44) : Colors.White),
            Child = new System.Windows.Controls.TextBlock
            {
                Text = "Lorem ipsum 文本编辑器\n第二行内容 1234567890\n" + new string('█', 12),
                FontSize = 22, Margin = new Thickness(16), Foreground = dark ? Brushes.White : Brushes.Black,
            },
        };
        System.Windows.Controls.Canvas.SetLeft(win, 40); System.Windows.Controls.Canvas.SetTop(win, 90);
        c.Children.Add(win);
        return c;
    }

    /// <summary>Approximates the DWM acrylic: blur + tint of what is behind, then the panel on top.</summary>
    static void RenderOnGlass(FrameworkElement content, string path, bool dark, bool glass)
    {
        double w = content.ActualWidth, h = content.ActualHeight;
        var g = new System.Windows.Controls.Grid { Width = w, Height = h, ClipToBounds = true };
        if (glass)
        {
            var desk = FakeDesktop(w + 120, h + 120, dark);
            desk.Margin = new Thickness(-60);
            desk.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 60, KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
            g.Children.Add(desk);
            g.Children.Add(new System.Windows.Shapes.Rectangle { Fill = new SolidColorBrush(dark ? Color.FromArgb(150, 32, 32, 32) : Color.FromArgb(140, 246, 246, 246)) });
        }
        else g.Children.Add(new System.Windows.Shapes.Rectangle { Fill = (Brush)Application.Current.Resources["WindowBg"] });
        g.Children.Add(new System.Windows.Shapes.Rectangle { Fill = new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top } });
        g.Measure(new Size(w, h));
        g.Arrange(new Rect(0, 0, w, h));
        g.UpdateLayout();
        Render(g, w, h, path);
    }

    static void AnimationTests(App app, string outDir)
    {
        var panel = new PanelWindow();
        // Keep synthetic focus changes out of a real in-progress IME composition on the user's desktop.
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(panel.SearchBox, false);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(panel).Handle;
        var settings = app.Settings;
        int? oldX = settings.PanelX, oldY = settings.PanelY;
        Native.GetCursorPos(out var cursor);
        var mon = Native.MonitorFromPoint(cursor, 2);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref mi);
        var progressProperty = (DependencyProperty)typeof(PanelWindow).GetField("AnimProgressProperty",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var expandDown = typeof(PanelWindow).GetField("_expandDown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        void Seek(double value) => panel.SetValue(progressProperty, value);
        Native.RECT Rect() { Native.GetWindowRect(hwnd, out var r); return r; }
        try
        {
            Theme.Apply(false);
            settings.PanelX = mi.rcWork.Left + 100;
            settings.PanelY = mi.rcWork.Top + 60;
            int firstVisibleHeight = 0;
            panel.IsVisibleChanged += (_, _) =>
            {
                if (panel.IsVisible && firstVisibleHeight == 0) { var r = Rect(); firstVisibleHeight = r.Bottom - r.Top; }
            };
            panel.ShowFor(IntPtr.Zero);
            Check(!SystemParameters.ClientAreaAnimation || firstVisibleHeight is > 0 and <= 2,
                $"open is collapsed before the first visible frame ({firstVisibleHeight}px)");
            Pump(320);
            var full = Rect();
            Check(panel.IsVisible && !panel.IsClosing && full.Bottom - full.Top >= 580,
                "open completes at full height");
            var viewport = (FrameworkElement)panel.Content;
            double viewportHeight = viewport.ActualHeight, rootHeight = panel.Root.ActualHeight;

            // Deterministic checkpoints use the actual native rect and WPF layout, independent of frame rate.
            foreach (bool down in new[] { true, false })
            {
                expandDown.SetValue(panel, down);
                // PlaceNear uses a remembered spot; select the edge afterwards to sample either direction.
                panel.Root.VerticalAlignment = down ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                Seek(0.5);
                Pump(40);
                var half = Rect();
                Check(Math.Abs((half.Bottom - half.Top) * 2 - (full.Bottom - full.Top)) <= 2 &&
                    (down ? half.Top == full.Top : half.Bottom == full.Bottom),
                    $"{(down ? "downward" : "upward")} reveal holds its anchor and halves the native height");
                Check(Math.Abs(panel.Root.ActualHeight - rootHeight) < 1 &&
                    viewport.ActualHeight < viewportHeight * 0.6,
                    "content keeps full layout inside the shrinking viewport");
                var offset = panel.Root.TranslatePoint(new Point(), viewport).Y;
                Check(down ? Math.Abs(offset) < 1 : offset < -rootHeight * 0.35,
                    "content is revealed from the correct edge without reflow");
                RenderOnGlass(viewport, Path.Combine(outDir, down ? "reveal-down-half.png" : "reveal-up-half.png"), false, true);
                Seek(1);
                Pump(30);
            }

            if (SystemParameters.ClientAreaAnimation)
            {
                panel.HidePanel();
                Pump(55);
                int closingHeight = Rect().Bottom - Rect().Top;
                panel.ShowFor(IntPtr.Zero);
                var reversed = Rect();
                Check(panel.IsVisible && !panel.IsClosing && Math.Abs(reversed.Bottom - reversed.Top - closingHeight) <= 2,
                    "reopening during close continues from the current height");
                Pump(320);
                Check(panel.IsVisible && !panel.IsClosing && Rect().Bottom - Rect().Top == full.Bottom - full.Top,
                    "a replaced close cannot hide the reopened panel");

                var beforeHide = new List<int> { Rect().Bottom - Rect().Top };
                System.Windows.Interop.HwndSourceHook sample = (IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (message == 0x0047 && panel.IsVisible) // WM_WINDOWPOSCHANGED, independent of compositor frame cadence
                    {
                        var r = Rect();
                        beforeHide.Add(r.Bottom - r.Top);
                    }
                    return IntPtr.Zero;
                };
                var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd)!;
                source.AddHook(sample);
                try { panel.HidePanel(); Pump(300); }
                finally { source.RemoveHook(sample); }
                Check(!panel.IsVisible && beforeHide.Count > 1 &&
                    beforeHide.Zip(beforeHide.Skip(1), (a, b) => b <= a).All(x => x),
                    $"close shrinks monotonically and hides without restoring full size ({string.Join(",", beforeHide)})");

                panel.ShowFor(IntPtr.Zero);
                Pump(35);
                panel.HidePanel(); // interrupt opening, then reverse again before it finishes closing
                panel.ShowFor(IntPtr.Zero);
                Pump(320);
                Check(panel.IsVisible && !panel.IsClosing && Rect().Bottom - Rect().Top == full.Bottom - full.Top,
                    "rapid open-close-open settles fully open");
                panel.Snapshot = true;
                panel.HidePanel();
                Check(!panel.IsVisible, "instant snapshot close cancels the live animation state");
                panel.Snapshot = false;
            }
            else { panel.HidePanel(); Check(!panel.IsVisible, "system animation preference closes instantly"); }

            // A real placement near the bottom must choose the upward anchor.
            settings.PanelX = settings.PanelY = null;
            Native.SetCursorPos(mi.rcWork.Left + 100, mi.rcWork.Bottom - 15);
            panel.ShowFor(IntPtr.Zero);
            Pump(320);
            Check(!(bool)expandDown.GetValue(panel)! && panel.Root.VerticalAlignment == VerticalAlignment.Bottom,
                "placement near the work-area bottom unfolds upward");
            panel.HidePanel();
            Pump(240);
        }
        finally
        {
            panel.Close();
            settings.PanelX = oldX; settings.PanelY = oldY;
            Native.SetCursorPos(cursor.X, cursor.Y);
        }
    }

    static void Snapshots(string outDir)
    {
        foreach (var (dark, glass) in new[] { (false, true), (true, true), (false, false) })
        {
            Theme.Apply(dark, glass);
            var tag = (dark ? "dark" : "light") + (glass ? "" : "-solid");
            var panel = new PanelWindow();
            panel.PrepareSnapshot(-1);
            panel.Show();
            Pump(500);
            var root = (FrameworkElement)panel.Content;
            RenderOnGlass(root, Path.Combine(outDir, $"panel-today-{tag}.png"), dark, glass);

            if (!dark && glass)
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(panel).Handle;
                long style = (long)Native.GetWindowLongPtr(hwnd, -16);
                Check((style & 0x00080000L) == 0, "panel has no system menu, so no stray caption ✕");
                // click the 固定 card with the mouse (index 0) → it must become selected
                var item = (System.Windows.Controls.ListBoxItem)panel.DayList.ItemContainerGenerator.ContainerFromIndex(0);
                item.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                Pump(200);
                Check(panel.DayList.SelectedIndex == 0 && panel.SectionTitle.Text.StartsWith("固定"), "clicking a date card selects it (固定)");
                panel.PrepareSnapshot(-1);
                Pump(200);

                // pin to desktop
                panel.StickyBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(panel.IsSticky && (string)panel.StickyBtn.Content == "\uE840", "pin button turns pin mode on");
                Pump(200);
                RenderOnGlass(root, Path.Combine(outDir, "panel-pinned-mode-light.png"), dark, glass);
                panel.HidePanel();
                Check(!panel.IsSticky, "closing the panel un-pins it");
                panel.Show();
                Pump(200);

                // touchpad: 10 tiny wheel events (Delta 8 each, like a precision touchpad) must scroll a little, not to the end
                var isv = SmoothWheel.FindScrollViewer(panel.ItemList)!;
                isv.ScrollToTop();
                Pump(100);
                for (int i = 0; i < 10; i++)
                    panel.ItemList.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, -8)
                        { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                Pump(100);
                Check(isv.ScrollableHeight > 150 && isv.VerticalOffset > 10 && isv.VerticalOffset < 90,
                    $"touchpad scroll moves proportionally ({isv.VerticalOffset:0}px of {isv.ScrollableHeight:0}px)");
                isv.ScrollToTop();
                Pump(100);
                panel.ItemList.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, -120)
                    { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                Pump(100);
                Check(isv.VerticalOffset > 30 && isv.VerticalOffset < 90, $"one mouse-wheel notch scrolls about 3 lines ({isv.VerticalOffset:0}px)");
                isv.ScrollToTop();

                // remembered position
                var st = App.Current.Settings;
                st.PanelX = 120; st.PanelY = 90;
                panel.PlaceNear(IntPtr.Zero);
                Native.GetWindowRect(hwnd, out var r1);
                Check(r1.Left == 120 && r1.Top == 90, $"opens where it was dragged to ({r1.Left},{r1.Top})");
                st.PanelX = 50000; st.PanelY = 50000;
                panel.PlaceNear(IntPtr.Zero);
                Native.GetWindowRect(hwnd, out var r2);
                Check(r2.Left < 10000, "a remembered spot on an unplugged monitor is ignored");
                st.PanelX = st.PanelY = null;
            }

            if (glass)
            {
                panel.PrepareSnapshot(2); // yesterday (index 0 = 固定, 1 = 今天)
                Pump(300);
                RenderOnGlass(root, Path.Combine(outDir, $"panel-yesterday-{tag}.png"), dark, glass);
                panel.PrepareSnapshot(-1, null, openCalendar: true);
                Pump(300);
                RenderOnGlass(root, Path.Combine(outDir, $"calendar-{tag}.png"), dark, glass);
            }
            if (!dark && glass)
            {
                panel.PrepareSnapshot(-1, "git");
                Pump(300);
                RenderOnGlass(root, Path.Combine(outDir, "panel-search-light.png"), dark, glass);
            }
            panel.Close();

            if (glass)
            {
                var sw = new SettingsWindow { Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
                sw.Show();
                Pump(400);
                Check(sw.Content is FrameworkElement, "settings window builds");
                string[] names = { "general", "history", "storage", "update", "about" };
                for (int pi = 0; pi < names.Length; pi++)
                {
                    if (dark && pi > 0) break; // dark: just the first page
                    sw.ShowPage(pi);
                    Pump(250);
                    RenderOnGlass((FrameworkElement)sw.Content, Path.Combine(outDir, $"settings-{names[pi]}-{tag}.png"), dark, glass);
                }
                sw.Close();
            }
        }
        Check(true, "snapshots rendered");
    }

    /// <summary>Live check against GitHub Releases (opt-in: needs network). Downloads and verifies, never installs.</summary>
    static void UpdateTest()
    {
        var t = Updater.CheckAsync();
        while (!t.IsCompleted) Pump(100);
        var u = t.Result;
        Check(u != null, $"update check finds a newer release than {Updater.Current} ({u?.Version})");
        if (u == null) return;
        Check(u.Sha256?.Length == 64, "update has a trusted SHA-256 (GitHub API digest / latest.json)");
        string lastSource = "";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var d = Updater.DownloadAsync(u, new Progress<DownloadProgress>(p => { if (p.Note == null && p.Source.Length > 0) lastSource = p.Source; }));
        while (!d.IsCompleted) Pump(200);
        if (d.IsFaulted) { Check(false, "download: " + d.Exception!.InnerException!.Message); return; }
        Check(File.Exists(d.Result) && new FileInfo(d.Result).Length == u.Size,
            $"installer downloaded via {lastSource} and SHA-256 verified ({u.Size / 1048576.0:0.#} MB in {sw.Elapsed.TotalSeconds:0} s)");
        File.Delete(d.Result);
    }

    static void Await(Task t)
    {
        while (!t.IsCompleted) Pump(50);
        if (t.IsFaulted) throw t.Exception!.InnerException!;
    }

    static void MoveTests(App app)
    {
        var s = app.Store;
        int before = s.DayCounts().Values.Sum();
        var images = s.ItemsOnDay(Today).Where(i => i.Kind == ClipKind.Image).ToList();
        var picked = Settings.ConfigDir + "-picked";
        if (Directory.Exists(picked)) Directory.Delete(picked, true);
        Directory.CreateDirectory(picked);
        File.WriteAllText(Path.Combine(picked, "someone-elses-file.txt"), "x");
        var target = DataMover.ResolveTarget(picked);
        Check(target == Path.Combine(picked, "AgosClip"), "non-empty folder → data goes into an AgosClip subfolder");
        Check(DataMover.Validate(Path.Combine(Settings.DataDir, "images")) != null, "moving into the current data folder is refused");

        Await(DataMover.MoveAsync(s, app.Settings, target, false, null));
        Check(Settings.DataDir == target && DataMover.HasData(target) && !DataMover.HasData(Settings.ConfigDir),
            "data moved to the new folder and removed from the old one");
        Check(s.DayCounts().Values.Sum() == before, $"all {before} items readable after the move");
        Check(images.Count > 0 && images.All(i => File.Exists(Path.Combine(target, i.ImagePath!))), "image files moved along");
        Check(File.Exists(Path.Combine(Settings.ConfigDir, "settings.json")) &&
              File.ReadAllText(Path.Combine(Settings.ConfigDir, "settings.json")).Contains("-picked"), "settings stay in the config folder and remember the location");
        App.Log("log line after move");
        Check(File.Exists(Path.Combine(target, "agosclip.log")) && !File.Exists(Path.Combine(Settings.ConfigDir, "agosclip.log"))
              && File.ReadAllText(Path.Combine(target, "agosclip.log")).Contains("data moved"), "log moved along with the data");
        s.Upsert(ClipKind.Text, Store.TextHash("written after move"), "written after move", null, null, 0, 0, null);
        Check(s.Search("written after move").Count == 1, "new items are written in the new folder");

        // reload from disk like a restart
        var reloaded = Settings.Load();
        Check(reloaded.ApplyDataLocation() && Settings.DataDir == target, "location survives a restart");

        Await(DataMover.MoveAsync(s, app.Settings, Settings.ConfigDir, false, null));
        Check(Settings.IsDefaultLocation(Settings.DataDir) && app.Settings.DataLocation == null && s.DayCounts().Values.Sum() == before + 1,
            "moving back to the default location keeps everything");
        Check(!Directory.Exists(target) && File.Exists(Path.Combine(picked, "someone-elses-file.txt")),
            "old custom folder removed; unrelated files untouched");
        Directory.Delete(picked, true);
    }

    static void LogLimitTest()
    {
        var saved = App.LogLimitBytes;
        App.LogLimitBytes = 8 * 1024;
        for (int i = 0; i < 400; i++) App.Log($"filler line {i} " + new string('x', 40));
        long size() => new[] { Feedback.LogFile, Feedback.OldLogFile }.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        Check(size() <= 8 * 1024 + 200, $"log size stays under the limit ({size()} bytes for 8 KB)");
        Check(File.ReadAllText(Feedback.LogFile).Contains("filler line 399"), "newest log lines are kept");
        App.LogLimitBytes = 2 * 1024;
        App.TrimLogs();
        Check(size() <= 2 * 1024 + 200, $"lowering the limit trims right away ({size()} bytes for 2 KB)");
        App.LogLimitBytes = saved;
    }

    static void FeedbackTests(App app)
    {
        LogLimitTest();
        var secretText = "SECRET-CLIP-" + Guid.NewGuid().ToString("N")[..6];
        app.Store.Upsert(ClipKind.Text, Store.TextHash(secretText), secretText, null, null, 0, 0, "记事本");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        App.Log($"test line with a personal path {profile}\\Documents and pc {Environment.MachineName}");
        var path = Feedback.WriteDiagnostics();
        var txt = File.ReadAllText(path);
        Check(txt.Contains("== Log") && txt.Contains("AgosClip " + Updater.Current.ToString(3)), "diagnostics file has version and log");
        Check(!txt.Contains(secretText), "diagnostics contain no clipboard content");
        Check(!txt.Contains(profile, StringComparison.OrdinalIgnoreCase) && !txt.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
              && txt.Contains("%USERPROFILE%"), "user folder and PC name are redacted");
        var url = Feedback.IssueUrl(Path.GetFileName(path));
        Check(url.StartsWith("https://github.com/Amigosazul/AgosClip/issues/new?title=") && url.Length < 6000, $"issue link is valid ({url.Length} chars)");
        File.Copy(path, Path.Combine(Path.GetDirectoryName(Settings.ConfigDir)!, "agosclip-selftest", "diagnostics-sample.txt"), true);
        File.Delete(path);
    }
}
