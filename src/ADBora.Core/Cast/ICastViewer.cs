using SkiaSharp;

namespace AdbTool.Cast;

/// <summary>
/// Window that shows the decoded casting picture. Implemented by the UI; every member
/// may be called from any thread (the implementation dispatches to its UI thread).
/// </summary>
internal interface ICastViewer
{
    /// <summary>Bitmaps no longer needed (for reuse by the decoder).</summary>
    event Action<SKBitmap>? Recycle;
    event Action? ScreenshotRequested;
    event Action? MuteToggleRequested;
    event Action<int>? VolumeStepRequested;
    /// <summary>The window was closed (by the user or by <see cref="Close"/>).</summary>
    event Action? Closed;

    bool IsClosed { get; }

    /// <summary>Called from the decoding thread; the viewer owns the frame afterwards.</summary>
    void Push(SKBitmap frame);
    /// <summary>Copy of the newest picture (for screenshots).</summary>
    SKBitmap? Snapshot();
    void Show();
    void Close();
}
