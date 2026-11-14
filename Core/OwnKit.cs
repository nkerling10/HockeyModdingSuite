using System.Text.RegularExpressions;

namespace NHL27Kit.Core;

/// <summary>
/// A uniform of your own, made from the game's create-a-team parts (league/custom): jersey designs, lettering fonts,
/// pants and socks. Each part is a dark detail layer (…_color; socks have none) and recolor masks whose R, G and B are
/// colour zones, like the jerseys (<see cref="JerseyStudio"/>). Pants and socks use the common meshes every kit uses and
/// the fonts the layout every kit's font uses, so the parts fit any kit folder.
/// </summary>
public static class OwnKit
{
    private static readonly Regex MaskName = new(@"league/custom/(font|pant|sock)/(\w+)/custom_(\d+)/\1_\2_custom_\3_recolor(\d)$", RegexOptions.IgnoreCase);

    /// <summary>Every create-a-team font, pant or sock design in the game ("font", "pant", "sock"), by number.</summary>
    public static List<StudioTemplate> Parts(AssetIndex index, string garment)
    {
        var found = new Dictionary<(string, int), SortedDictionary<int, string>>();
        foreach (var (name, e) in index.Ebx)
        {
            if (e.Type != "TextureAsset" || !name.Contains($"/custom/{garment}/", StringComparison.OrdinalIgnoreCase)) continue;
            var m = MaskName.Match(name);
            if (!m.Success) continue;
            var key = (m.Groups[2].Value.ToLowerInvariant(), int.Parse(m.Groups[3].Value));
            if (!found.TryGetValue(key, out var masks)) found[key] = masks = new();
            masks[int.Parse(m.Groups[4].Value)] = name;
        }
        return found.Select(f =>
            {
                string folder = f.Value.Values.First()[..f.Value.Values.First().LastIndexOf('/')];
                string color = $"{folder}/{garment}_{f.Key.Item1}_custom_{f.Key.Item2}_color";
                return new StudioTemplate(f.Key.Item1, f.Key.Item2, index.GetEbx(color) != null ? color : null, f.Value.Values.ToList(), garment);
            })
            .OrderBy(t => t.Number).ToList();
    }

    /// <summary>A part's normal or coeff map ("normal", "coeff"), null when it has none.</summary>
    public static string Map(AssetIndex index, StudioTemplate t, string kind)
    {
        string name = $"{t.Folder}/{t.Garment}_{t.Brand}_custom_{t.Number}_{kind}";
        return index.GetEbx(name) != null ? name : null;
    }

    /// <summary>What a font zone paints: its ring (0 the fill, 1 the outline round it, 2 the outer outline round that)
    /// and whether it belongs to the name letters (the sheet's top <see cref="LetterRows"/>) rather than the numbers.</summary>
    public sealed record FontRole(int Ring, bool Name);

    /// <summary>A font's lettering colours. The name's (null: the numbers'), and the outer ring's for the fonts that
    /// have a third ring (null: the outline's).</summary>
    public sealed record FontColors(int Fill, int Outline, int? Outer = null, int? NameFill = null, int? NameOutline = null);

