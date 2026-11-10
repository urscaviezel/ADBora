namespace AdbTool.Cast;

/// <summary>Colour information from an H.264 SPS (VUI): full range and matrix (BT.601 / BT.709).</summary>
internal readonly record struct SpsInfo(bool FullRange, bool? Bt709)
{
    /// <summary>Parses the NAL unit (including the 1-byte header). Null if no colour information is present.</summary>
    public static SpsInfo? Parse(ReadOnlySpan<byte> nal)
    {
        try
        {
            var r = new BitReader(Unescape(nal[1..]));
            int profile = r.Bits(8); r.Bits(16); // constraint flags, level
            r.Ue(); // seq_parameter_set_id
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                int chroma = r.Ue();
                if (chroma == 3) r.Bits(1);
                r.Ue(); r.Ue(); r.Bits(1); // bit depths, qpprime bypass
                if (r.Bits(1) == 1)
                {
                    for (int i = 0; i < (chroma != 3 ? 8 : 12); i++)
                        if (r.Bits(1) == 1) SkipScalingList(r, i < 6 ? 16 : 64);
                }
            }
            r.Ue(); // log2_max_frame_num
            int pocType = r.Ue();
            if (pocType == 0) r.Ue();
            else if (pocType == 1)
            {
                r.Bits(1); r.Se(); r.Se();
                int n = r.Ue();
                for (int i = 0; i < n; i++) r.Se();
            }
            r.Ue(); r.Bits(1); // max_num_ref_frames, gaps
            r.Ue(); r.Ue(); // width / height in macroblocks
            if (r.Bits(1) == 0) r.Bits(1); // frame_mbs_only → mb_adaptive
            r.Bits(1); // direct_8x8
            if (r.Bits(1) == 1) { r.Ue(); r.Ue(); r.Ue(); r.Ue(); } // cropping
            if (r.Bits(1) == 0) return null; // no VUI
            if (r.Bits(1) == 1) { if (r.Bits(8) == 255) r.Bits(32); } // aspect ratio
            if (r.Bits(1) == 1) r.Bits(1); // overscan
            if (r.Bits(1) == 0) return null; // no video signal type
            r.Bits(3); // video_format
            bool full = r.Bits(1) == 1;
            bool? bt709 = null;
            if (r.Bits(1) == 1)
            {
                r.Bits(8); r.Bits(8);
                int matrix = r.Bits(8);
                bt709 = matrix == 1 ? true : matrix is 5 or 6 ? false : null; // 2 = unspecified
            }
            return new SpsInfo(full, bt709);
        }
        catch { return null; }
    }

    private static void SkipScalingList(BitReader r, int size)
    {
        int last = 8, next = 8;
        for (int j = 0; j < size; j++)
        {
            if (next != 0) next = (last + r.Se() + 256) % 256;
            last = next == 0 ? last : next;
        }
    }

    private static byte[] Unescape(ReadOnlySpan<byte> d)
    {
        var o = new List<byte>(d.Length);
        int zeros = 0;
        foreach (byte b in d)
        {
            if (zeros >= 2 && b == 3) { zeros = 0; continue; }
            o.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return o.ToArray();
    }

    private sealed class BitReader
    {
        private readonly byte[] _d; private int _pos;
        public BitReader(byte[] d) => _d = d;
        public int Bits(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++)
            {
                if (_pos >> 3 >= _d.Length) throw new EndOfStreamException();
                v = v << 1 | (_d[_pos >> 3] >> (7 - (_pos & 7)) & 1);
                _pos++;
            }
            return v;
        }
        public int Ue()
        {
            int zeros = 0;
            while (Bits(1) == 0) if (++zeros > 31) throw new InvalidDataException();
            return (1 << zeros) - 1 + (zeros > 0 ? Bits(zeros) : 0);
        }
        public int Se() { int k = Ue(); return (k & 1) == 1 ? (k + 1) / 2 : -(k / 2); }
    }
}
