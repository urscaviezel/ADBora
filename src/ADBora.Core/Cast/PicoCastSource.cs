using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Net.Sockets;
using AdbTool.Core;

namespace AdbTool.Cast;

/// <summary>
/// PICO (4 / 4 Ultra …): the system treats every virtual display with the PRESENTATION flag
/// as a "capture display" (like its own screen recorder) and has the XR runtime render the
/// undistorted single-eye view into it. The companion app (ADBora Mic, mode "cast") creates
/// such a display on an H.264 encoder and streams it over "localabstract:adbora_cast".
/// Packets: length, flags (1 = key frame, 2 = codec config), pts (µs), Annex-B data.
/// Game audio: scrcpy-server (from ADBora's scrcpy folder) in audio-only mode with
/// "playback" capture and audio_dup – the headset keeps playing; raw 48 kHz stereo PCM.
/// The microphone comes from the same companion process (socket "adbora_mic").
/// </summary>
internal sealed class PicoCastSource : CastSource
{
    private TcpClient? _client;
    private int _localPort;

    public PicoCastSource(string adbPath, string serial) : base(adbPath, serial) { }

    public override string RecordExtension => ".mkv";
    public override bool WholeFrames => true;
    public override string Name => "PICO";

    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Bitrate { get; set; } = 16_000_000;
    /// <summary>The scrcpy program (its scrcpy-server is used for the game audio); null = no audio.</summary>
    public string? ScrcpyExe { get; set; }
    private Process? _audioProc;
    private TcpClient? _audioClient;
    private int _audioPort;
    private string _scid = "";

