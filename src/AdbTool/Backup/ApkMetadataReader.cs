using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text;

namespace AdbTool.Backup;

internal sealed class ApkMetadata
{
    public string Package { get; set; } = "";
    public string Label { get; set; } = "";
    public string VersionName { get; set; } = "";
    public string VersionCode { get; set; } = "";
    public byte[] Icon { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Reads app name, version and launcher icon directly from an APK
/// (binary AndroidManifest.xml + resources.arsc). The APK is only read,
/// never modified.
/// </summary>
internal static class ApkMetadataReader
{
    private const uint AttrLabel = 0x01010001;
    private const uint AttrIcon = 0x01010002;
    private const uint AttrName = 0x01010003;
    private const uint AttrDrawable = 0x01010199;
    private const uint AttrVersionCode = 0x0101021b;
    private const uint AttrVersionName = 0x0101021c;
    private const uint AttrRoundIcon = 0x0101052c;

    public static ApkMetadata Read(string apkPath)
    {
        using ZipArchive zip = ZipFile.OpenRead(apkPath);

        byte[] manifestBytes = ReadEntry(zip, "AndroidManifest.xml")
            ?? throw new InvalidDataException("AndroidManifest.xml missing");
        byte[]? arscBytes = ReadEntry(zip, "resources.arsc");

        ResourceTable? table = null;
        if (arscBytes is not null)
        {
            try { table = ResourceTable.Parse(arscBytes); } catch { table = null; }
        }

        List<XmlElement> elements = BinaryXml.Parse(manifestBytes);
        var result = new ApkMetadata();

        XmlElement? manifest = elements.FirstOrDefault(e => e.Name == "manifest");
        XmlElement? application = elements.FirstOrDefault(e => e.Name == "application");
        XmlElement? launcher = FindLauncherActivity(elements);

        if (manifest is not null)
        {
            result.Package = manifest.Attr(0, "package")?.AsString(table) ?? "";
            result.VersionName = manifest.Attr(AttrVersionName, "versionName")?.AsString(table) ?? "";
            result.VersionCode = manifest.Attr(AttrVersionCode, "versionCode")?.AsString(table) ?? "";
        }

        string? label = application?.Attr(AttrLabel, "label")?.AsString(table);
        if (string.IsNullOrWhiteSpace(label))
            label = launcher?.Attr(AttrLabel, "label")?.AsString(table);
        result.Label = string.IsNullOrWhiteSpace(label) ? result.Package : label.Trim();

        // Icon: application icon → round icon → launcher activity icon.
        var iconRefs = new List<XmlAttribute?>
        {
            application?.Attr(AttrIcon, "icon"),
            application?.Attr(AttrRoundIcon, "roundIcon"),
            launcher?.Attr(AttrIcon, "icon"),
            launcher?.Attr(AttrRoundIcon, "roundIcon"),
        };

        foreach (XmlAttribute? attr in iconRefs)
        {
            if (attr is null || table is null || attr.DataType != ResValue.TypeReference)
                continue;
            try
            {
                byte[]? icon = LoadIcon(zip, table, attr.Data);
                if (icon is { Length: > 0 and <= 5 * 1024 * 1024 })
                {
                    result.Icon = icon;
                    break;
                }
            }
            catch
            {
                // A missing icon must not discard a resolved name or version.
            }
        }

        return result;
    }

    /// <summary>
    /// Reads only the package name and the split name (empty for a base APK)
    /// from AndroidManifest.xml — fast, resources.arsc is not parsed.
    /// </summary>
    public static (string Package, string Split) ReadPackage(string apkPath)
    {
        using ZipArchive zip = ZipFile.OpenRead(apkPath);
        byte[] manifestBytes = ReadEntry(zip, "AndroidManifest.xml")
            ?? throw new InvalidDataException("AndroidManifest.xml missing");
        XmlElement? manifest = BinaryXml.Parse(manifestBytes).FirstOrDefault(e => e.Name == "manifest");
        string package = manifest?.Attr(0, "package")?.AsString(null) ?? "";
        string split = manifest?.Attr(0, "split")?.AsString(null) ?? "";
        return (package, split);
    }

    private static XmlElement? FindLauncherActivity(List<XmlElement> elements)
    {
        XmlElement? current = null;
        bool main = false, launcher = false;

        foreach (XmlElement e in elements)
        {
            if (e.Name is "activity" or "activity-alias")
            {
                current = e;
                main = launcher = false;
                continue;
            }
            if (current is null || e.Depth <= current.Depth)
            {
                current = null;
                continue;
            }

            string? name = e.Attr(AttrName, "name")?.AsString(null);
            if (e.Name == "action" && name == "android.intent.action.MAIN") main = true;
            if (e.Name == "category" && name == "android.intent.category.LAUNCHER") launcher = true;
            if (main && launcher)
                return current;
        }
        return null;
    }

    private static byte[]? ReadEntry(ZipArchive zip, string name)
    {
        ZipArchiveEntry? entry = zip.GetEntry(name);
        if (entry is null || entry.Length > 64L * 1024 * 1024)
            return null;
        using Stream s = entry.Open();
        using var ms = new MemoryStream((int)Math.Max(0, entry.Length));
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static bool IsRaster(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    private static byte[]? LoadIcon(ZipArchive zip, ResourceTable table, uint resId)
    {
        List<(int Density, ResValue Value)> candidates = table.ResolveAll(resId);
        var files = candidates
            .Where(c => c.Value.DataType == ResValue.TypeString)
            .Select(c => (c.Density, Path: table.GlobalString(c.Value.Data) ?? ""))
            .Where(c => c.Path.Length > 0)
            .ToList();

        // Prefer the largest raster icon up to xxxhdpi (640 dpi).
        var raster = files.Where(f => IsRaster(f.Path))
            .OrderByDescending(f => f.Density is > 0 and <= 640 ? f.Density : (f.Density == 0 ? 160 : -1))
            .ThenByDescending(f => f.Density)
            .ToList();

        foreach (var file in raster)
        {
            byte[]? data = ReadEntry(zip, file.Path);
            if (data is { Length: > 0 })
                return data;
        }

        // Adaptive icon (res/mipmap-anydpi-v26/ic_launcher.xml): compose
        // background and foreground layers if they are raster images/colours.
        foreach (var file in files.Where(f => f.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            byte[]? xml = ReadEntry(zip, file.Path);
            if (xml is null) continue;
            byte[]? composed = ComposeAdaptiveIcon(zip, table, xml);
            if (composed is not null)
                return composed;
        }

        return null;
    }

    private static byte[]? ComposeAdaptiveIcon(ZipArchive zip, ResourceTable table, byte[] xmlBytes)
    {
        List<XmlElement> elements = BinaryXml.Parse(xmlBytes);
        if (elements.Count == 0 || elements[0].Name != "adaptive-icon")
            return null;

        XmlAttribute? fgAttr = elements.FirstOrDefault(e => e.Name == "foreground")?.Attr(AttrDrawable, "drawable");
        XmlAttribute? bgAttr = elements.FirstOrDefault(e => e.Name == "background")?.Attr(AttrDrawable, "drawable");
        if (fgAttr is null || fgAttr.DataType != ResValue.TypeReference)
            return null;

        byte[]? fgBytes = LoadRasterOnly(zip, table, fgAttr.Data);
        if (fgBytes is null)
            return null;

        using Bitmap? fg = ImageLoader.Decode(fgBytes);
        if (fg is null)
            return null;

        int size = Math.Max(fg.Width, fg.Height);
        using var canvas = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            if (bgAttr is not null && bgAttr.DataType == ResValue.TypeReference)
            {
                List<(int Density, ResValue Value)> bgValues = table.ResolveAll(bgAttr.Data);
                var color = bgValues.FirstOrDefault(v => v.Value.IsColor);
                if (color.Value is not null)
                {
                    using var brush = new SolidBrush(Color.FromArgb(unchecked((int)color.Value.Data)));
                    g.FillRectangle(brush, 0, 0, size, size);
                }
                else if (LoadRasterOnly(zip, table, bgAttr.Data) is { } bgBytes && ImageLoader.Decode(bgBytes) is { } bg)
                {
                    using (bg) g.DrawImage(bg, 0, 0, size, size);
                }
            }
            else if (bgAttr is not null && bgAttr.IsColor)
            {
                using var brush = new SolidBrush(Color.FromArgb(unchecked((int)bgAttr.Data)));
                g.FillRectangle(brush, 0, 0, size, size);
            }

            g.DrawImage(fg, 0, 0, size, size);
        }

        // Visible area of an adaptive icon is the inner 72dp of 108dp.
        int inner = (int)Math.Round(size * 72.0 / 108.0);
        int offset = (size - inner) / 2;
        using var output = new Bitmap(inner, inner, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(output))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using GraphicsPath mask = UI.Theme.RoundedRect(new Rectangle(0, 0, inner - 1, inner - 1), inner / 5);
            g.SetClip(mask);
            g.DrawImage(canvas, new Rectangle(0, 0, inner, inner), new Rectangle(offset, offset, inner, inner), GraphicsUnit.Pixel);
        }

        using var ms = new MemoryStream();
        output.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static byte[]? LoadRasterOnly(ZipArchive zip, ResourceTable table, uint resId)
    {
        var files = table.ResolveAll(resId)
            .Where(c => c.Value.DataType == ResValue.TypeString)
            .Select(c => (c.Density, Path: table.GlobalString(c.Value.Data) ?? ""))
            .Where(c => IsRaster(c.Path))
            .OrderByDescending(f => f.Density is > 0 and <= 640 ? f.Density : (f.Density == 0 ? 160 : -1));
        foreach (var file in files)
        {
            byte[]? data = ReadEntry(zip, file.Path);
            if (data is { Length: > 0 })
                return data;
        }
        return null;
    }
}

// ----------------------------------------------------------------------
// Binary resource structures
// ----------------------------------------------------------------------

internal sealed class ResValue
{
    public const byte TypeNull = 0x00;
    public const byte TypeReference = 0x01;
    public const byte TypeAttribute = 0x02;
    public const byte TypeString = 0x03;
    public const byte TypeFloat = 0x04;
    public const byte TypeIntDec = 0x10;
    public const byte TypeIntHex = 0x11;
    public const byte TypeIntBoolean = 0x12;

    public byte DataType { get; init; }
    public uint Data { get; init; }

    public bool IsColor => DataType is >= 0x1c and <= 0x1f;
}

internal sealed class StringPool
{
    private readonly byte[] _data;
    private readonly int _stringsStart;
    private readonly int[] _offsets;
    private readonly bool _utf8;
    private readonly Dictionary<int, string> _cache = new();

    public int Count => _offsets.Length;

    public StringPool(byte[] data, int chunkStart)
    {
        _data = data;
        int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunkStart + 2));
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(chunkStart + 8));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(chunkStart + 16));
        _stringsStart = chunkStart + (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(chunkStart + 20));
        _utf8 = (flags & 0x100) != 0;

        _offsets = new int[count];
        int offsetsStart = chunkStart + headerSize;
        for (int i = 0; i < count; i++)
            _offsets[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offsetsStart + i * 4));
    }

    public string? Get(uint index)
    {
        if (index >= _offsets.Length)
            return null;
        int i = (int)index;
        if (_cache.TryGetValue(i, out string? cached))
            return cached;

        try
        {
            int pos = _stringsStart + _offsets[i];
            string value;
            if (_utf8)
            {
                // UTF-16 length (skip), then UTF-8 byte length.
                pos += (_data[pos] & 0x80) != 0 ? 2 : 1;
                int len = _data[pos];
                if ((len & 0x80) != 0)
                {
                    len = ((len & 0x7F) << 8) | _data[pos + 1];
                    pos += 2;
                }
                else
                {
                    pos += 1;
                }
                value = Encoding.UTF8.GetString(_data, pos, len);
            }
            else
            {
                int len = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(pos));
                pos += 2;
                if ((len & 0x8000) != 0)
                {
                    len = ((len & 0x7FFF) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(pos));
                    pos += 2;
                }
                value = Encoding.Unicode.GetString(_data, pos, len * 2);
            }
            _cache[i] = value;
            return value;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class XmlAttribute
{
    public uint ResourceId { get; init; }
    public string Name { get; init; } = "";
    public string? RawString { get; init; }
    public byte DataType { get; init; }
    public uint Data { get; init; }
    public StringPool Strings { get; init; } = null!;

    public bool IsColor => DataType is >= 0x1c and <= 0x1f;

    public string? AsString(ResourceTable? table)
    {
        if (RawString is not null)
            return RawString;

        return DataType switch
        {
            ResValue.TypeString => Strings.Get(Data),
            ResValue.TypeIntDec => ((int)Data).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ResValue.TypeIntHex => "0x" + Data.ToString("x"),
            ResValue.TypeIntBoolean => Data != 0 ? "true" : "false",
            ResValue.TypeReference => table?.ResolveString(Data),
            _ => null
        };
    }
}

internal sealed class XmlElement
{
    public string Name { get; init; } = "";
    public int Depth { get; init; }
    public List<XmlAttribute> Attributes { get; } = new();

    /// <summary>Find an attribute by Android resource id (robust against obfuscated names) or by name.</summary>
    public XmlAttribute? Attr(uint resId, string name) =>
        (resId != 0 ? Attributes.FirstOrDefault(a => a.ResourceId == resId) : null)
        ?? Attributes.FirstOrDefault(a => a.Name == name && (resId == 0 || a.ResourceId == 0 || a.ResourceId == resId));
}

/// <summary>Android binary XML (AXML) parser — only start elements and attributes.</summary>
internal static class BinaryXml
{
    public static List<XmlElement> Parse(byte[] data)
    {
        var elements = new List<XmlElement>();
        if (data.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0x0003)
            return elements;

        StringPool? strings = null;
        uint[] resourceMap = Array.Empty<uint>();
        int depth = 0;
        int pos = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        int end = (int)Math.Min(data.Length, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));

        while (pos + 8 <= end)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            if (size < 8 || pos + size > data.Length)
                break;

            switch (type)
            {
                case 0x0001:
                    strings = new StringPool(data, pos);
                    break;

                case 0x0180:
                    int count = (size - headerSize) / 4;
                    resourceMap = new uint[count];
                    for (int i = 0; i < count; i++)
                        resourceMap[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + headerSize + i * 4));
                    break;

                case 0x0102 when strings is not null:
                {
                    int ext = pos + headerSize;
                    uint nameIndex = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ext + 4));
                    int attrStart = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ext + 8));
                    int attrSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ext + 10));
                    int attrCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ext + 12));

                    var element = new XmlElement { Name = strings.Get(nameIndex) ?? "", Depth = depth };
                    for (int i = 0; i < attrCount; i++)
                    {
                        int a = ext + attrStart + i * attrSize;
                        if (a + 20 > pos + size) break;
                        uint attrName = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(a + 4));
                        uint rawValue = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(a + 8));
                        byte dataType = data[a + 15];
                        uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(a + 16));

                        element.Attributes.Add(new XmlAttribute
                        {
                            ResourceId = attrName < resourceMap.Length ? resourceMap[attrName] : 0,
                            Name = strings.Get(attrName) ?? "",
                            RawString = rawValue != 0xFFFFFFFF ? strings.Get(rawValue) : null,
                            DataType = dataType,
                            Data = value,
                            Strings = strings
                        });
                    }
                    elements.Add(element);
                    depth++;
                    break;
                }

                case 0x0103:
                    depth = Math.Max(0, depth - 1);
                    break;
            }

            pos += size;
        }

        return elements;
    }
}

