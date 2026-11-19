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

        int st = Array.FindIndex(e.Args, a => a == "--selftest");
        if (st >= 0)
        {
            Shutdown(SelfTest.Run(this, st + 1 < e.Args.Length ? e.Args[st + 1] : "selftest-out"));
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
        bool firstRun = !Settings.FirstRunDone;
        if (firstRun)
        {
            Settings.FirstRunDone = true;
            Settings.Save();
        }
        try { Settings.ApplyAutoStart(Settings.StartWithWindows); } catch (Exception ex) { Log("autostart: " + ex.Message); }
        RememberInstallDir();

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
        _hook.Pressed += () => TogglePanel(Native.GetForegroundWindow());
        if (Settings.TakeOverWinV) _hook.Enable();

        _panel = new PanelWindow();
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
        menu.Items.Add("设置…", null, (_, _) => ShowSettings());
        menu.Items.Add("检查更新…", null, (_, _) => ShowSettings(checkUpdate: true));
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
        _tray.BalloonTipClicked += (_, _) => { if (_balloonIsUpdate) ShowSettings(); };
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
        if (_panel.IsVisible) _panel.HidePanel();
        else ShowPanel(target);
    }

    public void ShowPanel(IntPtr target)
    {
        if (_panel == null || Moving) return;
        _panel.ShowFor(target);
    }

    bool _balloonIsUpdate;

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
            _balloonIsUpdate = true;
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
        Theme.Apply();
        if (Settings.TakeOverWinV) _hook?.Enable(); else _hook?.Disable();
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

    public void Quit()
    {
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
    public static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Settings.ConfigDir);
                var path = Path.Combine(Settings.ConfigDir, "agosclip.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
            }
        }
        catch { }
        Debug.WriteLine(msg);
    }
}
