using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AdbTool.Core;

namespace AdbTool.Cast;

internal enum CastKind { None, Quest, PlayForDream }

/// <summary>
/// Undistorted "casting" picture of a headset, delivered over ADB (USB or Wi-Fi ADB).
/// Implementations deliver H.264 in Annex-B format.
/// </summary>
internal abstract class CastSource : IDisposable
{
    protected readonly AdbClient Adb;
    protected readonly string AdbPath, Serial;
    protected readonly CancellationTokenSource Cts = new();
    private int _ended;

    protected CastSource(string adbPath, string serial)
    {
        AdbPath = adbPath;
        Serial = serial;
        Adb = new AdbClient(adbPath, serial);
    }

    /// <summary>H.264 Annex-B data (decoding thread).</summary>
    public event Action<byte[], int, int>? Video;
    /// <summary>Raw stream for recording (same thread as Video).</summary>
    public event Action<byte[], int, int>? Raw;
    public event Action<string>? Log;
    /// <summary>Audio as ADTS-framed AAC: frame, sample rate, channels.</summary>
    public event Action<byte[], int, int>? AudioAac;
    /// <summary>Audio as 16-bit PCM (interleaved): samples, count, sample rate, channels.</summary>
    public event Action<short[], int, int, int>? AudioPcm;
    /// <summary>Ask the headset for audio (set before StartAsync).</summary>
    public bool WantAudio { get; set; }
    /// <summary>Stream ended; argument = reason (null when stopped by the user).</summary>
    public event Action<string?>? Ended;

    public abstract string RecordExtension { get; }
    /// <summary>True when every Video call carries exactly one complete frame.</summary>
    public virtual bool WholeFrames => false;
    public abstract string Name { get; }

    public abstract Task StartAsync();

    protected abstract Task StopDeviceAsync();

    public async Task StopAsync()
    {
        Cts.Cancel();
        try { await StopDeviceAsync(); } catch { }
        End(null);
    }

    protected void OnVideo(byte[] data, int offset, int count) => Video?.Invoke(data, offset, count);
    protected void OnRaw(byte[] data, int offset, int count) => Raw?.Invoke(data, offset, count);
    protected void Write(string text) => Log?.Invoke(text);
    protected void OnAac(byte[] adts, int rate, int channels) => AudioAac?.Invoke(adts, rate, channels);
    protected void OnPcm(short[] samples, int count, int rate, int channels) => AudioPcm?.Invoke(samples, count, rate, channels);

