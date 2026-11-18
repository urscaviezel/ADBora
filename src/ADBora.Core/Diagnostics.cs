using System.Text;
using AdbTool.Backup;
using AdbTool.Core;
using SkiaSharp;

namespace AdbTool;

/// <summary>
/// Diagnostic modes without a window (also used by the build check):
///   --check-apk &lt;file.apk&gt;                                      name/version/icon → &lt;file&gt;.check.txt (+ .icon.png)
///   --cast-test &lt;adb&gt; &lt;serial&gt; &lt;quest|pfd|pico&gt; &lt;s&gt; &lt;out.png&gt;  headset casting, last picture + .log + .mkv
///   --mkv-test &lt;file.h264&gt; &lt;out.mkv&gt;                            H.264 → MKV with a test tone
///   --audio-test                                                 test tone on the PC
///   --ncm-test &lt;adb&gt; &lt;serial&gt; &lt;s&gt; &lt;log&gt;                        holds Quest USB networking
/// </summary>
internal static class Diagnostics
{
    /// <summary>Runs a diagnostic mode if one is requested; null = normal start.</summary>
    public static int? Run(string[] args)
    {
        int check = Array.IndexOf(args, "--check-apk");
        if (check >= 0 && check + 1 < args.Length)
            return CheckApk(args[check + 1]);

        int ncmTest = Array.IndexOf(args, "--ncm-test");
        if (ncmTest >= 0 && ncmTest + 4 < args.Length)
        {
            string log = args[ncmTest + 4];
            void W(string t) => File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss} {t}\n");
            var adbc = new AdbClient(args[ncmTest + 1], args[ncmTest + 2]);
            using var hold = new Cast.UsbNetworkHold(args[ncmTest + 1], args[ncmTest + 2]);
            try
            {
                hold.StartAsync(W).GetAwaiter().GetResult();
                W("holding");
                for (int i = 0; i < int.Parse(args[ncmTest + 3]); i += 10)
                {
                    Thread.Sleep(10_000);
                    W("usb0: " + adbc.CaptureAsync(CancellationToken.None, "shell", "ip -o -4 addr show usb0").GetAwaiter().GetResult().Output.Trim());
                }
                hold.StopAsync().GetAwaiter().GetResult();
                Thread.Sleep(3000);
                W("after stop usb0: '" + adbc.CaptureAsync(CancellationToken.None, "shell", "ip -o -4 addr show usb0; pidof com.adbora.mic").GetAwaiter().GetResult().Output.Trim() + "'");
            }
            catch (Exception ex) { W("ERROR " + ex.Message); }
            return 0;
        }

        int backupTest = Array.IndexOf(args, "--backup-test");
        if (backupTest >= 0 && backupTest + 4 < args.Length)
            return BackupTest(args[backupTest + 1], args[backupTest + 2], args[backupTest + 3], args[backupTest + 4]);

        if (args.Contains("--audio-test"))
            return AudioTest();

