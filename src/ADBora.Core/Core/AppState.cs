namespace AdbTool.Core;

/// <summary>
/// Shared application state: settings, ADB location, connected devices,
/// selected device and a global "one ADB operation at a time" lock.
/// All members are used from the UI thread.
/// </summary>
internal sealed class AppState
{
    public AppSettings Settings { get; }

    public string? AdbPath { get; private set; }
    public string AdbVersionText { get; private set; } = "";
    public IReadOnlyList<AdbDevice> Devices { get; private set; } = Array.Empty<AdbDevice>();
    public AdbDevice? SelectedDevice { get; private set; }
    public string? BusyOperation { get; private set; }
    public bool IsBusy => BusyOperation is not null;

    public event Action? AdbChanged;
    public event Action? DevicesChanged;
    public event Action? SelectedDeviceChanged;
    public event Action? BusyChanged;

    public AppState(AppSettings settings)
    {
        Settings = settings;
        Privacy.HideSerial = settings.HideSerial;
        AdbPath = AdbLocator.Discover(settings.AdbPath);
    }

    public bool HasAdb => AdbPath is not null && File.Exists(AdbPath);

    public AdbClient? Adb => HasAdb ? new AdbClient(AdbPath!) : null;

    public AdbClient? DeviceAdb =>
        HasAdb && SelectedDevice is not null ? new AdbClient(AdbPath!, SelectedDevice.Serial) : null;

    public void SetAdbPath(string? path)
    {
        AdbPath = string.IsNullOrWhiteSpace(path) ? null : path;
        Settings.AdbPath = AdbPath ?? "";
        Settings.Save();
        AdbVersionText = "";
        AdbChanged?.Invoke();
    }

    public void RediscoverAdb()
    {
        string? found = AdbLocator.Discover(Settings.AdbPath);
        SetAdbPath(found);
    }

    public async Task RefreshVersionAsync()
    {
        var adb = Adb;
        if (adb is null)
        {
            AdbVersionText = "";
            return;
        }
        try { AdbVersionText = await adb.VersionAsync(CancellationToken.None); }
        catch (Exception ex) { AdbVersionText = Loc.T("Fehler: ", "Error: ") + ex.Message; }
    }

    private bool _refreshing;

    /// <summary>Reads "adb devices -l". Returns false if it could not be executed.</summary>
    public async Task<bool> RefreshDevicesAsync()
    {
        var adb = Adb;
        if (adb is null || _refreshing)
            return false;

        _refreshing = true;
        try
        {
            List<AdbDevice> devices = await adb.DevicesAsync(CancellationToken.None);
            ApplyDevices(devices);
            return true;
        }
        catch
        {
            ApplyDevices(new List<AdbDevice>());
            return false;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private long _selectedMissingSince;
    private const long MissingGraceMs = 10000;

    private void ApplyDevices(List<AdbDevice> devices)
    {
        // The selected device can vanish for a moment (USB re-enumeration, e.g. a Quest switching
        // USB networking on or off): keep it selected – listed as offline – for a few seconds
        // instead of jumping to another device (which ends a running measurement).
        AdbDevice? selected = SelectedDevice;
        if (selected is not null && devices.All(d => d.Serial != selected.Serial))
        {
            long now = Environment.TickCount64;
            if (_selectedMissingSince == 0) _selectedMissingSince = now;
            if (now - _selectedMissingSince < MissingGraceMs)
                devices = devices.Append(selected with { State = "offline" }).ToList();
        }
        else
            _selectedMissingSince = 0;

        bool changed = devices.Count != Devices.Count || devices.Where((d, i) => d != Devices[i]).Any();
        Devices = devices;
        if (changed)
            DevicesChanged?.Invoke();

        // Keep the current selection if possible, otherwise prefer the last
        // used device, then the first ready one.
        AdbDevice? next =
            devices.FirstOrDefault(d => d.Serial == SelectedDevice?.Serial)
            ?? devices.FirstOrDefault(d => d.Serial == Settings.LastDeviceSerial && d.IsReady)
            ?? devices.FirstOrDefault(d => d.IsReady)
            ?? devices.FirstOrDefault();

        // An automatic choice is not remembered – the last device picked by the user stays preferred
        SelectDevice(next, remember: false);
    }

    public void SelectDevice(AdbDevice? device, bool remember = true)
    {
        if (device == SelectedDevice)
            return;

        SelectedDevice = device;
        if (device is not null && remember && Settings.LastDeviceSerial != device.Serial)
        {
            Settings.LastDeviceSerial = device.Serial;
            Settings.Save();
        }
        SelectedDeviceChanged?.Invoke();
    }

    public bool TryBeginOperation(string name)
    {
        if (IsBusy)
            return false;
        BusyOperation = name;
        BusyChanged?.Invoke();
        return true;
    }

    public void EndOperation()
    {
        BusyOperation = null;
        BusyChanged?.Invoke();
    }
}
