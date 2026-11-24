using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AgosClip;

public partial class App : Application
{
    const string MutexName = @"Local\AgosClip.SingleInstance";
    const string ShowEventName = @"Local\AgosClip.Show";

    public static new App Current => (App)Application.Current;
    public static bool Paused { get; private set; }
    /// <summary>True while the data folder is being moved: nothing is recorded and the panel stays closed.</summary>
    public static bool Moving { get; set; }

    public Settings Settings { get; internal set; } = new();
    public Store Store { get; internal set; } = null!;

    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    ClipboardMonitor? _monitor;
    WinVHook? _hook;
    Forms.NotifyIcon? _tray;
    Forms.ToolStripMenuItem? _pauseItem;
    PanelWindow? _panel;
    SettingsWindow? _settingsWnd;
    DispatcherTimer? _cleanupTimer, _updateTimer;

    public UpdateInfo? LatestUpdate { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) => { Log("unhandled: " + a.Exception); a.Handled = true; };

        int searchTest = Array.FindIndex(e.Args, a => a == "--selftest-search");
        if (searchTest >= 0)
        {
            Shutdown(SelfTest.RunSearchUi(this, searchTest + 1 < e.Args.Length ? e.Args[searchTest + 1] : "selftest-search-out"));
            return;
        }

        int st = Array.FindIndex(e.Args, a => a is "--selftest" or "--selftest-animation");
        if (st >= 0)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGOSCLIP_DATA")))
            {
                Shutdown(2); // self-tests must never open the normal history directory
                return;
            }
            Shutdown(SelfTest.Run(this, st + 1 < e.Args.Length ? e.Args[st + 1] : "selftest-out",
                animationOnly: e.Args[st] == "--selftest-animation"));
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running: ask that instance to open its panel, then quit.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => ShowPanel(IntPtr.Zero)); // launched again from Start menu: just open, no auto-paste
        }) { IsBackground = true, Name = "show-signal" }.Start();

        bool background = e.Args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));

        Settings = Settings.Load();
        bool dataFolderMissing = !Settings.ApplyDataLocation();
        if (dataFolderMissing) Log($"data folder missing: {Settings.DataLocation}; using {Settings.DataDir} for now");
        Settings.RememberDataDir();
        LogLimitBytes = Math.Max(1, Settings.LogMaxMB) * (1L << 20);
        if (!Settings.IsDefaultLocation(Settings.DataDir)) MoveLogs(Settings.ConfigDir); // logs used to live in %APPDATA%
        Log($"start v{Updater.Current.ToString(3)} on Windows {Environment.OSVersion.Version} from {AppContext.BaseDirectory}" +
            (background ? " (autostart)" : "") + (dataFolderMissing ? " [data folder missing]" : ""));
        System.Threading.Tasks.Task.Run(Plugins.Sweep); // old plugin folders a running copy still held
        bool firstRun = !Settings.FirstRunDone;
        if (firstRun)
        {
            Settings.FirstRunDone = true;
            Settings.Save();
        }
        if (Settings.UsesDefaultConfigDir)
        {
            try { Settings.ApplyAutoStart(Settings.StartWithWindows); } catch (Exception ex) { Log("autostart: " + ex.Message); }
            RememberInstallDir();
        }

        Theme.Apply();
        SystemEvents.UserPreferenceChanged += (_, a) =>
        {
            if (a.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color) Dispatcher.BeginInvoke(() => Theme.Apply());
        };

        Store = new Store();
        RunCleanup();
        _cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _cleanupTimer.Tick += (_, _) => RunCleanup();
        _cleanupTimer.Start();

        _monitor = new ClipboardMonitor(Store, () => Settings);
        _hook = new WinVHook(Dispatcher);
        _hook.Pressed += TogglePanel;
        if (Settings.TakeOverWinV) _hook.Enable();
        _shotHotkey = new GlobalHotkey();
        _shotHotkey.Pressed += StartScreenshot;
        ApplyScreenshotHotkey();
        _recHotkey = new GlobalHotkey();
        _recHotkey.Pressed += ToggleRecording;
        ApplyRecordHotkey();

        _panel = new PanelWindow();
        Watchdog.Start(Dispatcher);
        CreateTray();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);
            await AutoCheckUpdate();
        };
        _updateTimer.Start();

        if (dataFolderMissing)
        {
            _tray!.ShowBalloonTip(8000, "找不到数据文件夹",
                $"{Settings.DataLocation} 不存在（移动硬盘没插上？）。这次先用默认位置记录，插上后重启 AgosClip 即可。",
                Forms.ToolTipIcon.Warning);
        }
        else if (e.Args.Any(a => a == "--updated"))
        {
            _tray!.ShowBalloonTip(4000, "AgosClip 已更新", $"现在是 {Updater.Current.ToString(3)} 版。", Forms.ToolTipIcon.Info);
        }
        else if (!background)
        {
            _tray!.ShowBalloonTip(4000, "AgosClip 已在后台运行",
                Settings.TakeOverWinV ? "按 Win+V 打开剪贴板历史。托盘图标右键可打开设置。" : "双击托盘图标打开剪贴板历史。",
                Forms.ToolTipIcon.Info);
        }
    }

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开剪贴板", null, (_, _) => ShowPanel(IntPtr.Zero));
        _pauseItem = new Forms.ToolStripMenuItem("暂停记录", null, (_, _) => SetPaused(!Paused));
        menu.Items.Add(_pauseItem);
        _shotItem = new Forms.ToolStripMenuItem("截图", null, (_, _) => StartScreenshot()) { Enabled = Settings.EnableScreenshot };
        menu.Items.Add(_shotItem);
        _recItem = new Forms.ToolStripMenuItem("录屏", null, (_, _) => ToggleRecording()) { Visible = Settings.EnableRecording };
        menu.Items.Add(_recItem);
        menu.Opening += (_, _) => { if (_recItem != null) _recItem.Text = ScreenRecorder.IsActive ? "结束录屏并保存" : "录屏"; };
        menu.Items.Add("关闭所有钉图", null, (_, _) => PinWindow.CloseAll());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("设置…", null, (_, _) => ShowSettings());
        menu.Items.Add("检查更新…", null, (_, _) => ShowSettings(checkUpdate: true));
        menu.Items.Add("反馈问题…", null, (_, _) => StartFeedback(null));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 AgosClip", null, (_, _) => Quit());

        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/AgosClip.ico")).Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize),
            Text = "AgosClip 剪贴板",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ShowPanel(IntPtr.Zero); };
        _tray.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };
    }

    void SetPaused(bool paused)
    {
        Paused = paused;
        if (_pauseItem != null) _pauseItem.Checked = paused;
        if (_tray != null) _tray.Text = paused ? "AgosClip 剪贴板（已暂停记录）" : "AgosClip 剪贴板";
    }

    public void TogglePanel(IntPtr target)
    {
        if (_panel == null) return;
        if (_panel.IsOpening) { _panel.HidePanel(); return; }
        if (_panel.IsVisible && !_panel.IsClosing)
        {
            // pinned panel sitting in the background: Win+V brings it forward; a second Win+V closes it
            if (_panel.IsSticky && !_panel.IsActive) _panel.FocusSticky();
            else _panel.HidePanel();
        }
        else ShowPanel(target);
    }

    public void ShowPanel(IntPtr target)
    {
        if (_panel == null || Moving) return;
        _panel.ShowFor(target);
    }

    Action? _balloonClick;

    /// <summary>Tray tooltip says a recording is running (and how to end it), even when its bar can't be seen.</summary>
    public void SetRecordingTray(bool on)
    {
        if (_tray == null) return;
        var hk = HotkeyCombo.Parse(Settings.RecordHotkey);
        var text = on ? $"AgosClip 正在录屏 · {(hk != null ? hk + " 或" : "")}右键菜单结束" : Paused ? "AgosClip 剪贴板（已暂停记录）" : "AgosClip 剪贴板";
        _tray.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon limit
    }

    /// <summary>Tray notification; <paramref name="onClick"/> runs if the user clicks it.</summary>
    public void Notify(string title, string text, Action? onClick, bool warning = false)
    {
        _balloonClick = onClick;
        _tray?.ShowBalloonTip(6000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    async System.Threading.Tasks.Task AutoCheckUpdate()
    {
        if (!Settings.AutoCheckUpdates) return;
        try
        {
            var u = await Updater.CheckAsync();
            LatestUpdate = u ?? LatestUpdate;
            if (u == null || Settings.NotifiedVersion == u.Version.ToString(3)) return;
            Settings.NotifiedVersion = u.Version.ToString(3);
            Settings.Save();
            _balloonClick = () => ShowSettings();
            _tray?.ShowBalloonTip(6000, $"AgosClip 有新版本 {u.Version.ToString(3)}", "点这里打开设置，一键升级。", Forms.ToolTipIcon.Info);
        }
        catch (Exception ex) { Log("auto update check: " + ex.Message); }
    }

    /// <summary>Lets the installer default to wherever the user put AgosClip, even if the uninstall record is gone.</summary>
    static void RememberInstallDir()
    {
        try
        {
            var dir = AppContext.BaseDirectory.TrimEnd('\\');
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\AgosClip");
            k.SetValue("InstallDir", dir);
        }
        catch (Exception ex) { Log("remember dir: " + ex.Message); }
    }

    /// <summary>Asks first (the issue is public), then runs the one-click feedback flow.</summary>
    public void StartFeedback(Window? owner)
    {
        const string msg = "将会：\n\n" +
            "1. 生成一个诊断文件（版本、系统信息、设置、记录条数和最近的日志）。\n" +
            "   不含任何剪贴板内容，用户名、电脑名和用户目录会被替换。\n" +
            "2. 在资源管理器里选中这个文件，并把它复制到剪贴板。\n" +
            "3. 打开 GitHub 的新建问题页面，内容已经预先填好。\n\n" +
            "在页面里把文件拖进输入框（或按 Ctrl+V），写几句发生了什么，再点提交即可。\n" +
            "注意：GitHub 上的问题是公开的，需要登录 GitHub 账号。\n\n继续吗？";
        var r = owner != null
            ? MessageBox.Show(owner, msg, "一键反馈", MessageBoxButton.OKCancel, MessageBoxImage.Information)
            : MessageBox.Show(msg, "一键反馈", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (r != MessageBoxResult.OK) return;
        try { Feedback.Start(); }
        catch (Exception ex)
        {
            Log("feedback failed: " + ex);
            MessageBox.Show("生成诊断文件失败：" + ex.Message, "一键反馈", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void ShowSettings(bool checkUpdate = false)
    {
        _panel?.HidePanel();
        if (_settingsWnd == null)
        {
            _settingsWnd = new SettingsWindow();
            _settingsWnd.Closed += (_, _) => _settingsWnd = null;
            _settingsWnd.Show();
        }
        _settingsWnd.WindowState = WindowState.Normal;
        if (checkUpdate) _settingsWnd.StartCheck();
        _settingsWnd.Activate();
        Native.ForceForeground(new System.Windows.Interop.WindowInteropHelper(_settingsWnd).Handle);
    }

    /// <summary>Called by the settings window after any change.</summary>
    public void ApplySettings()
    {
        Settings.Save();
        LogLimitBytes = Math.Max(1, Settings.LogMaxMB) * (1L << 20);
        Theme.Apply();
        if (Settings.TakeOverWinV) _hook?.Enable(); else _hook?.Disable();
        ApplyScreenshotHotkey();
        ApplyRecordHotkey();
        if (_shotItem != null) _shotItem.Enabled = Settings.EnableScreenshot;
        if (_recItem != null) _recItem.Visible = Settings.EnableRecording;
        if (Settings.UsesDefaultConfigDir)
            try { Settings.ApplyAutoStart(Settings.StartWithWindows); } catch (Exception ex) { Log("autostart: " + ex.Message); }
        RunCleanup();
    }

    public void RunCleanup()
    {
        try
        {
            int n = Store.Cleanup(Settings.RetentionDays);
            if (n > 0) Log($"cleanup removed {n} items");
        }
        catch (Exception ex) { Log("cleanup: " + ex); }
    }

    // ───────────── screenshot & pin ─────────────

    GlobalHotkey? _shotHotkey;
    Forms.ToolStripMenuItem? _shotItem;

    /// <summary>True if the configured hotkey is active (false = disabled, empty, or taken by another program).</summary>
    public bool ScreenshotHotkeyActive => _shotHotkey?.Current != null;

    /// <summary>Registers Settings.ScreenshotHotkey. Returns false if another program already uses it.</summary>
    public bool ApplyScreenshotHotkey()
    {
        if (_shotHotkey == null) return false;
        if (!Settings.EnableScreenshot) { _shotHotkey.Clear(); return true; }
        return _shotHotkey.Set(HotkeyCombo.Parse(Settings.ScreenshotHotkey));
    }

    /// <summary>Lets the settings page record a new combination without the old ones firing.</summary>
    public void SuspendScreenshotHotkey(bool suspend)
    {
        if (suspend) { _shotHotkey?.Clear(); _recHotkey?.Clear(); }
        else { ApplyScreenshotHotkey(); ApplyRecordHotkey(); }
    }

    /// <summary>Tries a new screenshot combination; keeps the old one if the new one is taken.</summary>
    public bool TrySetScreenshotHotkey(HotkeyCombo combo)
    {
        if (combo == HotkeyCombo.Parse(Settings.RecordHotkey) && Settings.EnableRecording) return false; // already 录屏
        var old = Settings.ScreenshotHotkey;
        Settings.ScreenshotHotkey = combo.ToString();
        if (_shotHotkey == null || !Settings.EnableScreenshot || _shotHotkey.Set(combo)) { Settings.Save(); return true; }
        Settings.ScreenshotHotkey = old;
        ApplyScreenshotHotkey();
        return false;
    }

    // ───────────── screen recording ─────────────

    GlobalHotkey? _recHotkey;
    Forms.ToolStripMenuItem? _recItem;

    public bool RecordHotkeyActive => _recHotkey?.Current != null;

    public bool ApplyRecordHotkey()
    {
        if (_recHotkey == null) return false;
        if (!Settings.EnableRecording) { _recHotkey.Clear(); return true; }
        return _recHotkey.Set(HotkeyCombo.Parse(Settings.RecordHotkey));
    }

    /// <summary>Tries a new record combination; keeps the old one if the new one is taken (also by 截图).</summary>
    public bool TrySetRecordHotkey(HotkeyCombo combo)
    {
        if (combo == HotkeyCombo.Parse(Settings.ScreenshotHotkey) && Settings.EnableScreenshot) return false;
        var old = Settings.RecordHotkey;
        Settings.RecordHotkey = combo.ToString();
        if (_recHotkey == null || !Settings.EnableRecording || _recHotkey.Set(combo)) { Settings.Save(); return true; }
        Settings.RecordHotkey = old;
        ApplyRecordHotkey();
        return false;
    }

    /// <summary>Hotkey / tray: pick a region and start recording, or end (and save) the running one.</summary>
    public void ToggleRecording()
    {
        if (ScreenRecorder.Active is { } r) { _ = r.StopAsync(save: true); return; }
        if (!Settings.EnableRecording || ScreenRecorder.IsActive || CaptureSession.IsActive || LongShot.IsActive) return;
        if (_panel is { IsVisible: true, IsSticky: false })
        {
            _panel.HidePanel();
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            t.Tick += (_, _) => { t.Stop(); CaptureSession.Start(CaptureMode.Record); };
            t.Start();
        }
        else CaptureSession.Start(CaptureMode.Record);
    }

    public void StartScreenshot()
    {
        if (!Settings.EnableScreenshot || CaptureSession.IsActive) return;
        // our own panel must not end up in the picture
        if (_panel is { IsVisible: true, IsSticky: false })
        {
            _panel.HidePanel();
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            t.Tick += (_, _) => { t.Stop(); CaptureSession.Start(); };
            t.Start();
        }
        else CaptureSession.Start();
    }

    public void Quit()
    {
        // a recording in progress is finished and saved first, or the MP4 would be unplayable
        if (ScreenRecorder.Active is { } rec)
        {
            var done = rec.StopAsync(save: true);
            var frame = new DispatcherFrame();
            _ = done.ContinueWith(_ => frame.Continue = false);
            var limit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            limit.Tick += (_, _) => { limit.Stop(); frame.Continue = false; };
            limit.Start();
            Dispatcher.PushFrame(frame);
            limit.Stop();
        }
        PinWindow.CloseAll();
        _shotHotkey?.Dispose();
        _recHotkey?.Dispose();
        _hook?.Dispose();
        _monitor?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Store?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        if (_tray != null) _tray.Visible = false;
        base.OnExit(e);
    }

    static readonly object LogLock = new();
    /// <summary>agosclip.log + agosclip.old.log together stay under this (Settings.LogMaxMB).</summary>
    public static long LogLimitBytes = 5L << 20;

    /// <summary>Logs live in the data folder, next to the history (they move with it).</summary>
    public static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Settings.DataDir);
                var path = Feedback.LogFile;
                // rotate at half the limit, keeping one previous file → both together ≤ limit
                if (File.Exists(path) && new FileInfo(path).Length > LogLimitBytes / 2)
                    File.Move(path, Feedback.OldLogFile, true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
            }
        }
        catch { }
        Debug.WriteLine(msg);
    }

    /// <summary>Applies a (possibly smaller) limit right away: keeps only the newest part of each file.</summary>
    public static void TrimLogs()
    {
        lock (LogLock)
        {
            foreach (var f in new[] { Feedback.LogFile, Feedback.OldLogFile })
                try { KeepTail(f, LogLimitBytes / 2); } catch { }
        }
    }

    static void KeepTail(string path, long max)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= max) return;
        byte[] tail;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(-max, SeekOrigin.End);
            tail = new byte[max];
            fs.ReadExactly(tail);
        }
        int nl = Array.IndexOf(tail, (byte)'\n');          // start on a whole line
        File.WriteAllBytes(path, nl >= 0 && nl + 1 < tail.Length ? tail[(nl + 1)..] : tail);
    }

    /// <summary>Moves log files from one folder into the current data folder (startup migration, data moves).</summary>
    public static void MoveLogs(string fromDir)
    {
        lock (LogLock)
        {
            foreach (var name in new[] { "agosclip.old.log", "agosclip.log" })
            {
                var src = Path.Combine(fromDir, name);
                var dst = Path.Combine(Settings.DataDir, name);
                if (!File.Exists(src) || string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (!File.Exists(dst)) File.Move(src, dst);
                    else
                    {
                        // keep chronological order: older lines first
                        var merged = File.ReadAllText(src) + File.ReadAllText(dst);
                        File.WriteAllText(dst, merged);
                        File.Delete(src);
                    }
                }
                catch { }
            }
        }
        TrimLogs();
    }
}
