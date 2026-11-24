using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace AgosClip;

/// <summary>
/// Turns top-down 32-bit BGRX frames into an H.264 MP4 with the Media Foundation encoder that ships with
/// Windows (MediaTranscoder pulling from a MediaStreamSource) — nothing extra to install.
///
/// Frames live in a small pool of byte arrays: <see cref="Rent"/> hands one out, <see cref="Submit"/>
/// queues it, and it comes back to the pool once the pipeline reports the sample as processed.
/// When the encoder falls behind the pool runs dry and Rent returns null: the caller skips that frame
/// (timestamps are real time, so the video just holds the previous picture a little longer).
/// </summary>
public sealed class VideoEncoder
{
    static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    public int Width { get; }
    public int Height { get; }
    public string Path { get; }
    public bool Hardware { get; private set; }
    /// <summary>The MP4 has an AAC sound track fed through <see cref="SubmitAudio"/>.</summary>
    public bool HasAudio { get; }

    readonly int _frameBytes;
    readonly TimeSpan _frameDuration;
    readonly int MaxBuffers;    // at most ~256 MB of frames in flight (a 4K frame is 33 MB)
    readonly BlockingCollection<(byte[] Data, TimeSpan At)> _queue;
    readonly BlockingCollection<(byte[] Pcm, TimeSpan At, TimeSpan Duration)> _audio = new();
    readonly ConcurrentBag<byte[]> _pool = new();
    int _allocated, _processed;
    TimeSpan _last = TimeSpan.MinValue;
    Task _run = Task.CompletedTask;

    public int FramesWritten;
    public int FramesDropped;
    public long AudioBytes;

    VideoEncoder(string path, int w, int h, int fps, bool audio)
    {
        (Path, Width, Height, HasAudio) = (path, w, h, audio);
        _frameBytes = w * h * 4;
        _frameDuration = TimeSpan.FromSeconds(1.0 / fps);
        MaxBuffers = (int)Math.Clamp((256L << 20) / _frameBytes, 3, 12);
        _queue = new(MaxBuffers + 2);
    }

    /// <summary>H.264 wants even sizes; the region is trimmed by at most one pixel each way.</summary>
    public static (int W, int H) EvenSize(int w, int h) => (w & ~1, h & ~1);

    /// <summary>Opens the output file and the encoder. Throws if this Windows can't encode H.264.</summary>
    public static async Task<VideoEncoder> StartAsync(string path, int width, int height, int fps, bool audio = false, bool? hardware = null)
    {
        var (w, h) = EvenSize(width, height);
        if (w < 16 || h < 16) throw new ArgumentException($"region too small ({width}x{height})");
        fps = Math.Clamp(fps, 5, 60);
        var enc = new VideoEncoder(path, w, h, fps, audio);
        // hardware encoders (NVENC / Quick Sync / AMF) refuse tiny frames; software is fine for those
        bool hw = hardware ?? (w >= 320 && h >= 240);
        try { await enc.Open(fps, hw); }
        catch (Exception ex) when (hw && hardware == null)
        {
            App.Log($"recording: hardware encoder unavailable, using software: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}".TrimEnd());
            await enc.Open(fps, false);
        }
        return enc;
    }

    async Task Open(int fps, bool hardware)
    {
        Hardware = hardware;
        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Rgb32, (uint)Width, (uint)Height);
        input.FrameRate.Numerator = (uint)fps;
        input.FrameRate.Denominator = 1;
        input.PixelAspectRatio.Numerator = 1;
        input.PixelAspectRatio.Denominator = 1;
        input.Properties[MF_MT_DEFAULT_STRIDE] = (uint)(Width * 4); // positive stride = top-down rows (RGB defaults to bottom-up)

