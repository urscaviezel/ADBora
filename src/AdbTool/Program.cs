using System.Text;
using System.Reflection;
using AdbTool.Backup;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool;

internal static class AppInfo
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    /// <summary>Version for display, e.g. "1.3.0 (Test)" for test builds.</summary>
    public static string DisplayVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } info &&
        info.Contains("-test", StringComparison.OrdinalIgnoreCase)
            ? Version + " (Test)"
            : Version;
}

internal static class Program
{
    private static bool _smokeTest;

    [STAThread]
    private static int Main(string[] args)
    {
        // Diagnostic mode: ADBora.exe --check-apk <file.apk>
        // Writes name/version/icon information next to the APK (<file>.check.txt).
        int check = Array.IndexOf(args, "--check-apk");
        if (check >= 0 && check + 1 < args.Length)
            return CheckApk(args[check + 1]);

        // Diagnostic mode: ADBora.exe --cast-test <adb.exe> <serial> <quest|pfd> <seconds> <out.png>
        // Runs headset casting without a window and saves the last picture (+ <out>.log).
        int mkvTest = Array.IndexOf(args, "--mkv-test");
        if (mkvTest >= 0 && mkvTest + 2 < args.Length)
            return MkvTest(args[mkvTest + 1], args[mkvTest + 2]);
        int castTest = Array.IndexOf(args, "--cast-test");
        if (castTest >= 0 && castTest + 5 < args.Length)
            return CastTest(args[castTest + 1], args[castTest + 2], args[castTest + 3], int.Parse(args[castTest + 4]), args[castTest + 5]);

        _smokeTest = args.Contains("--smoke-test");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        // Test switch: behave as if an older version were installed (update check).
        int pretend = Array.IndexOf(args, "--pretend-version");
        if (pretend >= 0 && pretend + 1 < args.Length)
            Updater.PretendVersion = args[pretend + 1];

        // Only one ADBora at a time – two instances would fight over the same ADB device (build smoke test excepted)
        Mutex? instance = null;
        if (!_smokeTest)
        {
            instance = new Mutex(true, InstanceMutexName, out bool created);
            if (!created)
            {
                // Running instance with a window: bring it to the front and quit
                if (ActivateRunningInstance())
                {
                    instance.Dispose();
                    return 0;
                }
                // No window (yet / any more), e.g. the old instance is just closing after an update: wait briefly
                bool acquired;
                try { acquired = instance.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    ActivateRunningInstance();
                    instance.Dispose();
                    return 0;
                }
            }
        }

        ApplicationConfiguration.Initialize();
        Theme.InitScale();
        try
        {
            Application.Run(new MainForm(_smokeTest));
        }
        catch (Exception ex) when (_smokeTest)
        {
            ReportCrash(ex);
        }
        finally
        {
            if (instance is not null)
            {
                try { instance.ReleaseMutex(); } catch { }
                instance.Dispose();
            }
        }
        return 0;
    }

