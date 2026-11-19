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
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        SourceInitialized += (_, _) => Glass.Apply(this);

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

        RecordImages.IsChecked = S.RecordImages;
        GlassBox.IsChecked = S.Glass;
        GlassBox.IsEnabled = Glass.Supported;
        if (!Glass.Supported) GlassBox.Tag = "这台电脑的 Windows 版本不支持（需要 Windows 11 22H2 以上）。";
        TakeOverWinV.IsChecked = S.TakeOverWinV;
        PasteAfterSelect.IsChecked = S.PasteAfterSelect;
        StartWithWindows.IsChecked = S.StartWithWindows;
        ShowLocation();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        Version.Text = $"AgosClip {v?.ToString(3)} · 剪贴板数据只保存在本机，只有检查更新时会联网";
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
        Stats.Text = $"共 {total} 条记录，分布在 {counts.Count} 天；图片占用 {bytes / 1048576.0:0.#} MB";
    }

    void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.RecordImages = RecordImages.IsChecked == true;
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
    public void StartCheck() => Check_Click(this, new RoutedEventArgs());

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
        var progress = new Progress<double>(p =>
        {
            ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * p);
            UpdateStatus.Text = $"正在下载 {_update.Version.ToString(3)}… {p:P0}";
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
            UpdateStatus.Text = ex is InvalidDataException ? ex.Message : "下载失败：连接 GitHub 太慢或中断了。可以重试，或点“发布页”手动下载安装包。";
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
}
