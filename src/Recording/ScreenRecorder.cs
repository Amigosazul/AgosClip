using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AgosClip;

/// <summary>
/// Records a screen region to an MP4. The region comes from the screenshot overlay in record mode
/// (<see cref="CaptureSession"/>). A capture thread grabs the region at the chosen frame rate and feeds
/// <see cref="VideoEncoder"/>; an audio thread mixes system sound and/or the microphone
/// (<see cref="AudioRecorder"/>) onto the same timeline, so pauses cut sound and picture together.
///
/// On screen while recording, kept out of the video (outside the region, and excluded from capture
/// where Windows supports it): a red click-through frame around the region and a small bar with
/// ● 00:12 · 暂停/继续 · 结束并保存 · 放弃. The record hotkey pressed again also ends and saves.
/// </summary>
public sealed class ScreenRecorder
{
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    static ScreenRecorder? _active;
    /// <summary>A recording is running, paused or still being written.</summary>
    public static bool IsActive => _active != null || _starting;
    internal static ScreenRecorder? Active => _active;

    /// <summary>Raised after the file is complete (path, or null if discarded/failed) — self-test hook.</summary>
    internal static event Action<string?, ScreenRecorder>? Finished;

    enum State { Recording, Paused, Finishing }

    readonly Int32Rect _region;
    readonly VideoEncoder _enc;
    readonly Func<IFrameSource> _source;
    readonly int _fps;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly object _clockLock = new();
    TimeSpan _pausedFor, _pauseStart;
    volatile State _state = State.Recording;
    volatile bool _stop;
    Thread? _thread, _audioThread;
    Task? _finishing;
    readonly AudioRecorder? _audio;
    /// <summary>Sound inputs that were asked for but couldn't be opened (said in the final notice).</summary>
    string? _audioProblem;

