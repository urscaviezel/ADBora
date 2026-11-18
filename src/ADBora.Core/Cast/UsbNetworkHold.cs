using System.Net;
using System.Net.Sockets;
using AdbTool.Core;

namespace AdbTool.Cast;

/// <summary>
/// Meta Quest: switches on USB networking (NCM, "EthernetOverUsb") like Virtual Desktop does –
/// the companion app requests a network with TRANSPORT_USB and holds it as long as ADBora is
/// connected to its socket "adbora_ncm". Closing (or ADBora exiting) releases it again.
/// The headset asks for consent once if USB networking was never allowed.
/// </summary>
internal sealed class UsbNetworkHold : IDisposable
{
    private readonly AdbClient _adb;
    private TcpClient? _client;
    private int _port;

    public UsbNetworkHold(string adbPath, string serial)
    {
        _adb = new AdbClient(adbPath, serial);
        Serial = serial;
    }

    public string Serial { get; }
    public bool Active => _client?.Connected == true;

    private Task<AdbResult> Adb(int seconds, params string[] args) => _adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(seconds), args);

    public async Task StartAsync(Action<string>? log)
    {
        if (Active) return;
        await MicRelay.EnsureInstalledAsync(_adb, log);
        await Adb(10, "shell", $"am force-stop {MicRelay.Package}");
        await Adb(15, "shell", $"am instrument -e mode ncm {MicRelay.Package}/.MicInstrumentation");
        // Switching on NCM changes the USB functions of the headset: adb reconnects briefly and
        // drops existing port forwardings. So the forwarding is set up again on every attempt.
        for (int i = 0; i < 12; i++)
        {
            if (i > 0)
            {
                await Task.Delay(1000);
                try { await Adb(10, "wait-for-device"); } catch { }
            }
            AdbResult fwd = await Adb(10, "forward", "tcp:0", "localabstract:adbora_ncm");
            if (!fwd.Ok || !int.TryParse(fwd.Output.Trim(), out int port)) continue;
            _port = port;
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                // Handshake: the app answers 'K' once it holds the request. (adb accepts the TCP
                // connection on the PC even while nobody listens on the headset yet.)
                client.Client.ReceiveTimeout = 3000;
                var answer = new byte[1];
                if (client.Client.Receive(answer) != 1 || answer[0] != (byte)'K') throw new IOException("no handshake");
                client.Client.ReceiveTimeout = 0;
                _client = client;
                return;
            }
            catch
            {
                client.Dispose();
                try { await Adb(5, "forward", "--remove", $"tcp:{port}"); } catch { }
                _port = 0;
            }
        }
        throw new InvalidOperationException(Loc.T("ADBora Mic antwortet nicht.", "ADBora Mic does not answer."));
    }

    public async Task StopAsync()
    {
        try { _client?.Close(); } catch { }
        _client = null;
        try
        {
            if (_port > 0) await Adb(10, "forward", "--remove", $"tcp:{_port}");
            _port = 0;
        }
        catch { }
    }

    public void Dispose()
    {
        try { _client?.Close(); } catch { }
    }
}
