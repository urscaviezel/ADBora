using System.Globalization;
using System.IO.Compression;
using SkiaSharp;

namespace AdbTool.Backup;

/// <summary>
/// Renders Android XML drawables used as launcher icons: &lt;vector&gt; (paths, groups, solid colours and
/// simple gradients), &lt;adaptive-icon&gt; (raster / vector / colour layers), &lt;bitmap&gt;, &lt;layer-list&gt;,
/// &lt;inset&gt; and &lt;shape&gt; with a solid colour. Good enough for icons, not a full drawable engine.
/// </summary>
internal static class DrawableRenderer
{
    // android.R.attr ids (names are used as fallback – obfuscated APKs may strip them)
    private const uint AWidth = 0x01010159, AHeight = 0x01010155, AViewportWidth = 0x01010402, AViewportHeight = 0x01010403;
    private const uint AFillColor = 0x01010404, APathData = 0x01010405, AStrokeColor = 0x01010406, AStrokeWidth = 0x01010407;
    private const uint AStrokeLineCap = 0x0101040b, AStrokeLineJoin = 0x0101040c, AFillAlpha = 0x010104cc, AStrokeAlpha = 0x010104cb;
    private const uint AFillType = 0x0101051e, AAlpha = 0x0101031f;
    private const uint ARotation = 0x01010326, APivotX = 0x010101b5, APivotY = 0x010101b6, AScaleX = 0x01010324, AScaleY = 0x01010325;
    private const uint ATranslateX = 0x01010320, ATranslateY = 0x01010321;
    private const uint ADrawable = 0x01010199, ASrc = 0x01010119, AColor = 0x010101a5, AOffset = 0x01010514;
    private const uint AStartColor = 0x0101019d, AEndColor = 0x0101019e, ACenterColor = 0x0101020b;
    private const uint AStartX = 0x01010510, AStartY = 0x01010511, AEndX = 0x01010512, AEndY = 0x01010513;

    private sealed class Node
    {
        public XmlElement Element = null!;
        public List<Node> Children { get; } = new();
    }

    /// <summary>Renders an XML drawable (binary XML bytes) as PNG; null if it cannot be rendered.</summary>
    public static byte[]? RenderIconPng(ZipArchive zip, ResourceTable table, byte[] xml, int size = 192)
    {
        try
        {
            using SKBitmap? bmp = RenderXml(zip, table, xml, size, 0, icon: true);
            if (bmp is null || IsEmpty(bmp)) return null;
            return ImageLoader.EncodePng(bmp);
        }
        catch { return null; }
    }

    private static bool IsEmpty(SKBitmap bmp)
    {
        for (int y = 0; y < bmp.Height; y += Math.Max(1, bmp.Height / 24))
            for (int x = 0; x < bmp.Width; x += Math.Max(1, bmp.Width / 24))
                if (bmp.GetPixel(x, y).Alpha > 8) return false;
        return true;
    }

    private static Node? Tree(byte[] xml)
    {
        List<XmlElement> elements = BinaryXml.Parse(xml);
        if (elements.Count == 0) return null;
        var root = new Node { Element = elements[0] };
        var stack = new Stack<Node>();
        stack.Push(root);
        foreach (XmlElement e in elements.Skip(1))
        {
            while (stack.Count > 0 && stack.Peek().Element.Depth >= e.Depth) stack.Pop();
            if (stack.Count == 0) break;
            var node = new Node { Element = e };
            stack.Peek().Children.Add(node);
            stack.Push(node);
        }
        return root;
    }

    private static SKBitmap? RenderXml(ZipArchive zip, ResourceTable table, byte[] xml, int size, int depth, bool icon = false)
    {
        Node? root = Tree(xml);
        if (root is null) return null;
        return root.Element.Name switch
        {
            "vector" => RenderVector(zip, table, root, size),
            "adaptive-icon" => RenderAdaptive(zip, table, root, size, depth),
            "bitmap" or "nine-patch" => RenderReference(zip, table, root.Element.Attr(ASrc, "src"), size, depth),
            "layer-list" => RenderLayers(zip, table, root, size, depth),
            "inset" => RenderInset(zip, table, root, size, depth),
            "shape" => RenderShape(zip, table, root, size),
            _ => null
        };
    }

