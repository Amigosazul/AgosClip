using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace AgosClip;

public partial class SettingsWindow : Window
{
    static readonly int[] Choices = { 1, 3, 7, 15, 30, 60, 90, 180, 365, 0 };
    bool _loading = true;
    int _lastRetention;

    static Settings S => App.Current.Settings;

    public SettingsWindow()
    {
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        SmoothWheel.Attach(PageScroll);
        SourceInitialized += (_, _) => { HideSystemCaptionButtons(); Glass.Apply(this); };
        StateChanged += (_, _) => HideSystemCaptionButtons(); // WPF may rewrite the style on restore
        PreviewKeyDown += (_, e) =>
        {
            // without a system menu Windows no longer turns Alt+F4 into SC_CLOSE for us
            if (e.Key == System.Windows.Input.Key.System && e.SystemKey == System.Windows.Input.Key.F4) { Close(); e.Handled = true; }
        };

        int cur = S.RetentionDays;
        var list = Choices.ToList();
        if (!list.Contains(cur)) list.Insert(list.FindIndex(x => x == 0 || x > cur), cur);
        foreach (var d in list)
        {
            var chip = new RadioButton
            {
                Style = (Style)Resources["Chip"],
                Content = d == 0 ? "永久" : $"{d} 天",
                Tag = d,
                IsChecked = d == cur,
            };
            chip.Checked += Retention_Changed;
            Retention.Children.Add(chip);
        }
        _lastRetention = cur;

        foreach (var mb in new[] { 1, 5, 10, 50 }.Union(new[] { S.LogMaxMB }).OrderBy(x => x))
        {
            var chip = new RadioButton { Style = (Style)Resources["Chip"], GroupName = "logsize", Content = $"{mb} MB", Tag = mb, IsChecked = mb == S.LogMaxMB };
            chip.Checked += LogSize_Changed;
            LogSize.Children.Add(chip);
        }

        RecordImages.IsChecked = S.RecordImages;
        ShowDateCards.IsChecked = S.ShowDateCards;
        GlassBox.IsChecked = S.Glass;
        GlassBox.IsEnabled = Glass.Supported;
        if (!Glass.Supported) GlassBox.Tag = "这台电脑的 Windows 版本不支持（需要 Windows 11 22H2+）";
        TakeOverWinV.IsChecked = S.TakeOverWinV;
        PasteAfterSelect.IsChecked = S.PasteAfterSelect;
        StartWithWindows.IsChecked = S.StartWithWindows;
        ShowLocation();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        Version.Text = $"版本 {v?.ToString(3)}";
        AboutVersion.Text = $"AgosClip {v?.ToString(3)}";
        Nav.SelectedIndex = 0;
        AutoUpdate.IsChecked = S.AutoCheckUpdates;
        ShowUpdate(App.Current.LatestUpdate);
        UpdateStats();
        _loading = false;
    }

