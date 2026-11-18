using System.Runtime.InteropServices;

namespace AdbTool.Cast;

/// <summary>
/// AAC decoder (FFmpeg from the scrcpy package, input = ADTS frames) → 16-bit PCM.
/// </summary>
internal sealed unsafe class AacDecoder : IDisposable
{
    private const int CodecIdAac = 0x15002;
    private IntPtr _ctx, _packet, _frame;
    private short[] _pcm = new short[8192];

    /// <summary>Interleaved 16-bit samples, count, channels.</summary>
    public event Action<short[], int, int>? Decoded;

    public AacDecoder()
    {
        IntPtr codec = H264Decoder._findDecoder(CodecIdAac);
        if (codec == IntPtr.Zero) throw new InvalidOperationException("AAC decoder not available");
        _ctx = H264Decoder._allocContext(codec);
        int r = H264Decoder._open(_ctx, codec, IntPtr.Zero);
        if (r < 0) throw new InvalidOperationException($"avcodec_open2 (AAC) failed ({r})");
        _packet = H264Decoder._packetAlloc();
        _frame = H264Decoder._frameAlloc();
    }

    /// <summary>One ADTS frame (header + raw AAC).</summary>
    public void Feed(ReadOnlySpan<byte> adts, int channels)
    {
        if (_ctx == IntPtr.Zero) return;
        byte[] padded = new byte[adts.Length + 64];
        adts.CopyTo(padded);
        fixed (byte* p = padded)
        {
            *(IntPtr*)((byte*)_packet + 24) = (IntPtr)p;
            *(int*)((byte*)_packet + 32) = adts.Length;
            if (H264Decoder._sendPacket(_ctx, _packet) < 0) return;
        }
        while (H264Decoder._receiveFrame(_ctx, _frame) == 0)
        {
            try { Convert(channels); }
            finally { H264Decoder._frameUnref(_frame); }
        }
    }

    private void Convert(int channels)
    {
        byte* f = (byte*)_frame;
        byte** data = (byte**)f;
        int samples = *(int*)(f + 112), format = *(int*)(f + 116);
        if (samples <= 0 || channels <= 0) return;
        int n = samples * channels;
        if (_pcm.Length < n) _pcm = new short[n];
        for (int c = 0; c < channels; c++)
        {
            for (int i = 0; i < samples; i++)
            {
                float v = format switch
                {
                    8 => ((float*)data[c])[i],                       // fltp
                    3 => ((float*)data[0])[i * channels + c],        // flt
                    6 => ((short*)data[c])[i] / 32768f,              // s16p
                    1 => ((short*)data[0])[i * channels + c] / 32768f, // s16
                    _ => 0
                };
                int s = (int)(v * 32767f);
                _pcm[i * channels + c] = (short)(s < -32768 ? -32768 : s > 32767 ? 32767 : s);
            }
        }
        Decoded?.Invoke(_pcm, n, channels);
    }

    public void Dispose()
    {
        if (_packet != IntPtr.Zero) { IntPtr p = _packet; H264Decoder._packetFree(&p); _packet = IntPtr.Zero; }
        if (_frame != IntPtr.Zero) { IntPtr p = _frame; H264Decoder._frameFree(&p); _frame = IntPtr.Zero; }
        if (_ctx != IntPtr.Zero) { IntPtr p = _ctx; H264Decoder._freeContext(&p); _ctx = IntPtr.Zero; }
    }

    /// <summary>Builds a 7-byte ADTS header for a raw AAC frame.</summary>
    public static byte[] Adts(int objectType, int sampleRateIndex, int channels, ReadOnlySpan<byte> raw)
    {
        int len = raw.Length + 7;
        var b = new byte[len];
        b[0] = 0xFF;
        b[1] = 0xF1; // MPEG-4, no CRC
        b[2] = (byte)(((objectType - 1) & 3) << 6 | (sampleRateIndex & 15) << 2 | (channels >> 2 & 1));
        b[3] = (byte)((channels & 3) << 6 | (len >> 11 & 3));
        b[4] = (byte)(len >> 3 & 0xFF);
        b[5] = (byte)((len & 7) << 5 | 0x1F);
        b[6] = 0xFC;
        raw.CopyTo(b.AsSpan(7));
        return b;
    }

