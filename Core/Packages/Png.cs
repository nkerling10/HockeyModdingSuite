using System.Buffers.Binary;
using System.IO.Compression;

namespace NHL27Kit.Core.Packages;

/// <summary>
/// A small PNG reader for Core (the app reads images through WPF, which the CLI and an installer don't have).
/// Non-interlaced PNGs, every colour type, bit depths 1-16; the result is RGBA8.
/// </summary>
public static class Png
{
    public static (byte[] Rgba, int W, int H) Load(string file) => Decode(File.ReadAllBytes(file), file);

    public static (byte[] Rgba, int W, int H) Decode(byte[] png, string what = "image")
    {
        ReadOnlySpan<byte> sig = stackalloc byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(sig)) throw new InvalidDataException($"{what} isn't a PNG.");
        int w = 0, h = 0, depth = 0, type = 0, interlace = 0;
        byte[] palette = null, trns = null;
        using var idat = new MemoryStream();
        int p = 8;
        while (p + 8 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p));
            string kind = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
            var data = png.AsSpan(p + 8, len);
            switch (kind)
            {
                case "IHDR":
                    w = BinaryPrimitives.ReadInt32BigEndian(data); h = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    depth = data[8]; type = data[9]; interlace = data[12];
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": trns = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
            }
            if (kind == "IEND") break;
            p += 12 + len;
        }
        if (w <= 0 || h <= 0) throw new InvalidDataException($"{what}: no image header.");
        if (interlace != 0) throw new NotSupportedException($"{what} is an interlaced PNG; save it without interlacing.");
        int channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new NotSupportedException($"{what}: PNG colour type {type}.") };
        int bpp = Math.Max(1, channels * depth / 8);          // bytes per pixel for filtering
        int stride = (w * channels * depth + 7) / 8;

        idat.Position = 0;
        var raw = new byte[(stride + 1) * h];
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int got = 0;
            while (got < raw.Length)
            {
                int n = z.Read(raw, got, raw.Length - got);
                if (n == 0) throw new InvalidDataException($"{what}: the image data ends early.");
                got += n;
            }
        }

        // undo the per-row filters
        var cur = new byte[stride]; var prev = new byte[stride];
        var rows = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            int f = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                cur[i] = f switch
                {
                    0 => cur[i],
                    1 => (byte)(cur[i] + a),
                    2 => (byte)(cur[i] + b),
                    3 => (byte)(cur[i] + ((a + b) >> 1)),
                    4 => (byte)(cur[i] + Paeth(a, b, c)),
                    _ => throw new InvalidDataException($"{what}: bad row filter {f}."),
                };
            }
            Array.Copy(cur, 0, rows, y * stride, stride);
            (cur, prev) = (prev, cur);
        }

        // to RGBA8
        var rgba = new byte[w * h * 4];
        int Sample(int y, int x, int ch)
        {
            int bit = (x * channels + ch) * depth;
            int row = y * stride;
            return depth switch
            {
                16 => rows[row + bit / 8],                                        // high byte
                8 => rows[row + bit / 8],
                _ => (rows[row + bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1),
            };
        }
        int Scale(int v) => depth switch { 1 => v * 255, 2 => v * 85, 4 => v * 17, _ => v };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                switch (type)
                {
                    case 0:
                    {
                        int g = Scale(Sample(y, x, 0));
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = (byte)g; rgba[o + 3] = 255;
                        if (trns is { Length: >= 2 } && Sample(y, x, 0) == (depth == 16 ? trns[0] : trns[1])) rgba[o + 3] = 0;
                        break;
                    }
                    case 2:
                        rgba[o] = (byte)Sample(y, x, 0); rgba[o + 1] = (byte)Sample(y, x, 1); rgba[o + 2] = (byte)Sample(y, x, 2); rgba[o + 3] = 255;
                        break;
                    case 3:
                    {
                        int i = Sample(y, x, 0);
                        if (palette == null || i * 3 + 2 >= palette.Length) throw new InvalidDataException($"{what}: palette index out of range.");
                        rgba[o] = palette[i * 3]; rgba[o + 1] = palette[i * 3 + 1]; rgba[o + 2] = palette[i * 3 + 2];
                        rgba[o + 3] = trns != null && i < trns.Length ? trns[i] : (byte)255;
                        break;
                    }
                    case 4:
                    {
                        byte g = (byte)Sample(y, x, 0);
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = g; rgba[o + 3] = (byte)Sample(y, x, 1);
                        break;
                    }
                    case 6:
                        rgba[o] = (byte)Sample(y, x, 0); rgba[o + 1] = (byte)Sample(y, x, 1); rgba[o + 2] = (byte)Sample(y, x, 2); rgba[o + 3] = (byte)Sample(y, x, 3);
                        break;
                }
            }
        return (rgba, w, h);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>
    /// Scales an RGBA8 image to fit inside <paramref name="tw"/> x <paramref name="th"/> (keeping its shape) and centres it on
    /// a transparent canvas of that size.
    /// </summary>
    public static byte[] Fit(byte[] rgba, int w, int h, int tw, int th, bool srgb)
    {
        double s = Math.Min((double)tw / w, (double)th / h);
        int nw = Math.Max(1, (int)Math.Round(w * s)), nh = Math.Max(1, (int)Math.Round(h * s));
        var scaled = MipChain.Resize(rgba, w, h, nw, nh, srgb);
        var dst = new byte[tw * th * 4];
        int ox = (tw - nw) / 2, oy = (th - nh) / 2;
        for (int y = 0; y < nh; y++)
            Array.Copy(scaled, y * nw * 4, dst, ((oy + y) * tw + ox) * 4, nw * 4);
        return dst;
    }

    /// <summary>A white silhouette of the image (its alpha kept): the one-colour logo styles.</summary>
    public static byte[] Silhouette(byte[] rgba)
    {
        var d = (byte[])rgba.Clone();
        for (int i = 0; i < d.Length; i += 4) { d[i] = d[i + 1] = d[i + 2] = 255; }
        return d;
    }
}
