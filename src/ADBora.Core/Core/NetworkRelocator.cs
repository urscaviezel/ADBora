using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AdbTool.Core;

/// <summary>
/// Finds a network-connected device again after its IP changed – e.g. a Play For Dream
/// that switches from Wi-Fi to a USB Ethernet adapter (new IP, Wi-Fi off). ADBora remembers
/// the hardware serial (ro.serialno) of every "ip:port" device; when such an entry goes
/// offline it scans the PC's local subnets for the same port, connects to new hosts and
/// keeps the one with the same serial (the old entry is removed, the selection follows).
/// Runs on the UI thread (await-based), at most every 10 s and for 3 minutes per loss.
/// </summary>
internal sealed class NetworkRelocator
{
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(10);

    private readonly AppState _state;
    private readonly Dictionary<string, string> _hw = new();          // "ip:port" → ro.serialno
    private readonly Dictionary<string, DateTime> _lostSince = new(); // "ip:port" → first seen not ready
    private bool _running;
    private DateTime _lastScan = DateTime.MinValue;

    /// <summary>Short status text while searching (null = nothing to show).</summary>
    public event Action<string?>? Status;

    public NetworkRelocator(AppState state)
    {
        _state = state;
        Loop();
    }

    /// <summary>
    /// Runs every 3 s. Started on the UI thread, so the awaits resume there (like the former UI timer).
    /// </summary>
    private async void Loop()
    {
        while (true)
        {
            await Task.Delay(3000);
            try { await CheckAsync(); } catch { }
        }
    }

    private static bool IsNetwork(string serial) => Regex.IsMatch(serial, @"^\d+\.\d+\.\d+\.\d+:\d+$");

    private async Task CheckAsync()
    {
        if (_running || !_state.HasAdb || _state.IsBusy) return;
        _running = true;
        try
        {
            AdbClient adb = _state.Adb!;
            var ready = _state.Devices.Where(d => d.IsReady).ToList();

            // learn hardware serials of network devices
            foreach (AdbDevice d in ready.Where(d => IsNetwork(d.Serial) && !_hw.ContainsKey(d.Serial)))
            {
                string hw = await SerialNoAsync(adb, d.Serial);
                if (hw.Length > 0) _hw[d.Serial] = hw;
            }

            // which known network devices are lost?
            DateTime now = DateTime.Now;
            foreach (string ep in _hw.Keys.ToList())
            {
                if (ready.Any(d => d.Serial == ep)) { _lostSince.Remove(ep); continue; }
                if (!_lostSince.ContainsKey(ep)) _lostSince[ep] = now;
                if (now - _lostSince[ep] > GiveUpAfter) { _hw.Remove(ep); _lostSince.Remove(ep); }
            }
            if (_lostSince.Count == 0) return;

            // already back by another route (USB, other IP)?
            var readyHw = new Dictionary<string, string>();
            foreach (AdbDevice d in ready)
                readyHw[d.Serial] = IsNetwork(d.Serial) ? _hw.GetValueOrDefault(d.Serial, "") : d.Serial;
            foreach (string ep in _lostSince.Keys.ToList())
            {
                string? other = readyHw.FirstOrDefault(x => x.Value.Length > 0 && x.Value == _hw[ep]).Key;
                if (other is not null) await ReplaceAsync(adb, ep, other);
            }
            if (_lostSince.Count == 0 || now - _lastScan < ScanInterval) return;
            _lastScan = now;

            Status?.Invoke(Loc.T("Suche Headset im Netzwerk …", "Looking for the headset on the network …"));
            var ports = _lostSince.Keys.Select(ep => int.Parse(ep[(ep.LastIndexOf(':') + 1)..])).Distinct().ToList();
            var skip = new HashSet<string>(_state.Devices.Select(d => d.Serial));
            foreach (int port in ports)
            {
                foreach (IPAddress host in await Task.Run(() => OpenHosts(port)))
                {
                    if (_lostSince.Count == 0) break;
                    string ep = $"{host}:{port}";
                    if (skip.Contains(ep)) continue;
                    AdbResult c = await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(8), "connect", ep);
                    string hw = c.Combined.Contains("connected to", StringComparison.OrdinalIgnoreCase)
                        ? await SerialNoAsync(adb, ep) : "";
                    string? lost = _lostSince.Keys.FirstOrDefault(l => hw.Length > 0 && _hw[l] == hw);
                    if (lost is null)
                    {
                        await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(5), "disconnect", ep);
                        continue;
                    }
                    _hw[ep] = hw;
                    await _state.RefreshDevicesAsync();
                    await ReplaceAsync(adb, lost, ep);
                }
            }
        }
        catch { }
        finally
        {
            Status?.Invoke(null);
            _running = false;
        }
    }

    /// <summary>Removes the lost entry and moves the selection to the device's new entry.</summary>
    private async Task ReplaceAsync(AdbClient adb, string lost, string found)
    {
        bool wasSelected = _state.SelectedDevice?.Serial == lost;
        _hw.Remove(lost);
        _lostSince.Remove(lost);
        if (_state.Devices.Any(d => d.Serial == lost))
            await adb.CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(5), "disconnect", lost);
        await _state.RefreshDevicesAsync();
        if (wasSelected && _state.Devices.FirstOrDefault(d => d.Serial == found) is { } dev)
            _state.SelectDevice(dev);
    }

    private static async Task<string> SerialNoAsync(AdbClient adb, string serial)
    {
        try
        {
            AdbResult r = await adb.ForDevice(serial).CaptureAsync(CancellationToken.None, TimeSpan.FromSeconds(5), "shell", "getprop ro.serialno");
            return r.Ok ? r.Output.Trim() : "";
        }
        catch { return ""; }
    }

    /// <summary>Hosts in the PC's local IPv4 subnets (prefix /22 or smaller) with the TCP port open.</summary>
    private static List<IPAddress> OpenHosts(int port)
    {
        var hosts = new HashSet<uint>();
        var own = new HashSet<uint>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (UnicastIPAddressInformation u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.PrefixLength is < 22 or > 30) continue;
                byte[] b = u.Address.GetAddressBytes();
                uint ip = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
                if ((ip >> 16) == 0xA9FE) continue; // 169.254.x.x (USB-NCM without DHCP)
                own.Add(ip);
                uint mask = uint.MaxValue << (32 - u.PrefixLength), net = ip & mask;
                for (uint h = net + 1; h < (net | ~mask); h++) hosts.Add(h);
            }
        }
        hosts.ExceptWith(own);
        var probes = hosts.Select(h =>
        {
            var addr = new IPAddress(new[] { (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h });
            var client = new TcpClient();
            return (addr, client, task: client.ConnectAsync(addr, port));
        }).ToList();
        try { Task.WaitAll(probes.Select(p => p.task.ContinueWith(_ => { })).ToArray(), 2000); } catch { }
        var open = probes.Where(p => p.task.IsCompletedSuccessfully && p.client.Connected).Select(p => p.addr).ToList();
        foreach (var p in probes) p.client.Dispose();
        return open;
    }
}
