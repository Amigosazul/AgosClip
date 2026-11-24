using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgosClip;

internal static partial class SelfTest
{
    /// <summary>Fake screen for the recorder: top half red, bottom half blue, a white bar moving left to right.</summary>
    sealed class FakeFrames : IFrameSource
    {
        readonly int _w, _h;
        public int Grabs;
        public FakeFrames(int w, int h) => (_w, _h) = (w, h);
        public bool Grab(byte[] dest)
        {
            int bar = (Grabs++ * 7) % _w;
            for (int y = 0; y < _h; y++)
                for (int x = 0; x < _w; x++)
                {
                    int o = (y * _w + x) * 4;
                    bool white = Math.Abs(x - bar) < 6;
                    bool top = y < _h / 2;
                    dest[o] = (byte)(white ? 255 : top ? 0x30 : 0xF0);     // B
                    dest[o + 1] = (byte)(white ? 255 : 0x30);               // G
                    dest[o + 2] = (byte)(white ? 255 : top ? 0xF0 : 0x30); // R
                    dest[o + 3] = 255;
                }
            return true;
        }
        public void Dispose() { }
    }

    /// <summary>Fake sound device: 44.1 kHz mono 16-bit, a 1 kHz tone in 10 ms packets (or nothing, like idle loopback).</summary>
    sealed class FakeWaveIn : NAudio.Wave.IWaveIn
    {
        readonly bool _silent;
        System.Threading.Thread? _t;
        volatile bool _run;
        public FakeWaveIn(bool silent = false) => _silent = silent;
        public NAudio.Wave.WaveFormat WaveFormat { get; set; } = new(44100, 16, 1);
        public event EventHandler<NAudio.Wave.WaveInEventArgs>? DataAvailable;
        public event EventHandler<NAudio.Wave.StoppedEventArgs>? RecordingStopped;
        public void StartRecording()
        {
            _run = true;
            _t = new System.Threading.Thread(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long sent = 0;
                while (_run)
                {
                    long due = (long)(sw.Elapsed.TotalSeconds * 44100);
                    if (due - sent >= 441 && !_silent)
                    {
                        int n = (int)(due - sent);
                        var b = new byte[n * 2];
                        for (int i = 0; i < n; i++)
                        {
                            short v = (short)(Math.Sin(2 * Math.PI * 1000 * (sent + i) / 44100.0) * 8000);
                            b[i * 2] = (byte)v; b[i * 2 + 1] = (byte)(v >> 8);
                        }
                        sent += n;
                        DataAvailable?.Invoke(this, new NAudio.Wave.WaveInEventArgs(b, b.Length));
                    }
                    System.Threading.Thread.Sleep(10);
                }
            }) { IsBackground = true };
            _t.Start();
        }
        public void StopRecording() { _run = false; _t?.Join(500); RecordingStopped?.Invoke(this, new NAudio.Wave.StoppedEventArgs()); }
        public void Dispose() => StopRecording();
    }

    static AudioRecorder FakeAudio(bool silent = false)
    {
        var input = new AudioInput(new FakeWaveIn(silent), silent ? "fake idle loopback" : "fake mic");
        input.Start();
        return new AudioRecorder(new[] { input });
    }

    /// <summary>Peak and where the silent stretches are, per 50 ms block (for diagnosing level problems).</summary>
    static string Diag(byte[]? pcm)
    {
        if (pcm == null) return "none";
        int peak = 0; var blocks = new System.Text.StringBuilder();
        int block = 2400 * 4;
        for (int b = 0; b < pcm.Length; b += block)
        {
            int bp = 0;
            for (int i = b; i + 1 < Math.Min(pcm.Length, b + block); i += 2) bp = Math.Max(bp, Math.Abs((int)(short)(pcm[i] | pcm[i + 1] << 8)));
            peak = Math.Max(peak, bp);
            blocks.Append(bp / 1000).Append(' ');
        }
        return $"peak {peak}, per 50 ms peak/1000: {blocks}";
    }

    static double Rms(byte[] pcm, int from = 0)
    {
        double sum = 0; int n = 0;
        for (int i = from; i + 1 < pcm.Length; i += 2) { short v = (short)(pcm[i] | pcm[i + 1] << 8); sum += (double)v * v; n++; }
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }

    static void AudioMixerTests()
    {
        using (var a = FakeAudio())
        {
            Pump(400);
            // pulled the way the recorder does: every 20 ms, up to (time since start − Lag)
            a.Reset();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var all = new System.Collections.Generic.List<byte>();
            TimeSpan? firstAt = null;
            while (all.Count < 24000 * 4)
            {
                var upTo = TimeSpan.FromTicks(Math.Min(clock.Elapsed.Ticks - AudioRecorder.Lag.Ticks, TimeSpan.FromMilliseconds(500).Ticks));
                if (a.Pull(upTo) is { } part) { firstAt ??= part.At; all.AddRange(part.Pcm); }
                Pump(20);
            }
            (byte[] Pcm, TimeSpan At, TimeSpan Duration)? c = (all.ToArray(), firstAt ?? TimeSpan.MaxValue, TimeSpan.Zero);
            var again = a.Pull(TimeSpan.FromMilliseconds(500));
            // a 8000-amplitude sine has RMS ≈ 5657; resampling 44.1 → 48 kHz keeps it
            double rms = c == null ? 0 : Rms(c.Value.Pcm, 4000);
            Check(c != null && c.Value.Pcm.Length == 24000 * 4 && c.Value.At == TimeSpan.Zero && again == null && rms is > 4500 and < 6500,
                $"mono 44.1 kHz sound becomes 48 kHz stereo on the recording timeline ({c?.Pcm.Length} bytes, RMS {rms:0}, {Diag(c?.Pcm)})");
            Check(c != null && Enumerable.Range(1000, 200).All(i => c.Value.Pcm[i * 4] == c.Value.Pcm[i * 4 + 2] && c.Value.Pcm[i * 4 + 1] == c.Value.Pcm[i * 4 + 3]),
                "mono sound is copied to both channels");
        }
        using (var idle = FakeAudio(silent: true))
        {
            Pump(300);
            var c = idle.Pull(TimeSpan.FromMilliseconds(250));
            Check(c != null && c.Value.Pcm.Length == 12000 * 4 && c.Value.Pcm.All(b => b == 0),
                "no sound playing (idle loopback sends nothing) still fills the timeline with silence");
        }
    }

    static void RecordingTests(string outDir)
    {
        // settings defaults
        var def = new Settings();
        Check(!def.EnableRecording && def.RecordHotkey == "Alt+Shift+R" && def.RecordFps == 30 && def.RecordDir == Settings.DefaultRecordFolder,
            "recording is off by default, Alt+Shift+R, 30 fps, Videos\\AgosClip");
        Check(VideoEncoder.EvenSize(333, 201) == (332, 200), "the region is trimmed to even sizes for H.264");
        var dir = Path.Combine(outDir, "recordings");
        Directory.CreateDirectory(dir);
        var p1 = ScreenRecorder.NewFilePath(dir);
        File.WriteAllBytes(p1, new byte[1]);
        var p2 = ScreenRecorder.NewFilePath(dir);
        Check(p1 != p2 && p2.EndsWith("(2).mp4"), $"file names never overwrite an earlier recording ({Path.GetFileName(p2)})");
        File.Delete(p1);

        RecordOverlayTests(outDir);
        AudioMixerTests();

        // a real encode from a fake screen: 320×240 (hardware encoder if there is one), then 333×201 in software
        foreach (var (w, h, hw) in new[] { (320, 240, (bool?)null), (333, 201, (bool?)false) })
        {
            string? saved = null;
            ScreenRecorder? done = null;
            Action<string?, ScreenRecorder> onDone = (p, r) => { saved = p; done = r; };
            ScreenRecorder.Finished += onDone;
            try
            {
                var path = ScreenRecorder.NewFilePath(dir);
                var src = new FakeFrames(VideoEncoder.EvenSize(w, h).W, VideoEncoder.EvenSize(w, h).H);
                Task<ScreenRecorder> start;
                bool withAudio = w == 320;
                try { start = ScreenRecorder.StartForTest(new Int32Rect(200, 200, w, h), path, () => src, 30, showUi: true, hardware: hw, audio: withAudio ? FakeAudio() : null); Await(start); }
                catch (Exception ex) { Check(false, $"recorder starts ({w}x{h}): {ex.Message}"); continue; }
                var rec = start.Result;
                var wall = System.Diagnostics.Stopwatch.StartNew();
                Check(ScreenRecorder.IsActive && rec.Region.Width == (w & ~1) && rec.Region.Height == (h & ~1),
                    $"recording starts {rec.Region.Width}x{rec.Region.Height} ({(rec.Encoder.Hardware ? "hardware" : "software")} encoder)");
                Pump(1200);
                Check(rec.Bar is { IsVisible: true } && rec.TimeText == "00:01", $"control bar shows the time ({rec.TimeText})");
                Check(!rec.BarExcluded, "a bar outside the region stays visible to screen capture (remote-desktop tools can see it)");
                Check(rec.Veils.Count == System.Windows.Forms.Screen.AllScreens.Length && rec.Veils.All(v => v.IsVisible),
                    $"everything outside the region is greyed out ({rec.Veils.Count} veil window(s))");
                Check(rec.StopButton is { IsVisible: true }, "the bar has a 结束录制 button");
                if (rec.Bar != null && w == 320) RenderOnGlass((FrameworkElement)rec.Bar.Content, Path.Combine(outDir, "record-bar.png"), false, false);
                if (rec.Veils.Count > 0 && w == 320) RenderOnGlass((FrameworkElement)rec.Veils[0].Content, Path.Combine(outDir, "record-veil.png"), false, true);
                rec.TogglePause();
                var pausedWall = System.Diagnostics.Stopwatch.StartNew();
                int grabsAtPause = src.Grabs;
                Pump(700);
                Check(rec.Paused && src.Grabs - grabsAtPause <= 1, $"暂停 stops grabbing frames ({src.Grabs - grabsAtPause} while paused)");
                rec.TogglePause();
                pausedWall.Stop();
                Pump(800);
                var t = rec.Elapsed;
                var expect = wall.Elapsed - pausedWall.Elapsed;
                Check(Math.Abs((t - expect).TotalSeconds) < 0.15 && pausedWall.Elapsed.TotalSeconds > 0.65,
                    $"paused time is left out of the video ({t.TotalSeconds:0.00} s recorded = {wall.Elapsed.TotalSeconds:0.00} s − {pausedWall.Elapsed.TotalSeconds:0.00} s paused)");
                if (w == 320 && rec.StopButton != null) rec.StopButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); // 结束录制
                Await(rec.StopAsync(save: true));
                Check(rec.Veils.Count == 0, "the grey veil goes away when the recording ends");
                Check(saved == path && File.Exists(path) && new FileInfo(path).Length > 1000 && !ScreenRecorder.IsActive,
                    $"结束并保存 writes the MP4 ({(File.Exists(path) ? new FileInfo(path).Length : 0)} bytes, {rec.Encoder.FramesWritten} frames, {rec.Encoder.FramesDropped} skipped)");
                Check(rec.Encoder.FramesWritten >= 40, $"about 30 fps were captured ({rec.Encoder.FramesWritten} frames in {t.TotalSeconds:0.0} s)");
                if (withAudio)
                {
                    double audioSec = rec.Encoder.AudioBytes / (double)(AudioRecorder.Rate * 4);
                    Check(Math.Abs(audioSec - t.TotalSeconds) < 0.2, $"sound covers the same time as the picture, pause left out ({audioSec:0.00} s vs {t.TotalSeconds:0.00} s)");
                }
                if (saved != null) VerifyVideo(saved, rec.Region.Width, rec.Region.Height, t, outDir, w, withAudio);
            }
            finally { ScreenRecorder.Finished -= onDone; }
        }

        // 放弃: nothing is kept
        {
            string? saved = "unset";
            Action<string?, ScreenRecorder> onDone = (p, _) => saved = p;
            ScreenRecorder.Finished += onDone;
            try
            {
                var path = ScreenRecorder.NewFilePath(dir);
                var start = ScreenRecorder.StartForTest(new Int32Rect(10, 10, 160, 120), path, () => new FakeFrames(160, 120), 15, showUi: false, hardware: false);
                Await(start);
                Pump(500);
                Await(start.Result.StopAsync(save: false));
                Check(saved == null && !File.Exists(path) && !ScreenRecorder.IsActive, "放弃 deletes the recording");
            }
            catch (Exception ex) { Check(false, "discard test: " + ex.Message); }
            finally { ScreenRecorder.Finished -= onDone; }
        }
    }

    static void RecordOverlayTests(string outDir)
    {
        var bounds = ScreenCapture.VirtualBounds();
        var desk = FakeDesktop(bounds.Width, bounds.Height, false);
        desk.Measure(new Size(bounds.Width, bounds.Height)); desk.Arrange(new Rect(0, 0, bounds.Width, bounds.Height));
        var shot = new RenderTargetBitmap(bounds.Width, bounds.Height, 96, 96, PixelFormats.Pbgra32);
        shot.Render(desk); shot.Freeze();

        (CaptureAction A, Int32Rect R)? done = null;
        Action<CaptureAction, BitmapSource, Int32Rect> onDone = (a, _, r) => done = (a, r);
        CaptureSession.Completed += onDone;
        try
        {
            var session = CaptureSession.StartWith(shot, bounds, CaptureMode.Record);
            Pump(250);
            var o = session.Overlays[0];
            Check(o.RecordBtn.Visibility == Visibility.Visible && o.ShotTools.Visibility == Visibility.Collapsed && o.HintText.Text.Contains("录制"),
                "record mode shows only 开始录制 (no copy / pin / edit tools)");
            o.SelectForTest(new Rect(400, 120, 320, 200));
            Pump(150);
            RenderOnGlass((FrameworkElement)o.Content, Path.Combine(outDir, "record-selection.png"), false, false);
            o.RecordBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump(150);
            Check(!CaptureSession.IsActive && done?.A == CaptureAction.Record && done?.R.Width == 320 && done?.R.Height == 200,
                $"开始录制 hands over the selected region ({done?.R.Width}x{done?.R.Height})");

            done = null;
            session = CaptureSession.StartWith(shot, bounds, CaptureMode.Record);
            Pump(200);
            o = session.Overlays[0];
            o.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(o)!, Environment.TickCount, System.Windows.Input.Key.Enter)
                { RoutedEvent = UIElement.PreviewKeyDownEvent });
            Pump(150);
            Check(done?.A == CaptureAction.Record && done?.R.Width == o.MonitorRect.Width && done?.R.Height == o.MonitorRect.Height,
                "Enter without a selection records the whole monitor");
        }
        finally
        {
            CaptureSession.Completed -= onDone;
            CaptureSession.Active?.Cancel();
        }
    }

    /// <summary>Reads the MP4 back: size and duration from the shell, one decoded frame for orientation and colour.</summary>
    static void VerifyVideo(string path, int w, int h, TimeSpan expected, string outDir, int tag, bool audio)
    {
        try
        {
            var fileT = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask();
            Await(fileT);
            var profT = Windows.Media.MediaProperties.MediaEncodingProfile.CreateFromFileAsync(fileT.Result).AsTask();
            Await(profT);
            var a = profT.Result.Audio;
            if (audio)
                Check(a != null && a.SampleRate == 48000 && a.ChannelCount == 2 && a.Subtype == "AAC",
                    $"the MP4 has an AAC 48 kHz stereo sound track ({a?.Subtype} {a?.SampleRate} Hz {a?.ChannelCount} ch)");
            else Check(a == null, "without sound inputs the MP4 has no sound track");
        }
        catch (Exception ex) { Skip("reading the MP4's tracks: " + ex.Message); }

        try
        {
            var file = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask();
            Await(file);
            var props = file.Result.Properties.GetVideoPropertiesAsync().AsTask();
            Await(props);
            var vp = props.Result;
            Check(vp.Width == w && vp.Height == h && Math.Abs(vp.Duration.TotalSeconds - expected.TotalSeconds) < 0.6,
                $"the MP4 is {vp.Width}x{vp.Height}, {vp.Duration.TotalSeconds:0.00} s (expected {w}x{h}, {expected.TotalSeconds:0.00} s)");
        }
        catch (Exception ex) { Skip("video properties: " + ex.Message); }

        try
        {
            var fileT = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask();
            Await(fileT);
            var clipT = Windows.Media.Editing.MediaClip.CreateFromFileAsync(fileT.Result).AsTask();
            Await(clipT);
            var comp = new Windows.Media.Editing.MediaComposition();
            comp.Clips.Add(clipT.Result);
            var thumbT = comp.GetThumbnailAsync(TimeSpan.FromMilliseconds(500), 0, 0, Windows.Media.Editing.VideoFramePrecision.NearestFrame).AsTask();
            Await(thumbT);
            using var thumb = thumbT.Result;
            var decT = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(thumb).AsTask();
            Await(decT);
            var dec = decT.Result;
            var pxT = dec.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                new Windows.Graphics.Imaging.BitmapTransform(), Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage).AsTask();
            Await(pxT);
            var px = pxT.Result.DetachPixelData();
            int tw = (int)dec.PixelWidth, th = (int)dec.PixelHeight;
            (byte B, byte G, byte R) At(int x, int y) { int o = (y * tw + x) * 4; return (px[o], px[o + 1], px[o + 2]); }
            // sample away from the moving white bar: check a few columns and use the most saturated
            var top = Enumerable.Range(1, 6).Select(i => At(tw * i / 7, th / 4)).OrderBy(c => c.B + c.G).First();
            var bottom = Enumerable.Range(1, 6).Select(i => At(tw * i / 7, th * 3 / 4)).OrderBy(c => c.R + c.G).First();
            File.WriteAllBytes(Path.Combine(outDir, $"record-frame-{tag}.bin"), px);
            Check(top.R > 180 && top.B < 110 && bottom.B > 180 && bottom.R < 110,
                $"the video is the right way up with the right colours (top RGB {top.R},{top.G},{top.B}; bottom {bottom.R},{bottom.G},{bottom.B})");
        }
        catch (Exception ex) { Skip("decoding a frame of the MP4: " + ex.GetType().Name + " " + ex.Message); }
    }
}
