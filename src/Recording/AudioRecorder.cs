using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AgosClip;

/// <summary>
/// One sound input (system sound via WASAPI loopback, or a microphone) turned into 48 kHz stereo float.
/// Captured packets go into a buffer that reads as silence when empty: loopback delivers nothing at all
/// while no program plays sound, and the recording timeline still has to move on.
/// </summary>
public sealed class AudioInput : IDisposable
{
    public string Name { get; }
    readonly IWaveIn _capture;
    readonly BufferedWaveProvider _buffer;
    readonly byte[] _scratch = new byte[64 * 1024];
    public ISampleProvider Output { get; }
    public long BytesCaptured;

    public AudioInput(IWaveIn capture, string name)
    {
        Name = name;
        _capture = capture;
        _buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            ReadFully = true, BufferDuration = TimeSpan.FromSeconds(4), DiscardOnBufferOverflow = true,
        };
        capture.DataAvailable += (_, e) =>
        {
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            System.Threading.Interlocked.Add(ref BytesCaptured, e.BytesRecorded);
        };
        ISampleProvider sp = _buffer.ToSampleProvider();
        sp = sp.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(sp),
            2 => sp,
            _ => new Downmix(sp),
        };
        if (sp.WaveFormat.SampleRate != AudioRecorder.Rate) sp = new WdlResamplingSampleProvider(sp, AudioRecorder.Rate);
        Output = sp;
    }

    public WaveFormat SourceFormat => _capture.WaveFormat;
    public TimeSpan Buffered => _buffer.BufferedDuration;
    public void Start() => _capture.StartRecording();

    /// <summary>Drops captured sound (paused time) so the next sound lines up with the moment of resuming.</summary>
    public void Clear() => _buffer.ClearBuffer();

    /// <summary>Throws away the oldest sound beyond <paramref name="keep"/> (the device clock ran ahead of ours).</summary>
    public void Trim(TimeSpan keep)
    {
        var f = _buffer.WaveFormat;
        long excess = _buffer.BufferedBytes - (long)(keep.TotalSeconds * f.AverageBytesPerSecond);
        excess -= excess % f.BlockAlign;
        while (excess > 0)
        {
            int n = _buffer.Read(_scratch, 0, (int)Math.Min(excess, _scratch.Length - _scratch.Length % f.BlockAlign));
            if (n <= 0) break;
            excess -= n;
        }
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
    }

    /// <summary>Surround (FL FR FC LFE BL BR SL SR) to stereo; the LFE channel is left out.</summary>
    sealed class Downmix : ISampleProvider
    {
        readonly ISampleProvider _src;
        readonly int _ch;
        float[] _buf = Array.Empty<float>();
        public WaveFormat WaveFormat { get; }
        public Downmix(ISampleProvider src)
        {
            _src = src;
            _ch = src.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
        }
        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            if (_buf.Length < frames * _ch) _buf = new float[frames * _ch];
            int got = _src.Read(_buf, 0, frames * _ch) / _ch;
            for (int i = 0; i < got; i++)
            {
                int o = i * _ch;
                float l = _buf[o], r = _buf[o + 1];
                if (_ch > 2) { l += 0.707f * _buf[o + 2]; r += 0.707f * _buf[o + 2]; }
                for (int c = 4; c < _ch; c++) { if (c % 2 == 0) l += 0.707f * _buf[o + c]; else r += 0.707f * _buf[o + c]; }
                buffer[offset + i * 2] = l * 0.5f;
                buffer[offset + i * 2 + 1] = r * 0.5f;
            }
            return got * 2;
        }
    }
}

