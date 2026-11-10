using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using AdbTool.Core;

namespace AdbTool.Cast;

/// <summary>
/// Headset microphone via the companion app "ADBora Mic" (companion/mic, embedded as
/// resource): installed if missing or outdated, started as an instrumentation and read
/// through "adb forward tcp:N localabstract:adbora_mic". Delivers 16-bit mono PCM.
/// </summary>
internal sealed class MicRelay : IDisposable
{
    public const string Package = "com.adbora.mic";
    private const int VersionCode = 4; // keep in sync with build/BUILD-COMPANION.ps1
    private readonly AdbClient _adb;
    private readonly CancellationTokenSource _cts = new();
    private TcpClient? _client;
    private int _port;

    /// <summary>Samples (mono), count, sample rate. Reading thread.</summary>
    public event Action<short[], int, int>? Samples;
    public event Action<string>? Log;

    public MicRelay(string adbPath, string serial) => _adb = new AdbClient(adbPath, serial);

    private Task<AdbResult> Adb(int seconds, params string[] args) => _adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(seconds), args);

    public async Task StartAsync(int rate = 48000)
    {
        await EnsureInstalledAsync();
        await Adb(10, "shell", $"pm grant {Package} android.permission.RECORD_AUDIO");
        await Adb(10, "shell", $"am force-stop {Package}");
        AdbResult r = await Adb(15, "shell", $"am instrument -e rate {rate} {Package}/.MicInstrumentation");
        if (r.Combined.Contains("Error", StringComparison.OrdinalIgnoreCase) && !r.Combined.Contains("log_ctrl"))
            throw new InvalidOperationException("am instrument: " + r.Combined);
        AdbResult fwd = await Adb(10, "forward", "tcp:0", "localabstract:adbora_mic");
        if (!fwd.Ok || !int.TryParse(fwd.Output.Trim(), out _port))
            throw new InvalidOperationException("adb forward: " + fwd.Combined);

        for (int i = 0; i < 20 && !_cts.IsCancellationRequested; i++)
        {
            try
            {
                var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(IPAddress.Loopback, _port, _cts.Token);
                NetworkStream st = client.GetStream();
                st.ReadTimeout = 3000;
                byte[] h = new byte[10];
                int got = 0;
                while (got < h.Length) { int n = st.Read(h, got, h.Length - got); if (n <= 0) throw new EndOfStreamException(); got += n; }
                if (h[0] != 'A' || h[1] != 'D' || h[2] != 'B' || h[3] != 'M') throw new InvalidDataException("header");
                int channels = h[5], sampleRate = BitConverter.ToInt32(h, 6);
                st.ReadTimeout = Timeout.Infinite;
                _client = client;
                Log?.Invoke(Loc.T($"Mikrofon: {sampleRate} Hz", $"Microphone: {sampleRate} Hz"));
                _ = Task.Run(() => ReadLoop(st, sampleRate, channels));
                return;
            }
            catch when (!_cts.IsCancellationRequested) { await Task.Delay(300); }
        }
        throw new InvalidOperationException(Loc.T("ADBora Mic antwortet nicht.", "ADBora Mic does not answer."));
    }

    private void ReadLoop(NetworkStream st, int rate, int channels)
    {
        var buf = new byte[rate / 50 * 2 * channels * 2];
        var samples = new short[buf.Length / 2];
        int carry = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = st.Read(buf, carry, buf.Length - carry);
                if (n <= 0) break;
                n += carry;
                int even = n & ~1;
                Buffer.BlockCopy(buf, 0, samples, 0, even);
                carry = n - even;
                if (carry > 0) buf[0] = buf[even];
                if (channels == 1) Samples?.Invoke(samples, even / 2, rate);
            }
        }
        catch { }
    }

    private Task EnsureInstalledAsync() => EnsureInstalledAsync(_adb, s => Log?.Invoke(s));

    /// <summary>Installs the embedded companion APK if the app is missing or older.</summary>
    public static async Task EnsureInstalledAsync(AdbClient adb, Action<string>? log)
    {
        Task<AdbResult> Adb(int seconds, params string[] args) => adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(seconds), args);
        AdbResult info = await Adb(15, "shell", $"dumpsys package {Package} | grep versionCode");
        Match m = Regex.Match(info.Output, @"versionCode=(\d+)");
        if (m.Success && int.Parse(m.Groups[1].Value) >= VersionCode) return;

        string apk = Path.Combine(Path.GetTempPath(), "ADBoraMic.apk");
        await using (Stream res = Assembly.GetExecutingAssembly().GetManifestResourceStream("ADBoraMic.apk")
                                  ?? throw new InvalidOperationException("ADBoraMic.apk resource missing"))
        await using (FileStream f = File.Create(apk))
            await res.CopyToAsync(f);
        log?.Invoke(Loc.T("Installiere ADBora Mic auf dem Headset …", "Installing ADBora Mic on the headset …"));
        try
        {
            AdbResult r = await Adb(60, "install", "-r", "-g", apk);
            if (r.Combined.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE") || r.Combined.Contains("signatures do not match"))
            {
                // Signed with a different key (e.g. a self-built copy): reinstall
                await Adb(30, "uninstall", Package);
                r = await Adb(60, "install", "-g", apk);
            }
            if (!r.Combined.Contains("Success"))
                throw new InvalidOperationException(Loc.T("ADBora Mic konnte nicht installiert werden: ", "ADBora Mic could not be installed: ") + r.Combined);
        }
        finally { try { File.Delete(apk); } catch { } }
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        try { _client?.Close(); } catch { }
        try
        {
            if (_port > 0) await Adb(10, "forward", "--remove", $"tcp:{_port}");
            await Adb(10, "shell", $"am force-stop {Package}");
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _client?.Close(); } catch { }
    }
}
