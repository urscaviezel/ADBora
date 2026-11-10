using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace AdbTool.Cast;

/// <summary>
/// H.264 decoder on top of the FFmpeg libraries that ship with scrcpy
/// (avcodec / avutil, LGPL). Only a small, long-stable part of the API is used.
/// The scrcpy build has no H.264 parser, so access units (frames) are assembled
/// here from the NAL units; the decoder returns YUV 4:2:0 pictures that are
/// converted to 32-bit bitmaps.
/// </summary>
internal sealed unsafe class H264Decoder : IDisposable
{
    private const int CodecIdH264 = 27;
    private const int AvErrorEagain = -11;
    private const long NoPts = unchecked((long)0x8000000000000000UL);

    // FFmpeg entry points (cdecl)
    internal static delegate* unmanaged[Cdecl]<int, IntPtr> _findDecoder;
    internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _allocContext;
    internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> _open;
    internal static delegate* unmanaged[Cdecl]<IntPtr*, void> _freeContext;
    internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> _sendPacket;
    internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> _receiveFrame;
    internal static delegate* unmanaged[Cdecl]<IntPtr> _packetAlloc;
    internal static delegate* unmanaged[Cdecl]<IntPtr*, void> _packetFree;
    internal static delegate* unmanaged[Cdecl]<IntPtr> _frameAlloc;
    internal static delegate* unmanaged[Cdecl]<IntPtr*, void> _frameFree;
    internal static delegate* unmanaged[Cdecl]<IntPtr, void> _frameUnref;
    internal static delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, int, int> _optSet;
    private static string? _loadedFrom;

    private IntPtr _ctx, _packet, _frame;
    private readonly MemoryStream _au = new();
    private bool _auHasSlice;
    private SpsInfo? _color;
    private Bitmap? _spare;
    private volatile bool _disposed;