    private const string InstanceMutexName = @"Local\ADBora.SingleInstance";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    /// <summary>Restores and focuses the window of an already running ADBora. False if none was found.</summary>
    private static bool ActivateRunningInstance()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var other in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
        {
            using (other)
            {
                if (other.Id == current.Id) continue;
                IntPtr handle = other.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;
                if (IsIconic(handle)) ShowWindow(handle, 9); // SW_RESTORE
                SetForegroundWindow(handle);
                return true;
            }
        }
        return false;
    }

    private static int MkvTest(string h264, string mkvPath)
    {
        string scrcpy = Core.ScrcpyTool.Find("") ?? throw new FileNotFoundException("scrcpy");
        Cast.H264Decoder.TryLoad(Path.GetDirectoryName(scrcpy)!, out _);
        using var dec = new Cast.H264Decoder();
        using var mkv = new Cast.MkvWriter(mkvPath, Cast.MkvAudio.Pcm, 48000, 2);
        var tone = new short[1600 * 2];
        int frame = 0;
        dec.FrameDecoded += b => b.Dispose();
        dec.AccessUnit += (b, n) =>
        {
            mkv.WriteVideo(b.AsSpan(0, n), dec.Width, dec.Height);
            for (int i = 0; i < 1600; i++) tone[i * 2] = tone[i * 2 + 1] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * (frame * 1600 + i) / 48000.0));
            mkv.WriteAudio(tone, tone.Length, 48000, 2);
            frame++;
            Thread.Sleep(33);
        };
        dec.Feed(File.ReadAllBytes(h264), true);
        File.WriteAllText(mkvPath + ".log", $"frames={frame} size={dec.Width}x{dec.Height}");
        return 0;
    }

    private static int CastTest(string adb, string serial, string kind, int seconds, string png)
    {
        var log = new StringBuilder();
        void L(string t) { lock (log) File.AppendAllText(png + ".log", $"{DateTime.Now:HH:mm:ss.fff} {t}\r\n"); }
        try { File.Delete(png + ".log"); } catch { }
        Bitmap? last = null; int frames = 0; long bytes = 0;
        try
        {
            string scrcpy = Core.ScrcpyTool.Find("") ?? throw new FileNotFoundException("scrcpy");
            if (!Cast.H264Decoder.TryLoad(Path.GetDirectoryName(scrcpy)!, out string err)) throw new InvalidOperationException(err);
            L("ffmpeg loaded from " + Path.GetDirectoryName(scrcpy));
            using var decoder = new Cast.H264Decoder();
            L("decoder created");
            decoder.FrameDecoded += b => { frames++; Bitmap? old = Interlocked.Exchange(ref last, b); old?.Dispose(); };
            var source = Cast.CastSource.Create(kind == "pfd" ? Cast.CastKind.PlayForDream : Cast.CastKind.Quest, adb, serial);
            source.Log += L;
            source.WantAudio = true;
            int aac = 0, pcm = 0;
            using var player = (Cast.AudioPlayer?)null;
            Cast.AacDecoder? adec = null;
            using var mkv = new Cast.MkvWriter(png + ".mkv", kind == "pfd" ? Cast.MkvAudio.Aac : Cast.MkvAudio.Pcm, kind == "pfd" ? 0 : 48000, kind == "pfd" ? 0 : 2);
            decoder.AccessUnit += (b, n) => mkv.WriteVideo(b.AsSpan(0, n), decoder.Width, decoder.Height);
            source.AudioAac += (a, r, c) => { aac++; mkv.WriteAac(a); adec ??= new Cast.AacDecoder(); adec.Feed(a, c); };
            source.AudioPcm += (a, n, r, c) => { pcm++; mkv.WriteAudio(a, n, r, c); };
            source.Ended += r => L("ended: " + (r ?? "-"));
            source.Video += (b, o, n) => { if (bytes == 0) L($"first video {n} bytes"); bytes += n; decoder.Feed(b.AsSpan(o, n), kind == "pfd"); if (frames == 1 && bytes == n) L("first frame decoded"); };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            source.StartAsync().GetAwaiter().GetResult();
            L($"started after {sw.ElapsedMilliseconds} ms");
            long micSamples = 0; double micPeak = 0;
            var mic = new Cast.MicRelay(adb, serial);
            mic.Log += L;
            mic.Samples += (smp, n, r) => { micSamples += n; for (int i = 0; i < n; i++) micPeak = Math.Max(micPeak, Math.Abs((int)smp[i])); };
            try { mic.StartAsync().GetAwaiter().GetResult(); L("mic started"); } catch (Exception ex) { L("mic: " + ex.Message); }
            Thread.Sleep(seconds * 1000);
            L($"audio aac={aac} pcm={pcm} micSamples={micSamples} micPeak={micPeak}");
            mic.StopAsync().GetAwaiter().GetResult();
            adec?.Dispose();
            L($"frames={frames} bytes={bytes} size={decoder.Width}x{decoder.Height} fps={frames / Math.Max(1.0, sw.Elapsed.TotalSeconds):0.0}");
            source.StopAsync().GetAwaiter().GetResult();
            source.Dispose();
            Bitmap? b = Interlocked.Exchange(ref last, null);
            b?.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            b?.Dispose();
            L("OK");
        }
        catch (Exception ex) { L("ERROR " + ex); }
        return 0;
    }

    private static int CheckApk(string path)
    {
        string output = path + ".check.txt";
        try
        {
            ApkMetadata meta = ApkMetadataReader.Read(path);
            var app = new AppEntry(meta.Package) { Name = meta.Label, Version = meta.VersionName, Apks = { "/base.apk" } };
            using Bitmap? icon = ImageLoader.Decode(meta.Icon);
            File.WriteAllText(output,
                $"{meta.Label}\n{meta.VersionName}\n{meta.VersionCode}\n{meta.Package}\n{BackupService.ApkFileNames(app)[0]}\n" +
                $"icon={(icon is not null)} {icon?.Width}x{icon?.Height} bytes={meta.Icon.Length}\n");
            if (icon is not null)
                icon.Save(path + ".icon.png", System.Drawing.Imaging.ImageFormat.Png);
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, "ERROR\n" + ex);
            return 1;
        }
    }

    private static void ReportCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.AppendAllText(Path.Combine(AppSettings.DataDirectory, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            if (_smokeTest)
            {
                // Build check (BUILD.ps1): no dialog, fail with exit code 3
                Console.Error.WriteLine(ex);
                Environment.Exit(3);
            }
            MessageBox.Show(ex?.Message ?? "Unknown error", "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