        int mkvTest = Array.IndexOf(args, "--mkv-test");
        if (mkvTest >= 0 && mkvTest + 2 < args.Length)
            return MkvTest(args[mkvTest + 1], args[mkvTest + 2]);
        int castTest = Array.IndexOf(args, "--cast-test");
        if (castTest >= 0 && castTest + 5 < args.Length)
            return CastTest(args[castTest + 1], args[castTest + 2], args[castTest + 3], int.Parse(args[castTest + 4]), args[castTest + 5]);
        return null;
    }

    /// <summary>Scans the user apps and backs up APK + OBB + Data of one package (like the Backup tab).</summary>
    public static int BackupTest(string adb, string serial, string package, string destination)
    {
        void L(string t) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {t}");
        try
        {
            var service = new BackupService(new AdbClient(adb, serial), L, (c, t) => { }, CancellationToken.None);
            List<AppEntry> apps = service.ScanAsync(resolveNames: true).GetAwaiter().GetResult();
            AppEntry app = apps.FirstOrDefault(a => a.Package == package) ?? throw new InvalidOperationException("package not found: " + package);
            L($"{app.Name} {app.Version} apks={app.Apks.Count} obb={app.Obb} data={app.Data} debuggable={app.Debuggable}");
            var sel = new BackupSelection(app, true, app.Obb == FolderState.Present, app.Data == FolderState.Present || app.Debuggable);
            BackupReport report = service.BackupAsync(new List<BackupSelection> { sel }, destination, "TEST").GetAwaiter().GetResult();
            L($"dir={report.Directory} completed={report.Apps.Sum(a => a.Completed.Count)} errors={report.Apps.Sum(a => a.Errors.Count)}");
            foreach (var a in report.Apps) foreach (string e in a.Errors) L("ERROR " + e);
            return report.Apps.Sum(a => a.Errors.Count) == 0 ? 0 : 2;
        }
        catch (Exception ex) { L("ERROR " + ex); return 1; }
    }

    /// <summary>Plays a 1.5 s test tone (440/660 Hz) on the default output of the PC.</summary>
    public static int AudioTest()
    {
        try
        {
            using var player = new Cast.AudioPlayer(48000, 2);
            var block = new short[480 * 2]; // 10 ms
            for (int b = 0; b < 150; b++)
            {
                double f = b < 75 ? 440 : 660;
                for (int i = 0; i < 480; i++)
                    block[i * 2] = block[i * 2 + 1] = (short)(6000 * Math.Sin(2 * Math.PI * f * (b * 480 + i) / 48000.0));
                player.Write(block, block.Length);
                Thread.Sleep(10);
            }
            Thread.Sleep(400);
            Console.WriteLine("audio OK");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("audio ERROR " + ex.Message); return 1; }
    }

    public static int MkvTest(string h264, string mkvPath)
    {
        string scrcpy = Core.ScrcpyTool.Find("") ?? "";
        Cast.H264Decoder.TryLoad(Path.GetDirectoryName(scrcpy) ?? "", out _);
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

    public static int CastTest(string adb, string serial, string kind, int seconds, string png)
    {
        var log = new StringBuilder();
        void L(string t) { lock (log) File.AppendAllText(png + ".log", $"{DateTime.Now:HH:mm:ss.fff} {t}\n"); }
        try { File.Delete(png + ".log"); } catch { }
        SKBitmap? last = null; int frames = 0; long bytes = 0;
        try
        {
            string scrcpy = Core.ScrcpyTool.Find("") ?? "";
            if (!Cast.H264Decoder.TryLoad(Path.GetDirectoryName(scrcpy) ?? "", out string err)) throw new InvalidOperationException(err);
            L("ffmpeg loaded (scrcpy: " + (scrcpy.Length > 0 ? scrcpy : "-") + ")");
            using var decoder = new Cast.H264Decoder();
            L("decoder created");
            decoder.FrameDecoded += b => { frames++; SKBitmap? old = Interlocked.Exchange(ref last, b); old?.Dispose(); };
            var source = Cast.CastSource.Create(kind == "pfd" ? Cast.CastKind.PlayForDream : kind == "pico" ? Cast.CastKind.Pico : Cast.CastKind.Quest, adb, serial);
            source.Log += L;
            if (source is Cast.PicoCastSource pico && scrcpy.Length > 0) pico.ScrcpyExe = scrcpy;
            source.WantAudio = true;
            int aac = 0, pcm = 0;
            using var player = (Cast.AudioPlayer?)null;
            Cast.AacDecoder? adec = null;
            using var mkv = new Cast.MkvWriter(png + ".mkv", kind == "pfd" ? Cast.MkvAudio.Aac : Cast.MkvAudio.Pcm, kind == "pfd" ? 0 : 48000, kind == "pfd" ? 0 : 2);
            decoder.AccessUnit += (b, n) => mkv.WriteVideo(b.AsSpan(0, n), decoder.Width, decoder.Height);
            source.AudioAac += (a, r, c) => { aac++; mkv.WriteAac(a); adec ??= new Cast.AacDecoder(); adec.Feed(a, c); };
            source.AudioPcm += (a, n, r, c) => { pcm++; mkv.WriteAudio(a, n, r, c); };
            source.Ended += r => L("ended: " + (r ?? "-"));
            source.Video += (b, o, n) => { if (bytes == 0) L($"first video {n} bytes"); bytes += n; decoder.Feed(b.AsSpan(o, n), kind is "pfd" or "pico"); if (frames == 1 && bytes == n) L("first frame decoded"); };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            source.StartAsync().GetAwaiter().GetResult();
            L($"started after {sw.ElapsedMilliseconds} ms");
            long micSamples = 0; double micPeak = 0;
            var mic = new Cast.MicRelay(adb, serial) { AttachOnly = kind == "pico" };
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
            SKBitmap? b = Interlocked.Exchange(ref last, null);
            if (b is not null) ImageLoader.SavePng(b, png);
            b?.Dispose();
            L("OK");
        }
        catch (Exception ex) { L("ERROR " + ex); }
        return 0;
    }

    public static int CheckApk(string path)
    {
        string output = path + ".check.txt";
        try
        {
            ApkMetadata meta = ApkMetadataReader.Read(path);
            var app = new AppEntry(meta.Package) { Name = meta.Label, Version = meta.VersionName, Apks = { "/base.apk" } };
            using SKBitmap? icon = ImageLoader.Decode(meta.Icon);
            File.WriteAllText(output,
                $"{meta.Label}\n{meta.VersionName}\n{meta.VersionCode}\n{meta.Package}\n{BackupService.ApkFileNames(app)[0]}\n" +
                $"icon={(icon is not null)} {icon?.Width}x{icon?.Height} bytes={meta.Icon.Length}\n");
            if (icon is not null)
                ImageLoader.SavePng(icon, path + ".icon.png");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, "ERROR\n" + ex);
            return 1;
        }
    }
}