    /// <summary>Called on the decoding thread for every finished picture. The receiver owns the bitmap.</summary>
    public event Action<Bitmap>? FrameDecoded;
    /// <summary>Every complete access unit (Annex-B) after decoding, e.g. for recording.</summary>
    public event Action<byte[], int>? AccessUnit;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Folder that contains avcodec-*.dll (the scrcpy folder).</summary>
    public static bool TryLoad(string folder, out string error)
    {
        error = "";
        if (_loadedFrom is not null) return true;
        try
        {
            string? Find(string pattern) => Directory.EnumerateFiles(folder, pattern).OrderByDescending(f => f).FirstOrDefault();
            string avutil = Find("avutil-*.dll") ?? throw new FileNotFoundException("avutil-*.dll");
            string avcodec = Find("avcodec-*.dll") ?? throw new FileNotFoundException("avcodec-*.dll");
            // Load the dependencies first so avcodec finds them by name
            IntPtr util = NativeLibrary.Load(avutil);
            if (Find("swresample-*.dll") is { } swr) NativeLibrary.Load(swr);
            IntPtr codec = NativeLibrary.Load(avcodec);

            _findDecoder = (delegate* unmanaged[Cdecl]<int, IntPtr>)NativeLibrary.GetExport(codec, "avcodec_find_decoder");
            _allocContext = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)NativeLibrary.GetExport(codec, "avcodec_alloc_context3");
            _open = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)NativeLibrary.GetExport(codec, "avcodec_open2");
            _freeContext = (delegate* unmanaged[Cdecl]<IntPtr*, void>)NativeLibrary.GetExport(codec, "avcodec_free_context");
            _sendPacket = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)NativeLibrary.GetExport(codec, "avcodec_send_packet");
            _receiveFrame = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)NativeLibrary.GetExport(codec, "avcodec_receive_frame");
            _packetAlloc = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(codec, "av_packet_alloc");
            _packetFree = (delegate* unmanaged[Cdecl]<IntPtr*, void>)NativeLibrary.GetExport(codec, "av_packet_free");
            _frameAlloc = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(util, "av_frame_alloc");
            _frameFree = (delegate* unmanaged[Cdecl]<IntPtr*, void>)NativeLibrary.GetExport(util, "av_frame_free");
            _frameUnref = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(util, "av_frame_unref");
            _optSet = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, int, int>)NativeLibrary.GetExport(util, "av_opt_set");
            _loadedFrom = folder;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public H264Decoder()
    {
        if (_loadedFrom is null) throw new InvalidOperationException("FFmpeg not loaded");
        IntPtr codec = _findDecoder(CodecIdH264);
        if (codec == IntPtr.Zero) throw new InvalidOperationException("H.264 decoder not available");
        _ctx = _allocContext(codec);
        SetOption("flags", "low_delay");
        SetOption("threads", "auto");
        SetOption("thread_type", "slice"); // frame threading would add latency
        int r = _open(_ctx, codec, IntPtr.Zero);
        if (r < 0) throw new InvalidOperationException($"avcodec_open2 failed ({r})");
        _packet = _packetAlloc();
        _frame = _frameAlloc();
    }

    private void SetOption(string name, string value)
    {
        byte[] n = Encoding.ASCII.GetBytes(name + "\0"), v = Encoding.ASCII.GetBytes(value + "\0");
        fixed (byte* pn = n, pv = v) _optSet(_ctx, pn, pv, 1 /* AV_OPT_SEARCH_CHILDREN */);
    }

    /// <summary>
    /// Feeds Annex-B data that consists of whole NAL units. A frame is decoded as soon as
    /// the next one begins (new slice with first_mb_in_slice = 0, or AUD/SPS/PPS/SEI);
    /// <paramref name="frameComplete"/> = true decodes immediately (one frame per call).
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data, bool frameComplete = false)
    {
        if (_ctx == IntPtr.Zero) return;
        int i = NextStart(data, 0, out int sc);
        while (i >= 0)
        {
            int start = i + sc;
            int next = NextStart(data, start, out int nsc);
            int end = next >= 0 ? next : data.Length;
            if (end > start)
            {
                ReadOnlySpan<byte> nal = data[start..end];
                int type = nal[0] & 31;
                bool vcl = type is 1 or 5;
                bool firstSlice = vcl && nal.Length > 1 && (nal[1] & 0x80) != 0; // first_mb_in_slice == 0
                if (_auHasSlice && (firstSlice || type is 6 or 7 or 8 or 9)) FlushAu();
                if (type == 7 && SpsInfo.Parse(nal) is { } color) _color = color;
                _au.Write(StartCode);
                _au.Write(nal);
                if (vcl) _auHasSlice = true;
            }
            i = next; sc = nsc;
        }
        if (frameComplete && _auHasSlice) FlushAu();
    }

    private static ReadOnlySpan<byte> StartCode => new byte[] { 0, 0, 0, 1 };

    private static int NextStart(ReadOnlySpan<byte> d, int from, out int length)
    {
        for (int i = from; i + 3 <= d.Length; i++)
        {
            if (d[i] != 0 || d[i + 1] != 0) continue;
            if (d[i + 2] == 1) { length = 3; return i; }
            if (i + 4 <= d.Length && d[i + 2] == 0 && d[i + 3] == 1) { length = 4; return i; }
        }
        length = 0;
        return -1;
    }

    private void FlushAu()
    {
        int n = (int)_au.Length;
        if (n > 0)
        {
            // FFmpeg reads up to 64 bytes past the end (AV_INPUT_BUFFER_PADDING_SIZE)
            _au.Write(new byte[64]);
            fixed (byte* p = _au.GetBuffer()) Decode(p, n);
            AccessUnit?.Invoke(_au.GetBuffer(), n);
        }
        _au.SetLength(0);
        _auHasSlice = false;
    }

    private void Decode(byte* data, int size)
    {
        // AVPacket: buf (0), pts (8), dts (16), data (24), size (32) – stable since FFmpeg 4
        *(IntPtr*)((byte*)_packet + 24) = (IntPtr)data;
        *(int*)((byte*)_packet + 32) = size;
        if (_sendPacket(_ctx, _packet) < 0) return;
        while (_receiveFrame(_ctx, _frame) == 0)
        {
            try { Convert(); }
            finally { _frameUnref(_frame); }
        }
    }

    /// <summary>YUV 4:2:0 (planar) → 32-bit RGB, BT.709 limited range (BT.601 for SD).</summary>
    private void Convert()
    {
        // AVFrame: data[8] (0), linesize[8] (64), extended_data (96), width (104), height (108), nb_samples (112), format (116)
        byte* f = (byte*)_frame;
        byte* y0 = ((byte**)f)[0], u0 = ((byte**)f)[1], v0 = ((byte**)f)[2];
        int* ls = (int*)(f + 64);
        int w = *(int*)(f + 104), h = *(int*)(f + 108), format = *(int*)(f + 116);
        if (w <= 0 || h <= 0 || y0 == null || u0 == null || v0 == null) return;
        if (format != 0 && format != 12) return; // yuv420p / yuvj420p only
        // Colour range and matrix from the SPS; without it: full range for yuvj, BT.709 from 720p
        bool full = format == 12 || _color?.FullRange == true;
        bool hd = _color?.Bt709 ?? h >= 700;
        // Fixed-point coefficients (×1024)
        int cy = full ? 1024 : 1192, yOff = full ? 0 : 16;
        int crv = hd ? (full ? 1612 : 1836) : (full ? 1436 : 1634);
        int cgu = hd ? (full ? 192 : 218) : (full ? 352 : 401);
        int cgv = hd ? (full ? 479 : 546) : (full ? 731 : 832);
        int cbu = hd ? (full ? 1900 : 2163) : (full ? 1815 : 2066);

        Bitmap? spare = Interlocked.Exchange(ref _spare, null);
        if (spare is not null && (spare.Width != w || spare.Height != h)) { spare.Dispose(); spare = null; }
        Bitmap bmp = spare ?? new Bitmap(w, h, PixelFormat.Format32bppRgb);
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            nint dstP = bd.Scan0, yP = (nint)y0, uP = (nint)u0, vP = (nint)v0;
            int stride = bd.Stride, lsY = ls[0], lsU = ls[1], lsV = ls[2];
            Parallel.For(0, (h + 1) / 2, row2 =>
            {
                byte* dst0 = (byte*)dstP, yb = (byte*)yP;
                byte* u = (byte*)uP + row2 * lsU, v = (byte*)vP + row2 * lsV;
                for (int k = 0; k < 2; k++)
                {
                    int yy = row2 * 2 + k;
                    if (yy >= h) break;
                    byte* ys = yb + yy * lsY;
                    uint* d = (uint*)(dst0 + yy * stride);
                    for (int x = 0; x < w; x++)
                    {
                        int c = (ys[x] - yOff) * cy;
                        int du = u[x >> 1] - 128, dv = v[x >> 1] - 128;
                        int r = (c + crv * dv) >> 10, g = (c - cgu * du - cgv * dv) >> 10, b = (c + cbu * du) >> 10;
                        r = r < 0 ? 0 : r > 255 ? 255 : r;
                        g = g < 0 ? 0 : g > 255 ? 255 : g;
                        b = b < 0 ? 0 : b > 255 ? 255 : b;
                        d[x] = (uint)(r << 16 | g << 8 | b);
                    }
                }
            });
        }
        finally { bmp.UnlockBits(bd); }
        Width = w; Height = h;
        if (FrameDecoded is { } handler) handler(bmp); else bmp.Dispose();
    }

    /// <summary>Gives a bitmap back for reuse (avoids allocating one per frame).</summary>
    public void Recycle(Bitmap bmp)
    {
        if (_disposed || bmp.Width != Width || bmp.Height != Height || Interlocked.CompareExchange(ref _spare, bmp, null) is not null) bmp.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        if (_packet != IntPtr.Zero) { IntPtr p = _packet; _packetFree(&p); _packet = IntPtr.Zero; }
        if (_frame != IntPtr.Zero) { IntPtr p = _frame; _frameFree(&p); _frame = IntPtr.Zero; }
        if (_ctx != IntPtr.Zero) { IntPtr p = _ctx; _freeContext(&p); _ctx = IntPtr.Zero; }
        Interlocked.Exchange(ref _spare, null)?.Dispose();
    }
}