        var video = new VideoStreamDescriptor(input) { Name = "video" };
        var source = HasAudio
            ? new MediaStreamSource(video, new AudioStreamDescriptor(
                AudioEncodingProperties.CreatePcm(AudioRecorder.Rate, AudioRecorder.Channels, 16)) { Name = "audio" })
            : new MediaStreamSource(video);
        source.BufferTime = TimeSpan.Zero;
        source.CanSeek = false;
        source.Starting += (_, a) => a.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += OnSampleRequested;

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        profile.Audio = HasAudio ? AudioEncodingProperties.CreateAac(AudioRecorder.Rate, AudioRecorder.Channels, 192_000) : null;
        profile.Video.Width = (uint)Width;
        profile.Video.Height = (uint)Height;
        profile.Video.FrameRate.Numerator = (uint)fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        // screen content compresses well; ~0.08 bit per pixel per frame, 1–24 Mbit/s
        profile.Video.Bitrate = (uint)Math.Clamp((long)Width * Height * fps / 12, 1_000_000, 24_000_000);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.Create(Path).Dispose();
        var file = await StorageFile.GetFileFromPathAsync(Path);
        var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        try
        {
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = hardware, AlwaysReencode = true };
            var prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile);
            if (!prep.CanTranscode) throw new InvalidOperationException("cannot encode: " + prep.FailureReason);
            var job = prep.TranscodeAsync().AsTask();
            _run = job.ContinueWith(t => { stream.Dispose(); t.GetAwaiter().GetResult(); }, TaskScheduler.Default);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var req = args.Request;
        bool audio = req.StreamDescriptor.Name == "audio";
        // Usually the next sample is already queued. If not (encoder ahead of the capture, or paused), wait
        // off the Media Foundation thread with a deferral so the other stream's requests keep flowing.
        if (TryNext(audio, 0, out var sample)) { req.Sample = sample; return; }
        var deferral = req.GetDeferral();
        Task.Run(() =>
        {
            try { TryNext(audio, Timeout.Infinite, out var s); req.Sample = s; }
            catch (Exception ex) { App.Log("recording: sample request failed: " + ex.Message); }
            finally { deferral.Complete(); }
        });
    }

    /// <summary>False = nothing within the timeout. True with a null sample = that stream has ended.</summary>
    bool TryNext(bool audio, int timeoutMs, out MediaStreamSample? sample)
    {
        sample = null;
        if (audio)
        {
            if (!_audio.TryTake(out var a, timeoutMs)) return _audio.IsCompleted;
            sample = MediaStreamSample.CreateFromBuffer(a.Pcm.AsBuffer(), a.At);
            sample.Duration = a.Duration;
            return true;
        }
        if (!_queue.TryTake(out var f, timeoutMs)) return _queue.IsCompleted;
        sample = MediaStreamSample.CreateFromBuffer(f.Data.AsBuffer(0, _frameBytes), f.At);
        sample.Duration = _frameDuration;
        var data = f.Data;
        sample.Processed += (_, _) => { Interlocked.Increment(ref _processed); _pool.Add(data); };
        return true;
    }

    /// <summary>Queues mixed 16-bit PCM placed at <paramref name="at"/> on the recording timeline.</summary>
    public void SubmitAudio(byte[] pcm, TimeSpan at, TimeSpan duration)
    {
        if (!HasAudio) return;
        try { _audio.Add((pcm, at, duration)); Interlocked.Add(ref AudioBytes, pcm.Length); }
        catch (InvalidOperationException) { }
    }

    /// <summary>A buffer for the next frame, or null if the encoder is behind (skip this frame).</summary>
    public byte[]? Rent()
    {
        if (_pool.TryTake(out var b)) return b;
        if (Interlocked.Increment(ref _allocated) <= MaxBuffers) return new byte[_frameBytes];
        Interlocked.Decrement(ref _allocated);
        // The pipeline normally returns buffers through Processed. If it never does on this machine,
        // fall back to fresh arrays instead of freezing the video.
        if (Volatile.Read(ref _processed) == 0 && FramesWritten > MaxBuffers * 2) return new byte[_frameBytes];
        Interlocked.Increment(ref FramesDropped);
        return null;
    }

    public void Return(byte[] buffer) => _pool.Add(buffer);

    /// <summary>Queues a frame shown <paramref name="at"/> since the recording started (pauses excluded).</summary>
    public void Submit(byte[] frame, TimeSpan at)
    {
        if (at <= _last) at = _last + TimeSpan.FromTicks(1); // timestamps must strictly increase
        _last = at;
        bool queued = false;
        try { queued = _queue.TryAdd((frame, at)); } // never blocks: if the encoder died, the capture thread must not hang
        catch (InvalidOperationException) { }        // already finishing
        if (queued) Interlocked.Increment(ref FramesWritten);
        else { Interlocked.Increment(ref FramesDropped); _pool.Add(frame); }
    }

    /// <summary>True once the encoder has stopped on an error (the recording can't continue).</summary>
    public bool Failed => _run.IsFaulted;

    /// <summary>Ends the stream and waits until the MP4 is complete on disk.</summary>
    public Task FinishAsync()
    {
        _queue.CompleteAdding();
        _audio.CompleteAdding();
        return _run;
    }
}