    protected void End(string? reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1) return;
        Cts.Cancel();
        Ended?.Invoke(reason);
    }

    protected async Task<string> ShellAsync(string command, int seconds = 20)
    {
        AdbResult r = await Adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(seconds), "shell", command);
        return r.Combined;
    }

    public virtual void Dispose() => Cts.Cancel();

    /// <summary>Which casting method fits the device (by model / installed system services).</summary>
    public static async Task<CastKind> DetectAsync(string adbPath, AdbDevice device)
    {
        string model = device.Model + " " + device.Product + " " + device.Device;
        if (model.Contains("Quest", StringComparison.OrdinalIgnoreCase) || model.Contains("eureka", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("hollywood", StringComparison.OrdinalIgnoreCase) || model.Contains("seacliff", StringComparison.OrdinalIgnoreCase))
            return CastKind.Quest;
        if (model.Contains("PFDM", StringComparison.OrdinalIgnoreCase) || model.Contains("YVR", StringComparison.OrdinalIgnoreCase))
            return CastKind.PlayForDream;
        try
        {
            AdbResult r = await new AdbClient(adbPath, device.Serial).CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10),
                "shell", "pm path com.oculus.metacam; pm path com.yvr.screencast");
            if (r.Output.Contains("com.yvr.screencast", StringComparison.OrdinalIgnoreCase) || r.Output.Contains("YvrScreencast")) return CastKind.PlayForDream;
            if (r.Output.Contains("metacam", StringComparison.OrdinalIgnoreCase)) return CastKind.Quest;
        }
        catch { }
        return CastKind.None;
    }

    public static CastSource Create(CastKind kind, string adbPath, string serial) => kind switch
    {
        CastKind.Quest => new QuestCastSource(adbPath, serial),
        CastKind.PlayForDream => new PfdCastSource(adbPath, serial),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

/// <summary>
/// Meta Quest: the system casting service ("Magic Island" / metacam) is told via debug
/// properties to send its casting picture to localhost:4445 on the headset, which
/// "adb reverse" connects to a listener in ADBora. Same mechanism as the Meta Quest
/// Developer Hub. After the session token arrives a short handshake is sent and
/// frame markers are acknowledged; video arrives as H.264 NAL units.
/// </summary>
internal sealed class QuestCastSource : CastSource
{
    private const string Service = "com.oculus.magicislandcastingservice/.CastingService";
    private const string Pkg = "com.oculus.magicislandcastingservice";
    private const int DevicePort = 4445;

    private TcpListener? _listener;
    private readonly object _gate = new();
    private readonly List<Conn> _conns = new();
    private int _nextId = 1;
    private readonly string _uuid = Guid.NewGuid().ToString();
    private readonly string _stamp = ((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L) % 1_000_000_000_000_000L).ToString().PadLeft(15, '0');
    private byte[]? _token;
    private Conn? _tokenConn, _ctl;
    private uint _txSeq, _frameCounter = 3;
    private bool _handshake, _acked;
    private uint? _lastMarker;
    private int _width = 2064, _height = 2208;
    private long _videoBytes;
    private bool _audioPulse;
    private int _audioLogged;
    private byte[] _adtsBuf = Array.Empty<byte>();
    private short[] _pcm = new short[4096];
    private static readonly HashSet<int> ControlWords = new() { 3, 5, 7, 9, 10, 12, 24, 31 };

    private string Audio => WantAudio ? "true" : "false";

    public QuestCastSource(string adbPath, string serial) : base(adbPath, serial) { }

    public override string RecordExtension => ".mkv";
    public override string Name => "Meta Quest";

    private sealed class Conn
    {
        public int Id; public TcpClient Client = null!; public long Rx; public uint? LastFrame;
    }

    public override async Task StartAsync()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        await Adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "reverse", "--remove", $"tcp:{DevicePort}");
        AdbResult rev = await Adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "reverse", $"tcp:{DevicePort}", $"tcp:{port}");
        if (!rev.Ok) throw new InvalidOperationException("adb reverse: " + rev.Combined);
        _ = AcceptLoopAsync();

        string pm = await ShellAsync($"pm path {Pkg}");
        if (!pm.Contains("package:"))
            throw new InvalidOperationException(Loc.T("Der Casting-Dienst der Quest fehlt (com.oculus.magicislandcastingservice).",
                "The Quest casting service is missing (com.oculus.magicislandcastingservice)."));

        string ex = $"--es launch_surface 'Casting' --es capture_type 'eye_buffer' --es launch_surface_session_uuid {_uuid} --es session_uuid {_uuid} " +
                    "--es eye both --es eye_selection both --es selectedEye both --es video_capture_eye_selection both --es INTENT_EXTRA_KEY_EYE BOTH --es INTENT_KEY_EYE_SELECTION BOTH " +
                    $"--ez support_mic_audio {Audio} --ez include_audio {Audio} --ez new_audio_capture {Audio} --ez INTENT_KEY_NEW_AUDIO_CAPTURE {Audio} --ez INTENT_KEY_AUDIO_ONLY false --ez audio_only false";
        string props = "setprop debug.oculus.command_line_media_capture true; setprop debug.oculus.magic.enabled 1; setprop debug.oculus.magic.usb 1; " +
                       $"setprop debug.oculus.magic.host localhost; setprop debug.oculus.magic.port {DevicePort}; setprop debug.oculus.magic.serialNumber {Serial}; " +
                       $"setprop debug.oculus.magic.LaunchSurface Casting; setprop debug.oculus.magic.LaunchSurfaceSessionUuid {_uuid}; " +
                       "setprop debug.oculus.magic.video_capture_eye_selection both; setprop debug.oculus.magic.selectedEye both; setprop debug.oculus.magic.captureFormat eye_buffer; " +
                       (WantAudio ? "setprop debug.oculus.magic.support_mic_audio 1; setprop debug.oculus.magic.capture_audio 1; setprop debug.oculus.magic.include_audio 1; setprop debug.oculus.magic.audioCaptureBitrate 64000; "
                                  : "setprop debug.oculus.magic.capture_audio 0; setprop debug.oculus.magic.include_audio 0; ");
        for (int attempt = 0; attempt < 2 && !Cts.IsCancellationRequested; attempt++)
        {
            string xr = attempt == 0 ? "true" : "false";
            Write(Loc.T($"Starte Quest-Casting (Versuch {attempt + 1}) …", $"Starting Quest casting (attempt {attempt + 1}) …"));
            await ShellAsync(props +
                $"am start-foreground-service -n {Service} -a {Pkg}.STOP_CASTING; am force-stop {Pkg}; sleep 0.3; " +
                $"am start-foreground-service -n {Service} -a {Pkg}.START_SERVICE --ez use_openxr {xr} {ex}; sleep 0.35; " +
                $"am start-foreground-service -n {Service} -a {Pkg}.CONNECT {ex}; sleep 0.15; " +
                $"am start-foreground-service -n {Service} -a {Pkg}.START_CASTING --es id {_uuid} {ex}; sleep 0.15; " +
                $"am startservice -a CONNECT_MAGIC_CASTING --es launch_surface_session_uuid {_uuid} --es session_uuid {_uuid} com.oculus.metacam/com.oculus.metacam.casting.CastingService; sleep 0.15; " +
                $"am broadcast -a com.oculus.magicisland.sdk.intent.CONNECT {ex}; sleep 0.15; am broadcast -a {Pkg}.CONNECT {ex}", 30);
            for (int i = 0; i < 80 && Interlocked.Read(ref _videoBytes) == 0 && !Cts.IsCancellationRequested; i++)
                await Task.Delay(100);
            if (Interlocked.Read(ref _videoBytes) > 0) return;
        }
        if (!Cts.IsCancellationRequested)
            throw new InvalidOperationException(Loc.T("Die Quest liefert kein Bild. Läuft schon ein anderes Casting (Handy-App, Browser, SideQuest)? Dieses zuerst beenden.",
                "The Quest sends no picture. Is another casting running (phone app, browser, SideQuest)? Stop it first."));
    }

    protected override async Task StopDeviceAsync()
    {
        try { _listener?.Stop(); } catch { }
        lock (_gate) foreach (Conn c in _conns) try { c.Client.Close(); } catch { }
        await ShellAsync($"am start-foreground-service -n {Service} -a {Pkg}.STOP_CASTING", 10);
        await Adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "reverse", "--remove", $"tcp:{DevicePort}");
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!Cts.IsCancellationRequested)
            {
                TcpClient client = await _listener!.AcceptTcpClientAsync(Cts.Token);
                client.NoDelay = true;
                var c = new Conn { Id = _nextId++, Client = client };
                lock (_gate) { _conns.Add(c); SelectControl(); }
                _ = Task.Run(() => ReadLoop(c));
            }
        }
        catch { }
    }

    private void ReadLoop(Conn c)
    {
        var buf = new byte[1 << 17];
        byte[] pend = new byte[1 << 18];
        int pendLen = 0;
        try
        {
            NetworkStream st = c.Client.GetStream();
            while (!Cts.IsCancellationRequested)
            {
                int n = st.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                c.Rx += n;
                if (pendLen + n > pend.Length) Array.Resize(ref pend, Math.Max(pend.Length * 2, pendLen + n));
                Buffer.BlockCopy(buf, 0, pend, pendLen, n);
                pendLen += n;
                int off = 0;
                while (pendLen - off >= 4)
                {
                    byte v = pend[off], t = pend[off + 1];
                    int words = pend[off + 2] | pend[off + 3] << 8, len = 4 + words * 4;
                    if (!((v == 16 || v == 24) && (t == 2 || t == 3) && words >= 2)) { off++; continue; }
                    if (pendLen - off < len) break;
                    uint seq = BitConverter.ToUInt32(pend, off + 4);
                    lock (_gate) OnPacket(c, words, seq, pend, off + 8, len - 8);
                    off += len;
                }
                if (off > 0) { Buffer.BlockCopy(pend, off, pend, 0, pendLen - off); pendLen -= off; }
            }
        }
        catch { }
        lock (_gate)
        {
            _conns.Remove(c);
            if (_ctl == c) { _ctl = null; SelectControl(); }
            if (_conns.Count == 0 && Interlocked.Read(ref _videoBytes) > 0 && !Cts.IsCancellationRequested)
                Task.Delay(1500).ContinueWith(_ => { lock (_gate) if (_conns.Count == 0) End(Loc.T("Die Quest hat das Casting beendet.", "The Quest stopped casting.")); });
        }
    }

    private static uint Be(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    private static void PutBe(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private void OnPacket(Conn c, int words, uint seq, byte[] b, int o, int len)
    {
        if (words == 31 && len >= 16 && Be(b, o) == 300)
        {
            int w = (int)Be(b, o + 8), h = (int)Be(b, o + 12);
            if (w is >= 160 and <= 10000 && h is >= 160 and <= 10000) { _width = w; _height = h; }
        }
        if (seq == 1 && words == 7 && len >= 4)
        {
            _token = b[o..(o + 4)];
            _tokenConn = c;
            _handshake = false;
            _acked = false;
            SelectControl();
        }
        if (_token is not null && words == 7 && len == 24 && b.AsSpan(o, 4).SequenceEqual(_token))
            c.LastFrame = Be(b, o + 4);
        if (_token is not null && len == 8 && b[o] == 0 && b[o + 1] == 0 && b[o + 2] == 0 && b[o + 3] == 3)
            Ack(Be(b, o + 4), c.LastFrame is uint lf && lf >= 2 ? lf + 1 : null);
        // Video: payload starts with an Annex-B start code and a video NAL (types 1–23)
        if (len >= 5 && b[o] == 0 && b[o + 1] == 0 && (b[o + 2] == 1 || (b[o + 2] == 0 && b[o + 3] == 1)))
        {
            int nal = b[o + (b[o + 2] == 1 ? 3 : 4)] & 31;
            if (nal is >= 1 and <= 23)
            {
                Interlocked.Add(ref _videoBytes, len);
                OnRaw(b, o, len);
                OnVideo(b, o, len);
                return;
            }
        }
        if (WantAudio && len >= 2 && !ControlWords.Contains(words)) OnAudioPayload(b, o, len, words);
    }

    /// <summary>Audio packets: PCM 16-bit big-endian stereo 48 kHz, or AAC (ADTS).</summary>
    private void OnAudioPayload(byte[] b, int o, int len, int words)
    {
        if (len >= 1024 && (len & 3) == 0 && words == len / 4 + 1)
        {
            int n = len / 2;
            if (_pcm.Length < n) _pcm = new short[n];
            for (int i = 0; i < n; i++) _pcm[i] = (short)(b[o + i * 2] << 8 | b[o + i * 2 + 1]);
            if (_audioLogged++ == 0) Write("Audio: PCM 48 kHz");
            OnPcm(_pcm, n, 48000, 2);
            return;
        }
        int sync = -1;
        for (int i = 0; i + 7 <= len; i++) if (b[o + i] == 0xFF && (b[o + i + 1] & 0xF0) == 0xF0) { sync = i; break; }
        if (sync < 0)
        {
            if (_audioLogged++ < 3) Write($"Audio: unknown packet words={words} len={len} head={Convert.ToHexString(b, o, Math.Min(24, len))}");
            return;
        }
        byte[] all = new byte[_adtsBuf.Length + len - sync];
        _adtsBuf.CopyTo(all, 0);
        Buffer.BlockCopy(b, o + sync, all, _adtsBuf.Length, len - sync);
        int p = 0;
        while (p + 7 <= all.Length)
        {
            if (all[p] != 0xFF || (all[p + 1] & 0xF0) != 0xF0) { p++; continue; }
            int frameLen = (all[p + 3] & 3) << 11 | all[p + 4] << 3 | all[p + 5] >> 5;
            if (frameLen < 7) { p++; continue; }
            if (p + frameLen > all.Length) break;
            int sf = all[p + 2] >> 2 & 15, ch = (all[p + 2] & 1) << 2 | all[p + 3] >> 6;
            if (_audioLogged++ == 0) Write($"Audio: AAC {AacDecoder.SampleRates[Math.Min(sf, 12)]} Hz, {ch} ch");
            OnAac(all[p..(p + frameLen)], AacDecoder.SampleRates[Math.Min(sf, 12)], ch);
            p += frameLen;
        }
        _adtsBuf = all[p..];
        if (_adtsBuf.Length > 1 << 16) _adtsBuf = Array.Empty<byte>();
    }

    private void SelectControl()
    {
        if (_token is null || _tokenConn is null) return;
        Conn c = _conns.Where(x => x != _tokenConn).OrderBy(x => x.Rx).FirstOrDefault() ?? _tokenConn;
        if (_ctl != c) { _ctl = c; _handshake = false; }
        Handshake();
    }

    private byte[] Packet(int words)
    {
        var b = new byte[4 + words * 4];
        b[0] = 16; b[1] = 2; b[2] = (byte)words; b[3] = (byte)(words >> 8);
        BitConverter.GetBytes(_txSeq++).CopyTo(b, 4);
        return b;
    }

    private byte[] Words(int count, uint[] values, byte[]? tail = null)
    {
        byte[] b = Packet(count);
        int o = 8;
        Array.Copy(_token!, 0, b, o, 4); o += 4;
        foreach (uint v in values) { PutBe(b, o, v); o += 4; }
        tail?.CopyTo(b, o);
        return b;
    }

    private byte[] Status(uint frame, uint value, uint tail = 0) => Words(9, new[] { frame, frame, 0u, 1u, 2u, value, tail });

    private void Send(byte[] data)
    {
        try { _ctl?.Client.GetStream().Write(data); } catch { }
    }

    private void Handshake()
    {
        if (_handshake || _ctl is null || _token is null) return;
        _handshake = true;
        Send(Words(12, new uint[] { 1, 1, 0, 1, 2, 600, 0, 0, 0, 0 }));
        Send(Words(24, new uint[] { 2, 2, 0, 1, 2, 7, (uint)_width, (uint)_height, 0, 1, 0, 0, 36 }, Encoding.ASCII.GetBytes(_uuid)));
        Send(Status(3, 101));
    }

    private void Ack(uint marker, uint? frame)
    {
        Handshake();
        if (_lastMarker == marker) return;
        _lastMarker = marker;
        uint f = frame ?? _frameCounter++;
        var tail = new byte[36 + 4 + 15 + 1];
        Encoding.ASCII.GetBytes(_uuid).CopyTo(tail, 0);
        PutBe(tail, 36, 15);
        Encoding.ASCII.GetBytes(_stamp).CopyTo(tail, 40);
        Send(Words(24, new uint[] { f, f, 0, 1, 2, 4, marker, 36 }, tail));
        if (_acked) return;
        _acked = true;
        foreach ((uint d, uint v) in new[] { (2u, 205u), (3u, 101u), (4u, 205u), (5u, 205u), (6u, 301u), (7u, 101u) })
            Send(Status(f + d, v));
        f += 8;
        if (WantAudio && !_audioPulse)
        {
            // "audio enable" pulse (value 503, tail 1), like the Meta Quest Developer Hub
            _audioPulse = true;
            Send(Status(f, 503, 1));
            _frameCounter = Math.Max(_frameCounter, f + 1);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        try { _listener?.Stop(); } catch { }
    }
}

/// <summary>
/// Play For Dream (YVR): the built-in casting app "com.yvr.screencast" can serve the
/// undistorted single-eye picture itself as HTTP-FLV on port 5000 (meant for the
/// browser / phone casting). ADBora starts it with a broadcast and reads it through
/// "adb forward", so it runs over the USB cable.
/// </summary>
internal sealed class PfdCastSource : CastSource
{
    private const string Receiver = "com.yvr.screencast/.RtspReceiver";
    private TcpClient? _client;
    private int _localPort;

    public PfdCastSource(string adbPath, string serial) : base(adbPath, serial) { }

    public override string RecordExtension => ".mkv";
    public override bool WholeFrames => true;
    public override string Name => "Play For Dream";

    public override async Task StartAsync()
    {
        Write(Loc.T("Starte PFD-Casting …", "Starting PFD casting …"));
        await ShellAsync($"am broadcast -a android.intent.action.START_FLV -n {Receiver}");
        AdbResult fwd = await Adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "forward", "tcp:0", "tcp:5000");
        if (!fwd.Ok || !int.TryParse(fwd.Output.Trim(), out _localPort))
            throw new InvalidOperationException("adb forward: " + fwd.Combined);

        Stream? stream = null;
        for (int i = 0; i < 20 && !Cts.IsCancellationRequested && stream is null; i++)
        {
            try { stream = await OpenAsync(); }
            catch { await Task.Delay(500); }
        }
        if (stream is null)
            throw new InvalidOperationException(Loc.T("Der Casting-Dienst der PFD antwortet nicht (Port 5000).", "The PFD casting service does not answer (port 5000)."));
        _ = Task.Run(() => ReadFlv(stream));
    }

    private async Task<Stream> OpenAsync()
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, _localPort, Cts.Token);
        NetworkStream st = client.GetStream();
        byte[] req = Encoding.ASCII.GetBytes("GET //flv HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: keep-alive\r\n\r\n");
        await st.WriteAsync(req, Cts.Token);
        var head = new StringBuilder();
        st.ReadTimeout = 8000;
        while (!head.ToString().EndsWith("\r\n\r\n"))
        {
            int b = st.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            head.Append((char)b);
            if (head.Length > 8192) throw new InvalidDataException("HTTP header");
        }
        string h = head.ToString();
        if (!h.StartsWith("HTTP/1.1 200") && !h.StartsWith("HTTP/1.0 200")) { client.Close(); throw new InvalidDataException(h.Split('\r')[0]); }
        _client = client;
        return Regex.IsMatch(h, @"Transfer-Encoding:\s*chunked", RegexOptions.IgnoreCase) ? new ChunkedStream(st) : st;
    }

    private void ReadFlv(Stream st)
    {
        string? reason = null;
        try
        {
            byte[] header = ReadExact(st, 13); // FLV header (9) + PreviousTagSize0 (4)
            if (header[0] != 'F' || header[1] != 'L' || header[2] != 'V') throw new InvalidDataException("no FLV");
            OnRaw(header, 0, header.Length);
            int nalLength = 4;
            var annexB = new MemoryStream();
            while (!Cts.IsCancellationRequested)
            {
                byte[] th = ReadExact(st, 11);
                int type = th[0] & 31, size = th[1] << 16 | th[2] << 8 | th[3];
                byte[] data = ReadExact(st, size + 4); // tag data + PreviousTagSize
                OnRaw(th, 0, 11);
                OnRaw(data, 0, data.Length);
                if (type == 8 && size >= 2 && (data[0] >> 4) == 10) { OnFlvAudio(data, size); continue; }
                if (type != 9 || size < 5 || (data[0] & 15) != 7) continue; // video / AVC only
                annexB.SetLength(0);
                if (data[1] == 0)
                {
                    // AVCDecoderConfigurationRecord → SPS / PPS
                    int o = 5;
                    nalLength = (data[o + 4] & 3) + 1;
                    int count = data[o + 5] & 31; o += 6;
                    for (int k = 0; k < 2; k++)
                    {
                        for (int i = 0; i < count && o + 2 <= size; i++)
                        {
                            int len = data[o] << 8 | data[o + 1]; o += 2;
                            annexB.Write(StartCode); annexB.Write(data, o, len); o += len;
                        }
                        if (k == 0 && o < size) count = data[o++];
                    }
                }
                else if (data[1] == 1)
                {
                    int o = 5;
                    while (o + nalLength <= size)
                    {
                        int len = 0;
                        for (int i = 0; i < nalLength; i++) len = len << 8 | data[o + i];
                        o += nalLength;
                        if (len <= 0 || o + len > size) break;
                        annexB.Write(StartCode); annexB.Write(data, o, len); o += len;
                    }
                }
                if (annexB.Length > 0) OnVideo(annexB.GetBuffer(), 0, (int)annexB.Length);
            }
        }
        catch (Exception ex) when (!Cts.IsCancellationRequested)
        {
            reason = Loc.T("Verbindung zur PFD unterbrochen: ", "Connection to the PFD lost: ") + ex.Message;
        }
        catch { }
        if (!Cts.IsCancellationRequested) End(reason ?? Loc.T("Die PFD hat das Casting beendet.", "The PFD stopped casting."));
    }

    private static readonly byte[] StartCode = { 0, 0, 0, 1 };
    private int _aacType, _aacRateIndex = -1, _aacChannels;

    /// <summary>FLV AAC audio: sequence header (AudioSpecificConfig) or raw frame → ADTS.</summary>
    private void OnFlvAudio(byte[] data, int size)
    {
        if (data[1] == 0 && size >= 4)
        {
            _aacType = data[2] >> 3;
            _aacRateIndex = (data[2] & 7) << 1 | data[3] >> 7;
            _aacChannels = data[3] >> 3 & 15;
            Write($"Audio: AAC {AacDecoder.SampleRates[Math.Min(_aacRateIndex, 12)]} Hz, {_aacChannels} ch");
        }
        else if (data[1] == 1 && _aacRateIndex >= 0 && WantAudio)
            OnAac(AacDecoder.Adts(_aacType, _aacRateIndex, _aacChannels, data.AsSpan(2, size - 2)), AacDecoder.SampleRates[Math.Min(_aacRateIndex, 12)], _aacChannels);
    }

    private static byte[] ReadExact(Stream st, int count)
    {
        var b = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = st.Read(b, got, count - got);
            if (n <= 0) throw new EndOfStreamException();
            got += n;
        }
        return b;
    }

    protected override async Task StopDeviceAsync()
    {
        try { _client?.Close(); } catch { }
        await ShellAsync($"am broadcast -a android.intent.action.STOP_FLV -n {Receiver}", 10);
        if (_localPort > 0)
            await Adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "forward", "--remove", $"tcp:{_localPort}");
    }

    public override void Dispose()
    {
        base.Dispose();
        try { _client?.Close(); } catch { }
    }

    /// <summary>Minimal reader for HTTP "Transfer-Encoding: chunked".</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly Stream _inner;
        private int _left;
        private bool _done;
        public ChunkedStream(Stream inner) => _inner = inner;

        private string ReadLine()
        {
            var sb = new StringBuilder();
            while (true)
            {
                int b = _inner.ReadByte();
                if (b < 0) throw new EndOfStreamException();
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)b);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_done) return 0;
            if (_left == 0)
            {
                string line = ReadLine();
                if (line.Length == 0) line = ReadLine(); // CRLF after the previous chunk
                _left = Convert.ToInt32(line.Split(';')[0].Trim(), 16);
                if (_left == 0) { _done = true; return 0; }
            }
            int n = _inner.Read(buffer, offset, Math.Min(count, _left));
            if (n <= 0) throw new EndOfStreamException();
            _left -= n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
