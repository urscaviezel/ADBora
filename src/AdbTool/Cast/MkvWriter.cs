using System.Diagnostics;
using System.Text;

namespace AdbTool.Cast;

/// <summary>
/// Minimal Matroska (MKV) writer for live recordings: H.264 video (access units with
/// Annex-B start codes, converted to length-prefixed NAL units) and optional 16-bit PCM
/// audio. Segment and clusters have "unknown" size, so the file stays playable even if
/// recording is interrupted. Timestamps come from the arrival time (video) and the
/// sample count (audio). Display size = pixel size (square pixels).
/// Audio: 16-bit PCM or AAC (given as ADTS frames, stored raw with AudioSpecificConfig).
/// </summary>
internal enum MkvAudio { None, Pcm, Aac }

internal sealed class MkvWriter : IDisposable
{
    private readonly FileStream _file;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();
    private MkvAudio _audio;
    private int _rate, _channels;
    private byte[]? _aacConfig, _sps, _pps;
    private long _waitForAudioSince = -1;
    private (byte[] Data, int Width, int Height)? _pendingKey;
    private bool _headerWritten;
    private long _clusterTime = -1, _lastVideoTime;
    private long _audioSamples = -1, _audioStart;
    private bool _anyVideo;
    private long _segmentStart, _segmentSizePos, _seekHeadPos, _infoPos, _tracksPos, _durationPos;
    private long _clusterStart = -1, _clusterSizePos;
    private readonly List<(long Time, long Position)> _cues = new();
    private const int SeekHeadSpace = 220;

    /// <summary>rate/channels 0 = taken from the first audio block (the header waits up to 3 s for it).</summary>
    public MkvWriter(string path, MkvAudio audio, int rate = 0, int channels = 0)
    {
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        _audio = audio;
        _rate = rate;
        _channels = channels;
    }

    /// <summary>One access unit (Annex-B). The header is written with the first key frame that carries SPS and PPS.</summary>
    public void WriteVideo(ReadOnlySpan<byte> annexB, int width, int height)
    {
        lock (_gate)
        {
            var nals = SplitNals(annexB);
            bool key = nals.Any(n => (n[0] & 31) == 5);
            // Remember SPS/PPS: some devices (PFD) send them only once at the start
            _sps = nals.FirstOrDefault(n => (n[0] & 31) == 7) ?? _sps;
            _pps = nals.FirstOrDefault(n => (n[0] & 31) == 8) ?? _pps;
            if (!_headerWritten)
            {
                byte[]? sps = _sps, pps = _pps;
                if (!key || sps is null || pps is null || width <= 0 || height <= 0) return;
                if ((_audio == MkvAudio.Aac && _aacConfig is null) || (_audio == MkvAudio.Pcm && _rate <= 0))
                {
                    // The audio track needs the format of the first audio block; wait up to 3 s
                    // Keep the key frame and write it as soon as the first audio frame has arrived
                    if (_waitForAudioSince < 0) _waitForAudioSince = Environment.TickCount64;
                    if (Environment.TickCount64 - _waitForAudioSince < 3000) { _pendingKey = (annexB.ToArray(), width, height); return; }
                    _audio = MkvAudio.None;
                }
                WriteHeader(sps, pps, width, height);
                _clock.Start();
            }
            long t = _clock.ElapsedMilliseconds;
            if (_anyVideo && t <= _lastVideoTime) t = _lastVideoTime + 1;
            _lastVideoTime = t;
            _anyVideo = true;
            var data = new MemoryStream();
            foreach (byte[] n in nals)
            {
                int type = n[0] & 31;
                if (type == 9) continue; // access unit delimiter
                data.WriteByte((byte)(n.Length >> 24)); data.WriteByte((byte)(n.Length >> 16)); data.WriteByte((byte)(n.Length >> 8)); data.WriteByte((byte)n.Length);
                data.Write(n);
            }
            if (key || _clusterTime < 0 || t - _clusterTime > 5000) StartCluster(t);
            if (key) _cues.Add((t, _clusterStart - _segmentStart));
            WriteBlock(1, t, key, data.GetBuffer(), (int)data.Length);
        }
    }