/// <summary>
/// Mixes the inputs into one 48 kHz stereo 16-bit PCM track whose timeline follows the recording clock
/// (pauses excluded), so sound stays in step with the video frames.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    public const int Rate = 48000, Channels = 2;
    /// <summary>Sound is taken this far behind the clock so packets that arrive a little late still make it.</summary>
    public static readonly TimeSpan Lag = TimeSpan.FromMilliseconds(150);

    readonly List<AudioInput> _inputs;
    readonly object _lock = new();
    float[] _f = Array.Empty<float>(), _one = Array.Empty<float>();
    long _framesOut;

    public IReadOnlyList<AudioInput> Inputs => _inputs;
    public string Description => string.Join(" + ", _inputs.Select(i => i.Name));
    public TimeSpan Written => TimeSpan.FromSeconds((double)_framesOut / Rate);

    public AudioRecorder(IEnumerable<AudioInput> inputs)
    {
        _inputs = inputs.ToList();
    }

    /// <summary>Opens the requested devices. Returns null if none could be opened; <paramref name="problems"/> says which failed.</summary>
    public static AudioRecorder? TryCreate(bool systemSound, bool microphone, out string? problems)
    {
        var list = new List<AudioInput>();
        var bad = new List<string>();
        if (systemSound)
        {
            try { list.Add(new AudioInput(new WasapiLoopbackCapture(), "系统声音")); }
            catch (Exception ex) { App.Log("recording: system sound unavailable: " + ex.Message); bad.Add("系统声音"); }
        }
        if (microphone)
        {
            try { list.Add(new AudioInput(new WasapiCapture(), "麦克风")); }
            catch (Exception ex) { App.Log("recording: microphone unavailable: " + ex.Message); bad.Add("麦克风"); }
        }
        foreach (var i in list.ToArray())
        {
            try { i.Start(); }
            catch (Exception ex)
            {
                App.Log($"recording: {i.Name} failed to start: {ex.Message}");
                bad.Add(i.Name);
                list.Remove(i);
                i.Dispose();
            }
        }
        problems = bad.Count > 0 ? string.Join("、", bad) : null;
        foreach (var i in list) App.Log($"recording: audio input {i.Name}: {i.SourceFormat}");
        return list.Count > 0 ? new AudioRecorder(list) : null;
    }

    /// <summary>Starts the timeline here: sound captured before now is dropped.</summary>
    public void Reset()
    {
        lock (_lock) foreach (var i in _inputs) i.Clear();
    }

    /// <summary>
    /// Mixes sound up to <paramref name="upTo"/> on the recording timeline. Returns 16-bit PCM and where it
    /// starts on the timeline, or null if there's nothing new yet.
    /// </summary>
    public (byte[] Pcm, TimeSpan At, TimeSpan Duration)? Pull(TimeSpan upTo)
    {
        lock (_lock)
        {
            long target = (long)(Math.Max(0, upTo.TotalSeconds) * Rate);
            int frames = (int)Math.Min(target - _framesOut, Rate); // at most a second per call
            if (frames <= 0) return null;
            foreach (var i in _inputs) if (i.Buffered > Lag + TimeSpan.FromMilliseconds(250)) i.Trim(Lag);
            int n = frames * Channels;
            if (_f.Length < n) _f = new float[n];
            if (_one.Length < n) _one = new float[n];
            Array.Clear(_f, 0, n);
            // Mixed by hand: NAudio's MixingSampleProvider drops an input for good the first time it returns
            // short, and the resampler can be a frame short now and then.
            foreach (var input in _inputs)
            {
                int got = 0;
                for (int tries = 0; got < n && tries < 8; tries++)
                {
                    int r = input.Output.Read(_one, got, n - got);
                    if (r <= 0) break;
                    got += r;
                }
                for (int k = 0; k < got; k++) _f[k] += _one[k];
            }
            var pcm = new byte[n * 2];
            for (int k = 0; k < n; k++)
            {
                int v = (int)Math.Round(Math.Clamp(_f[k], -1f, 1f) * 32767);
                pcm[k * 2] = (byte)v;
                pcm[k * 2 + 1] = (byte)(v >> 8);
            }
            var at = TimeSpan.FromSeconds((double)_framesOut / Rate);
            _framesOut += frames;
            return (pcm, at, TimeSpan.FromSeconds((double)frames / Rate));
        }
    }

    public void Dispose()
    {
        foreach (var i in _inputs) i.Dispose();
    }
}
