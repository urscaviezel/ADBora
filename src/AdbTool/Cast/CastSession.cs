using System.Collections.Concurrent;
using AdbTool.Core;

namespace AdbTool.Cast;

/// <summary>
/// One casting session: source (headset) → decode thread → viewer window.
/// Audio: headset audio (PCM or decoded AAC), optionally mixed with the headset
/// microphone (ADBora Mic), played on the PC and/or recorded to MKV (PCM).
/// </summary>
internal sealed class CastSession
{
    private readonly CastSource _source;
    private readonly string _ffmpegFolder;
    private readonly BlockingCollection<byte[]> _queue = new(new ConcurrentQueue<byte[]>(), 240);
    private readonly object _recordGate = new();
    private H264Decoder? _decoder;
    private CastViewer? _viewer;
    private FileStream? _record;
    private MkvWriter? _mkv;
    private Thread? _thread;
    private int _stopped;
    private readonly object _audioGate = new();
    private AacDecoder? _aac;
    private AudioPlayer? _player;
    private bool _audioFailed;

    public CastSession(CastKind kind, string adbPath, string serial, string ffmpegFolder)
    {
        _source = CastSource.Create(kind, adbPath, serial);
        _adbPath = adbPath;
        _serial = serial;
        _ffmpegFolder = ffmpegFolder;
        _source.Log += s => Log?.Invoke(s);
        _source.Ended += reason => Ended?.Invoke(reason);
    }

    public event Action<string>? Log;
    /// <summary>Session ended (null = by the user). Any thread.</summary>
    public event Action<string?>? Ended;
    public event Action? ScreenshotRequested;

    public string RecordExtension => _source.RecordExtension;
    /// <summary>Playback on the PC muted (recording keeps the audio).</summary>
    public bool Muted { get; set; }
    /// <summary>Playback volume in percent (0–200).</summary>
    public int Volume { get; set; } = 100;
    public event Action? MuteToggleRequested;
    public event Action<int>? VolumeStepRequested;
    private short[] _gain = new short[4096];
    private short[] _mix = new short[4096];
    private MicRelay? _mic;
    private readonly object _micGate = new();
    private readonly List<short> _micQueue = new();
    private int _micRate = 48000;
    private string _adbPath = "", _serial = "";
    /// <summary>Microphone mixed into the audio.</summary>
    public bool MicActive => _mic is not null;
    public string? RecordFile { get; private set; }
    public CastViewer? Viewer => _viewer;