    /// <summary>Interleaved 16-bit PCM in the configured format.</summary>
    public void WriteAudio(short[] samples, int count, int rate, int channels)
    {
        if (_audio != MkvAudio.Pcm || rate <= 0 || channels <= 0) return;
        lock (_gate)
        {
            if (_rate <= 0)
            {
                _rate = rate;
                _channels = channels;
                if (!_headerWritten && _pendingKey is { } k) { _pendingKey = null; WriteVideo(k.Data, k.Width, k.Height); }
            }
            if (!_headerWritten || rate != _rate || channels != _channels) return;
            byte[] bytes = new byte[count * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length); // little-endian
            WriteAudioBlock(bytes, bytes.Length, count / _channels);
        }
    }

    /// <summary>One AAC frame with ADTS header.</summary>
    public void WriteAac(byte[] adts)
    {
        if (_audio != MkvAudio.Aac || adts.Length < 8) return;
        lock (_gate)
        {
            if (_aacConfig is null)
            {
                int objectType = (adts[2] >> 6) + 1, sf = adts[2] >> 2 & 15, ch = (adts[2] & 1) << 2 | adts[3] >> 6;
                _aacConfig = new[] { (byte)(objectType << 3 | sf >> 1), (byte)((sf & 1) << 7 | ch << 3) };
                _rate = AacDecoder.SampleRates[Math.Min(sf, 12)];
                _channels = Math.Max(1, ch);
                if (!_headerWritten && _pendingKey is { } k) { _pendingKey = null; WriteVideo(k.Data, k.Width, k.Height); }
            }
            if (!_headerWritten) return;
            int header = (adts[1] & 1) == 1 ? 7 : 9; // without / with CRC
            if (adts.Length <= header) return;
            WriteAudioBlock(adts[header..], adts.Length - header, 1024);
        }
    }

    private void WriteAudioBlock(byte[] data, int length, int samples)
    {
        if (_audioSamples < 0) { _audioSamples = 0; _audioStart = _clock.ElapsedMilliseconds; }
        long t = _audioStart + _audioSamples * 1000 / _rate;
        // Re-sync if the audio clock drifts more than 300 ms from the wall clock
        long now = _clock.ElapsedMilliseconds;
        if (Math.Abs(now - t) > 300) { _audioStart = now; _audioSamples = 0; t = now; }
        _audioSamples += samples;
        if (_clusterTime < 0 || t - _clusterTime > 5000 || t - _clusterTime < -30000) StartCluster(t);
        WriteBlock(2, t, true, data, length);
    }

    private void WriteHeader(byte[] sps, byte[] pps, int width, int height)
    {
        var ebml = new MemoryStream();
        Uint(ebml, 0x4286, 1); Uint(ebml, 0x42F7, 1); Uint(ebml, 0x42F2, 4); Uint(ebml, 0x42F3, 8);
        Str(ebml, 0x4282, "matroska"); Uint(ebml, 0x4287, 4); Uint(ebml, 0x4285, 2);
        Master(_file, 0x1A45DFA3, ebml);

        Id(_file, 0x18538067);
        _segmentSizePos = _file.Position;
        _file.Write(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }); // unknown size, patched when closing
        _segmentStart = _file.Position;

        // Space for the SeekHead (written when closing)
        _seekHeadPos = _file.Position;
        Void(_file, SeekHeadSpace);

        var info = new MemoryStream();
        Uint(info, 0x2AD7B1, 1_000_000); // timestamps in ms
        Str(info, 0x4D80, "ADBora"); Str(info, 0x5741, "ADBora " + AppInfo.Version);
        Id(info, 0x4489); Size(info, 8);
        long durationInInfo = info.Position;
        info.Write(new byte[8]); // duration, patched when closing
        _infoPos = _file.Position;
        Master(_file, 0x1549A966, info);
        _durationPos = _file.Position - info.Length + durationInInfo;