    public override async Task StartAsync()
    {
        string cap = (await ShellAsync("getprop pico.pxr.new_capture.enable", 10)).Trim();
        if (cap != "1")
            throw new InvalidOperationException(Loc.T("Diese PICO-Firmware unterstützt die Aufnahme-Schnittstelle nicht (pico.pxr.new_capture.enable).",
                                                      "This PICO firmware does not support the capture interface (pico.pxr.new_capture.enable)."));
        await MicRelay.EnsureInstalledAsync(Adb, Write);
        await ShellAsync($"pm grant {MicRelay.Package} android.permission.RECORD_AUDIO", 10);
        Write(Loc.T("Starte PICO-Casting …", "Starting PICO casting …"));
        await ShellAsync($"am force-stop {MicRelay.Package}", 10);
        AdbResult r = await Adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(15), "shell",
            $"am instrument -e mode cast -e width {Width} -e height {Height} -e bitrate {Bitrate} -e fps 60 {MicRelay.Package}/.MicInstrumentation");
        if (r.Combined.Contains("Error", StringComparison.OrdinalIgnoreCase) && !r.Combined.Contains("log_ctrl"))
            throw new InvalidOperationException("am instrument: " + r.Combined);
        AdbResult fwd = await Adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "forward", "tcp:0", "localabstract:adbora_cast");
        if (!fwd.Ok || !int.TryParse(fwd.Output.Trim(), out _localPort))
            throw new InvalidOperationException("adb forward: " + fwd.Combined);

        for (int i = 0; i < 20 && !Cts.IsCancellationRequested; i++)
        {
            try
            {
                var client = new TcpClient { NoDelay = true, ReceiveBufferSize = 1 << 20 };
                await client.ConnectAsync(IPAddress.Loopback, _localPort, Cts.Token);
                NetworkStream st = client.GetStream();
                st.ReadTimeout = 5000;
                byte[] h = ReadExact(st, 13);
                if (h[0] != 'A' || h[1] != 'D' || h[2] != 'B' || h[3] != 'C') throw new InvalidDataException("header");
                Write($"PICO: {BitConverter.ToInt32(h, 5)} × {BitConverter.ToInt32(h, 9)}");
                st.ReadTimeout = 10000;
                _client = client;
                _ = Task.Run(() => ReadLoop(st));
                if (WantAudio)
                {
                    try { await StartAudioAsync(); }
                    catch (Exception ex) { Write(Loc.T("PICO-Ton nicht verfügbar: ", "PICO audio not available: ") + ex.Message); }
                }
                return;
            }
            catch when (!Cts.IsCancellationRequested) { await Task.Delay(300); }
        }
        throw new InvalidOperationException(Loc.T("ADBora Mic (Casting) antwortet nicht.", "ADBora Mic (casting) does not answer."));
    }

    private void ReadLoop(NetworkStream st)
    {
        string? reason = null;
        byte[]? config = null;
        try
        {
            while (!Cts.IsCancellationRequested)
            {
                byte[] ph = ReadExact(st, 16);
                int len = BitConverter.ToInt32(ph, 0), flags = BitConverter.ToInt32(ph, 4);
                if (len <= 0 || len > 32 << 20) throw new InvalidDataException("packet " + len);
                byte[] data = ReadExact(st, len);
                if ((flags & 2) != 0) { config = data; continue; } // SPS/PPS: with the next frame
                if (config is not null)
                {
                    byte[] merged = new byte[config.Length + data.Length];
                    Buffer.BlockCopy(config, 0, merged, 0, config.Length);
                    Buffer.BlockCopy(data, 0, merged, config.Length, data.Length);
                    data = merged;
                    config = null;
                }
                OnVideo(data, 0, data.Length);
            }
        }
        catch (Exception ex) when (!Cts.IsCancellationRequested)
        {
            reason = Loc.T("Verbindung zur PICO unterbrochen: ", "Connection to the PICO lost: ") + ex.Message;
        }
        catch { }
        if (!Cts.IsCancellationRequested) End(reason ?? Loc.T("Die PICO hat das Casting beendet.", "The PICO stopped casting."));
    }

    private async Task StartAudioAsync()
    {
        string? exe = ScrcpyExe;
        string server = (exe is null ? null : ScrcpyTool.ServerFor(exe)) ?? throw new FileNotFoundException("scrcpy-server");
        string version = Regex.Match(Path.GetDirectoryName(exe!) ?? "", @"v(\d+\.\d+(?:\.\d+)?)").Groups[1].Value;
        if (version.Length == 0)
        {
            AdbResult v = await new AdbClient(exe!).CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "--version");
            version = Regex.Match(v.Output, @"scrcpy (\d+\.\d+(?:\.\d+)?)").Groups[1].Value;
        }
        if (version.Length == 0) throw new InvalidOperationException("scrcpy version");
        const string jar = "/data/local/tmp/adbora-scrcpy.jar";
        AdbResult push = await Adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(30), "push", server, jar);
        if (!push.Ok) throw new InvalidOperationException("push: " + push.Combined);
        _scid = Random.Shared.Next(1, int.MaxValue).ToString("x8");
        var psi = new ProcessStartInfo(AdbPath)
        {
            UseShellExecute = false, RedirectStandardInput = true, // keep stdin open (Linux: inherited /dev/null ends adb shell)
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string a in new[] { "-s", Serial, "shell",
            $"CLASSPATH={jar} app_process / com.genymobile.scrcpy.Server {version} scid={_scid} log_level=warn video=false control=false " +
            "audio=true audio_codec=raw audio_source=playback audio_dup=true tunnel_forward=true send_device_meta=false " +
            "send_frame_meta=false send_dummy_byte=false raw_stream=true cleanup=false" })
            psi.ArgumentList.Add(a);
        _audioProc = Process.Start(psi);
        if (_audioProc is not null)
        {
            _audioProc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Write("scrcpy: " + e.Data); };
            _audioProc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Write("scrcpy: " + e.Data); };
            _audioProc.BeginOutputReadLine();
            _audioProc.BeginErrorReadLine();
        }
        AdbResult fwd = await Adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "forward", "tcp:0", $"localabstract:scrcpy_{_scid}");
        if (!fwd.Ok || !int.TryParse(fwd.Output.Trim(), out _audioPort)) throw new InvalidOperationException("adb forward: " + fwd.Combined);

        // The server needs a moment; until then adb accepts and closes the connection at once.
        for (int i = 0; i < 25 && !Cts.IsCancellationRequested; i++)
        {
            await Task.Delay(300);
            try
            {
                var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(IPAddress.Loopback, _audioPort, Cts.Token);
                NetworkStream st = client.GetStream();
                st.ReadTimeout = 3000;
                byte[] first = new byte[3840];
                int n = st.Read(first, 0, first.Length);
                if (n <= 0) { client.Close(); continue; }
                st.ReadTimeout = Timeout.Infinite;
                _audioClient = client;
                Write(Loc.T("PICO-Ton: 48000 Hz, Stereo", "PICO audio: 48000 Hz, stereo"));
                _ = Task.Run(() => AudioLoop(st, first, n));
                return;
            }
            catch when (!Cts.IsCancellationRequested) { }
        }
        throw new InvalidOperationException(Loc.T("scrcpy-server antwortet nicht.", "scrcpy-server does not answer."));
    }

    /// <summary>Raw 16-bit PCM, 48 kHz stereo (interleaved) → AudioPcm in 20 ms blocks.</summary>
    private void AudioLoop(NetworkStream st, byte[] first, int firstCount)
    {
        var buf = new byte[3840 * 2];
        Buffer.BlockCopy(first, 0, buf, 0, firstCount);
        int have = firstCount;
        var samples = new short[buf.Length / 2];
        try
        {
            while (!Cts.IsCancellationRequested)
            {
                int frames = have / 4 * 4;
                if (frames > 0)
                {
                    Buffer.BlockCopy(buf, 0, samples, 0, frames);
                    OnPcm(samples, frames / 2, 48000, 2);
                    Buffer.BlockCopy(buf, frames, buf, 0, have - frames);
                    have -= frames;
                }
                int n = st.Read(buf, have, buf.Length - have);
                if (n <= 0) break;
                have += n;
            }
        }
        catch { }
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
        try { _audioClient?.Close(); } catch { }
        try { _audioProc?.Kill(); } catch { }
        if (_scid.Length > 0) await ShellAsync($"pkill -f scid={_scid}", 10);
        if (_audioPort > 0)
            await Adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "forward", "--remove", $"tcp:{_audioPort}");
        if (_localPort > 0)
            await Adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(10), "forward", "--remove", $"tcp:{_localPort}");
        await ShellAsync($"am force-stop {MicRelay.Package}", 10);
    }

    public override void Dispose()
    {
        base.Dispose();
        try { _client?.Close(); } catch { }
        try { _audioClient?.Close(); } catch { }
        try { _audioProc?.Kill(); } catch { }
    }
}