    public static readonly int[] SampleRates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
}

/// <summary>Plays 16-bit PCM on the default audio device of the PC (Windows: waveOut, Linux: PulseAudio/PipeWire).</summary>
internal sealed class AudioPlayer : IDisposable
{
    private readonly IAudioOutput _out;

    public int Rate { get; }
    public int Channels { get; }

    public AudioPlayer(int rate, int channels, int maxLatencyMs = 300)
    {
        Rate = rate;
        Channels = channels;
        _out = OperatingSystem.IsWindows()
            ? new WaveOutPlayer(rate, channels, maxLatencyMs)
            : new PulsePlayer(rate, channels, maxLatencyMs);
    }

    /// <summary>Queues interleaved samples; never blocks (drops when more than the latency is queued).</summary>
    public void Write(short[] samples, int count) => _out.Write(samples, count);

    public void Dispose() => _out.Dispose();
}

internal interface IAudioOutput : IDisposable
{
    void Write(short[] samples, int count);
}

/// <summary>
/// Linux: libpulse-simple (PulseAudio or PipeWire via pipewire-pulse). pa_simple_write blocks,
/// so a writer thread takes the blocks from a queue; the queue is limited to the latency.
/// </summary>
internal sealed class PulsePlayer : IAudioOutput
{
    private const string Lib = "libpulse-simple.so.0";

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleSpec { public int Format; public uint Rate; public byte Channels; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferAttr { public uint MaxLength, TLength, PreBuf, MinReq, FragSize; }

    [DllImport(Lib)] private static extern IntPtr pa_simple_new(string? server, string name, int dir, string? dev, string streamName,
        ref SampleSpec ss, IntPtr map, ref BufferAttr attr, out int error);
    [DllImport(Lib)] private static extern unsafe int pa_simple_write(IntPtr s, void* data, nuint bytes, out int error);
    [DllImport(Lib)] private static extern int pa_simple_flush(IntPtr s, out int error);
    [DllImport(Lib)] private static extern void pa_simple_free(IntPtr s);
    [DllImport("libpulse.so.0")] private static extern IntPtr pa_strerror(int error);

    private const int SampleS16Le = 3, DirectionPlayback = 1;
    private readonly IntPtr _handle;
    private readonly object _gate = new();
    private readonly Queue<short[]> _queue = new();
    private readonly int _maxQueuedSamples;
    private int _queuedSamples;
    private bool _stop;
    private readonly Thread _thread;

    public PulsePlayer(int rate, int channels, int maxLatencyMs)
    {
        var spec = new SampleSpec { Format = SampleS16Le, Rate = (uint)rate, Channels = (byte)channels };
        int bytesPerSecond = rate * channels * 2;
        // ~60 ms server-side buffer: low latency without crackling
        var attr = new BufferAttr { MaxLength = uint.MaxValue, TLength = (uint)(bytesPerSecond * 60 / 1000), PreBuf = uint.MaxValue, MinReq = uint.MaxValue, FragSize = uint.MaxValue };
        _handle = pa_simple_new(null, "ADBora", DirectionPlayback, null, "Headset", ref spec, IntPtr.Zero, ref attr, out int error);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("PulseAudio: " + (Marshal.PtrToStringUTF8(pa_strerror(error)) ?? error.ToString()));
        _maxQueuedSamples = rate * channels * maxLatencyMs / 1000;
        _thread = new Thread(Run) { IsBackground = true, Name = "ADBora audio out" };
        _thread.Start();
    }

    public void Write(short[] samples, int count)
    {
        lock (_gate)
        {
            if (_stop || _queuedSamples + count > _maxQueuedSamples) return; // keep latency low: drop
            _queue.Enqueue(samples.AsSpan(0, count).ToArray());
            _queuedSamples += count;
            Monitor.Pulse(_gate);
        }
    }

    private unsafe void Run()
    {
        while (true)
        {
            short[] block;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_stop) Monitor.Wait(_gate);
                if (_stop) return;
                block = _queue.Dequeue();
                _queuedSamples -= block.Length;
            }
            fixed (short* p = block)
                if (pa_simple_write(_handle, p, (nuint)(block.Length * 2), out _) < 0) return;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stop) return;
            _stop = true;
            Monitor.PulseAll(_gate);
        }
        _thread.Join(1000);
        pa_simple_flush(_handle, out _);
        pa_simple_free(_handle);
    }
}