        var tracks = new MemoryStream();
        var v = new MemoryStream();
        Uint(v, 0xD7, 1); Uint(v, 0x73C5, 1); Uint(v, 0x83, 1); Uint(v, 0x9C, 0);
        Str(v, 0x86, "V_MPEG4/ISO/AVC");
        var avcc = new MemoryStream();
        avcc.Write(new byte[] { 1, sps[1], sps[2], sps[3], 0xFF, 0xE1, (byte)(sps.Length >> 8), (byte)sps.Length });
        avcc.Write(sps);
        avcc.Write(new byte[] { 1, (byte)(pps.Length >> 8), (byte)pps.Length });
        avcc.Write(pps);
        Bin(v, 0x63A2, avcc.ToArray());
        var video = new MemoryStream();
        Uint(video, 0xB0, (ulong)width); Uint(video, 0xBA, (ulong)height);
        Uint(video, 0x54B0, (ulong)width); Uint(video, 0x54BA, (ulong)height);
        Master(v, 0xE0, video);
        Master(tracks, 0xAE, v);
        if (_audio != MkvAudio.None)
        {
            var a = new MemoryStream();
            Uint(a, 0xD7, 2); Uint(a, 0x73C5, 2); Uint(a, 0x83, 2); Uint(a, 0x9C, 0);
            if (_audio == MkvAudio.Aac) { Str(a, 0x86, "A_AAC"); Bin(a, 0x63A2, _aacConfig!); }
            else Str(a, 0x86, "A_PCM/INT/LIT");
            var audio = new MemoryStream();
            Id(audio, 0xB5); Size(audio, 8); byte[] f = BitConverter.GetBytes((double)_rate); Array.Reverse(f); audio.Write(f);
            Uint(audio, 0x9F, (ulong)_channels);
            if (_audio == MkvAudio.Pcm) Uint(audio, 0x6264, 16);
            Master(a, 0xE1, audio);
            Master(tracks, 0xAE, a);
        }
        _tracksPos = _file.Position;
        Master(_file, 0x1654AE6B, tracks);
        _headerWritten = true;
    }

    private void StartCluster(long t)
    {
        CloseCluster();
        _clusterStart = _file.Position;
        Id(_file, 0x1F43B675);
        _clusterSizePos = _file.Position;
        _file.Write(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        var tc = new MemoryStream();
        Uint(tc, 0xE7, (ulong)Math.Max(0, t));
        tc.WriteTo(_file);
        _clusterTime = Math.Max(0, t);
    }

    /// <summary>Writes the real size of the current cluster (keeps the file seekable).</summary>
    private void CloseCluster()
    {
        if (_clusterStart < 0) return;
        PatchSize(_clusterSizePos, _file.Position - _clusterSizePos - 8);
        _clusterStart = -1;
    }

    private void PatchSize(long position, long size)
    {
        long end = _file.Position;
        _file.Position = position;
        Size(_file, size);
        _file.Position = end;
    }

    private static void Void(Stream s, int total)
    {
        // Void element (0xEC) occupying exactly "total" bytes: ID (1) + size (8) + data
        Id(s, 0xEC); Size(s, total - 9); s.Write(new byte[total - 9]);
    }

    private void WriteBlock(int track, long t, bool key, byte[] data, int length)
    {
        long rel = Math.Clamp(t - _clusterTime, short.MinValue, short.MaxValue);
        Id(_file, 0xA3);
        Size(_file, 4 + length);
        _file.WriteByte((byte)(0x80 | track));
        _file.WriteByte((byte)(rel >> 8)); _file.WriteByte((byte)rel);
        _file.WriteByte(key ? (byte)0x80 : (byte)0);
        _file.Write(data, 0, length);
    }

    private static List<byte[]> SplitNals(ReadOnlySpan<byte> d)
    {
        var list = new List<byte[]>();
        int start = -1;
        for (int i = 0; i + 3 <= d.Length; i++)
        {
            int sc = d[i] == 0 && d[i + 1] == 0 ? (d[i + 2] == 1 ? 3 : i + 3 < d.Length && d[i + 2] == 0 && d[i + 3] == 1 ? 4 : 0) : 0;
            if (sc == 0) continue;
            if (start >= 0 && i > start) list.Add(d[start..i].ToArray());
            start = i + sc;
            i += sc - 1;
        }
        if (start >= 0 && start < d.Length) list.Add(d[start..].ToArray());
        list.RemoveAll(n => n.Length == 0);
        return list;
    }

    // --- EBML helpers
    private static void Id(Stream s, uint id)
    {
        if (id > 0xFFFFFF) s.WriteByte((byte)(id >> 24));
        if (id > 0xFFFF) s.WriteByte((byte)(id >> 16));
        if (id > 0xFF) s.WriteByte((byte)(id >> 8));
        s.WriteByte((byte)id);
    }

    private static void Size(Stream s, long size)
    {
        // 8-byte size field: simple and always valid
        s.WriteByte(0x01);
        for (int i = 6; i >= 0; i--) s.WriteByte((byte)(size >> (i * 8)));
    }

    private static void Uint(Stream s, uint id, ulong value)
    {
        int n = 1;
        while (n < 8 && value >> (n * 8) != 0) n++;
        Id(s, id); Size(s, n);
        for (int i = n - 1; i >= 0; i--) s.WriteByte((byte)(value >> (i * 8)));
    }

    private static void Str(Stream s, uint id, string value) => Bin(s, id, Encoding.UTF8.GetBytes(value));

    private static void Bin(Stream s, uint id, byte[] value) { Id(s, id); Size(s, value.Length); s.Write(value); }

    private static void Master(Stream s, uint id, MemoryStream content) { Id(s, id); Size(s, content.Length); content.WriteTo(s); }

    public void Dispose()
    {
        lock (_gate)
        {
            try { if (_headerWritten) Finish(); } catch { }
            _file.Dispose();
        }
    }

    /// <summary>Cluster size, cues (index for seeking), duration, segment size and SeekHead.</summary>
    private void Finish()
    {
        CloseCluster();
        long cuesPos = _file.Position;
        var cues = new MemoryStream();
        foreach (var (time, pos) in _cues)
        {
            var point = new MemoryStream();
            Uint(point, 0xB3, (ulong)Math.Max(0, time));
            var tp = new MemoryStream();
            Uint(tp, 0xF7, 1); Uint(tp, 0xF1, (ulong)pos);
            Master(point, 0xB7, tp);
            Master(cues, 0xBB, point);
        }
        if (_cues.Count > 0) Master(_file, 0x1C53BB6B, cues);
        long end = _file.Position;

        double duration = Math.Max(_lastVideoTime, _audioSamples > 0 ? _audioStart + _audioSamples * 1000 / _rate : 0);
        _file.Position = _durationPos;
        byte[] d = BitConverter.GetBytes(duration); Array.Reverse(d); _file.Write(d);

        _file.Position = _segmentSizePos;
        Size(_file, end - _segmentStart);

        var seek = new MemoryStream();
        void Entry(uint id, long pos)
        {
            var e = new MemoryStream();
            byte[] idBytes = BitConverter.GetBytes(id); Array.Reverse(idBytes);
            Bin(e, 0x53AB, idBytes.SkipWhile(b => b == 0).ToArray());
            Uint(e, 0x53AC, (ulong)(pos - _segmentStart));
            Master(seek, 0x4DBB, e);
        }
        Entry(0x1549A966, _infoPos);
        Entry(0x1654AE6B, _tracksPos);
        if (_cues.Count > 0) Entry(0x1C53BB6B, cuesPos);
        var head = new MemoryStream();
        Master(head, 0x114D9B74, seek);
        _file.Position = _seekHeadPos;
        head.WriteTo(_file);
        Void(_file, SeekHeadSpace - (int)head.Length);
        _file.Position = end;
    }
}
