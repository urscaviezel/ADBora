using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AdbTool.Core;

internal class AdbException : Exception
{
    public AdbException(string message) : base(message) { }
}

internal sealed record AdbDevice(string Serial, string State, string Model, string Product, string Device, string TransportId)
{
    public bool IsReady => State == "device";

    public string DisplayName => string.IsNullOrWhiteSpace(Model) ? Serial : Model;

    public string StateText => State switch
    {
        "device" => Loc.T("Bereit", "Ready"),
        "unauthorized" => Loc.T("RSA-Dialog am Gerät bestätigen", "Accept the RSA prompt on the device"),
        "offline" => "Offline",
        "recovery" => "Recovery",
        "sideload" => "Sideload",
        "bootloader" => "Bootloader",
        _ => State
    };
}

internal readonly record struct AdbResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string Combined => string.IsNullOrWhiteSpace(Error) ? Output : (string.IsNullOrWhiteSpace(Output) ? Error : Output + Environment.NewLine + Error);
}

/// <summary>Locates adb.exe on this PC.</summary>
internal static class AdbLocator
{
    public const string PlatformToolsUrl = "https://developer.android.com/tools/releases/platform-tools";

    public static string? Discover(string configured)
    {
        var candidates = new List<string?>
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe"),
            Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe"),
            Path.Combine(AppContext.BaseDirectory, "adb.exe"),
        };

        foreach (string variable in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
        {
            string? root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root))
                candidates.Add(Path.Combine(root, "platform-tools", "adb.exe"));
        }

        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (pathVar is not null)
        {
            foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try { candidates.Add(Path.Combine(dir.Trim('"'), "adb.exe")); } catch { }
            }
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"));
        candidates.Add(@"C:\Android\platform-tools\adb.exe");
        candidates.Add(@"C:\platform-tools\adb.exe");

        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            try
            {
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch { }
        }

        return null;
    }
}

/// <summary>
/// Thin wrapper around adb.exe. Every call starts a separate process; the
/// optional serial is passed with -s so that several devices can be connected.
/// </summary>
internal sealed class AdbClient
{
    private static readonly Regex SafeShellWord = new(@"^[A-Za-z0-9@%+=:,./_-]+$", RegexOptions.Compiled);

    public string Executable { get; }
    public string Serial { get; }

    public AdbClient(string executable, string serial = "")
    {
        Executable = executable;
        Serial = serial ?? "";
    }

    public AdbClient ForDevice(string serial) => new(Executable, serial);

    /// <summary>POSIX shell quoting (equivalent to Python's shlex.quote).</summary>
    public static string ShellQuote(string value)
    {
        if (value.Length > 0 && SafeShellWord.IsMatch(value))
            return value;
        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }

    public static string ShellJoin(IEnumerable<string> args) => string.Join(" ", args.Select(ShellQuote));