/// <summary>Plays 16-bit PCM through the default Windows audio device (waveOut), low latency.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WaveOutPlayer : IAudioOutput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat { public ushort Tag, Channels; public uint SamplesPerSec, AvgBytesPerSec; public ushort BlockAlign, BitsPerSample, Size; }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hwo, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);

    // WAVEHDR (x64): lpData 0, dwBufferLength 8, dwBytesRecorded 12, dwUser 16, dwFlags 24, dwLoops 28, lpNext 32, reserved 40
    private const int HeaderSize = 48, FlagsOffset = 24, Done = 1;
    private readonly object _gate = new();
    private readonly List<(IntPtr Header, IntPtr Data, int Bytes)> _queued = new();
    private IntPtr _handle;
    private readonly int _bytesPerSecond;
    private readonly int _maxQueuedBytes;

    public int Rate { get; }
    public int Channels { get; }

    public WaveOutPlayer(int rate, int channels, int maxLatencyMs = 300)
    {
        Rate = rate;
        Channels = channels;
        var fmt = new WaveFormat
        {
            Tag = 1, Channels = (ushort)channels, SamplesPerSec = (uint)rate, BitsPerSample = 16,
            BlockAlign = (ushort)(channels * 2), AvgBytesPerSec = (uint)(rate * channels * 2)
        };
        _bytesPerSecond = rate * channels * 2;
        _maxQueuedBytes = _bytesPerSecond * maxLatencyMs / 1000;
        int r = waveOutOpen(out _handle, 0xFFFFFFFF, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (r != 0) throw new InvalidOperationException($"waveOutOpen failed ({r})");
    }

    public void Write(short[] samples, int count)
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            Reclaim();
            int bytes = count * 2;
            if (_queued.Sum(q => q.Bytes) + bytes > _maxQueuedBytes) return; // keep latency low: drop
            IntPtr data = Marshal.AllocHGlobal(bytes);
            Marshal.Copy(samples, 0, data, count);
            IntPtr hdr = Marshal.AllocHGlobal(HeaderSize);
            for (int i = 0; i < HeaderSize; i += 8) Marshal.WriteInt64(hdr, i, 0);
            Marshal.WriteIntPtr(hdr, 0, data);
            Marshal.WriteInt32(hdr, 8, bytes);
            if (waveOutPrepareHeader(_handle, hdr, HeaderSize) != 0 || waveOutWrite(_handle, hdr, HeaderSize) != 0)
            {
                waveOutUnprepareHeader(_handle, hdr, HeaderSize);
                Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(data);
                return;
            }
            _queued.Add((hdr, data, bytes));
        }
    }

    private void Reclaim()
    {
        for (int i = _queued.Count - 1; i >= 0; i--)
        {
            var q = _queued[i];
            if ((Marshal.ReadInt32(q.Header, FlagsOffset) & Done) == 0) continue;
            waveOutUnprepareHeader(_handle, q.Header, HeaderSize);
            Marshal.FreeHGlobal(q.Header); Marshal.FreeHGlobal(q.Data);
            _queued.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            waveOutReset(_handle);
            foreach (var q in _queued)
            {
                waveOutUnprepareHeader(_handle, q.Header, HeaderSize);
                Marshal.FreeHGlobal(q.Header); Marshal.FreeHGlobal(q.Data);
            }
            _queued.Clear();
            waveOutClose(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