    /// <summary>Renders a drawable reference (raster, XML drawable or colour) at the given size.</summary>
    private static SKBitmap? RenderReference(ZipArchive zip, ResourceTable table, XmlAttribute? attr, int size, int depth)
    {
        if (attr is null || depth > 6) return null;
        if (attr.IsColor) return Solid(Argb(attr.Data), size);
        if (attr.DataType != ResValue.TypeReference) return null;
        var values = table.ResolveAll(attr.Data);
        var color = values.FirstOrDefault(v => v.Value.IsColor);
        if (color.Value is not null) return Solid(Argb(color.Value.Data), size);

        var files = values.Where(v => v.Value.DataType == ResValue.TypeString)
            .Select(v => (v.Density, Path: table.GlobalString(v.Value.Data) ?? ""))
            .Where(f => f.Path.Length > 0)
            .OrderByDescending(f => f.Density is > 0 and <= 640 ? f.Density : (f.Density == 0 ? 160 : -1))
            .ToList();
        foreach (var f in files.Where(f => !f.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            byte[]? data = ReadEntry(zip, f.Path);
            if (data is null) continue;
            using SKBitmap? decoded = ImageLoader.Decode(data);
            if (decoded is null) continue;
            SKBitmap scaled = ImageLoader.NewBitmap(size, size);
            using var g = new SKCanvas(scaled);
            ImageLoader.Draw(g, decoded, new SKRect(0, 0, size, size));
            return scaled;
        }
        foreach (var f in files.Where(f => f.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            byte[]? data = ReadEntry(zip, f.Path);
            if (data is not null && RenderXml(zip, table, data, size, depth + 1) is { } bmp) return bmp;
        }
        return null;
    }

    private static SKBitmap Solid(SKColor color, int size)
    {
        SKBitmap bmp = ImageLoader.NewBitmap(size, size);
        bmp.Erase(color);
        return bmp;
    }

    private static SKBitmap? RenderAdaptive(ZipArchive zip, ResourceTable table, Node root, int size, int depth)
    {
        // Layers are 108dp, the visible area the inner 72dp – render the full layers, then crop and round
        int full = (int)Math.Round(size * 108.0 / 72.0);
        SKBitmap? Layer(string name)
        {
            Node? n = root.Children.FirstOrDefault(c => c.Element.Name == name);
            if (n is null) return null;
            XmlAttribute? attr = n.Element.Attr(ADrawable, "drawable");
            if (attr is not null) return RenderReference(zip, table, attr, full, depth + 1);
            // inline drawable as child element is not supported (rare)
            return null;
        }
        using SKBitmap? bg = Layer("background");
        using SKBitmap? fg = Layer("foreground");
        if (fg is null && bg is null) return null;
        SKBitmap output = ImageLoader.NewBitmap(size, size);
        using var g = new SKCanvas(output);
        ImageLoader.ClipIcon(g, size);
        int offset = (full - size) / 2;
        var src = SKRect.Create(offset, offset, size, size);
        var dest = new SKRect(0, 0, size, size);
        if (bg is not null) ImageLoader.Draw(g, bg, dest, src);
        if (fg is not null) ImageLoader.Draw(g, fg, dest, src);
        return output;
    }

    private static SKBitmap? RenderLayers(ZipArchive zip, ResourceTable table, Node root, int size, int depth)
    {
        SKBitmap output = ImageLoader.NewBitmap(size, size);
        bool any = false;
        using (var g = new SKCanvas(output))
        {
            foreach (Node item in root.Children.Where(c => c.Element.Name == "item"))
            {
                using SKBitmap? layer = RenderReference(zip, table, item.Element.Attr(ADrawable, "drawable"), size, depth + 1);
                if (layer is null) continue;
                ImageLoader.Draw(g, layer, new SKRect(0, 0, size, size));
                any = true;
            }
        }
        if (any) return output;
        output.Dispose();
        return null;
    }

    private static SKBitmap? RenderInset(ZipArchive zip, ResourceTable table, Node root, int size, int depth) =>
        RenderReference(zip, table, root.Element.Attr(ADrawable, "drawable"), size, depth + 1);

    private static SKBitmap? RenderShape(ZipArchive zip, ResourceTable table, Node root, int size)
    {
        Node? solid = root.Children.FirstOrDefault(c => c.Element.Name == "solid");
        SKColor? c = solid is null ? null : ColorOf(table, solid.Element.Attr(AColor, "color"));
        return c is { } color ? Solid(color, size) : null;
    }

    // ------------------------------------------------------------------ vector

    private static SKBitmap? RenderVector(ZipArchive zip, ResourceTable table, Node root, int size)
    {
        float vw = Float(root.Element.Attr(AViewportWidth, "viewportWidth"), 24f);
        float vh = Float(root.Element.Attr(AViewportHeight, "viewportHeight"), 24f);
        if (vw <= 0 || vh <= 0) return null;
        SKBitmap bmp = ImageLoader.NewBitmap(size, size);
        using var g = new SKCanvas(bmp);
        float scale = Math.Min(size / vw, size / vh);
        SKMatrix m = SKMatrix.CreateTranslation((size - vw * scale) / 2f, (size - vh * scale) / 2f)
            .PreConcat(SKMatrix.CreateScale(scale, scale));
        float alpha = Float(root.Element.Attr(AAlpha, "alpha"), 1f);
        DrawChildren(g, table, root, m, alpha);
        return bmp;
    }

    private static void DrawChildren(SKCanvas g, ResourceTable table, Node parent, SKMatrix transform, float alpha)
    {
        foreach (Node child in parent.Children)
        {
            XmlElement e = child.Element;
            if (e.Name == "group")
            {
                float px = Float(e.Attr(APivotX, "pivotX"), 0), py = Float(e.Attr(APivotY, "pivotY"), 0);
                SKMatrix m = transform
                    .PreConcat(SKMatrix.CreateTranslation(Float(e.Attr(ATranslateX, "translateX"), 0) + px, Float(e.Attr(ATranslateY, "translateY"), 0) + py))
                    .PreConcat(SKMatrix.CreateRotationDegrees(Float(e.Attr(ARotation, "rotation"), 0)))
                    .PreConcat(SKMatrix.CreateScale(Float(e.Attr(AScaleX, "scaleX"), 1), Float(e.Attr(AScaleY, "scaleY"), 1)))
                    .PreConcat(SKMatrix.CreateTranslation(-px, -py));
                DrawChildren(g, table, child, m, alpha);
            }
            else if (e.Name == "path")
            {
                string? data = e.Attr(APathData, "pathData")?.AsString(table);
                if (string.IsNullOrWhiteSpace(data)) continue;
                using SKPath? path = PathParser.Parse(data);
                if (path is null) continue;
                path.FillType = (int)Float(e.Attr(AFillType, "fillType"), 0) == 1 ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
                path.Transform(transform);

                float fillAlpha = Float(e.Attr(AFillAlpha, "fillAlpha"), 1f) * alpha;
                using (SKPaint? fill = PaintFor(table, child, e.Attr(AFillColor, "fillColor"), "fillColor", transform, fillAlpha))
                    if (fill is not null) g.DrawPath(path, fill);

                float width = Float(e.Attr(AStrokeWidth, "strokeWidth"), 0);
                if (width > 0)
                {
                    float strokeAlpha = Float(e.Attr(AStrokeAlpha, "strokeAlpha"), 1f) * alpha;
                    using SKPaint? pen = PaintFor(table, child, e.Attr(AStrokeColor, "strokeColor"), "strokeColor", transform, strokeAlpha);
                    if (pen is not null)
                    {
                        float scale = (float)Math.Sqrt(Math.Abs(transform.ScaleX * transform.ScaleY - transform.SkewX * transform.SkewY));
                        pen.Style = SKPaintStyle.Stroke;
                        pen.StrokeWidth = width * scale;
                        int cap = (int)Float(e.Attr(AStrokeLineCap, "strokeLineCap"), 0);
                        pen.StrokeCap = cap switch { 1 => SKStrokeCap.Round, 2 => SKStrokeCap.Square, _ => SKStrokeCap.Butt };
                        int join = (int)Float(e.Attr(AStrokeLineJoin, "strokeLineJoin"), 0);
                        pen.StrokeJoin = join switch { 1 => SKStrokeJoin.Round, 2 => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter };
                        g.DrawPath(path, pen);
                    }
                }
            }
            else if (e.Name == "clip-path")
            {
                // clip paths are ignored (rarely relevant for icons)
            }
        }
    }

    private static SKPaint SolidPaint(SKColor c) => new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = c };

    /// <summary>Solid colour (attribute, colour resource) or an inline &lt;aapt:attr&gt; gradient.</summary>
    private static SKPaint? PaintFor(ResourceTable table, Node pathNode, XmlAttribute? attr, string name, SKMatrix transform, float alpha)
    {
        if (ColorOf(table, attr) is { } c)
            return c.Alpha == 0 ? null : SolidPaint(WithAlpha(c, alpha));

        Node? aapt = pathNode.Children.FirstOrDefault(n => n.Element.Name == "attr" &&
            (n.Element.Attributes.FirstOrDefault(a => a.Name == "name")?.AsString(table) ?? "").EndsWith(name));
        Node? gradient = aapt?.Children.FirstOrDefault(n => n.Element.Name == "gradient");
        if (gradient is null) return null;

        XmlElement ge = gradient.Element;
        var stops = gradient.Children.Where(n => n.Element.Name == "item")
            .Select(n => (Offset: Float(n.Element.Attr(AOffset, "offset"), 0), Color: ColorOf(table, n.Element.Attr(AColor, "color"))))
            .Where(s => s.Color is not null).Select(s => (s.Offset, Color: s.Color!.Value)).OrderBy(s => s.Offset).ToList();
        if (stops.Count == 0)
        {
            if (ColorOf(table, ge.Attr(AStartColor, "startColor")) is { } s0) stops.Add((0f, s0));
            if (ColorOf(table, ge.Attr(ACenterColor, "centerColor")) is { } s1) stops.Add((0.5f, s1));
            if (ColorOf(table, ge.Attr(AEndColor, "endColor")) is { } s2) stops.Add((1f, s2));
        }
        if (stops.Count == 0) return null;
        if (stops.Count == 1) return SolidPaint(WithAlpha(stops[0].Color, alpha));

        int type = (int)Float(ge.Attributes.FirstOrDefault(a => a.Name == "type"), 0);
        if (type == 0)   // linear
        {
            SKPoint p0 = transform.MapPoint(Float(ge.Attr(AStartX, "startX"), 0), Float(ge.Attr(AStartY, "startY"), 0));
            SKPoint p1 = transform.MapPoint(Float(ge.Attr(AEndX, "endX"), 1), Float(ge.Attr(AEndY, "endY"), 1));
            if (p0 == p1) return SolidPaint(WithAlpha(stops[^1].Color, alpha));
            var colors = new List<SKColor>();
            var positions = new List<float>();
            foreach (var s in stops) { colors.Add(WithAlpha(s.Color, alpha)); positions.Add(Math.Clamp(s.Offset, 0f, 1f)); }
            for (int i = 1; i < positions.Count; i++) if (positions[i] < positions[i - 1]) positions[i] = positions[i - 1];
            SKPaint paint = SolidPaint(SKColors.Black);
            paint.Shader = SKShader.CreateLinearGradient(p0, p1, colors.ToArray(), positions.ToArray(), SKShaderTileMode.Clamp);
            return paint;
        }
        // radial / sweep: average colour is a good enough approximation for a small icon
        int r = 0, gr = 0, b = 0, a = 0;
        foreach (var s in stops) { r += s.Color.Red; gr += s.Color.Green; b += s.Color.Blue; a += s.Color.Alpha; }
        int n = stops.Count;
        return SolidPaint(WithAlpha(new SKColor((byte)(r / n), (byte)(gr / n), (byte)(b / n), (byte)(a / n)), alpha));
    }

    private static SKColor? ColorOf(ResourceTable table, XmlAttribute? attr)
    {
        if (attr is null) return null;
        if (attr.IsColor) return Argb(attr.Data);
        if (attr.DataType == ResValue.TypeReference)
        {
            var v = table.ResolveAll(attr.Data).FirstOrDefault(x => x.Value.IsColor);
            if (v.Value is not null) return Argb(v.Value.Data);
        }
        if (attr.RawString is { } s && s.StartsWith('#') && SKColor.TryParse(s, out SKColor parsed))
            return parsed;   // #RGB, #ARGB, #RRGGBB, #AARRGGBB – same order as Android
        return null;
    }

    internal static SKColor Argb(uint data) => new(data);
    private static SKColor WithAlpha(SKColor c, float alpha) => c.WithAlpha((byte)Math.Clamp(c.Alpha * alpha, 0, 255));

    private static float Float(XmlAttribute? attr, float fallback)
    {
        if (attr is null) return fallback;
        switch (attr.DataType)
        {
            case ResValue.TypeFloat: return BitConverter.Int32BitsToSingle(unchecked((int)attr.Data));
            case ResValue.TypeIntDec: case ResValue.TypeIntHex: return unchecked((int)attr.Data);
            case ResValue.TypeIntBoolean: return attr.Data != 0 ? 1 : 0;
            case 0x05: // dimension: value in the upper 24 bits (radix ignored for icons)
                return (attr.Data >> 8) * ((attr.Data >> 4 & 3) switch { 0 => 1f, 1 => 1f / 128, 2 => 1f / 32768, _ => 1f / 8388608 });
            case 0x06: // fraction
                return (attr.Data >> 8) * ((attr.Data >> 4 & 3) switch { 0 => 1f, 1 => 1f / 128, 2 => 1f / 32768, _ => 1f / 8388608 });
        }
        return float.TryParse(attr.RawString, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
    }

    private static byte[]? ReadEntry(ZipArchive zip, string name)
    {
        ZipArchiveEntry? entry = zip.GetEntry(name);
        if (entry is null || entry.Length > 16L * 1024 * 1024) return null;
        using Stream s = entry.Open();
        using var ms = new MemoryStream((int)Math.Max(0, entry.Length));
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>SVG/Android path data ("M0,0h108v108h-108z", arcs included) → SKPath.</summary>
internal static class PathParser
{
    public static SKPath? Parse(string data)
    {
        var path = new SKPath();
        int i = 0;
        char cmd = ' ';
        float cx = 0, cy = 0, sx = 0, sy = 0;     // current point, sub-path start
        float lcx = 0, lcy = 0;                    // last control point (for S/T)
        char last = ' ';
        bool open = false;

        float Num()
        {
            while (i < data.Length && (char.IsWhiteSpace(data[i]) || data[i] == ',')) i++;
            int start = i;
            if (i < data.Length && data[i] is '-' or '+') i++;
            bool dot = false, exp = false;
            while (i < data.Length)
            {
                char c = data[i];
                if (char.IsDigit(c)) { i++; continue; }
                if (c == '.' && !dot && !exp) { dot = true; i++; continue; }
                if ((c == 'e' || c == 'E') && !exp) { exp = true; i++; if (i < data.Length && data[i] is '-' or '+') i++; continue; }
                break;
            }
            return float.Parse(data.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        float Flag()
        {
            while (i < data.Length && (char.IsWhiteSpace(data[i]) || data[i] == ',')) i++;
            char c = data[i++];
            return c == '1' ? 1 : 0;
        }

        try
        {
            while (i < data.Length)
            {
                while (i < data.Length && (char.IsWhiteSpace(data[i]) || data[i] == ',')) i++;
                if (i >= data.Length) break;
                if (char.IsLetter(data[i]) && data[i] is not ('e' or 'E')) cmd = data[i++];
                else if (cmd == ' ') return null;
                bool rel = char.IsLower(cmd);
                char up = char.ToUpperInvariant(cmd);
                switch (up)
                {
                    case 'M':
                    {
                        float x = Num(), y = Num();
                        if (rel) { x += cx; y += cy; }
                        if (open) path.Close();         // Android keeps sub-paths; a new M starts a new figure
                        path.MoveTo(x, y);
                        cx = sx = x; cy = sy = y; open = true;
                        cmd = rel ? 'l' : 'L';          // following pairs are line-tos
                        break;
                    }
                    case 'L':
                    {
                        float x = Num(), y = Num();
                        if (rel) { x += cx; y += cy; }
                        path.LineTo(x, y); cx = x; cy = y;
                        break;
                    }
                    case 'H':
                    {
                        float x = Num(); if (rel) x += cx;
                        path.LineTo(x, cy); cx = x;
                        break;
                    }
                    case 'V':
                    {
                        float y = Num(); if (rel) y += cy;
                        path.LineTo(cx, y); cy = y;
                        break;
                    }
                    case 'C':
                    {
                        float x1 = Num(), y1 = Num(), x2 = Num(), y2 = Num(), x = Num(), y = Num();
                        if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                        path.CubicTo(x1, y1, x2, y2, x, y);
                        lcx = x2; lcy = y2; cx = x; cy = y;
                        break;
                    }
                    case 'S':
                    {
                        float x2 = Num(), y2 = Num(), x = Num(), y = Num();
                        if (rel) { x2 += cx; y2 += cy; x += cx; y += cy; }
                        float x1 = cx, y1 = cy;
                        if (char.ToUpperInvariant(last) is 'C' or 'S') { x1 = 2 * cx - lcx; y1 = 2 * cy - lcy; }
                        path.CubicTo(x1, y1, x2, y2, x, y);
                        lcx = x2; lcy = y2; cx = x; cy = y;
                        break;
                    }
                    case 'Q':
                    {
                        float qx = Num(), qy = Num(), x = Num(), y = Num();
                        if (rel) { qx += cx; qy += cy; x += cx; y += cy; }
                        Quad(path, cx, cy, qx, qy, x, y);
                        lcx = qx; lcy = qy; cx = x; cy = y;
                        break;
                    }
                    case 'T':
                    {
                        float x = Num(), y = Num();
                        if (rel) { x += cx; y += cy; }
                        float qx = cx, qy = cy;
                        if (char.ToUpperInvariant(last) is 'Q' or 'T') { qx = 2 * cx - lcx; qy = 2 * cy - lcy; }
                        Quad(path, cx, cy, qx, qy, x, y);
                        lcx = qx; lcy = qy; cx = x; cy = y;
                        break;
                    }
                    case 'A':
                    {
                        float rx = Num(), ry = Num(), rot = Num(), large = Flag(), sweep = Flag(), x = Num(), y = Num();
                        if (rel) { x += cx; y += cy; }
                        Arc(path, cx, cy, rx, ry, rot, large != 0, sweep != 0, x, y);
                        cx = x; cy = y;
                        break;
                    }
                    case 'Z':
                        path.Close();
                        cx = sx; cy = sy; open = false;
                        break;
                    default:
                        return path.PointCount > 0 ? path : null;
                }
                last = up == 'M' ? 'M' : cmd;
            }
        }
        catch
        {
            // malformed data: keep what was parsed
        }
        if (path.PointCount == 0) { path.Dispose(); return null; }
        return path;
    }

    private static void Quad(SKPath path, float x0, float y0, float qx, float qy, float x, float y) =>
        path.QuadTo(qx, qy, x, y);

    /// <summary>Endpoint arc (SVG) as cubic Béziers.</summary>
    private static void Arc(SKPath path, float x1, float y1, float rx, float ry, float angle, bool large, bool sweep, float x2, float y2)
    {
        if (rx == 0 || ry == 0 || (x1 == x2 && y1 == y2)) { path.LineTo(x2, y2); return; }
        double phi = angle * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (x1 - x2) / 2.0, dy = (y1 - y2) / 2.0;
        double x1p = cos * dx + sin * dy, y1p = -sin * dx + cos * dy;
        double arx = Math.Abs(rx), ary = Math.Abs(ry);
        double lambda = x1p * x1p / (arx * arx) + y1p * y1p / (ary * ary);
        if (lambda > 1) { arx *= Math.Sqrt(lambda); ary *= Math.Sqrt(lambda); }
        double num = arx * arx * ary * ary - arx * arx * y1p * y1p - ary * ary * x1p * x1p;
        double den = arx * arx * y1p * y1p + ary * ary * x1p * x1p;
        double coef = (large != sweep ? 1 : -1) * Math.Sqrt(Math.Max(0, num / den));
        double cxp = coef * arx * y1p / ary, cyp = -coef * ary * x1p / arx;
        double ccx = cos * cxp - sin * cyp + (x1 + x2) / 2.0, ccy = sin * cxp + cos * cyp + (y1 + y2) / 2.0;
        double Ang(double ux, double uy, double vx, double vy)
        {
            double a = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            return a;
        }
        double t1 = Ang(1, 0, (x1p - cxp) / arx, (y1p - cyp) / ary);
        double dt = Ang((x1p - cxp) / arx, (y1p - cyp) / ary, (-x1p - cxp) / arx, (-y1p - cyp) / ary);
        if (!sweep && dt > 0) dt -= 2 * Math.PI;
        else if (sweep && dt < 0) dt += 2 * Math.PI;
        int segments = (int)Math.Ceiling(Math.Abs(dt) / (Math.PI / 2));
        double delta = dt / segments;
        double k = 4.0 / 3 * Math.Tan(delta / 4);
        double t = t1;
        double px = x1, py = y1;
        for (int s = 0; s < segments; s++)
        {
            double c1 = Math.Cos(t), s1 = Math.Sin(t), c2 = Math.Cos(t + delta), s2 = Math.Sin(t + delta);
            double ex1 = c1 - k * s1, ey1 = s1 + k * c1, ex2 = c2 + k * s2, ey2 = s2 - k * c2;
            (double X, double Y) Map(double ux, double uy) => (ccx + arx * ux * cos - ary * uy * sin, ccy + arx * ux * sin + ary * uy * cos);
            var p1 = Map(ex1, ey1); var p2 = Map(ex2, ey2); var p3 = Map(c2, s2);
            path.CubicTo((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, (float)p3.X, (float)p3.Y);
            px = p3.X; py = p3.Y; t += delta;
        }
    }
}