/// <summary>resources.arsc parser (simple values only; bags/styles are skipped).</summary>
internal sealed class ResourceTable
{
    private readonly StringPool? _globalStrings;
    private readonly Dictionary<uint, List<(int Density, bool DefaultLocale, string Locale, ResValue Value)>> _entries = new();

    private ResourceTable(StringPool? globalStrings) => _globalStrings = globalStrings;

    public string? GlobalString(uint index) => _globalStrings?.Get(index);

    public static ResourceTable Parse(byte[] data)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(data) != 0x0002)
            throw new InvalidDataException("resources.arsc: bad header");

        int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        int pos = headerSize;
        StringPool? global = null;
        var chunks = new List<int>();

        while (pos + 8 <= data.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            if (size < 8) break;
            if (type == 0x0001 && global is null) global = new StringPool(data, pos);
            else if (type == 0x0200) chunks.Add(pos);
            pos += size;
        }

        var table = new ResourceTable(global);
        foreach (int package in chunks)
            table.ParsePackage(data, package);
        return table;
    }

    private void ParsePackage(byte[] data, int start)
    {
        int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(start + 2));
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start + 4));
        uint packageId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start + 8));
        int end = Math.Min(data.Length, start + size);
        int pos = start + headerSize;

        while (pos + 8 <= end)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            int chunkHeader = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
            int chunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            if (chunkSize < 8 || pos + chunkSize > end) break;

            if (type == 0x0201)
            {
                try { ParseType(data, pos, chunkHeader, chunkSize, packageId); }
                catch { /* skip malformed type chunk */ }
            }

            pos += chunkSize;
        }
    }

    private void ParseType(byte[] data, int start, int headerSize, int size, uint packageId)
    {
        byte typeId = data[start + 8];
        byte flags = data[start + 9];
        int entryCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start + 12));
        int entriesStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start + 16));

        int config = start + 20;
        int configSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(config));
        string language = configSize >= 12 ? Locale(data, config + 8) : "";
        string country = configSize >= 12 ? Locale(data, config + 10) : "";
        int density = configSize >= 16 ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(config + 14)) : 0;
        string locale = country.Length > 0 ? language + "-" + country : language;

        bool sparse = (flags & 0x01) != 0;
        bool offset16 = (flags & 0x02) != 0;
        int offsets = start + headerSize;
        int end = start + size;

        for (int i = 0; i < entryCount; i++)
        {
            int entryIndex;
            long offset;

            if (sparse)
            {
                entryIndex = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offsets + i * 4));
                offset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offsets + i * 4 + 2)) * 4L;
            }
            else if (offset16)
            {
                entryIndex = i;
                ushort o = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offsets + i * 2));
                if (o == 0xFFFF) continue;
                offset = o * 4L;
            }
            else
            {
                entryIndex = i;
                uint o = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offsets + i * 4));
                if (o == 0xFFFFFFFF) continue;
                offset = o;
            }

            int entry = (int)(start + entriesStart + offset);
            if (entry + 8 > end) continue;

            ushort entryFlags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(entry + 2));
            ResValue value;

            if ((entryFlags & 0x0008) != 0)
            {
                // Compact entry: key(16) flags(16, high byte = data type) data(32)
                value = new ResValue { DataType = (byte)(entryFlags >> 8), Data = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 4)) };
            }
            else if ((entryFlags & 0x0001) != 0)
            {
                continue; // complex (bag) entry
            }
            else
            {
                int entrySize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(entry));
                int v = entry + entrySize;
                if (v + 8 > end) continue;
                value = new ResValue { DataType = data[v + 3], Data = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(v + 4)) };
            }

            uint resId = (packageId << 24) | ((uint)typeId << 16) | (uint)entryIndex;
            if (!_entries.TryGetValue(resId, out var list))
                _entries[resId] = list = new();
            list.Add((density, language.Length == 0, locale, value));
        }
    }

    private static string Locale(byte[] data, int pos)
    {
        byte a = data[pos], b = data[pos + 1];
        if (a == 0) return "";
        if ((a & 0x80) != 0) return ""; // packed 3-letter codes: not needed here
        return Encoding.ASCII.GetString(new[] { a, b });
    }

    /// <summary>All values for a resource id; references are followed (max. depth 8).</summary>
    public List<(int Density, ResValue Value)> ResolveAll(uint resId, int depth = 0)
    {
        var result = new List<(int, ResValue)>();
        if (depth > 8 || !_entries.TryGetValue(resId, out var list))
            return result;

        foreach (var entry in list)
        {
            if (entry.Value.DataType == ResValue.TypeReference && entry.Value.Data != 0)
            {
                foreach (var nested in ResolveAll(entry.Value.Data, depth + 1))
                    result.Add((nested.Density != 0 ? nested.Density : entry.Density, nested.Value));
            }
            else
            {
                result.Add((entry.Density, entry.Value));
            }
        }
        return result;
    }

    /// <summary>String value in the default locale (falls back to English, then anything).</summary>
    public string? ResolveString(uint resId, int depth = 0)
    {
        if (depth > 8 || !_entries.TryGetValue(resId, out var list) || list.Count == 0)
            return null;

        var ordered = list
            .OrderByDescending(e => e.DefaultLocale)
            .ThenByDescending(e => e.Locale.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var entry in ordered)
        {
            string? s = entry.Value.DataType switch
            {
                ResValue.TypeString => GlobalString(entry.Value.Data),
                ResValue.TypeReference => ResolveString(entry.Value.Data, depth + 1),
                _ => null
            };
            if (!string.IsNullOrEmpty(s))
                return s;
        }
        return null;
    }
}