    void UpdateStats()
    {
        var counts = App.Current.Store.DayCounts();
        int total = counts.Values.Sum();
        long bytes = 0;
        try
        {
            if (Directory.Exists(Store.ImagesDir))
                bytes = new DirectoryInfo(Store.ImagesDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { }
        Stats.Text = $"共 {total} 条，分布在 {counts.Count} 天";
        long logBytes = new[] { Feedback.LogFile, Feedback.OldLogFile }.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        long dbBytes = File.Exists(Store.DbFile) ? new FileInfo(Store.DbFile).Length : 0;
        StorageStats.Text = $"记录 {total} 条 · 数据库 {dbBytes / 1048576.0:0.#} MB · 图片 {bytes / 1048576.0:0.#} MB · 日志 {logBytes / 1048576.0:0.##} MB";
        LogStats.Text = $"当前占用 {logBytes / 1048576.0:0.##} MB；超出后自动删掉最旧的部分。日志不含复制的内容";
    }

    void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.RecordImages = RecordImages.IsChecked == true;
        S.ShowDateCards = ShowDateCards.IsChecked == true;
        S.TakeOverWinV = TakeOverWinV.IsChecked == true;
        S.PasteAfterSelect = PasteAfterSelect.IsChecked == true;
        S.StartWithWindows = StartWithWindows.IsChecked == true;
        S.Glass = GlassBox.IsChecked == true;
        S.AutoCheckUpdates = AutoUpdate.IsChecked == true;
        App.Current.ApplySettings();
        Glass.Apply(this);
    }

    void Retention_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: int days }) return;
        bool shrinking = days != 0 && (_lastRetention == 0 || days < _lastRetention);
        if (shrinking)
        {
            var r = MessageBox.Show(this, $"改为保留 {days} 天后，会立即删除更早的记录（固定的条目除外）。继续吗？",
                "AgosClip", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK)
            {
                _loading = true;
                foreach (RadioButton c in Retention.Children) c.IsChecked = (int)c.Tag == _lastRetention;
                _loading = false;
                return;
            }
        }
        _lastRetention = days;
        S.RetentionDays = days;
        App.Current.ApplySettings();
        UpdateStats();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Settings.DataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.DataDir}\"") { UseShellExecute = true });
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this, "清空全部历史记录吗？\n\n选“是”保留固定的条目，选“否”连固定的条目一起删除。",
            "清空历史", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (r == MessageBoxResult.Cancel) return;
        App.Current.Store.ClearAll(keepPinned: r == MessageBoxResult.Yes);
        UpdateStats();
    }

    // ───────────── updates ─────────────

    UpdateInfo? _update;
    CancellationTokenSource? _download;

    void ShowUpdate(UpdateInfo? u)
    {
        _update = u;
        if (u == null)
        {
            UpdateStatus.Text = $"当前版本 {Updater.Current.ToString(3)}";
            InstallBtn.Visibility = Visibility.Collapsed;
            UpdateNotes.Visibility = Visibility.Collapsed;
            return;
        }
        UpdateStatus.Text = $"当前版本 {Updater.Current.ToString(3)}，发现新版本 {u.Version.ToString(3)}（{u.Size / 1048576.0:0.#} MB）";
        UpdateNotes.Text = u.Notes;
        UpdateNotes.Visibility = string.IsNullOrWhiteSpace(u.Notes) ? Visibility.Collapsed : Visibility.Visible;
        InstallBtn.Content = $"下载并安装 {u.Version.ToString(3)}";
        InstallBtn.Visibility = Visibility.Visible;
    }

    /// <summary>Called by the app (tray "检查更新…") to start a check as soon as the window is open.</summary>
    public void StartCheck()
    {
        Nav.SelectedIndex = 3;
        Check_Click(this, new RoutedEventArgs());
    }

    internal void ShowPage(int i) => Nav.SelectedIndex = i;

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var pages = new FrameworkElement[] { PageGeneral, PageHistory, PageStorage, PageUpdate, PageAbout };
        for (int i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == Nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.ScrollToTop();
        if (Nav.SelectedIndex == 2 && !_loading) UpdateStats();
    }

    void LogSize_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: int mb }) return;
        S.LogMaxMB = mb;
        App.Current.ApplySettings();
        App.TrimLogs();
        UpdateStats();
    }

    async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_download != null) return;
        CheckBtn.IsEnabled = false;
        UpdateStatus.Text = "正在检查更新…";
        try
        {
            var u = await Updater.CheckAsync();
            App.Current.LatestUpdate = u;
            ShowUpdate(u);
            if (u == null) UpdateStatus.Text = $"当前版本 {Updater.Current.ToString(3)}，已经是最新版本";
        }
        catch (Exception ex)
        {
            App.Log("update check: " + ex.Message);
            UpdateStatus.Text = "检查失败：连不上 GitHub。可以稍后再试，或点“发布页”手动下载。";
        }
        finally { CheckBtn.IsEnabled = true; }
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_update == null) return;
        if (_download != null) { _download.Cancel(); return; }
        _download = new CancellationTokenSource();
        CheckBtn.IsEnabled = false;
        InstallBtn.Content = "取消下载";
        ProgressTrack.Visibility = Visibility.Visible;
        ProgressFill.Width = 0;
        BrowserBtn.Visibility = Visibility.Visible;
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Note != null) { UpdateStatus.Text = p.Note; return; }
            ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * p.Fraction);
            var speed = p.BytesPerSecond >= 1048576 ? $"{p.BytesPerSecond / 1048576:0.0} MB/s" : $"{p.BytesPerSecond / 1024:0} KB/s";
            UpdateStatus.Text = $"正在通过 {p.Source} 下载 {_update.Version.ToString(3)}… {p.Fraction:P0} · {speed}";
        });
        try
        {
            var path = await Updater.DownloadAsync(_update, progress, _download.Token);
            UpdateStatus.Text = "下载完成，正在安装。AgosClip 会关闭并在安装后自动重新打开。";
            await Task.Delay(800);
            Updater.RunInstaller(path);
        }
        catch (OperationCanceledException)
        {
            UpdateStatus.Text = "已取消下载";
        }
        catch (Exception ex)
        {
            App.Log("update download: " + ex);
            UpdateStatus.Text = ex is InvalidDataException ? ex.Message
                : "下载失败：" + ex.Message + "。可以再点一次继续（会接着已下载的部分），或点“用浏览器下载”。";
        }
        finally
        {
            _download = null;
            CheckBtn.IsEnabled = true;
            ProgressTrack.Visibility = Visibility.Collapsed;
            if (_update != null) InstallBtn.Content = $"下载并安装 {_update.Version.ToString(3)}";
        }
    }

    void Page_Click(object sender, RoutedEventArgs e) => Updater.OpenReleasesPage(_update?.PageUrl);

    void Browser_Click(object sender, RoutedEventArgs e)
    {
        if (_update != null) Updater.OpenUrl(Updater.BrowserUrl(_update));
    }

    // ───────────── data location ─────────────

    void ShowLocation()
    {
        bool custom = !Settings.IsDefaultLocation(Settings.DataDir);
        DataPath.Text = "位置：" + Settings.DataDir + (custom ? "（自定义）" : "（默认）");
        ResetBtn.Visibility = custom || S.DataLocation != null ? Visibility.Visible : Visibility.Collapsed;
    }

    async void Move_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择保存剪贴板历史的文件夹",
            InitialDirectory = Directory.Exists(Settings.DataDir) ? Path.GetDirectoryName(Settings.DataDir) : null,
        };
        if (dlg.ShowDialog(this) != true) return;
        await MoveTo(DataMover.ResolveTarget(dlg.FolderName));
    }

    async void ResetLocation_Click(object sender, RoutedEventArgs e)
    {
        if (Settings.IsDefaultLocation(Settings.DataDir))
        {
            // custom folder was missing at startup; we already run on the default → just forget it
            S.SetDataLocation(Settings.ConfigDir);
            ShowLocation();
            return;
        }
        await MoveTo(Settings.ConfigDir);
    }

    async Task MoveTo(string target)
    {
        var problem = DataMover.Validate(target);
        if (problem != null) { MessageBox.Show(this, problem, "更改位置", MessageBoxButton.OK, MessageBoxImage.Information); return; }

        bool existing = DataMover.HasData(target);
        string question = existing
            ? $"{target}\n\n这里已经有 AgosClip 的历史记录。改为使用这里的记录吗？\n\n当前位置的记录不会合并过去，会原样留在：\n{Settings.DataDir}"
            : $"把历史记录和图片（约 {DataMover.CurrentSize() / 1048576.0:0.#} MB）移动到：\n\n{target}\n\n复制完整后会删除原来位置的文件。移动期间暂停记录。";
        if (MessageBox.Show(this, question, "更改位置", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        SetBusy(true, existing ? "正在切换…" : "正在移动…");
        try
        {
            var progress = new Progress<string>(m => MoveStatus.Text = m);
            await DataMover.MoveAsync(App.Current.Store, S, target, existing, progress);
            MoveStatus.Text = existing ? "已切换到新位置的记录" : "移动完成";
        }
        catch (Exception ex)
        {
            App.Log("move data: " + ex);
            MoveStatus.Text = "移动失败，数据仍在原来的位置：" + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
            ShowLocation();
            UpdateStats();
        }
    }

    void SetBusy(bool busy, string? msg)
    {
        MoveBtn.IsEnabled = ResetBtn.IsEnabled = ClearBtn.IsEnabled = !busy;
        if (msg != null) MoveStatus.Text = msg;
        MoveStatus.Visibility = Visibility.Visible;
    }

    // ───────────── feedback & log ─────────────

    void Feedback_Click(object sender, RoutedEventArgs e) => App.Current.StartFeedback(this);
    void OpenLog_Click(object sender, RoutedEventArgs e) => Feedback.OpenLog();
    void OpenLogFolder_Click(object sender, RoutedEventArgs e) => Feedback.OpenLogFolder();

    // ───────────── immersive title bar ─────────────

    /// <summary>
    /// Windows 11 draws its own caption buttons onto the glass frame of any window with a system menu,
    /// which showed up as ghost min/max/close buttons behind ours. Drop WS_SYSMENU (and WS_MAXIMIZEBOX)
    /// so DWM draws nothing there; keep WS_MINIMIZEBOX so clicking the taskbar icon still minimises.
    /// </summary>
    internal void HideSystemCaptionButtons()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        const long WS_SYSMENU = 0x00080000, WS_MAXIMIZEBOX = 0x00010000, WS_MINIMIZEBOX = 0x00020000;
        long style = (long)Native.GetWindowLongPtr(hwnd, -16);
        long want = (style & ~(WS_SYSMENU | WS_MAXIMIZEBOX)) | WS_MINIMIZEBOX;
        if (want != style)
        {
            Native.SetWindowLongPtr(hwnd, -16, (IntPtr)want);
            Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED
        }
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
}
