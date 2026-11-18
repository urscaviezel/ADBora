using System.IO.Pipes;

namespace AdbTool;

/// <summary>
/// Only one ADBora at a time – two instances would fight over the same ADB device.
/// A second start asks the running instance (named pipe; on Linux a Unix socket) to show its window and exits.
/// </summary>
internal static class SingleInstance
{
    private static readonly string Name = "ADBora.SingleInstance." + Environment.UserName;
    private static Mutex? _mutex;

    /// <summary>True if this is the only instance (keep running); false = another one was activated.</summary>
    public static bool Acquire()
    {
        _mutex = new Mutex(true, (OperatingSystem.IsWindows() ? @"Local\" : "") + Name, out bool created);
        if (created) return true;
        if (Signal()) return false;
        // No answer (the old instance may be closing after an update): wait briefly
        try
        {
            if (_mutex.WaitOne(TimeSpan.FromSeconds(10))) return true;
        }
        catch (AbandonedMutexException) { return true; }
        Signal();
        return false;
    }

    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
    }

    private static bool Signal()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            client.Connect(1500);
            client.WriteByte(1);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Waits for activation requests of later starts (background).</summary>
    public static void Listen(Action activate)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1);
                    server.WaitForConnection();
                    if (server.ReadByte() >= 0) activate();
                }
                catch { Thread.Sleep(1000); }
            }
        }) { IsBackground = true, Name = "ADBora single instance" };
        thread.Start();
    }
}
