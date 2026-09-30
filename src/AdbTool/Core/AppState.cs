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

    private void ApplyDevices(List<AdbDevice> devices)
    {
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

        SelectDevice(next);
    }

    public void SelectDevice(AdbDevice? device)
    {
        if (device == SelectedDevice)
            return;

        SelectedDevice = device;
        if (device is not null)
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