    /// <summary>
    /// Each zone's role in a font: the zones are read inside the glyphs only (the masks' alpha). Of two zones that
    /// meet, the one lying deeper in the glyph (further from its edge, on average) is the inner; a zone's ring is how
    /// many zones lie inside it (0 the fill, 1 its outline, 2 an outer outline). A zone mostly in the top rows is the
    /// name's.
    /// </summary>
    public static Dictionary<int, FontRole> Roles(StudioPattern font)
    {
        int size = font.Size, n = size * size, rows = LetterRows(size);
        int zoneCount = font.Masks.Count * 3;
        var inside = new bool[n];
        for (int i = 0; i < n; i++) inside[i] = font.Detail[i * 4 + 3] >= 128;
        // each glyph texel's zone: its strongest channel (faint lines too); -1 for none
        var zoneOf = new sbyte[n];
        Parallel.For(0, size, y =>
        {
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x, best = -1, bestV = 63;
                if (inside[i])
                    foreach (var (zone, _) in font.Zones)
                    {
                        int v = font.Masks[zone / 3][i * 4 + zone % 3];
                        if (v > bestV) { bestV = v; best = zone; }
                    }
                zoneOf[i] = (sbyte)best;
            }
        });
        var depth = Depth(inside, size);
        var count = new long[zoneCount];
        var top = new long[zoneCount];
        var depthSum = new double[zoneCount];
        var border = new long[zoneCount];
        var touch = new long[zoneCount, zoneCount];
        var steps = new[] { (2, 0), (0, 2), (2, 2), (-2, 2) };
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = y * size + x, z = zoneOf[i];
            if (z >= 0) { count[z]++; depthSum[z] += depth[i]; if (y < rows) top[z]++; }
            foreach (var (dx, dy) in steps)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= size || yy >= size) continue;
                int j = yy * size + xx, w = zoneOf[j];
                if (z == w) continue;
                if (z >= 0) border[z]++;
                if (w >= 0) border[w]++;
                if (z >= 0 && w >= 0) { touch[z, w]++; touch[w, z]++; }
            }
        }
        long least = Math.Max(8, n / 50000);
        var zones = Enumerable.Range(0, zoneCount).Where(z => count[z] >= least).ToList();
        double Mean(int z) => depthSum[z] / count[z];
        // the zones lying inside each zone (they meet along a good share of the smaller one's edge, and lie deeper)
        var inner = zones.ToDictionary(z => z, z => zones.Where(a => a != z && touch[z, a] >= Math.Max(4, 0.05 * Math.Min(border[z], border[a])) && Mean(a) > Mean(z)).ToList());
        var ring = new Dictionary<int, int>();
        int Ring(int z, HashSet<int> seen)
        {
            if (ring.TryGetValue(z, out int r)) return r;
            seen.Add(z);
            r = 0;
            foreach (int a in inner[z]) if (!seen.Contains(a)) r = Math.Max(r, 1 + Ring(a, seen));
            seen.Remove(z);
            return ring[z] = r;
        }
        return zones.ToDictionary(z => z, z => new FontRole(Math.Min(2, Ring(z, new HashSet<int>())), top[z] * 2 > count[z]));
    }

    /// <summary>Each texel's distance (in texels, 3-4 chamfer) from the nearest texel outside the glyphs.</summary>
    private static float[] Depth(bool[] inside, int size)
    {
        var d = new float[size * size];
        const float big = 1e9f;
        for (int i = 0; i < d.Length; i++) d[i] = inside[i] ? big : 0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = y * size + x;
            if (d[i] == 0) continue;
            float v = d[i];
            if (x > 0) v = Math.Min(v, d[i - 1] + 3);
            if (y > 0)
            {
                v = Math.Min(v, d[i - size] + 3);
                if (x > 0) v = Math.Min(v, d[i - size - 1] + 4);
                if (x < size - 1) v = Math.Min(v, d[i - size + 1] + 4);
            }
            d[i] = v;
        }
        for (int y = size - 1; y >= 0; y--)
        for (int x = size - 1; x >= 0; x--)
        {
            int i = y * size + x;
            if (d[i] == 0) continue;
            float v = d[i];
            if (x < size - 1) v = Math.Min(v, d[i + 1] + 3);
            if (y < size - 1)
            {
                v = Math.Min(v, d[i + size] + 3);
                if (x < size - 1) v = Math.Min(v, d[i + size + 1] + 4);
                if (x > 0) v = Math.Min(v, d[i + size - 1] + 4);
            }
            d[i] = v;
        }
        for (int i = 0; i < d.Length; i++) d[i] = d[i] >= big ? size : d[i] / 3;
        return d;
    }

    /// <summary>
    /// Loads a font design: its glyphs (numbers, the name letters in the top <see cref="LetterRows"/>, the captain's
    /// letters) are the masks' alpha, and the masks' channels are the colour zones: fills, outlines and the
    /// backgrounds round them (which the alpha cuts down to an outer ring). The detail's alpha is the glyphs.
    /// </summary>
    public static StudioPattern LoadFont(AssetIndex index, StudioTemplate t, int size)
    {
        var p = JerseyStudio.LoadPattern(index, t, size);
        var detail = new byte[size * size * 4];
        bool masked = false;
        for (int i = 0; i < size * size; i++)
        {
            int a = 0;
            foreach (var m in p.Masks) a = Math.Max(a, m[i * 4 + 3]);
            detail[i * 4 + 3] = (byte)a;
            if (a < 128) masked = true;
        }
        // masks with no alpha: the colour sheet's
        if (!masked && t.Color != null)
            for (int i = 0; i < size * size; i++) detail[i * 4 + 3] = p.Detail[i * 4 + 3];
        return new StudioPattern { Template = t, Size = size, Detail = detail, Masks = p.Masks, Zones = p.Zones };
    }

    /// <summary>How many rows of a font sheet <paramref name="size"/> square the name letters take (two rows of 192 in 2048).</summary>
    public static int LetterRows(int size) => size * 384 / 2048;

    /// <summary>A font's normal or coeff map at its full size.</summary>
    public static (byte[] Rgba, int Width, int Height)? FontMap(AssetIndex index, StudioTemplate t, string kind) =>
        Map(index, t, kind) is { } name && Kits.Load(index, name, 4096, 0) is { Rgba: not null } m ? (m.Rgba, m.Width, m.Height) : null;

    /// <summary>
    /// A font painted in <paramref name="colors"/>: each zone in its ring's colour (the name's zones in the name's),
    /// blended in by its mask over the fill; the alpha is the glyphs'.
    /// </summary>
    public static byte[] PaintFont(StudioPattern font, Dictionary<int, FontRole> roles, FontColors colors)
    {
        int size = font.Size, rows = LetterRows(size);
        int Color(FontRole r) => (r.Name, r.Ring) switch
        {
            (false, 0) => colors.Fill,
            (false, 1) => colors.Outline,
            (false, _) => colors.Outer ?? colors.Outline,
            (true, 0) => colors.NameFill ?? colors.Fill,
            (true, 1) => colors.NameOutline ?? colors.Outline,
            _ => colors.Outer ?? colors.NameOutline ?? colors.Outline,
        };
        var zones = roles.OrderBy(r => r.Key).Select(r => (Zone: r.Key, Color: Color(r.Value))).ToList();
        var img = new byte[size * size * 4];
        Parallel.For(0, size, y =>
        {
            int start = y < rows ? colors.NameFill ?? colors.Fill : colors.Fill;
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                double r = start >> 16 & 255, g = start >> 8 & 255, b = start & 255;
                foreach (var (zone, c) in zones)
                {
                    double w = font.Masks[zone / 3][o + zone % 3] / 255.0;
                    if (w <= 0) continue;
                    r = r * (1 - w) + (c >> 16 & 255) * w;
                    g = g * (1 - w) + (c >> 8 & 255) * w;
                    b = b * (1 - w) + (c & 255) * w;
                }
                img[o] = (byte)Math.Round(r);
                img[o + 1] = (byte)Math.Round(g);
                img[o + 2] = (byte)Math.Round(b);
                img[o + 3] = font.Detail[o + 3];
            }
        });
        return img;
    }

    /// <summary>
    /// Pants or socks painted: the big zones (a fifth of the sheet or more: the pant legs, a sock's upper and lower leg)
    /// in the main colour, the rest (stripes, panels, trims) dealt the other colours in turn; <paramref name="shift"/>
    /// moves the colours along.
    /// </summary>
    public static byte[] PaintPart(StudioPattern part, IReadOnlyList<int> palette, int shift)
    {
        var colors = new Dictionary<int, int>();
        int n = palette.Count, other = 0;
        foreach (var (zone, share) in part.Zones)
            colors[zone] = share >= 0.2 || n == 1 ? palette[shift % n] : palette[(shift + 1 + other++ % (n - 1)) % n];
        var img = JerseyStudio.Paint(part.Detail, part.Size, part, colors);
        for (int i = 3; i < img.Length; i += 4) img[i] = 255;
        return img;
    }

    /// <summary>The front band of a painted font (the back numbers 0–4), for a picker.</summary>
    public static (byte[] Rgba, int Width, int Height) FontThumb(byte[] font, int size, int width = 120, int height = 40)
    {
        var img = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int sx = Math.Min(size - 1, (int)((x + 0.5) / width * 1600 / 2048 * size));
            int sy = Math.Min(size - 1, (int)((448 + (y + 0.5) / height * 512) / 2048 * size));
            int o = (sy * size + sx) * 4, d = (y * width + x) * 4;
            double a = font[o + 3] / 255.0;
            for (int c = 0; c < 3; c++) img[d + c] = (byte)(font[o + c] * a + 0x2A * (1 - a));
            img[d + 3] = 255;
        }
        return (img, width, height);
    }

    /// <summary>The whole sheet of a painted part, small, for a picker.</summary>
    public static (byte[] Rgba, int Width, int Height) Thumb(byte[] rgba, int size, int n = 56)
    {
        var img = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int sx = (int)((x + 0.5) / n * size), sy = (int)((y + 0.5) / n * size);
            Array.Copy(rgba, (sy * size + sx) * 4, img, (y * n + x) * 4, 4);
            img[(y * n + x) * 4 + 3] = 255;
        }
        return (img, n, n);
    }

    /// <summary>
    /// The kit whose models and lettering spots a jersey of your own borrows: an NHL adidas home kit with a stamped
    /// jersey (the cut most kits use), else any stamped kit.
    /// </summary>
    public static Kit Carrier(IEnumerable<Kit> kits)
    {
        var stamped = kits.Where(k => k.IsStamped("jersey") && k.Slot("jersey_base_color") != null).ToList();
        return stamped.FirstOrDefault(k => k.League == "nhl" && k.Brand == "adidas" && k.Name == "home")
               ?? stamped.FirstOrDefault(k => k.Brand == "adidas") ?? stamped.FirstOrDefault();
    }

    /// <summary>
    /// The studio source for a jersey of your own: the carrier's fabric map under a flat <paramref name="body"/> colour,
    /// its lettering spots (its logos left out), and <paramref name="font"/> (a painted font, <paramref name="fontSize"/> square).
    /// </summary>
    public static StudioSource Source(AssetIndex index, Kit carrier, int size, IReadOnlyList<int> palette, byte[] font, int fontSize)
    {
        var src = JerseyStudio.LoadSource(index, carrier, size);
        var b = (byte[])src.Base.Clone();
        Fill(b, palette[0]);
        // the logo spots as an NHL kit has them: the team's (main, shoulders) get a stand-in crest, the league's and maker's
        // marks keep their images, the sponsor is off
        var spots = new List<StampHotspot>();
        var logos = new Dictionary<int, (byte[] Rgba, int Width, int Height)>();
        var brands = KitStamp.Brands(src.Spots).ToDictionary(x => x.Spot, x => x.Layer);
        foreach (var s in src.Spots)
        {
            if (s.Type == "font") { spots.Add(s); continue; }
            if (!brands.TryGetValue(s, out int layer)) continue;
            switch (Role(s, layer))
            {
                case LogoRole.Main or LogoRole.Shoulder: spots.Add(s with { Enabled = true }); logos[layer] = (Crest(palette), 512, 512); break;
                case LogoRole.League when src.Logos.TryGetValue(layer, out var l): spots.Add(s); logos[layer] = l; break;
                default: spots.Add(s with { Enabled = false }); break;
            }
        }
        return new StudioSource
        {
            Kit = carrier, Size = size, Base = b, StampEbx = src.StampEbx, Spots = spots,
            Logos = logos, Font = font, FontSize = fontSize, Shape = src.Shape, Own = true,
        };
    }

    public enum LogoRole { Main, Shoulder, League, Sponsor }

    /// <summary>
    /// What a jersey stamp's logo spot is for. NHL kits: "mainlogo" (or "0") on the chest, right/left shoulder (turned 90/270),
    /// the NHL shield on the collar, the maker's mark on the back and hem, and sometimes a sponsor.
    /// </summary>
    public static LogoRole Role(StampHotspot s, int layer)
    {
        string n = s.Name.ToLowerInvariant();
        if (n.Contains("sponsor")) return LogoRole.Sponsor;
        if (n.Contains("main") || layer == 0) return LogoRole.Main;
        if (n.Contains("shoulder") || Math.Abs(s.R - 90) < 1 || Math.Abs(s.R - 270) < 1) return LogoRole.Shoulder;
        return LogoRole.League;
    }

    /// <summary>The stand-in crest (512²) on your uniform until you give it a logo: a round badge with a star, in your colours.</summary>
    public static byte[] Crest(IReadOnlyList<int> palette)
    {
        const int n = 512;
        int main = palette[0], second = palette.Count > 1 ? palette[1] : 0xFFFFFF, third = palette.Count > 2 ? palette[2] : second;
        var star = Enumerable.Range(0, 10).Select(i =>
        {
            double a = -Math.PI / 2 + i * Math.PI / 5, r = i % 2 == 0 ? 0.62 : 0.25;
            return (X: r * Math.Cos(a), Y: r * Math.Sin(a));
        }).ToArray();
        bool InStar(double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = star.Length - 1; i < star.Length; j = i++)
                if ((star[i].Y > y) != (star[j].Y > y) && x < (star[j].X - star[i].X) * (y - star[i].Y) / (star[j].Y - star[i].Y) + star[i].X) inside = !inside;
            return inside;
        }
        var img = new byte[n * n * 4];
        Parallel.For(0, n, py =>
        {
            for (int px = 0; px < n; px++)
            {
                double r = 0, g = 0, bl = 0, a = 0;
                for (int sy = 0; sy < 3; sy++)
                    for (int sx = 0; sx < 3; sx++)
                    {
                        double x = (px + (sx + 0.5) / 3) / n * 2 - 1, y = (py + (sy + 0.5) / 3) / n * 2 - 1, d = Math.Sqrt(x * x + y * y);
                        if (d > 0.98) continue;
                        int c = d > 0.86 ? third : d > 0.76 ? second : InStar(x / 0.76, y / 0.76) ? second : main;
                        r += c >> 16 & 255; g += c >> 8 & 255; bl += c & 255; a += 1;
                    }
                if (a == 0) continue;
                int o = (py * n + px) * 4;
                img[o] = (byte)(r / a); img[o + 1] = (byte)(g / a); img[o + 2] = (byte)(bl / a); img[o + 3] = (byte)(a / 9 * 255);
            }
        });
        return img;
    }

    /// <summary>Fills an image's colour, keeping its alpha (the fabric map).</summary>
    public static void Fill(byte[] rgba, int color)
    {
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (byte)(color >> 16); rgba[i + 1] = (byte)(color >> 8); rgba[i + 2] = (byte)color;
        }
    }

    /// <summary>Helmet zones (15) all in the main colour.</summary>
    public static int[] HelmetZones(IReadOnlyList<int> palette) => Enumerable.Repeat(palette[0], 15).ToArray();

    /// <summary>Glove zones (15): main, second, third colour in turn.</summary>
    public static int[] GloveZones(IReadOnlyList<int> palette) => Enumerable.Range(0, 15).Select(i => palette[i % palette.Count]).ToArray();
}
