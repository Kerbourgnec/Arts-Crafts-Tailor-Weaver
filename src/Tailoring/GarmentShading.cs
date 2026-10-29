using System.Collections.Generic;
using System.Linq;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>Single-material statistics of a set of texels (<see cref="GarmentShading.Stats"/>).</summary>
public readonly record struct MaterialStats(int Texels, double ColorSpread, double LightSpread, double Chroma, bool Single);

/// <summary>
/// Shading of garment parts from a vanilla texture (lane/garment-layers; pure, covered by tests/tailoring-selftest).
/// Owner rule: shading is extracted only within a part's mask and only where the masked vanilla texels are essentially
/// ONE material (vanilla textures often mix several, e.g. the fortune-teller scarf = cloth + gold pearls), so a part
/// opts in (<see cref="PartDef.Shade"/>) and the selftest refuses it when <see cref="Stats"/> says mixed.
/// The brightness ratio per texel is then applied to whatever material is sewn there (multiplicative darkening, plus
/// optional highlights), after the materials are painted and before any embroidery (<see cref="GarmentLayout.Compose"/>).
/// scripts/garment-segment.py mirrors <see cref="Stats"/> (same constants) to propose masks.
/// </summary>
public static class GarmentShading
{
    /// <summary>Fewer opaque texels than this: never one material (too little to tell, or to shade).</summary>
    public const int MinTexels = 8;
    /// <summary>Max colour spread: RMS distance of (a*, b*) from their mean / max(mean chroma, <see cref="ChromaFloor"/>).</summary>
    public const double MaxColorSpread = 0.30;
    /// <summary>Chroma below this counts as this much (greys: the spread is measured against a fixed scale).</summary>
    public const double ChromaFloor = 10.0;
    /// <summary>Max lightness spread: standard deviation of L* (0..100).</summary>
    public const double MaxLightSpread = 12.0;
    /// <summary>Texels with alpha below this are not part of the vanilla material (transparent).</summary>
    public const int MinAlpha = 128;
    /// <summary>Shading ratio range: darkest a texel can get; brightest with highlights.</summary>
    public const float MinRatio = 0.35f, MaxRatio = 1.6f;
    /// <summary>Reference brightness percentile without highlights (ratios above it are capped at 1).</summary>
    public const double DarkenPercentile = 0.9;

