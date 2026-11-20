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

    /// <summary>
    /// False in a non-interactive session (e.g. over SSH, session 0): cursor and foreground APIs fail with
    /// ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION, so tests that move the mouse or need focus are skipped.
    /// </summary>
    static bool InteractiveDesktop => Native.GetCursorPos(out _);

    static void Skip(string name) => Report.AppendLine("SKIP " + name + " (needs an interactive desktop)");

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

    static string _outDir = ".";

    public static int Run(App app, string outDir, bool animationOnly = false)
    {
        _outDir = outDir;
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
                IssueTests(app);
                Issue8To13Tests(app, outDir);
                PasteContentionTest(app);
            }
            Seed(app);
            PlacementTests();
            AnimationTests(outDir);
            Snapshots(outDir);
            if (!animationOnly && Environment.GetEnvironmentVariable("AGOSCLIP_UPDATE_TEST") == "1") UpdateTest();
            if (Environment.GetEnvironmentVariable("AGOSCLIP_BENCH") == "1") OpenBenchmark(app);
        }
        catch (Exception ex)
        {
            Check(false, "exception: " + ex);
        }
        Report.AppendLine(_fails == 0 ? "ALL PASSED" : $"{_fails} FAILED");
        File.WriteAllText(Path.Combine(outDir, "results.txt"), Report.ToString(), new UTF8Encoding(false));
        return _fails == 0 ? 0 : 1;
    }

    /// <summary>Focused WPF search checks; uses scratch data and never reads/writes the clipboard.</summary>
    public static int RunSearchUi(App app, string outDir)
    {
        Directory.CreateDirectory(outDir);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGOSCLIP_DATA")))
        {
            Check(false, "--selftest-search requires AGOSCLIP_DATA pointing at a scratch folder");
        }
        else if (Directory.Exists(Settings.ConfigDir) && Directory.EnumerateFileSystemEntries(Settings.ConfigDir).Any())
        {
            Check(false, "--selftest-search requires an empty AGOSCLIP_DATA folder");
        }
        else try
        {
            app.Settings = new Settings();
            app.Store = new Store();
            app.Store.Upsert(ClipKind.Text, "Tsearch-today", "today clipboard entry", null, null, 0, 0, "记事本", At(0, 10, 0));
            app.Store.Upsert(ClipKind.Text, "Tsearch-old", "git log --oneline", null, null, 0, 0, "Terminal", At(1, 10, 0));
            app.Store.Upsert(ClipKind.Text, "Tsearch-source", "meeting notes", null, null, 0, 0, "微信", At(1, 11, 0));
            foreach (bool dark in new[] { false, true }) SearchUiTests(outDir, dark);
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        finally { app.Store?.Dispose(); }
        Report.AppendLine(_fails == 0 ? "ALL PASSED" : $"{_fails} FAILED");
        File.WriteAllText(Path.Combine(outDir, "results.txt"), Report.ToString(), new UTF8Encoding(false));
        return _fails == 0 ? 0 : 1;
    }

    static void SearchUiTests(string outDir, bool dark)
    {
        Theme.Apply(dark, true);
        var tag = dark ? "dark" : "light";
        var panel = new PanelWindow();
        try
        {
            panel.ShowFor(IntPtr.Zero);
            Pump(250);
            // Keep test windows open if desktop focus changes, while exercising normal search animations.
            panel.StickyBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var root = (FrameworkElement)panel.Content;
            bool FullyRevealed() => Math.Abs(panel.SearchReveal.Height - 41) < 0.01 &&
                Math.Abs(panel.SearchReveal.ActualHeight - 41) <= 1 / VisualTreeHelper.GetDpi(panel.SearchReveal).DpiScaleY;
            void ClickSearch() => panel.SearchButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            void Key(System.Windows.Input.Key key) => panel.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, key)
                { RoutedEvent = UIElement.PreviewKeyDownEvent });
            Check(panel.SearchReveal.Visibility == Visibility.Collapsed && !panel.SearchBox.IsEnabled,
                $"{tag}: search is collapsed by default");
            Check(panel.ItemList.Items.Count == 1 && panel.IsKeyboardFocused, $"{tag}: collapsed panel has today's data and keyboard focus");
            RenderOnGlass(root, Path.Combine(outDir, $"search-collapsed-{tag}.png"), dark, true);

            ClickSearch();
            Check(panel.SearchBox.IsKeyboardFocused, $"{tag}: clicking search focuses input immediately");
            Pump(260);
            Check(FullyRevealed() && panel.SearchReveal.Opacity == 1,
                $"{tag}: reveal completes with full height and opacity");
            RenderOnGlass(root, Path.Combine(outDir, $"search-expanded-{tag}.png"), dark, true);
            panel.SearchBox.Text = "git";
            Pump(240);
            Check(panel.ItemList.Items.Count == 1 && panel.SectionTitle.Text.Contains("1 条结果") &&
                ((ItemVm)panel.ItemList.Items[0]).Item.Text == "git log --oneline", $"{tag}: search finds history from another day");
            RenderOnGlass(root, Path.Combine(outDir, $"search-results-{tag}.png"), dark, true);
            panel.SearchBox.Text = "微信";
            Pump(240);
            Check(panel.ItemList.Items.Count == 1 && ((ItemVm)panel.ItemList.Items[0]).Item.Text == "meeting notes",
                $"{tag}: search matches source app");
            panel.SearchBox.Text = "no-matching-search-entry";
            Pump(240);
            Check(panel.ItemList.Items.Count == 0 && panel.EmptyState.Visibility == Visibility.Visible,
                $"{tag}: unmatched query shows empty state");
            Key(System.Windows.Input.Key.Escape);
            Check(panel.SearchBox.Text.Length == 0 && panel.SearchReveal.IsHitTestVisible && panel.ItemList.Items.Count == 1,
                $"{tag}: first Escape clears query and immediately restores today's items");
            Key(System.Windows.Input.Key.Escape);
            Pump(220);
            Check(panel.SearchReveal.Visibility == Visibility.Collapsed && panel.IsVisible && panel.IsKeyboardFocused,
                $"{tag}: next Escape collapses search and keeps panel keyboard focus");
            Key(System.Windows.Input.Key.Right);
            Check(panel.DayList.SelectedIndex == 1 && panel.ItemList.Items.Count == 2,
                $"{tag}: date navigation works while search is collapsed");
            ClickSearch();
            Pump(220);
            panel.SearchBox.Text = "git";
            Pump(220);
            ClickSearch();
            Pump(220);
            Check(panel.SearchBox.Text.Length == 0 && panel.ItemList.Items.Count == 2 && !panel.SearchBox.IsKeyboardFocused,
                $"{tag}: search button clears query, restores selected date and releases hidden focus");

            // Use the same key/modifier handler as WPF, without injecting keys into the user's desktop.
            panel.HandleKey(System.Windows.Input.Key.F, System.Windows.Input.ModifierKeys.Control);
            Pump(250);
            Check(panel.SearchReveal.IsHitTestVisible && panel.SearchBox.IsKeyboardFocused,
                $"{tag}: Ctrl+F opens and focuses search");
            panel.SearchBox.Text = "git";
            panel.HandleKey(System.Windows.Input.Key.F, System.Windows.Input.ModifierKeys.Control);
            Pump(100);
            Check(panel.SearchBox.SelectedText == "git", $"{tag}: repeated Ctrl+F selects query for replacement");
            Pump(150);
            panel.SearchBox.Clear(); // no dispatcher pump: the old search results are still displayed
            Key(System.Windows.Input.Key.Escape);
            Check(panel.ItemList.Items.Count == 2 && !panel.SectionTitle.Text.StartsWith("搜索"),
                $"{tag}: clearing then immediately collapsing flushes the pending date refresh");
            Pump(220);
            ClickSearch();
            Pump(220);
            panel.SearchBox.Text = "git";
            Pump(220);
            panel.SearchBox.Clear();
            ClickSearch();
            Check(panel.ItemList.Items.Count == 2 && panel.DayList.Opacity == 1,
                $"{tag}: button collapse flushes a pending manual clear");
            Pump(220);
            ClickSearch();
            Pump(220);
            ClickSearch();
            Pump(60);
            ClickSearch();
            Pump(60);
            ClickSearch();
            Pump(65);
            if (SystemParameters.ClientAreaAnimation)
                Check(panel.SearchReveal.Visibility == Visibility.Visible && panel.SearchReveal.ActualHeight > 0,
                    $"{tag}: replaced close clock cannot hide the latest close animation early");
            Pump(135);
            Check(panel.SearchReveal.Visibility == Visibility.Collapsed && !panel.SearchBox.IsEnabled,
                $"{tag}: rapid toggles finish in the requested collapsed state");
            ClickSearch();
            Pump(35);
            ClickSearch();
            Pump(35);
            ClickSearch();
            Pump(230);
            Check(FullyRevealed() && panel.SearchBox.IsKeyboardFocused,
                $"{tag}: rapid toggles finish in the requested expanded state");
            panel.CalButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Key(System.Windows.Input.Key.Escape);
            Check(panel.CalOverlay.Visibility == Visibility.Collapsed && panel.SearchBox.IsEnabled,
                $"{tag}: Escape dismisses calendar before search");
            panel.HidePanel();
            Pump(220);
            panel.ShowFor(IntPtr.Zero);
            Pump(250);
            Check(panel.SearchReveal.Visibility == Visibility.Collapsed && panel.SearchBox.Text.Length == 0,
                $"{tag}: reopening resets search to collapsed");
            Key(System.Windows.Input.Key.Escape);
            Pump(220);
            Check(!panel.IsVisible, $"{tag}: Escape closes panel when search is collapsed");
            panel.PrepareSnapshot(-1, "git");
            panel.Show();
            Pump(150);
            Check(FullyRevealed() && panel.ItemList.Items.Count == 1,
                $"{tag}: existing snapshot helper expands search for a query");
        }
        finally { panel.Close(); }
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

        s.Upsert(ClipKind.Files, Store.FilesHash(new[] { @"D:\Projects\季度报告.docx", @"D:\Projects\预算.xlsx" }),
            "D:\\Projects\\季度报告.docx\nD:\\Projects\\预算.xlsx", null, null, 2, 0, "文件资源管理器", At(0, 15, 40));
        s.Upsert(ClipKind.Text, Store.TextHash("名称\t数量\n苹果\t3"), "名称\t数量\n苹果\t3", null, null, 0, 0, "Excel", At(0, 15, 55),
            new[] { new ClipFormat("XML Spreadsheet", Encoding.UTF8.GetBytes("<Workbook/>"), false) });
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

    static void PlacementTests()
    {
        var cursor = new Native.POINT { X = -450, Y = 410 };
        var work = new Native.RECT { Left = -1200, Top = 0, Right = 0, Bottom = 900 };
        var field = new Native.RECT { Left = -1100, Top = 60, Right = -800, Bottom = 90 };
        var anchor = new PanelAnchor(cursor, field);
        var below = PanelPlacement.Calculate(anchor, work, 400, 580, 6);
        Check(below.X == field.Left && below.Y == field.Bottom + 6 && below.ExpandDown,
            "focused field takes priority over a distant pointer on negative screen coordinates");
        field.Top = 810;
        field.Bottom = 850;
        var above = PanelPlacement.Calculate(anchor with { Field = field }, work, 400, 580, 6);
        Check(above.Y + above.Height == field.Top - 6 && !above.ExpandDown,
            "field near the bottom opens upward without overlapping the field");
        field.Top = 410;
        field.Bottom = 450;
        var beside = PanelPlacement.Calculate(anchor with { Field = field }, work, 400, 580, 6);
        Check(beside.X == field.Right + 6 && beside.Y + beside.Height <= work.Bottom,
            "panel opens beside a field when it fits on neither vertical edge");
        var atCursor = PanelPlacement.Calculate(new PanelAnchor(new Native.POINT { X = -850, Y = 120 }), work, 400, 580, 6);
        Check(atCursor.X == -850 && atCursor.Y == 126 && atCursor.ExpandDown,
            "without a focused field the panel opens by the mouse");

        var input = new System.Windows.Controls.TextBox { Width = 200, Height = 28 };
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(input, false);
        var button = new System.Windows.Controls.Button { Content = "No text focus", Width = 200, Height = 28 };
        var controls = new System.Windows.Controls.StackPanel();
        controls.Children.Add(input);
        controls.Children.Add(button);
        var host = new Window { Title = "AgosClip placement test", Width = 300, Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = controls };
        if (!InteractiveDesktop) Skip("UI Automation focused-field placement: edit / read-only / button (3 checks)");
        else try
        {
            host.Show();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(host).Handle;
            Native.ForceForeground(hwnd);

            PanelAnchor Capture(UIElement element)
            {
                element.Focus();
                Pump(60);
                var capture = PanelPlacement.CaptureAsync(hwnd);
                while (!capture.IsCompleted) Pump(40);
                return capture.GetAwaiter().GetResult();
            }

            var edit = Capture(input);
            var expected = input.PointToScreen(new Point());
            Check(edit.Field is { } rect && Math.Abs(rect.Left - expected.X) <= 2 && Math.Abs(rect.Top - expected.Y) <= 2,
                $"UI Automation captures a real focused WPF text box (field {edit.Field.HasValue})");
            input.IsReadOnly = true;
            var readOnly = Capture(input);
            Check(readOnly.Field == null && readOnly.Caret == null, "read-only text box uses the mouse");
            var none = Capture(button);
            Check(none.Field == null && none.Caret == null, "switching focus to a button uses the mouse");
        }
        finally { host.Close(); }
    }

    static void AnimationTests(string outDir)
    {
        var panel = new PanelWindow();
        // Keep synthetic focus changes out of a real in-progress IME composition on the user's desktop.
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(panel.SearchBox, false);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(panel).Handle;
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
            Native.SetCursorPos(mi.rcWork.Left + 100, mi.rcWork.Top + 60);
            int firstVisibleHeight = 0;
            panel.IsVisibleChanged += (_, _) =>
            {
                if (panel.IsVisible && firstVisibleHeight == 0) { var r = Rect(); firstVisibleHeight = r.Bottom - r.Top; }
            };
            panel.ShowFor(IntPtr.Zero);
            Pump(320);
            var full = Rect();
            Check(!SystemParameters.ClientAreaAnimation || firstVisibleHeight == full.Bottom - full.Top,
                $"the window has its final size from the first visible frame — no per-frame resize ({firstVisibleHeight}px)");
            Check(panel.IsVisible && !panel.IsClosing && full.Bottom - full.Top >= 580,
                "open completes at full size");
            var viewport = (FrameworkElement)panel.Content;

            // Deterministic checkpoints, independent of frame rate.
            foreach (bool down in new[] { true, false })
            {
                expandDown.SetValue(panel, down);
                Seek(0.5);
                Pump(40);
                var half = Rect();
                int expectedShift = (int)Math.Round(panel.SlidePx * 0.5);
                Check(half.Bottom - half.Top == full.Bottom - full.Top && half.Right - half.Left == full.Right - full.Left,
                    $"{(down ? "downward" : "upward")} reveal keeps the window size");
                Check(down ? full.Top - half.Top == expectedShift : half.Top - full.Top == expectedShift,
                    $"{(down ? "downward" : "upward")} reveal slides out of the anchor edge ({half.Top - full.Top}px)");
                RenderOnGlass(viewport, Path.Combine(outDir, down ? "reveal-down-half.png" : "reveal-up-half.png"), false, true);
                Seek(1);
                Pump(30);
            }
            expandDown.SetValue(panel, true);

            if (SystemParameters.ClientAreaAnimation)
            {
                panel.HidePanel();
                Pump(85); // ease-in close: give it a few pixels of travel before reversing
                int closingShift = Math.Abs(Rect().Top - full.Top);
                panel.ShowFor(IntPtr.Zero);
                Check(panel.IsVisible && !panel.IsClosing && closingShift > 0 && Math.Abs(Math.Abs(Rect().Top - full.Top) - closingShift) <= 2,
                    $"reopening during close continues from the current position ({closingShift}px)");
                Pump(320);
                Check(panel.IsVisible && !panel.IsClosing && Rect().Top == full.Top,
                    "a replaced close cannot hide the reopened panel");

                var shifts = new List<int> { 0 };
                EventHandler sample = (_, _) => { if (panel.IsVisible) shifts.Add(Math.Abs(Rect().Top - full.Top)); };
                System.Windows.Media.CompositionTarget.Rendering += sample;
                try { panel.HidePanel(); Pump(300); }
                finally { System.Windows.Media.CompositionTarget.Rendering -= sample; }
                Check(!panel.IsVisible && shifts.Count > 1 && shifts.Zip(shifts.Skip(1), (x, y) => y >= x).All(x => x)
                      && shifts.Max() <= panel.SlidePx,
                    $"close slides monotonically back toward the anchor, then hides ({string.Join(",", shifts)})");

                panel.ShowFor(IntPtr.Zero);
                Pump(35);
                panel.HidePanel(); // interrupt opening, then reverse again before it finishes closing
                panel.ShowFor(IntPtr.Zero);
                Pump(320);
                Check(panel.IsVisible && !panel.IsClosing && Rect().Top == full.Top &&
                      Rect().Bottom - Rect().Top == full.Bottom - full.Top,
                    "rapid open-close-open settles fully open");
                panel.Snapshot = true;
                panel.HidePanel();
                Check(!panel.IsVisible, "instant snapshot close cancels the live animation state");
                panel.Snapshot = false;
            }
            else { panel.HidePanel(); Check(!panel.IsVisible, "system animation preference closes instantly"); }

            // A real placement near the bottom must choose the upward anchor.
            if (InteractiveDesktop)
            {
                Native.SetCursorPos(mi.rcWork.Left + 100, mi.rcWork.Bottom - 15);
                panel.ShowFor(IntPtr.Zero);
                Pump(320);
                Check(!(bool)expandDown.GetValue(panel)! && panel.Root.VerticalAlignment == VerticalAlignment.Bottom,
                    "placement near the work-area bottom unfolds upward");
                panel.HidePanel();
                Pump(240);
            }
            else Skip("placement near the work-area bottom unfolds upward");
        }
        finally
        {
            panel.Close();
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
                // click the 固定 card with the mouse (index 0) → it must become selected (needs the date-card strip)
                App.Current.Settings.ShowDateCards = true;
                panel.PrepareSnapshot(-1);
                Pump(200);
                var item = (System.Windows.Controls.ListBoxItem)panel.DayList.ItemContainerGenerator.ContainerFromIndex(0);
                item.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                Pump(200);
                Check(panel.DayList.SelectedIndex == 0 && panel.SectionTitle.Text.StartsWith("固定"), "clicking a date card selects it (固定)");
                App.Current.Settings.ShowDateCards = false;
                panel.PrepareSnapshot(-1);
                Pump(200);

                // pin to desktop
                // editor overlay (#6) and the full date-card strip, for review
                if (panel.ItemList.Items.OfType<ItemVm>().FirstOrDefault(v => v.IsText) is { } ev)
                {
                    panel.OpenEditor(ev);
                    Pump(200);
                    RenderOnGlass(root, Path.Combine(outDir, "panel-editor-light.png"), dark, glass);
                    panel.HandleKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None);
                }
                App.Current.Settings.ShowDateCards = true;
                panel.PrepareSnapshot(-1);
                Pump(200);
                RenderOnGlass(root, Path.Combine(outDir, "panel-today-cards-light.png"), dark, glass);
                App.Current.Settings.ShowDateCards = false;
                panel.PrepareSnapshot(-1);
                Pump(200);

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
                {
                    long st = (long)Native.GetWindowLongPtr(new System.Windows.Interop.WindowInteropHelper(sw).Handle, -16);
                    Check((st & 0x00080000L) == 0 && (st & 0x00010000L) == 0 && (st & 0x00020000L) != 0,
                        "settings window: no system menu/maximize (no ghost caption buttons), minimize kept for the taskbar");
                    sw.WindowState = WindowState.Minimized; Pump(150); sw.WindowState = WindowState.Normal; Pump(150);
                    st = (long)Native.GetWindowLongPtr(new System.Windows.Interop.WindowInteropHelper(sw).Handle, -16);
                    Check((st & 0x00080000L) == 0, "…and it stays that way after minimise/restore");
                }
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
        File.Copy(path, Path.Combine(_outDir, "diagnostics-sample.txt"), true); // kept with the screenshots for review
        File.Delete(path);
    }

    // ───────────── issues #4 (files, rich text, tables), #5 (compact dates), #6 (edit) ─────────────

    static void IssueTests(App app)
    {
        var s = app.Store;
        _mon ??= new ClipboardMonitor(s, () => app.Settings);

        // #4 files: Explorer-style copy → one Files item; paste-back is a copy and skips missing files
        var dir = Path.Combine(Settings.ConfigDir, "files-test");
        Directory.CreateDirectory(Path.Combine(dir, "子文件夹"));
        var f1 = Path.Combine(dir, "报告.txt"); File.WriteAllText(f1, "a");
        var f2 = Path.Combine(dir, "数据.csv"); File.WriteAllText(f2, "b");
        var d1 = Path.Combine(dir, "子文件夹");
        var drop = new System.Collections.Specialized.StringCollection { f1, f2, d1 };
        var fobj = new DataObject();
        fobj.SetFileDropList(drop);
        Clipboard.SetDataObject(fobj, true);
        Pump(900);
        var files = s.ItemsOnDay(Today).FirstOrDefault(i => i.Kind == ClipKind.Files);
        Check(files != null && files.Paths.SequenceEqual(new[] { f1, f2, d1 }), "copied files/folders are recorded as one item (#4)");
        if (files != null)
        {
            var vm = new ItemVm(files, false);
            Check(vm.FilesTitle == "报告.txt 等 3 项" && vm.Meta.Contains("3 项"), $"files item shows names and count ({vm.FilesTitle})");
            Check(Paster.SetClipboard(files, null, out var p0) && p0 == null && Clipboard.ContainsFileDropList()
                  && Clipboard.GetFileDropList().Cast<string>().SequenceEqual(new[] { f1, f2, d1 }), "paste-back restores the file list");
            var effect = Clipboard.GetData("Preferred DropEffect") as MemoryStream;
            Check(effect != null && BitConverter.ToInt32(effect.ToArray(), 0) == 1, "paste-back is a copy (DROPEFFECT_COPY), never a move");
            File.Delete(f2);
            Check(Paster.SetClipboard(files, null, out var p1) && p1 != null && p1.Contains("1 个")
                  && Clipboard.GetFileDropList().Count == 2, "missing files are skipped and reported");
            Check(new ItemVm(files, false).Meta.Contains("1 项已不存在"), "the list marks missing files");
            Directory.Delete(dir, true);
            Check(!Paster.SetClipboard(files, null, out var p2) && p2 != null, "nothing to paste when all files are gone");
        }

        // #4 rich text + tables: HTML/RTF/XML Spreadsheet are kept next to the text and pasted back
        var html = "Version:0.9\r\nStartHTML:00000097\r\nEndHTML:00000170\r\nStartFragment:00000131\r\nEndFragment:00000134\r\n<html><body><!--StartFragment--><b>粗体</b> <a href=\"https://example.com\">链接</a><!--EndFragment--></body></html>";
        var rtf = @"{\rtf1\ansi {\b bold} text}";
        var rich = new DataObject();
        rich.SetText("粗体 链接", TextDataFormat.UnicodeText);
        rich.SetData(DataFormats.Html, html);
        rich.SetData(DataFormats.Rtf, rtf);
        Clipboard.SetDataObject(rich, true);
        Pump(900);
        var richItem = s.Search("粗体 链接").FirstOrDefault();
        Check(richItem != null && richItem.Formats.Contains("HTML Format") && richItem.Formats.Contains("Rich Text Format"),
            $"rich text keeps HTML and RTF ({string.Join(", ", richItem?.Formats ?? Array.Empty<string>())})");
        if (richItem != null)
        {
            Check(new ItemVm(richItem, false).Badge == "富文本", "rich text is labelled 富文本");
            Clipboard.SetText("something else");
            Pump(500);
            Check(Paster.SetClipboard(richItem, s.GetFormats(richItem.Id)) && Clipboard.GetText() == "粗体 链接"
                  && (Clipboard.GetData(DataFormats.Html) as string)?.Contains("<b>粗体</b>") == true
                  && (Clipboard.GetData(DataFormats.Rtf) as string)?.Contains(@"{\b bold}") == true,
                "paste-back offers plain text, HTML and RTF");
            Pump(700);
        }
        var table = new DataObject();
        table.SetText("名称\t数量\r\n苹果\t3\r\n", TextDataFormat.UnicodeText);
        table.SetData(DataFormats.Html, html.Replace("<b>粗体</b>", "<table><tr><td>名称</td><td>数量</td></tr></table>"));
        table.SetData("XML Spreadsheet", new MemoryStream(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Workbook/>")));
        Clipboard.SetDataObject(table, true);
        Pump(900);
        var tableItem = s.Search("苹果").FirstOrDefault();
        Check(tableItem != null && tableItem.Formats.Contains("XML Spreadsheet") && new ItemVm(tableItem, false).Badge == "表格",
            "Excel-style table keeps its spreadsheet format and is labelled 表格");

        // #6 edit: text changes in place, pinned stays, rich formats are dropped, duplicates merge
        if (richItem != null)
        {
            s.SetPinned(richItem.Id, true);
            Check(s.UpdateText(richItem.Id, "粗体 链接（已修改）\n第二行"), "editing a text item saves");
            var edited = s.PinnedItems().FirstOrDefault(i => i.Id == richItem.Id);
            Check(edited != null && edited.Text == "粗体 链接（已修改）\n第二行" && edited.Formats.Count == 0,
                "edited item keeps its pinned state and becomes plain text");
            Check(s.Search("粗体 链接（已修改）").Count == 1, "search finds the new text");
            s.Upsert(ClipKind.Text, Store.TextHash("合并目标"), "合并目标", null, null, 0, 0, null);
            s.UpdateText(richItem.Id, "合并目标");
            var merged = s.Search("合并目标");
            Check(merged.Count == 1 && merged[0].Id == richItem.Id && merged[0].Pinned, "editing into an existing text merges the duplicate");
        }
        _mon.Dispose();
        _mon = null;

        // #5 compact mode and #6 editor UI
        app.Settings.ShowDateCards = false;
        s.Upsert(ClipKind.Text, Store.TextHash("昨天的一条"), "昨天的一条", null, null, 0, 0, null, At(1, 9, 0));
        var panel = new PanelWindow();
        panel.PrepareSnapshot(-1);
        Pump(200);
        Check(panel.DateStrip.Visibility != Visibility.Visible && panel.DayOlder.Visibility == Visibility.Visible
              && panel.SectionTitle.Text.StartsWith("今天 · "), $"compact mode hides the date strip ({panel.SectionTitle.Text})");
        int before = panel.DayList.SelectedIndex;
        panel.DayOlder.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump(200);
        Check(panel.DayList.SelectedIndex == before + 1 && panel.SectionTitle.Text.StartsWith("昨天 · "),
            $"‹ goes to the previous day ({panel.SectionTitle.Text})");
        Check(panel.HandleKey(System.Windows.Input.Key.Right, System.Windows.Input.ModifierKeys.None) && panel.DayList.SelectedIndex == before,
            "→ goes back to the newer day in compact mode (same direction as ›)");
        Check(panel.HandleKey(System.Windows.Input.Key.Left, System.Windows.Input.ModifierKeys.None) && panel.DayList.SelectedIndex == before + 1,
            "← goes to the older day in compact mode (same direction as ‹) — issue #12");
        panel.HandleKey(System.Windows.Input.Key.Right, System.Windows.Input.ModifierKeys.None);
        app.Settings.ShowDateCards = true;
        panel.PrepareSnapshot(-1);
        Pump(150);
        Check(panel.DateStrip.Visibility == Visibility.Visible && panel.DayOlder.Visibility != Visibility.Visible,
            "the 显示日期卡片栏 setting brings the strip back");
        app.Settings.ShowDateCards = false;

        panel.PrepareSnapshot(-1);
        Pump(150);
        var target = panel.ItemList.Items.OfType<ItemVm>().FirstOrDefault(v => v.IsText);
        if (target != null)
        {
            panel.ItemList.SelectedItem = target;
            Check(panel.HandleKey(System.Windows.Input.Key.F2, System.Windows.Input.ModifierKeys.None) && panel.EditOpen
                  && panel.EditBox.Text == target.Item.Text, "F2 opens the editor with the full text");
            Check(!panel.HandleKey(System.Windows.Input.Key.Down, System.Windows.Input.ModifierKeys.None),
                "arrow keys go to the editor, not the list");
            panel.HandleKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None);
            Check(!panel.EditOpen, "Esc cancels editing");
            panel.OpenEditor(target);
            panel.EditBox.Text = "在面板里改过的文字";
            Check(panel.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.Control) && !panel.EditOpen
                  && s.Search("在面板里改过的文字").Count == 1, "Ctrl+Enter saves the edit");
        }
        else Check(false, "editor test: no text item in the list");
        panel.Close();
    }

    /// <summary>Issues #9–#13: Markdown notes, preview, resize, date keys / calendar keyboard, drag to reorder.</summary>
    static void Issue8To13Tests(App app, string outDir)
    {
        var s = app.Store;
        var none = System.Windows.Input.ModifierKeys.None;

        // ── #13 store: hand-arranged order ──
        var day5 = Today.AddDays(-5);
        foreach (var (t, h) in new[] { ("排序A", 9), ("排序B", 10), ("排序C", 11) })
            s.Upsert(ClipKind.Text, Store.TextHash(t), t, null, null, 0, 0, null, At(5, h, 0));
        string Order(List<ClipItem> l) => string.Join("", l.Where(i => i.Text?.StartsWith("排序") == true).Select(i => i.Text![2..]));
        long Id(string t) => s.Search(t)[0].Id;
        Check(Order(s.ItemsOnDay(day5)) == "CBA", "a day lists newest first by default");
        s.Reorder(Id("排序A"), null, Id("排序C"), false);
        Check(Order(s.ItemsOnDay(day5)) == "ACB", $"dragging to the top saves ({Order(s.ItemsOnDay(day5))})");
        s.Reorder(Id("排序B"), Id("排序A"), Id("排序C"), false);
        Check(Order(s.ItemsOnDay(day5)) == "ABC", $"dragging between two cards saves ({Order(s.ItemsOnDay(day5))})");
        foreach (var t in new[] { "排序A", "排序B", "排序C" }) s.SetPinned(Id(t), true);
        var firstPinned = s.PinnedItems()[0];
        s.Reorder(Id("排序C"), null, firstPinned.Id, true);
        Check(s.PinnedItems()[0].Text == "排序C" && Order(s.ItemsOnDay(day5)) == "ABC",
            "the 固定 list has its own order; arranging it leaves the day untouched");
        s.Upsert(ClipKind.Text, Store.TextHash("排序B"), "排序B", null, null, 0, 0, null, At(5, 11, 30));
        Check(Order(s.ItemsOnDay(day5)) == "BAC", $"copying an item again brings it back to the top ({Order(s.ItemsOnDay(day5))})");
        foreach (var t in new[] { "排序A", "排序B", "排序C" }) s.SetPinned(Id(t), false);

        var day6 = Today.AddDays(-6);
        foreach (var (t, h) in new[] { ("精度Q", 9), ("精度Y", 10), ("精度X", 11), ("精度P", 12) })
            s.Upsert(ClipKind.Text, Store.TextHash(t), t, null, null, 0, 0, null, At(6, h, 0));
        long p = Id("精度P"), x = Id("精度X"), y = Id("精度Y");
        for (int i = 0; i < 70; i++)
        {
            bool moveX = i % 2 == 0;
            s.Reorder(moveX ? x : y, p, moveX ? y : x, false); // always straight under P: halves the gap each time
        }
        var d6 = string.Join("", s.ItemsOnDay(day6).Select(i => i.Text![2..]));
        Check(d6 == "PYXQ", $"70 inserts into the same gap keep the order (keys renumbered when needed: {d6})");

        // ── #13 panel: drag in the list ──
        app.Settings.ShowDateCards = true;
        var panel = new PanelWindow();
        panel.PrepareSnapshot(-1);
        int idx5 = panel.DayList.Items.OfType<DayCard>().ToList().FindIndex(c => !c.IsPinned && c.Date == day5);
        panel.DayList.SelectedIndex = idx5;
        Pump(150);
        string ListOrder() => string.Join("", panel.ItemList.Items.OfType<ItemVm>().Select(v => v.Item.Text![2..]));
        Check(ListOrder() == "BAC", $"panel shows the saved order ({ListOrder()})");
        panel.MoveItemForTest(2, 0);
        Check(ListOrder() == "CBA" && Order(s.ItemsOnDay(day5)) == "CBA", $"dragging a card in the panel saves the new order ({Order(s.ItemsOnDay(day5))})");
        panel.MoveItemForTest(0, 1);
        Check(Order(s.ItemsOnDay(day5)) == "BCA", "dragging down one place saves");

        // ── #12 date-strip mode keeps ← = card on the left ──
        int cardIdx = panel.DayList.SelectedIndex;
        panel.HandleKey(System.Windows.Input.Key.Left, none);
        Check(panel.DayList.SelectedIndex == cardIdx - 1, "date strip: ← selects the card on the left (newer)");
        panel.Close();

        // ── #12 compact mode ──
        app.Settings.ShowDateCards = false;
        panel = new PanelWindow();
        panel.PrepareSnapshot(-1);
        Pump(150);
        bool hasPinned = panel.DayList.Items.OfType<DayCard>().Any(c => c.IsPinned);
        Check(panel.CurrentCardForTest is { IsPinned: false } t0 && t0.Date == Today && !panel.DayNewer.IsEnabled && panel.DayOlder.IsEnabled,
            "compact: on today › is disabled (固定 is not a day), ‹ is enabled");
        Check(panel.HandleKey(System.Windows.Input.Key.Right, none) && panel.CurrentCardForTest is { IsPinned: false } t1 && t1.Date == Today,
            "compact: → on today stays on today");
        if (hasPinned)
        {
            panel.DayList.SelectedItem = panel.DayList.Items.OfType<DayCard>().First(c => c.IsPinned);
            Pump(80);
            Check(panel.DayOlder.IsEnabled && !panel.DayNewer.IsEnabled, "compact: from 固定, ‹ goes back to the days");
            panel.HandleKey(System.Windows.Input.Key.Left, none);
            Check(panel.CurrentCardForTest is { IsPinned: false } t2 && t2.Date == Today, "compact: ← from 固定 returns to today");
        }
        else Check(false, "compact: expected a 固定 card");

        // ── #12 calendar takes the keyboard ── (earlier tests clear the store: give ↑ a week to go back into)
        s.Upsert(ClipKind.Text, Store.TextHash("十二天前"), "十二天前", null, null, 0, 0, null, At(12, 9, 0));
        panel.PrepareSnapshot(-1, null, openCalendar: true);
        Pump(100);
        int itemSel = panel.ItemList.SelectedIndex, itemCount = panel.ItemList.Items.Count;
        var dayBefore = panel.CurrentCardForTest;
        Check(panel.HandleKey(System.Windows.Input.Key.Down, none) && panel.ItemList.SelectedIndex == itemSel && panel.CalCursor == Today,
            "calendar: ↓ doesn't move the list underneath (and can't go past today)");
        Check(panel.HandleKey(System.Windows.Input.Key.Left, none) && panel.CalCursor == Today.AddDays(-1) && panel.CurrentCardForTest == dayBefore,
            "calendar: ← moves the calendar cursor, not the day underneath");
        Check(panel.HandleKey(System.Windows.Input.Key.Delete, none) && panel.ItemList.Items.Count == itemCount,
            "calendar: Delete doesn't delete the item underneath");
        panel.HandleKey(System.Windows.Input.Key.Up, none);
        Check(panel.CalCursor == Today.AddDays(-8) && panel.CalMonth == new DateOnly(Today.AddDays(-8).Year, Today.AddDays(-8).Month, 1),
            $"calendar: ↑ goes back a week, and the month follows ({panel.CalCursor})");
        panel.HandleKey(System.Windows.Input.Key.Home, none);
        // find an empty day in the last 3 weeks to check Enter does nothing there
        var counts = s.DayCounts();
        var empty = Enumerable.Range(2, 20).Select(n => Today.AddDays(-n)).FirstOrDefault(d => counts.GetValueOrDefault(d) == 0);
        if (empty != default)
        {
            while (panel.CalCursor > empty) panel.HandleKey(System.Windows.Input.Key.Left, none);
            panel.HandleKey(System.Windows.Input.Key.Enter, none);
            Check(panel.CalOpenForTest && panel.CurrentCardForTest == dayBefore, "calendar: Enter on a day without records does nothing");
        }
        panel.HandleKey(System.Windows.Input.Key.Home, none);
        panel.HandleKey(System.Windows.Input.Key.Left, none);
        panel.HandleKey(System.Windows.Input.Key.Enter, none);
        Check(!panel.CalOpenForTest && panel.CurrentCardForTest is { IsPinned: false } y1 && y1.Date == Today.AddDays(-1),
            "calendar: Enter opens the highlighted day and closes the calendar");
        panel.PrepareSnapshot(-1, null, openCalendar: true);
        panel.HandleKey(System.Windows.Input.Key.Escape, none);
        Check(!panel.CalOpenForTest && panel.CurrentCardForTest is { IsPinned: false } t3 && t3.Date == Today, "calendar: Esc only closes the calendar");

        // ── #10 preview ──
        string longText = string.Join("\n", Enumerable.Range(1, 120).Select(n => $"第 {n} 行：日志 log line {n} " + new string('x', n % 30)));
        s.Upsert(ClipKind.Text, Store.TextHash(longText), longText, null, null, 0, 0, "记事本");
        _mon ??= new ClipboardMonitor(s, () => app.Settings);
        var (ih, ir, it) = _mon.SaveImage(MakeImage(1800, 1000, "预览大图 1800×1000", Colors.MediumPurple, Colors.Orange));
        s.Upsert(ClipKind.Image, ih, null, ir, it, 1800, 1000, "截图");
        var missing = new[] { @"C:\definitely\missing\报告.docx" };
        s.Upsert(ClipKind.Files, Store.FilesHash(missing), missing[0], null, null, 0, 0, "资源管理器");
        var gbk = Path.Combine(outDir, "gbk-sample.txt");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllBytes(gbk, Encoding.GetEncoding(936).GetBytes("中文 GBK 文本\r\n第二行"));
        s.Upsert(ClipKind.Files, Store.FilesHash(new[] { gbk }), gbk, null, null, 0, 0, "资源管理器");
        _mon.Dispose(); _mon = null;
        panel.PrepareSnapshot(-1);
        Pump(150);
        var vms = panel.ItemList.Items.OfType<ItemVm>().ToList();
        var longVm = vms.First(v => v.Item.Text == longText);
        panel.ItemList.SelectedItem = longVm;
        string clipBefore = s.ItemsOnDay(Today).Count.ToString();
        Check(panel.HandleKey(System.Windows.Input.Key.Space, none) && panel.PreviewOpen && panel.PreviewMode == "text"
              && panel.PreviewText.Text == longText, "Space previews the complete text, line breaks kept");
        Check(panel.HandleKey(System.Windows.Input.Key.Enter, none) && panel.PreviewOpen && panel.HandleKey(System.Windows.Input.Key.Delete, none)
              && s.ItemsOnDay(Today).Count.ToString() == clipBefore, "Enter / Delete in the preview don't paste or delete");
        panel.Show(); Pump(300);
        RenderOnGlass((FrameworkElement)panel.Content, Path.Combine(outDir, "preview-text-light.png"), false, true);
        panel.HandleKey(System.Windows.Input.Key.Escape, none);
        Check(!panel.PreviewOpen && panel.ItemList.SelectedItem == longVm, "Esc closes the preview and keeps the list position");

        var imgVm = vms.First(v => v.IsImage && v.Item.Width == 1800);
        panel.OpenPreview(imgVm);
        Pump(200);
        double fit = panel.CurrentZoom;
        Check(panel.PreviewMode == "image" && panel.PreviewImage.Source != null && fit < 1, $"image preview fits the panel (zoom {fit:0.00})");
        RenderOnGlass((FrameworkElement)panel.Content, Path.Combine(outDir, "preview-image-light.png"), false, true);
        panel.HandleKey(System.Windows.Input.Key.OemPlus, none);
        Check(panel.CurrentZoom > fit, "+ zooms in");
        panel.HandleKey(System.Windows.Input.Key.D0, none);
        Check(Math.Abs(panel.CurrentZoom - 1) < 1e-6 || Math.Abs(panel.CurrentZoom - fit) < 1e-6, "0 toggles fit / 100%");
        panel.HandleKey(System.Windows.Input.Key.Escape, none);

        panel.OpenPreview(vms.First(v => v.IsFiles && v.Item.Text == missing[0]));
        Check(panel.PreviewMode == "message" && panel.PreviewMessage.Text.Contains("已被移动或删除"), "a missing file says so");
        panel.ClosePreview();
        panel.OpenPreview(vms.First(v => v.IsFiles && v.Item.Text == gbk));
        Check(panel.PreviewMode == "text" && panel.PreviewText.Text == "中文 GBK 文本\r\n第二行", "a GBK text file previews readably");
        panel.ClosePreview();
        panel.Hide();
        panel.Close();

        // ── #11 resize and remember ──
        var rp = new PanelWindow();
        rp.ShowFor(IntPtr.Zero);
        Pump(350);
        var (w0, h0) = rp.PhysicalSize;
        rp.BeginResizeForTest();
        rp.ResizeBy("BR", 120, 90);
        Pump(120);
        var (w1, h1) = rp.PhysicalSize;
        Check(w1 == w0 + 120 && h1 == h0 + 90, $"dragging the corner resizes ({w0}×{h0} → {w1}×{h1})");
        Check(Math.Abs(rp.Root.ActualHeight - rp.SizeDip.H) < 1.5 && Math.Abs(rp.Root.ActualWidth - rp.SizeDip.W) < 1.5,
            $"content follows the new size ({rp.Root.ActualWidth:0}×{rp.Root.ActualHeight:0})");
        RenderOnGlass((FrameworkElement)rp.Content, Path.Combine(outDir, "panel-resized-light.png"), false, true);
        rp.ResizeBy("BR", -5000, -5000);
        Check(Math.Abs(rp.SizeDip.W - PanelWindow.MinWDip) < 1 && Math.Abs(rp.SizeDip.H - PanelWindow.MinHDip) < 1, "there is a minimum size");
        rp.ResizeBy("BR", 120, 90);
        rp.EndResizeForTest();
        double savedW = app.Settings.PanelWidth, savedH = app.Settings.PanelHeight;
        Check(savedW > PanelWindow.DefaultW && savedH > PanelWindow.DefaultH && Settings.Load().PanelWidth == savedW, $"the size is saved ({savedW}×{savedH})");
        rp.HidePanel(); Pump(300); rp.Close();
        var rp2 = new PanelWindow();
        Check(Math.Abs(rp2.SizeDip.W - savedW) < 0.5 && Math.Abs(rp2.SizeDip.H - savedH) < 0.5, "the next panel opens at the remembered size");
        rp2.Close();
        app.Settings.PanelWidth = app.Settings.PanelHeight = 0;
        app.Settings.Save();

        // ── #9 release notes as Markdown ──
        const string notes = "## AgosClip 1.8.3\n\n**重要修复：粘贴时卡死约 10 秒**\n- 第一条，带 `open timing` 代码\n- 第二条 *强调* 和 [发布页](https://github.com/Amigosazul/AgosClip/releases)\n  续行\n  - 子项\n1. 有序\n2. 第二\n\n> 引用\n\n```\ncode block\n```\n---\n- https://ghfast.top/https://github.com/x/y.exe";
        foreach (bool dark in new[] { false, true })
        {
            Theme.Apply(dark);
            var host = new System.Windows.Controls.StackPanel();
            MarkdownView.Render(host, notes);
            var box = new System.Windows.Controls.Border { Width = 460, Padding = new Thickness(14), Child = host };
            box.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PanelBg");
            box.Measure(new Size(460, double.PositiveInfinity));
            box.Arrange(new Rect(box.DesiredSize));
            box.UpdateLayout();
            Render(box, box.ActualWidth, box.ActualHeight, Path.Combine(outDir, $"notes-markdown-{(dark ? "dark" : "light")}.png"));
            if (dark) continue;
            var blocks = host.Children.OfType<FrameworkElement>().ToList();
            var all = string.Join("\n", blocks.SelectMany(AllText));
            Check(blocks[0] is System.Windows.Controls.TextBlock { FontWeight: var fw } hd && fw == FontWeights.SemiBold && new System.Windows.Documents.TextRange(hd.ContentStart, hd.ContentEnd).Text == "AgosClip 1.8.3",
                "## becomes a heading");
            Check(!all.Contains("**") && !all.Contains("##") && !all.Contains("](") && !all.Contains('`'), $"no raw Markdown marks remain");
            Check(blocks.OfType<System.Windows.Controls.TextBlock>().Any(tb => tb.Inlines.OfType<System.Windows.Documents.Bold>().Any()), "**text** is bold");
            int links = blocks.SelectMany(Inlines).OfType<System.Windows.Documents.Hyperlink>().Count();
            Check(links == 2, $"links and bare URLs are clickable ({links})");
            Check(blocks.Count(b => b is System.Windows.Controls.Grid) == 6, $"list items render as bullets ({blocks.Count(b => b is System.Windows.Controls.Grid)})");
        }
        Theme.Apply(false);
    }

    static IEnumerable<System.Windows.Documents.Inline> Inlines(FrameworkElement e)
    {
        IEnumerable<System.Windows.Documents.Inline> Walk(System.Windows.Documents.InlineCollection c)
        {
            foreach (var i in c)
            {
                yield return i;
                if (i is System.Windows.Documents.Span sp) foreach (var j in Walk(sp.Inlines)) yield return j;
            }
        }
        var tbs = e is System.Windows.Controls.TextBlock t ? new[] { t }
            : e is System.Windows.Controls.Panel p ? p.Children.OfType<System.Windows.Controls.TextBlock>().ToArray()
            : e is System.Windows.Controls.Border { Child: System.Windows.Controls.TextBlock bt } ? new[] { bt }
            : Array.Empty<System.Windows.Controls.TextBlock>();
        foreach (var tb in tbs) foreach (var i in Walk(tb.Inlines)) yield return i;
    }

    static IEnumerable<string> AllText(FrameworkElement e)
    {
        var tbs = e is System.Windows.Controls.TextBlock t ? new[] { t }
            : e is System.Windows.Controls.Panel p ? p.Children.OfType<System.Windows.Controls.TextBlock>().ToArray()
            : e is System.Windows.Controls.Border { Child: System.Windows.Controls.TextBlock bt } ? new[] { bt }
            : Array.Empty<System.Windows.Controls.TextBlock>();
        foreach (var tb in tbs) yield return new System.Windows.Documents.TextRange(tb.ContentStart, tb.ContentEnd).Text;
    }

    /// <summary>
    /// Opt-in (AGOSCLIP_BENCH=1): fills the store with a heavy history and times Win+V opens —
    /// data rebuild per open, and the worst gap between rendered frames while the panel animates.
    /// </summary>
    static void OpenBenchmark(App app)
    {
        var s = app.Store;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 3000; i++)
            s.Upsert(ClipKind.Text, Store.TextHash("bench " + i), $"bench item {i} " + new string('文', i % 200), null, null, 0, 0, "Bench",
                At(i % 30, 8 + i % 12, i % 60));
        _mon ??= new ClipboardMonitor(s, () => app.Settings);
        for (int i = 0; i < 40; i++)
        {
            var (h, r, t) = _mon.SaveImage(MakeImage(1600, 900, "bench " + i, Colors.SlateBlue, Colors.Teal));
            s.Upsert(ClipKind.Image, h, null, r, t, 1600, 900, "Bench", At(0, 7, i));
        }
        Report.AppendLine($"BENCH seeded 3040 items in {sw.ElapsedMilliseconds} ms");

        var panel = new PanelWindow();

        // Per-frame cost of the two animation techniques on the same window: resizing the HWND (old) vs
        // moving it (new). Each step waits until WPF has finished layout + render for that frame.
        panel.ShowFor(IntPtr.Zero);
        Pump(400);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(panel).Handle;
        Native.GetWindowRect(hwnd, out var rr);
        int pw = rr.Right - rr.Left, ph = rr.Bottom - rr.Top;
        double Cost(Func<int, (int y, int hh)> step, uint flags)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 1; i <= 30; i++)
            {
                var (y, hh) = step(i);
                Native.SetWindowPos(hwnd, IntPtr.Zero, rr.Left, y, pw, hh, 0x0004 | 0x0010 | flags);
                Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); // after layout + render
            }
            Native.SetWindowPos(hwnd, IntPtr.Zero, rr.Left, rr.Top, pw, ph, 0x0004 | 0x0010);
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            return t.Elapsed.TotalMilliseconds / 30;
        }
        double resize = Cost(i => (rr.Top, Math.Max(1, ph * i / 30)), 0);
        double move = Cost(i => (rr.Top - 10 + i / 3, ph), 0x0001 /* NOSIZE */);
        Report.AppendLine($"BENCH per-frame UI cost: resize-window (old) {resize:0.0} ms/frame, move-window (new) {move:0.0} ms/frame");
        Check(move < resize, "moving the window per frame costs less than resizing it");
        panel.HidePanel();
        Pump(300);

        var gaps = new List<double>();
        var frame = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        EventHandler onFrame = (_, _) => { double now = frame.Elapsed.TotalMilliseconds; if (last > 0) gaps.Add(now - last); last = now; };
        for (int round = 0; round < 6; round++)
        {
            System.Windows.Media.CompositionTarget.Rendering += onFrame;
            gaps.Clear(); last = 0;
            panel.ShowFor(IntPtr.Zero);
            Pump(450);
            System.Windows.Media.CompositionTarget.Rendering -= onFrame;
            var (a, d, total) = panel.LastOpenTiming;
            if (round == 3) s.Upsert(ClipKind.Text, Store.TextHash("bench change"), "bench change", null, null, 0, 0, null); // forces one rebuild
            Report.AppendLine($"BENCH open {round}{(panel.LastOpenReusedList ? " (reused)" : "")}: anchor {a:0.0} ms, data {d:0.0} ms, shown at {total:0.0} ms; " +
                              $"frames {gaps.Count}, worst frame gap {(gaps.Count > 0 ? gaps.Max() : 0):0.0} ms, avg {(gaps.Count > 0 ? gaps.Average() : 0):0.0} ms");
            panel.HidePanel();
            Pump(400);
        }
        panel.ShowFor(IntPtr.Zero);
        Pump(300);
        s.Upsert(ClipKind.Text, Store.TextHash("copied while hidden?"), "copied while hidden?", null, null, 0, 0, null);
        panel.HidePanel(); Pump(300);
        s.Upsert(ClipKind.Text, Store.TextHash("copied while hidden"), "copied while hidden", null, null, 0, 0, null);
        panel.ShowFor(IntPtr.Zero);
        Pump(300);
        Check(!panel.LastOpenReusedList && panel.ItemList.Items.OfType<ItemVm>().First().Item.Text == "copied while hidden",
            "something copied while the panel was closed shows on the next open");
        panel.HidePanel(); Pump(300);
        panel.ShowFor(IntPtr.Zero);
        Pump(300);
        Check(panel.LastOpenReusedList, "an unchanged history reuses the built list");
        panel.Close();
    }

    /// <summary>
    /// Regression for the 10 s "paste: put text on clipboard" hang: while another thread keeps reading the
    /// clipboard (our own reader thread, or remote-desktop / sync tools that watch it), pasting must stay fast.
    /// </summary>
    static void PasteContentionTest(App app)
    {
        var s = app.Store;
        var html = "Version:0.9\r\nStartHTML:00000097\r\nEndHTML:00000160\r\nStartFragment:00000131\r\nEndFragment:00000124\r\n<html><body><!--StartFragment--><b>contention</b><!--EndFragment--></body></html>";
        s.Upsert(ClipKind.Text, Store.TextHash("contention test text"), "contention test text", null, null, 0, 0, "Chrome", null,
            new[] { new ClipFormat(DataFormats.Html, Encoding.UTF8.GetBytes(html), true) });
        var item = s.Search("contention test text")[0];
        var formats = s.GetFormats(item.Id);

        using var stop = new System.Threading.CancellationTokenSource();
        int reads = 0;
        var reader = new System.Threading.Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var d = Clipboard.GetDataObject();
                    foreach (var f in d?.GetFormats(false) ?? Array.Empty<string>()) { try { d!.GetData(f, false); } catch { } }
                    reads++;
                }
                catch { }
            }
        }) { IsBackground = true };
        reader.SetApartmentState(System.Threading.ApartmentState.STA);
        reader.Start();
        double worst = 0;
        int ok = 0;
        try
        {
            for (int i = 0; i < 15; i++)
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                if (Paster.SetClipboard(item, formats)) ok++;
                worst = Math.Max(worst, t.Elapsed.TotalMilliseconds);
                Pump(40);
                if (worst > 3000) break;
            }
        }
        finally { stop.Cancel(); reader.Join(15000); }
        Check(worst < 1000 && ok >= 10, $"pasting stays fast while another thread reads the clipboard (worst {worst:0} ms, {ok} ok, {reads} reads)");
    }
}