    /// <summary>Must be called on the UI thread.</summary>
    public async Task StartAsync(string title, bool onTop, string? recordFile, IWin32Window? owner, bool audio)
    {
        // Audio is always requested, so playback can be switched on and off while streaming
        Muted = !audio;
        _source.WantAudio = true;
        _source.AudioPcm += GameAudio;
        _source.AudioAac += (adts, rate, ch) =>
        {
            lock (_audioGate)
            {
                if (_audioFailed || Volatile.Read(ref _stopped) == 1) return;
                try
                {
                    if (_aac is null) { _aac = new AacDecoder(); _aac.Decoded += (pcm, n, c) => GameAudio(pcm, n, rate, c); }
                    _aac.Feed(adts, ch);
                }
                catch (Exception ex) { _audioFailed = true; Log?.Invoke(Loc.T("Ton nicht verfügbar: ", "Audio not available: ") + ex.Message); }
            }
        };
        if (!H264Decoder.TryLoad(_ffmpegFolder, out string error))
            throw new InvalidOperationException(Loc.T("FFmpeg-Bibliotheken aus dem scrcpy-Paket konnten nicht geladen werden: ", "FFmpeg libraries from the scrcpy package could not be loaded: ") + error);
        _decoder = new H264Decoder();
        _viewer = new CastViewer(title) { TopMost = onTop };
        _viewer.Recycle += b => _decoder?.Recycle(b);
        _viewer.ScreenshotRequested += () => ScreenshotRequested?.Invoke();
        _viewer.MuteToggleRequested += () => MuteToggleRequested?.Invoke();
        _viewer.VolumeStepRequested += d => VolumeStepRequested?.Invoke(d);
        _viewer.FormClosed += (_, _) => { if (Volatile.Read(ref _stopped) == 0) _ = StopAsync(); };
        _decoder.FrameDecoded += b => { if (_viewer is { IsDisposed: false } v) v.Push(b); else b.Dispose(); };

        if (recordFile is not null)
        {
            RecordFile = Path.ChangeExtension(recordFile, _source.RecordExtension);
            if (_source.RecordExtension == ".mkv")
            {
                // Matroska with timestamps, display size and PCM audio (headset + microphone mixed)
                var mkv = _mkv = new MkvWriter(RecordFile, MkvAudio.Pcm);
                H264Decoder dec = _decoder;
                dec.AccessUnit += (b, n) => { lock (_recordGate) if (_mkv is not null) mkv.WriteVideo(b.AsSpan(0, n), dec.Width, dec.Height); };
            }
            else
            {
                _record = new FileStream(RecordFile, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                _source.Raw += (b, o, n) => { lock (_recordGate) _record?.Write(b, o, n); };
            }
        }
        _source.Video += (b, o, n) =>
        {
            byte[] copy = b.AsSpan(o, n).ToArray();
            if (!_queue.TryAdd(copy)) { while (_queue.TryTake(out _)) { } _queue.TryAdd(copy); } // fell behind: drop the backlog
        };
        _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "ADBora cast decoder" };
        _thread.Start();
        _viewer.Show(owner);
        try
        {
            await _source.StartAsync();
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    /// <summary>Headset audio block: mix in the microphone, then play and record.</summary>
    private void GameAudio(short[] pcm, int count, int rate, int channels)
    {
        if (channels <= 0 || count <= 0 || Volatile.Read(ref _stopped) == 1) return;
        if (_mic is not null) pcm = MixMic(pcm, count, rate, channels);
        lock (_recordGate) _mkv?.WriteAudio(pcm, count, rate, channels);
        Play(pcm, count, rate, channels);
    }

    /// <summary>Adds the queued microphone samples (mono, resampled linearly) to every channel.</summary>
    private short[] MixMic(short[] pcm, int count, int rate, int channels)
    {
        int frames = count / channels;
        if (_mix.Length < count) _mix = new short[count];
        Array.Copy(pcm, _mix, count);
        lock (_micGate)
        {
            double step = _micRate / (double)rate;
            int available = _micQueue.Count;
            int used = 0;
            for (int f = 0; f < frames; f++)
            {
                double pos = f * step;
                int i = (int)pos;
                if (i + 1 >= available) break;
                double frac = pos - i;
                int m = (int)(_micQueue[i] * (1 - frac) + _micQueue[i + 1] * frac);
                for (int c = 0; c < channels; c++)
                {
                    int v = _mix[f * channels + c] + m;
                    _mix[f * channels + c] = (short)(v < -32768 ? -32768 : v > 32767 ? 32767 : v);
                }
                used = i + 1;
            }
            _micQueue.RemoveRange(0, Math.Min(used, _micQueue.Count));
        }
        return _mix;
    }

    /// <summary>Starts or stops the headset microphone (ADBora Mic) while streaming.</summary>
    public async Task SetMicAsync(bool on)
    {
        if (on == (_mic is not null) || Volatile.Read(ref _stopped) == 1) return;
        if (!on)
        {
            MicRelay? old = _mic;
            _mic = null;
            if (old is not null) await old.StopAsync();
            lock (_micGate) _micQueue.Clear();
            return;
        }
        var mic = new MicRelay(_adbPath, _serial);
        mic.Log += s => Log?.Invoke(s);
        mic.Samples += (samples, n, rate) =>
        {
            lock (_micGate)
            {
                _micRate = rate;
                _micQueue.AddRange(new ArraySegment<short>(samples, 0, n));
                int max = rate * 3 / 10; // keep at most 300 ms (latency)
                if (_micQueue.Count > max) _micQueue.RemoveRange(0, _micQueue.Count - max);
            }
        };
        _mic = mic;
        try { await mic.StartAsync(); }
        catch
        {
            _mic = null;
            await mic.StopAsync();
            throw;
        }
    }

    private void Play(short[] pcm, int count, int rate, int channels)
    {
        lock (_audioGate)
        {
            if (_audioFailed || Volatile.Read(ref _stopped) == 1 || channels <= 0 || Muted) return;
            int volume = Volume;
            if (volume != 100)
            {
                if (_gain.Length < count) _gain = new short[count];
                for (int i = 0; i < count; i++)
                {
                    int v = pcm[i] * volume / 100;
                    _gain[i] = (short)(v < -32768 ? -32768 : v > 32767 ? 32767 : v);
                }
                pcm = _gain;
            }
            try
            {
                if (_player is null || _player.Rate != rate || _player.Channels != channels)
                {
                    _player?.Dispose();
                    _player = new AudioPlayer(rate, channels);
                }
                _player.Write(pcm, count);
            }
            catch (Exception ex) { _audioFailed = true; Log?.Invoke(Loc.T("Ton nicht verfügbar: ", "Audio not available: ") + ex.Message); }
        }
    }

    private void DecodeLoop()
    {
        try
        {
            foreach (byte[] data in _queue.GetConsumingEnumerable())
                _decoder?.Feed(data, _source.WholeFrames);
        }
        catch { }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        MicRelay? mic = _mic;
        _mic = null;
        if (mic is not null) await mic.StopAsync();
        await _source.StopAsync();
        _queue.CompleteAdding();
        _thread?.Join(2000);
        lock (_recordGate) { _record?.Dispose(); _record = null; _mkv?.Dispose(); _mkv = null; }
        _decoder?.Dispose();
        _decoder = null;
        lock (_audioGate)
        {
            _player?.Dispose(); _player = null;
            _aac?.Dispose(); _aac = null;
        }
        _source.Dispose();
        if (_viewer is { IsDisposed: false } v)
        {
            try { if (v.InvokeRequired) v.BeginInvoke(new Action(v.Close)); else v.Close(); } catch { }
        }
    }

    public Bitmap? Snapshot() => _viewer?.Snapshot();
}