    /// <summary>CIELAB (D65) of an 0xAARRGGBB colour.</summary>
    public static (double L, double A, double B) Lab(int argb)
    {
        static double Lin(int v)
        {
            double c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : System.Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        double r = Lin((argb >> 16) & 0xff), g = Lin((argb >> 8) & 0xff), b = Lin(argb & 0xff);
        double x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
        double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        double z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        static double F(double t) => t > 0.008856 ? System.Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    /// <summary>Luma (0..255) of an 0xAARRGGBB colour.</summary>
    public static double Luma(int argb) => 0.299 * ((argb >> 16) & 0xff) + 0.587 * ((argb >> 8) & 0xff) + 0.114 * (argb & 0xff);

    /// <summary>
    /// Single-material test of opaque colours: colour spread (relative (a*, b*) RMS spread, owner: low hue/chroma
    /// variance) and lightness spread (L* standard deviation) both small, at least <see cref="MinTexels"/>.
    /// Thresholds calibrated on known vanilla textures (scripts/garment-segment-calibrate.py, docs/garments.md "Shading").
    /// </summary>
    public static MaterialStats Stats(IReadOnlyCollection<int> argb)
    {
        int n = argb.Count;
        if (n == 0) return new MaterialStats(0, 0, 0, 0, false);
        var labs = argb.Select(Lab).ToArray();
        double mL = labs.Average(v => v.L), ma = labs.Average(v => v.A), mb = labs.Average(v => v.B);
        double sab = System.Math.Sqrt(labs.Average(v => (v.A - ma) * (v.A - ma) + (v.B - mb) * (v.B - mb)));
        double sl = System.Math.Sqrt(labs.Average(v => (v.L - mL) * (v.L - mL)));
        double chroma = System.Math.Sqrt(ma * ma + mb * mb);
        double color = sab / System.Math.Max(chroma, ChromaFloor);
        return new MaterialStats(n, color, sl, chroma, n >= MinTexels && color <= MaxColorSpread && sl <= MaxLightSpread);
    }

    /// <summary>
    /// Texels (row-major garment indices) whose vanilla colour shades part <paramref name="part"/>: its coverage minus
    /// texels a higher layer covers (vanilla draws the overlay there, e.g. pearls on the scarf), inside
    /// <paramref name="src"/> (top-left aligned) and opaque there. Overlays count whether or not they are sewn on (a part left
    /// off = its none option has no texels, <see cref="GarmentDef.AllPresent"/>): the vanilla trim must never shade the bare cloth.
    /// </summary>
    public static List<int> SourceTexels(GarmentDef g, int part, MaterialTexture src)
    {
        var cov = g.Coverage;
        var over = g.AllPresent().Coverage;
        int w = g.TextureWidth, layer = g.Parts[part].Layer;
        var list = new List<int>();
        for (int i = 0; i < cov[part].Length; i++)
        {
            if (!cov[part][i]) continue;
            bool under = false;
            for (int q = 0; q < g.Parts.Length && !under; q++) under = g.Parts[q].Layer > layer && over[q][i];
            if (under) continue;
            int x = i % w, y = i / w;
            if (x >= src.Width || y >= src.Height) continue;
            if (((src.At(x, y) >> 24) & 0xff) < MinAlpha) continue;
            list.Add(i);
        }
        return list;
    }

    /// <summary>Single-material statistics of a part's shading source texels (<see cref="SourceTexels"/>).</summary>
    public static MaterialStats PartStats(GarmentDef g, int part, MaterialTexture src)
    {
        int w = g.TextureWidth;
        return Stats(SourceTexels(g, part, src).Select(i => src.At(i % w, i / w)).ToList());
    }

    /// <summary>
    /// Brightness ratio per garment texel for part <paramref name="part"/> (1 outside it): vanilla luma / reference luma
    /// (90th percentile, or the median with <see cref="PartDef.Highlight"/>), clamped to [<see cref="MinRatio"/>, 1]
    /// (or <see cref="MaxRatio"/>), scaled by <see cref="GarmentDef.ShadeStrength"/>. Covered texels without a source
    /// texel (under an overlay, transparent in vanilla) take the mean of known neighbours, else 1. Null: no source texel.
    /// </summary>
    public static float[]? Map(GarmentDef g, int part, MaterialTexture src)
    {
        int w = g.TextureWidth, h = g.TextureHeight, n = w * h;
        var source = SourceTexels(g, part, src);
        if (source.Count == 0) return null;
        bool hi = g.Parts[part].Highlight;
        var lumas = source.Select(i => Luma(src.At(i % w, i / w))).OrderBy(v => v).ToArray();
        double reference = System.Math.Max(1.0, lumas[(int)System.Math.Floor((hi ? 0.5 : DarkenPercentile) * (lumas.Length - 1))]);
        var map = new float[n];
        System.Array.Fill(map, 1f);
        var known = new bool[n];
        foreach (int i in source)
        {
            float r = (float)(Luma(src.At(i % w, i / w)) / reference);
            map[i] = System.Math.Clamp(r, MinRatio, hi ? MaxRatio : 1f);
            known[i] = true;
        }
        var cov = g.Coverage[part];
        for (int pass = 0; pass < 8; pass++)
        {
            bool changed = false;
            var next = (float[])map.Clone();
            var nextKnown = (bool[])known.Clone();
            for (int i = 0; i < n; i++)
            {
                if (!cov[i] || known[i]) continue;
                int x = i % w, y = i / w, k = 0;
                float sum = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if ((dx | dy) == 0 || xx < 0 || yy < 0 || xx >= w || yy >= h || !known[yy * w + xx]) continue;
                        sum += map[yy * w + xx];
                        k++;
                    }
                if (k == 0) continue;
                next[i] = sum / k;
                nextKnown[i] = true;
                changed = true;
            }
            map = next;
            known = nextKnown;
            if (!changed) break;
        }
        float s = System.Math.Clamp(g.ShadeStrength, 0f, 1f);
        for (int i = 0; i < n; i++) map[i] = cov[i] ? 1f + (map[i] - 1f) * s : 1f;
        return map;
    }

    /// <summary>One texel shaded by ratio <paramref name="r"/>: below 1 multiplies RGB, above 1 lightens towards white (screen).</summary>
    public static int ApplyRatio(int argb, float r)
    {
        int a = (argb >> 24) & 0xff;
        int Ch(int c) => r <= 1f ? (int)(c * r) : (int)(c + (255 - c) * (r - 1f));
        int R = System.Math.Clamp(Ch((argb >> 16) & 0xff), 0, 255), G = System.Math.Clamp(Ch((argb >> 8) & 0xff), 0, 255), B = System.Math.Clamp(Ch(argb & 0xff), 0, 255);
        return (a << 24) | (R << 16) | (G << 8) | B;
    }

    /// <summary>
    /// Stage 3 of <see cref="GarmentLayout.Compose"/>: texels whose visible part shades and holds a material get that
    /// part's ratio. The source is asked from <paramref name="textureOf"/> as <c>@</c> + <see cref="GarmentDef.ShadingTexture"/>
    /// (the fixed-texture key form, <see cref="GarmentLayout.TryParseFixedKey"/>); missing → no shading.
    /// </summary>
    public static void Apply(GarmentDef g, int[] visible, SlotMaterial?[] mats, int[] px, System.Func<string, MaterialTexture?>? textureOf)
    {
        if (string.IsNullOrEmpty(g.ShadingTexture) || textureOf == null || !g.Parts.Any(p => p.Shade)) return;
        var src = textureOf("@" + g.ShadingTexture);
        if (src == null) return;
        for (int p = 0; p < g.Parts.Length; p++)
        {
            if (!g.Parts[p].Shade || mats[p] == null) continue;
            var map = Map(g, p, src);
            if (map == null) continue;
            for (int i = 0; i < px.Length; i++) if (visible[i] == p) px[i] = ApplyRatio(px[i], map[i]);
        }
    }
}