    Window? _frame, _bar;
    TextBlock? _time;
    Ellipse? _dot;
    Button? _pauseBtn;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };

    internal Int32Rect Region => _region;
    internal VideoEncoder Encoder => _enc;
    internal AudioRecorder? Audio => _audio;
    internal bool Paused => _state == State.Paused;
    internal Window? Bar => _bar;
    internal string TimeText => _time?.Text ?? "";

    ScreenRecorder(Int32Rect region, VideoEncoder enc, Func<IFrameSource> source, int fps, AudioRecorder? audio)
    {
        (_region, _enc, _source, _fps, _audio) = (region, enc, source, fps, audio);
    }

    static bool _starting;

    /// <summary>Starts recording a region (physical pixels) with the user's settings.</summary>
    public static async void Start(Int32Rect region)
    {
        if (_active != null || _starting) return;
        var s = App.Current.Settings;
        var (w, h) = VideoEncoder.EvenSize(region.Width, region.Height);
        if (w < 32 || h < 32) { App.Current.Notify("录屏区域太小", "请选择至少 32 × 32 像素的区域。", null); return; }
        region = new Int32Rect(region.X, region.Y, w, h);
        string path = NewFilePath(s.RecordDir);
        _starting = true;
        AudioRecorder? audio = null;
        try
        {
            string? problem = null;
            if (s.RecordSystemAudio || s.RecordMic) audio = AudioRecorder.TryCreate(s.RecordSystemAudio, s.RecordMic, out problem);
            var enc = await VideoEncoder.StartAsync(path, w, h, s.RecordFps, audio: audio != null);
            bool cursor = s.RecordCursor;
            var r = new ScreenRecorder(region, enc, () => new FrameGrabber(region, cursor), s.RecordFps, audio) { _audioProblem = problem };
            _active = r;
            r.Begin(showUi: true);
        }
        catch (Exception ex)
        {
            audio?.Dispose();
            App.Log("recording: could not start: " + ex);
            try { if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path); } catch { }
            App.Current.Notify("无法开始录屏", "这台电脑的 Windows 没有可用的 H.264 视频编码器，或保存位置无法写入。详情见日志。", null);
        }
        finally { _starting = false; }
    }

    /// <summary>Self-test: record from a fake frame source.</summary>
    internal static async Task<ScreenRecorder> StartForTest(Int32Rect region, string path, Func<IFrameSource> source, int fps, bool showUi,
        bool? hardware = null, AudioRecorder? audio = null)
    {
        var enc = await VideoEncoder.StartAsync(path, region.Width, region.Height, fps, audio: audio != null, hardware: hardware);
        var r = new ScreenRecorder(new Int32Rect(region.X, region.Y, enc.Width, enc.Height), enc, source, fps, audio);
        _active = r;
        r.Begin(showUi);
        return r;
    }

    internal static string NewFilePath(string dir)
    {
        var name = $"AgosClip 录屏 {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        var path = System.IO.Path.Combine(dir, name + ".mp4");
        for (int i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(dir, $"{name} ({i}).mp4");
        return path;
    }

    void Begin(bool showUi)
    {
        if (showUi) BuildUi();
        _thread = new Thread(Loop) { IsBackground = true, Name = "AgosClip recorder", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        if (_audio != null)
        {
            _audio.Reset(); // sound from before this moment isn't part of the recording
            _audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "AgosClip recorder audio", Priority = ThreadPriority.AboveNormal };
            _audioThread.Start();
        }
        App.Current.SetRecordingTray(true);
        _tick.Tick += (_, _) => UpdateBar();
        _tick.Start();
        App.Log($"recording: start {_region.Width}x{_region.Height} at {_region.X},{_region.Y}, {_fps} fps, {(_enc.Hardware ? "hardware" : "software")} encoder, sound: {_audio?.Description ?? "none"} → {_enc.Path}");
    }

    /// <summary>Recorded time so far, pauses left out — also the timestamp of the next frame.</summary>
    internal TimeSpan Elapsed
    {
        get
        {
            lock (_clockLock)
                return _clock.Elapsed - _pausedFor - (_state == State.Paused ? _clock.Elapsed - _pauseStart : TimeSpan.Zero);
        }
    }

    void Loop()
    {
        timeBeginPeriod(1); // 1 ms sleeps, or 30 fps comes out as an uneven 21–32 fps
        try
        {
            using var src = _source();
            var interval = TimeSpan.FromSeconds(1.0 / _fps);
            var next = _clock.Elapsed;
            while (!_stop)
            {
                if (_enc.Failed) { App.Current.Dispatcher.BeginInvoke(() => _ = StopAsync(save: true, error: "视频编码出错")); break; }
                if (_state == State.Recording && _enc.Rent() is byte[] buf)
                {
                    var at = Elapsed;
                    if (src.Grab(buf)) _enc.Submit(buf, at); else _enc.Return(buf);
                }
                next += interval;
                var wait = next - _clock.Elapsed;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                else if (wait < -interval) next = _clock.Elapsed; // fell behind: don't try to catch up in a burst
            }
        }
        catch (Exception ex)
        {
            App.Log("recording: capture failed: " + ex);
            App.Current.Dispatcher.BeginInvoke(() => _ = StopAsync(save: true, error: "抓取屏幕失败"));
        }
        finally { timeEndPeriod(1); }
    }

    /// <summary>Every 20 ms: mix the sound that has arrived (kept <see cref="AudioRecorder.Lag"/> behind the clock).</summary>
    void AudioLoop()
    {
        try
        {
            while (!_stop)
            {
                if (_state == State.Recording) SubmitAudio(Elapsed - AudioRecorder.Lag);
                Thread.Sleep(20);
            }
        }
        catch (Exception ex) { App.Log("recording: audio failed: " + ex); }
    }

    void SubmitAudio(TimeSpan upTo)
    {
        if (_audio == null) return;
        while (_audio.Pull(upTo) is { } chunk) _enc.SubmitAudio(chunk.Pcm, chunk.At, chunk.Duration);
    }

    internal void TogglePause()
    {
        lock (_clockLock)
        {
            if (_state == State.Recording)
            {
                SubmitAudio(Elapsed); // everything up to the pause, nothing after it
                _pauseStart = _clock.Elapsed;
                _state = State.Paused;
            }
            else if (_state == State.Paused)
            {
                _audio?.Reset(); // drop what played while paused: sound resumes exactly where it stopped
                _pausedFor += _clock.Elapsed - _pauseStart;
                _state = State.Recording;
            }
        }
        UpdateBar();
    }

    /// <summary>Ends the recording; with save=false the file is deleted. Safe to call more than once.</summary>
    internal Task StopAsync(bool save, string? error = null) => _finishing ??= Finish(save, error);

    async Task Finish(bool save, string? error)
    {
        var duration = Elapsed;
        lock (_clockLock) { if (_state == State.Paused) _pausedFor += _clock.Elapsed - _pauseStart; _state = State.Finishing; }
        _stop = true;
        _tick.Stop();
        CloseUi();
        App.Current.SetRecordingTray(false);
        await Task.Run(() => { _thread?.Join(3000); _audioThread?.Join(3000); });
        if (_audio != null)
        {
            try { SubmitAudio(duration); } catch (Exception ex) { App.Log("recording: last audio: " + ex.Message); }
            _audio.Dispose();
        }
        string? path = _enc.Path;
        try { await _enc.FinishAsync(); }
        catch (Exception ex)
        {
            App.Log("recording: encoder finished with an error: " + ex.Message);
            error ??= "视频编码出错";
        }
        long size = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (!save || _enc.FramesWritten == 0 || size == 0)
        {
            try { File.Delete(path); } catch { }
            path = null;
        }
        _active = null;
        App.Log($"recording: {(path != null ? "saved" : "discarded")} {duration:hh\\:mm\\:ss}, {_enc.FramesWritten} frames ({_enc.FramesDropped} skipped), {size / 1048576.0:0.0} MB, sound {_enc.AudioBytes / 192000.0:0.0} s{(error != null ? ", " + error : "")}");
        if (Finished != null) { Finished(path, this); return; }

        if (path == null)
        {
            if (error != null) App.Current.Notify("录屏失败", error + "，没有保存视频。详情见日志。", null);
            return;
        }
        bool copied = App.Current.Settings.RecordCopyFile
                      && ClipboardWriter.Write(new ClipboardWriter.Payload().Files(new[] { path }));
        var saved = path;
        App.Current.Notify(error != null ? "录屏已保存（提前结束：" + error + "）" : "录屏已保存",
            $"{System.IO.Path.GetFileName(path)} · {FormatTime(duration)} · {size / 1048576.0:0.#} MB"
            + (_audioProblem != null ? $"\n没能录到{_audioProblem}（设备不可用或被系统隐私设置禁止）。" : "")
            + (copied ? "\n已复制视频文件，可以直接粘贴发送。" : "") + "\n点这里打开所在文件夹。",
            () => { try { Process.Start("explorer.exe", $"/select,\"{saved}\""); } catch { } });
    }

    internal static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    // ───────────── on-screen frame and control bar ─────────────

    readonly System.Collections.Generic.List<Window> _veils = new();
    internal System.Collections.Generic.IReadOnlyList<Window> Veils => _veils;
    internal Button? StopButton { get; private set; }

    /// <summary>
    /// A light grey click-through veil over everything except the region, one window per monitor, so it is
    /// obvious what is being recorded. It never covers the region, so it is never in the video; clicks and
    /// the mouse wheel pass straight through to the programs underneath.
    /// </summary>
    void BuildVeils()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x6E, 0x8A, 0x8A, 0x8A));
        brush.Freeze();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var b = screen.Bounds;
            bool coveredByRegion = _region.X <= b.X && _region.Y <= b.Y
                && _region.X + _region.Width >= b.Right && _region.Y + _region.Height >= b.Bottom;
            if (coveredByRegion) continue; // full-screen recording of this monitor: nothing to grey out
            var outer = new RectangleGeometry();
            var hole = new RectangleGeometry();
            var path = new System.Windows.Shapes.Path
            {
                Fill = brush, IsHitTestVisible = false,
                Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { outer, hole } },
            };
            var w = LongShot.Floating(new Grid { Children = { path } }, clickThrough: true);
            w.SizeToContent = SizeToContent.Manual;
            w.Show();
            Native.SetWindowPos(new WindowInteropHelper(w).Handle, new IntPtr(-1), b.X, b.Y, b.Width, b.Height, 0x0010);
            void Layout()
            {
                if (w.ActualWidth <= 0 || w.ActualHeight <= 0) return;
                double sx = b.Width / w.ActualWidth, sy = b.Height / w.ActualHeight; // physical px per DIP here
                outer.Rect = new Rect(0, 0, w.ActualWidth, w.ActualHeight);
                // 1 DIP bigger than the region: rounding must never leave grey inside the recorded pixels
                var r = new Rect((_region.X - b.X) / sx, (_region.Y - b.Y) / sy, _region.Width / sx, _region.Height / sy);
                r.Inflate(1, 1);
                hole.Rect = r;
            }
            w.SizeChanged += (_, _) => Layout();
            Layout();
            _veils.Add(w);
        }
    }

    void BuildUi()
    {
        var red = Color.FromRgb(0xFF, 0x3B, 0x30);
        BuildVeils(); // first, so the frame and the bar end up above it
        _frame = LongShot.Floating(new Border { BorderBrush = new SolidColorBrush(red), BorderThickness = new Thickness(2) }, clickThrough: true);
        _frame.SizeToContent = SizeToContent.Manual;
        _frame.Show();
        var fh = new WindowInteropHelper(_frame).Handle;
        // The frame sits 3 px outside the region, so it is never in the video. It is deliberately NOT
        // excluded from capture: remote-desktop tools (UU远程, ToDesk, …) capture the screen the same way
        // and would then show nothing at all — the recording looked like it had crashed.
        Native.SetWindowPos(fh, new IntPtr(-1), _region.X - 3, _region.Y - 3, _region.Width + 6, _region.Height + 6, 0x0010);

        _dot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(red), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _time = new TextBlock { Text = "00:00", FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 46 };
        _time.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var size = new TextBlock { Text = $"{_region.Width}×{_region.Height}", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 1, 8, 0) };
        size.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = "拖动可以移开" };
        var grip = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Children = { _dot, _time, size } };
        row.Children.Add(grip);
        _pauseBtn = Icon("", "暂停", TogglePause);
        row.Children.Add(_pauseBtn);
        var hk = HotkeyCombo.Parse(App.Current.Settings.RecordHotkey);
        row.Children.Add(Icon("", "放弃这段录屏（不保存）", ConfirmDiscard, red));
        var stop = new Button
        {
            Style = (Style)Application.Current.Resources["PillButton"], Padding = new Thickness(12, 6, 14, 6), Margin = new Thickness(4, 0, 2, 0),
            Background = new SolidColorBrush(red), BorderBrush = new SolidColorBrush(red), Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = "结束录制并保存视频" + (hk != null ? $"（也可以再按 {hk}）" : ""),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = Brushes.White, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "结束录制", FontWeight = FontWeights.SemiBold, FontSize = 13 },
                },
            },
        };
        stop.Click += (_, _) => _ = StopAsync(save: true);
        StopButton = stop;
        row.Children.Add(stop);
        var box = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(4), BorderThickness = new Thickness(1), Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.3 },
        };
        box.SetResourceReference(Border.BackgroundProperty, "OverlayBg");
        box.SetResourceReference(Border.BorderBrushProperty, "Hairline");
        _bar = LongShot.Floating(box, clickThrough: false);
        grip.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { _bar?.DragMove(); } catch (InvalidOperationException) { }
        };
        _bar.Show();
        PlaceBar();
        _bar.LocationChanged += (_, _) => UpdateBarExclusion(); // dragged into or out of the region
    }

    Button Icon(string glyph, string tip, Action click, Color? color = null)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 15 },
            ToolTip = tip, Width = 38, Height = 34, Focusable = false, Cursor = Cursors.Hand,
            Style = (Style)Application.Current.Resources["IconButton"], Margin = new Thickness(1, 0, 1, 0),
        };
        if (color is Color c) b.Foreground = new SolidColorBrush(c);
        else b.SetResourceReference(Control.ForegroundProperty, "Fg");
        b.Click += (_, _) => click();
        return b;
    }

    void ConfirmDiscard()
    {
        if (Elapsed > TimeSpan.FromSeconds(10)
            && MessageBox.Show("放弃这段录屏？视频不会保存。", "AgosClip 录屏", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _ = StopAsync(save: false);
    }

    /// <summary>Under the region's bottom-right; above it if there's no room; inside it (bottom centre) for full-screen.</summary>
    void PlaceBar()
    {
        if (_bar == null) return;
        var screen = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(_region.X, _region.Y, _region.Width, _region.Height));
        var wa = screen.WorkingArea;
        var (bw, bh) = LongShot.SizeOf(_bar);
        int gap = 10;
        int x = Math.Clamp(_region.X + _region.Width - bw, wa.Left, Math.Max(wa.Left, wa.Right - bw));
        int y;
        if (_region.Y + _region.Height + gap + bh <= wa.Bottom) y = _region.Y + _region.Height + gap;
        else if (_region.Y - gap - bh >= wa.Top) y = _region.Y - gap - bh;
        else
        {
            // no room outside: sit inside, bottom centre (excluded from capture on Windows 10 2004+)
            x = Math.Clamp(_region.X + (_region.Width - bw) / 2, wa.Left, Math.Max(wa.Left, wa.Right - bw));
            y = Math.Clamp(_region.Y + _region.Height - bh - 40, wa.Top, Math.Max(wa.Top, wa.Bottom - bh));
        }
        LongShot.PlaceAt(_bar, x, y);
        UpdateBarExclusion();
    }

    bool _barExcluded;
    internal bool BarExcluded => _barExcluded;

    /// <summary>
    /// Only while the bar overlaps the recorded region (full-screen recording, or dragged there) is it hidden
    /// from screen capture, so it stays out of the video. Elsewhere it stays visible to remote-desktop tools.
    /// </summary>
    void UpdateBarExclusion()
    {
        if (_bar == null) return;
        var h = new WindowInteropHelper(_bar).Handle;
        if (!Native.GetWindowRect(h, out var r)) return;
        bool overlaps = r.Left < _region.X + _region.Width && r.Right > _region.X && r.Top < _region.Y + _region.Height && r.Bottom > _region.Y;
        if (overlaps == _barExcluded) return;
        bool ok = SetWindowDisplayAffinity(h, overlaps ? WDA_EXCLUDEFROMCAPTURE : 0);
        _barExcluded = overlaps && ok;
        App.Log($"recording: control bar {(overlaps ? "inside" : "outside")} the region, excluded from capture: {_barExcluded}{(overlaps && !ok ? " (not supported here: it will show in the video)" : "")}");
    }

    bool _blink;
    void UpdateBar()
    {
        if (_time == null || _dot == null || _pauseBtn == null) return;
        _time.Text = FormatTime(Elapsed);
        bool paused = _state == State.Paused;
        _blink = !_blink;
        _dot.Opacity = paused ? 0.35 : _blink ? 1 : 0.45;
        ((TextBlock)_pauseBtn.Content).Text = paused ? "" : "";
        _pauseBtn.ToolTip = paused ? "继续录制" : "暂停";
    }

    void CloseUi()
    {
        try { _frame?.Close(); } catch { }
        try { _bar?.Close(); } catch { }
        foreach (var v in _veils) try { v.Close(); } catch { }
        _veils.Clear();
        _frame = _bar = null;
    }
}