    public ProcessStartInfo CreateStartInfo(IEnumerable<string> args, bool withSerial = true, bool redirectInput = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Executable) ?? AppContext.BaseDirectory
        };

        if (withSerial && Serial.Length > 0)
        {
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(Serial);
        }

        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        return psi;
    }

    public Process Start(params string[] args)
    {
        return Process.Start(CreateStartInfo(args))
               ?? throw new AdbException(Loc.T("adb.exe konnte nicht gestartet werden.", "adb.exe could not be started."));
    }

    /// <summary>
    /// Runs adb and reports every output line (stdout and stderr) as it
    /// arrives. Returns the exit code; kills the process on cancellation.
    /// </summary>
    public async Task<int> RunStreamingAsync(IEnumerable<string> args, Action<string, bool> onLine, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var p = new Process { StartInfo = CreateStartInfo(args, redirectInput: true), EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) onLine(e.Data, false); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) onLine(e.Data, true); };
        if (!p.Start())
            throw new AdbException(Loc.T("adb.exe konnte nicht gestartet werden.", "adb.exe could not be started."));
        try { p.StandardInput.Close(); } catch { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(p);
            throw;
        }
        p.WaitForExit(); // flush output events
        return p.ExitCode;
    }

    /// <summary>Runs adb and captures stdout/stderr. Does not throw on a non-zero exit code.</summary>
    public async Task<AdbResult> CaptureAsync(CancellationToken token, TimeSpan? timeout, params string[] args)
    {
        token.ThrowIfCancellationRequested();

        using Process p = Start(args);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (timeout is not null)
            timeoutCts.CancelAfter(timeout.Value);

        try
        {
            Task<string> stdout = p.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> stderr = p.StandardError.ReadToEndAsync(timeoutCts.Token);
            await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new AdbResult(p.ExitCode, (await stdout.ConfigureAwait(false)).TrimEnd(), (await stderr.ConfigureAwait(false)).TrimEnd());
        }
        catch (OperationCanceledException)
        {
            KillQuietly(p);
            if (token.IsCancellationRequested)
                throw;
            throw new AdbException(Loc.T(
                $"ADB-Zeitlimit überschritten ({timeout?.TotalSeconds:0} s)",
                $"ADB timed out ({timeout?.TotalSeconds:0} s)"));
        }
    }

    public Task<AdbResult> CaptureAsync(CancellationToken token, params string[] args) =>
        CaptureAsync(token, TimeSpan.FromSeconds(60), args);

    /// <summary>Runs adb and returns trimmed stdout; throws AdbException on failure.</summary>
    public async Task<string> RunAsync(CancellationToken token, TimeSpan timeout, params string[] args)
    {
        AdbResult result = await CaptureAsync(token, timeout, args).ConfigureAwait(false);
        if (!result.Ok)
        {
            string message = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            throw new AdbException(string.IsNullOrWhiteSpace(message) ? Loc.T("ADB fehlgeschlagen", "ADB failed") : message.Trim());
        }
        return result.Output.Trim();
    }

    public Task<string> RunAsync(CancellationToken token, params string[] args) =>
        RunAsync(token, TimeSpan.FromSeconds(60), args);

    /// <summary>adb shell with correctly quoted arguments.</summary>
    public Task<string> ShellAsync(CancellationToken token, params string[] args) =>
        RunAsync(token, TimeSpan.FromSeconds(60), "shell", ShellJoin(args));

    public async Task<string> VersionAsync(CancellationToken token)
    {
        var raw = new AdbClient(Executable);
        return await raw.RunAsync(token, TimeSpan.FromSeconds(30), "version").ConfigureAwait(false);
    }

    public async Task<List<AdbDevice>> DevicesAsync(CancellationToken token)
    {
        var raw = new AdbClient(Executable);
        string output = await raw.RunAsync(token, TimeSpan.FromSeconds(30), "devices", "-l").ConfigureAwait(false);
        var devices = new List<AdbDevice>();

        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("List ") || trimmed.StartsWith("*"))
                continue;

            string[] parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            string Field(string key) =>
                parts.Skip(2).FirstOrDefault(p => p.StartsWith(key + ":"))?[(key.Length + 1)..].Replace('_', ' ') ?? "";

            devices.Add(new AdbDevice(parts[0], parts[1], Field("model"), Field("product"), Field("device"), Field("transport_id")));
        }

        return devices;
    }

    public async Task KillServerAsync()
    {
        if (!File.Exists(Executable))
            return;
        try
        {
            using Process p = Process.Start(new AdbClient(Executable).CreateStartInfo(new[] { "kill-server" }))!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    /// <summary>Streams a remote file binary-safe through "adb exec-out cat".</summary>
    public async Task StreamFileAsync(string source, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using Process p = Start("exec-out", ShellJoin(new[] { "cat", source }));
        Task<string> stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromHours(1));
                await p.StandardOutput.BaseStream.CopyToAsync(output, 1024 * 1024, timeoutCts.Token).ConfigureAwait(false);
                await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            KillQuietly(p);
            if (token.IsCancellationRequested)
                throw;
            throw new AdbException(Loc.T("ADB-Zeitlimit überschritten (3600 s)", "ADB timed out (3600 s)"));
        }

        if (p.ExitCode != 0)
        {
            string err = (await stderr.ConfigureAwait(false)).Trim();
            throw new AdbException(err.Length > 0 ? err : Loc.T("ADB fehlgeschlagen", "ADB failed"));
        }
    }

    public static void KillQuietly(Process? p)
    {
        try
        {
            if (p is { HasExited: false })
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
            }
        }
        catch { }
    }
}
