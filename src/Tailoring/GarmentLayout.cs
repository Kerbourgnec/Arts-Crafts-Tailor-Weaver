using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.Weaving;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// How one part takes its material: <see cref="Cloth"/> = material index in a stored garment (<see cref="GarmentData.Materials"/>,
/// -1 = none; unused in a live session, where part i takes the item in sewing slot i), offset in cells, quarter turns.
/// </summary>
public struct PieceAssignment
{
    public int Cloth;
    public int Dx;
    public int Dy;
    public int Rot;

    public PieceAssignment(int cloth, int dx = 0, int dy = 0, int rot = 0)
    {
        Cloth = cloth;
        Dx = GarmentLayout.Mod(dx, GarmentLayout.OffsetPeriod);
        Dy = GarmentLayout.Mod(dy, GarmentLayout.OffsetPeriod);
        Rot = GarmentLayout.Mod(rot, 4);
    }

    public static PieceAssignment None => new(-1);
}

/// <summary>
/// What a sewing slot (or a stored garment entry) holds: a patterned woven cloth, or a vanilla item by code
/// (<see cref="MaterialCatalog"/>: leather, pelt, vanilla cloth-{color}, hard items). Items carry no pattern: their
/// texture is tiled under the part.
/// </summary>
public sealed class SlotMaterial
{
    /// <summary>Material family (cloth, leather, pelt, wood, metal, bone...; "hard" on garments sewn before families).</summary>
    public readonly string Kind;
    /// <summary>Group of the family (textile | hard), "" when unknown (stored garments: only needed while sewing).</summary>
    public readonly string Group;
    public readonly ClothPattern? Cloth;
    /// <summary>Item code of a catalog material ("" for a patterned woven cloth).</summary>
    public readonly string Code;

    SlotMaterial(string kind, string group, ClothPattern? cloth, string code)
    {
        Kind = kind;
        Group = group;
        Cloth = cloth;
        Code = code;
    }

    /// <summary>Patterned woven cloth or woven linen: family cloth, group textile.</summary>
    public static SlotMaterial OfCloth(ClothPattern cloth) => new(MaterialKind.Cloth, MaterialKind.Textile, cloth, "");
    /// <summary>Catalog item of <paramref name="family"/> (group from <see cref="MaterialCatalog.GroupOf"/>).</summary>
    public static SlotMaterial OfItem(string family, string code, string group = "") => new(family, group, null, code);
    /// <summary>
    /// Built-in material of a fixed part (<see cref="PartDef.FixedTexture"/>): never in a slot, always rendered.
    /// <see cref="Code"/> = <see cref="GarmentLayout.FixedTextureKey"/>.
    /// </summary>
    public static SlotMaterial OfFixed(PartDef part) => new(MaterialKind.Fixed, "", null, GarmentLayout.FixedTextureKey(part));
    /// <summary>Patterned woven cloth (offset + turn); false = an item texture (turn only).</summary>
    public bool IsPatterned => Cloth != null;
}

/// <summary>Texture of a catalog material for composition: 0xAARRGGBB pixels and the texel rectangle tiled under a part.</summary>
public sealed class MaterialTexture
{
    public readonly int[] Pixels;
    public readonly int Width, Height;
    public readonly int X1, Y1, X2, Y2;

    public MaterialTexture(int[] pixels, int width, int height, int[]? crop = null)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        bool ok = crop != null && crop.Length == 4 && crop[0] >= 0 && crop[1] >= 0 && crop[2] <= width && crop[3] <= height
            && crop[0] < crop[2] && crop[1] < crop[3];
        (X1, Y1, X2, Y2) = ok ? (crop![0], crop[1], crop[2], crop[3]) : (0, 0, width, height);
    }

    /// <summary>One flat colour (texture missing).</summary>
    public static MaterialTexture Solid(int argb) => new(new[] { argb }, 1, 1);

    public int At(int x, int y) => Pixels[y * Width + x];
}

/// <summary>
/// A woven cloth's pattern as read from its stack (see <see cref="ItemWovenCloth"/>), at the cloth's OWN size
/// (lane/pattern-density: 16 x 16 or 32 x 32, never resampled; a 32 cloth covers 4x the area before it repeats).
/// </summary>
public sealed class ClothPattern
{
    public readonly byte[] Pattern;
    public readonly string[] Colors;
    public readonly int Width, Height;
    /// <summary>Lifted-warp cells of a draft cloth (null = parity): the garment checker follows warp / weft instead of (x + y).</summary>
    public readonly bool[]? WarpUp;

    public ClothPattern(byte[] pattern, string[] colors, int width = GarmentLayout.Tile, int height = GarmentLayout.Tile, bool[]? warpUp = null)
    {
        WarpUp = warpUp?.Length == pattern.Length ? warpUp : null;
        if (width <= 0 || height <= 0 || pattern.Length != width * height)
            throw new System.ArgumentException($"pattern of {pattern.Length} cells is not {width} x {height}");
        Pattern = pattern;
        Colors = colors;
        Width = width;
        Height = height;
    }

    public string ColorAt(int cx, int cy)
    {
        byte idx = Pattern[cy * Width + cx];
        return idx < Colors.Length ? Colors[idx] : TwineColors.PlainHex;
    }
}

/// <summary>
/// Pure layout maths (no client calls; covered by tests/tailoring-selftest): part masks, pattern sampling
/// with offset + rotation (the cloth pattern, at its own size, tiles under each part at ONE global density:
/// <see cref="CellTexels"/> texel per cell on both axes, every garment and part, never stretched or resampled),
/// texture composition.
/// </summary>
public static class GarmentLayout
{
    /// <summary>Size of a cloth without a stored size (garments sewn before lane/pattern-density, tests): 16 x 16.</summary>
    public const int Tile = 16;
    /// <summary>
    /// Global density (owner, lane/pattern-density): one cloth cell = one garment texel (vanilla clothing texel, 2 per
    /// shape unit) on every garment and part, for 16 and 32 cloths alike. No per-garment density.
    /// </summary>
    public const int CellTexels = 1;
    /// <summary>Offsets are kept mod this (the largest cloth, 32; a multiple of every cloth size, so 16 cloths wrap too).</summary>
    public const int OffsetPeriod = 32;
    /// <summary>Texel colour where a part has no cloth yet (preview only): translucent grey.</summary>
    public const int UnassignedArgb = unchecked((int)0x80808080);

    public static int Mod(int a, int m) => ((a % m) + m) % m;

    /// <summary>
    /// Texture key of a fixed part for <c>textureOf</c> / <see cref="MaterialTextureCache"/>: <c>@texture</c>, plus
    /// <c>#x1,y1,x2,y2</c> with a crop. Item codes never start with '@'.
    /// </summary>
    public static string FixedTextureKey(PartDef part)
        => "@" + part.FixedTexture + (part.FixedCrop is { Length: 4 } c ? "#" + string.Join(",", c) : "");

    /// <summary>Reverse of <see cref="FixedTextureKey"/>; false for an item code.</summary>
    public static bool TryParseFixedKey(string key, out string texture, out int[]? crop)
    {
        texture = "";
        crop = null;
        if (!key.StartsWith("@")) return false;
        int hash = key.IndexOf('#');
        texture = hash < 0 ? key.Substring(1) : key.Substring(1, hash - 1);
        if (hash >= 0)
        {
            var parts = key.Substring(hash + 1).Split(',');
            if (parts.Length == 4 && parts.All(p => int.TryParse(p, out _))) crop = parts.Select(int.Parse).ToArray();
        }
        return true;
    }
    static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    static void FillRects(bool[] into, int[][] rects, int w, int h)
    {
        foreach (var r in rects)
        {
            if (r.Length != 4) continue;
            for (int y = System.Math.Max(0, r[1]); y < System.Math.Min(h, r[3]); y++)
                for (int x = System.Math.Max(0, r[0]); x < System.Math.Min(w, r[2]); x++)
                    into[y * w + x] = true;
        }
    }

    /// <summary>
    /// Texels per part (lane/garment-layers): regions &amp; mask; a texel an earlier part of the SAME layer covers is not
    /// covered again (first listed wins, the rule before layers). Parts of different layers may overlap.
    /// </summary>
    public static bool[][] BuildCoverage(GarmentDef g)
    {
        int w = g.TextureWidth, h = g.TextureHeight, n = w * h;
        var cov = new bool[g.Parts.Length][];
        for (int p = 0; p < g.Parts.Length; p++)
        {
            var part = g.Parts[p];
            var c = new bool[n];
            FillRects(c, part.Regions, w, h);
            if (part.Mask != null)
            {
                var m = new bool[n];
                FillRects(m, part.Mask, w, h);
                for (int i = 0; i < n; i++) c[i] &= m[i];
            }
            for (int q = 0; q < p; q++)
            {
                if (g.Parts[q].Layer != part.Layer) continue;
                for (int i = 0; i < n; i++) if (cov[q][i]) c[i] = false;
            }
            cov[p] = c;
        }
        return cov;
    }

    /// <summary>Mask texels of a part that lie outside its regions (a recipe mistake; validation).</summary>
    public static int MaskOutsideRegions(PartDef part)
    {
        if (part.Mask == null) return 0;
        int w = 0, h = 0;
        foreach (var r in part.Mask.Concat(part.Regions)) if (r.Length == 4) { w = System.Math.Max(w, r[2]); h = System.Math.Max(h, r[3]); }
        var reg = new bool[w * h];
        var m = new bool[w * h];
        FillRects(reg, part.Regions, w, h);
        FillRects(m, part.Mask, w, h);
        int k = 0;
        for (int i = 0; i < m.Length; i++) if (m[i] && !reg[i]) k++;
        return k;
    }

    /// <summary>Topmost covering part per texel (highest layer; same-layer parts never share a texel), -1 = none.</summary>
    public static int[] BuildOwnerMask(GarmentDef g) => VisibleOwners(g, _ => true);

    /// <summary>
    /// Layer order (composition stage 1): per texel, the highest-layer covering part that is <paramref name="present"/>;
    /// an absent part (an empty optional one) lets the layer below show through. -1 = no present part.
    /// </summary>
    public static int[] VisibleOwners(GarmentDef g, System.Func<int, bool> present)
    {
        var cov = g.Coverage;
        var owner = new int[g.TextureWidth * g.TextureHeight];
        System.Array.Fill(owner, -1);
        for (int p = 0; p < g.Parts.Length; p++)
        {
            if (!present(p)) continue;
            var c = cov[p];
            int layer = g.Parts[p].Layer;
            for (int i = 0; i < owner.Length; i++)
            {
                if (c[i] && (owner[i] < 0 || g.Parts[owner[i]].Layer < layer)) owner[i] = p;
            }
        }
        return owner;
    }

    /// <summary>A part is present in the composed texture when filled, required (grey while empty, preview) or fixed.</summary>
    public static bool Present(GarmentDef g, int part, SlotMaterial? material) => material != null || !g.Parts[part].Optional || g.Parts[part].IsFixed;

    /// <summary>Top-left of the part's bounding box: the pattern's (0,0) cell sits there at offset 0.</summary>
    public static (int x, int y) PartOrigin(GarmentDef g, int part)
    {
        int x = int.MaxValue, y = int.MaxValue;
        foreach (var r in g.Parts[part].Regions)
        {
            if (r.Length != 4) continue;
            x = System.Math.Min(x, r[0]);
            y = System.Math.Min(y, r[1]);
        }
        return (x == int.MaxValue ? 0 : x, y == int.MaxValue ? 0 : y);
    }

    /// <summary>Bounding box of the texels the part owns (x1, y1, x2, y2 half-open); empty part → zeros.</summary>
    public static (int x1, int y1, int x2, int y2) PartBounds(GarmentDef g, int part)
    {
        int x1 = int.MaxValue, y1 = int.MaxValue, x2 = 0, y2 = 0;
        var cov = g.Coverage[part];
        for (int y = 0; y < g.TextureHeight; y++)
        {
            for (int x = 0; x < g.TextureWidth; x++)
            {
                if (!cov[y * g.TextureWidth + x]) continue;
                x1 = System.Math.Min(x1, x); y1 = System.Math.Min(y1, y);
                x2 = System.Math.Max(x2, x + 1); y2 = System.Math.Max(y2, y + 1);
            }
        }
        return x1 == int.MaxValue ? (0, 0, 0, 0) : (x1, y1, x2, y2);
    }

    public static int PartArea(GarmentDef g, int part)
    {
        int n = 0;
        foreach (bool c in g.Coverage[part]) if (c) n++;
        return n;
    }

    /// <summary>
    /// Pattern cell under texel (x, y) of a part, for a <paramref name="w"/> x <paramref name="h"/> cloth: one cell per
    /// texel (<see cref="CellTexels"/>), repeating every cloth size. The offset applies on screen (texel) axes, then the
    /// turn, so dragging right always moves the pattern right whatever the rotation.
    /// </summary>
    public static (int cx, int cy) CellAt(GarmentDef g, int part, int x, int y, PieceAssignment a, int w = Tile, int h = Tile)
    {
        var (bx, by) = PartOrigin(g, part);
        int u = FloorDiv(x - bx, CellTexels) - a.Dx;
        int v = FloorDiv(y - by, CellTexels) - a.Dy;
        (int cu, int cv) = Mod(a.Rot, 4) switch
        {
            1 => (v, -u - 1),
            2 => (-u - 1, -v - 1),
            3 => (-v - 1, u),
            _ => (u, v),
        };
        return (Mod(cu, w), Mod(cv, h));
    }

    /// <summary>
    /// Texel of an item texture (leather, pelt, vanilla cloth, hard) under texel (x, y) of a part: the crop tiles from the part's origin, turned by
    /// <paramref name="rot"/> quarter turns (same turn as <see cref="CellAt"/>); no offset.
    /// </summary>
    public static (int tx, int ty) TextureTexelAt(GarmentDef g, int part, int x, int y, int rot, MaterialTexture tex)
    {
        var (bx, by) = PartOrigin(g, part);
        int u = x - bx, v = y - by;
        (int cu, int cv) = Mod(rot, 4) switch
        {
            1 => (v, -u - 1),
            2 => (-u - 1, -v - 1),
            3 => (-v - 1, u),
            _ => (u, v),
        };
        return (tex.X1 + Mod(cu, tex.X2 - tex.X1), tex.Y1 + Mod(cv, tex.Y2 - tex.Y1));
    }

    /// <summary>Quarter turns of cell coordinates (same turn as <see cref="CellAt"/>).</summary>
    public static (int u, int v) Turn(int u, int v, int rot) => Mod(rot, 4) switch
    {
        1 => (v, -u - 1),
        2 => (-u - 1, -v - 1),
        3 => (-v - 1, u),
        _ => (u, v),
    };

    /// <summary>
    /// Cloth cell at body-space coordinates (u, v) of a segment (lane/body-wrap, <see cref="BodyWrapMap"/>): the segment's
    /// placement first (offset, then turn), then the part's own offset and turn (the sewing dialog), wrapped by the
    /// <paramref name="w"/> x <paramref name="h"/> cloth. One cell per texel.
    /// </summary>
    public static (int cx, int cy) WrapCell(int u, int v, SegmentPlacement s, PieceAssignment a, int w, int h)
    {
        var (su, sv) = Turn(u - s.Du, v - s.Dv, s.Rot);
        var (cu, cv) = Turn(su - a.Dx, sv - a.Dy, a.Rot);
        return (Mod(cu, w), Mod(cv, h));
    }

    /// <summary>Item texture texel at body-space (u, v) (lane/body-wrap): segment placement, then the part's turn; tiled 1:1 from the crop.</summary>
    public static (int tx, int ty) WrapTexel(int u, int v, SegmentPlacement s, int rot, MaterialTexture tex)
    {
        var (su, sv) = Turn(u - s.Du, v - s.Dv, s.Rot);
        var (cu, cv) = Turn(su, sv, rot);
        return (tex.X1 + Mod(cu, tex.X2 - tex.X1), tex.Y1 + Mod(cv, tex.Y2 - tex.Y1));
    }

    /// <summary>Flat stand-in colour of a material whose texture is unavailable.</summary>
    public static int FallbackArgb(string kind) => TwineColors.ToArgb(kind switch
    {
        MaterialKind.Pelt => "#8a6a4a",
        MaterialKind.Hard or MaterialKind.Bone => "#b8b0a0",
        MaterialKind.Wood => "#9a7a50",
        MaterialKind.Metal => "#b07040",
        MaterialKind.Cloth => "#cfc4a8",
        _ => "#6b4a2e",
    });

    /// <summary>
    /// Garment texture, 0xAARRGGBB row-major (BakedBitmap layout), as a pipeline (lane/garment-layers):
    /// 1. layer order (<see cref="VisibleOwners"/>): each texel shows the highest-layer part covering it that is present
    ///    (filled, required or fixed); an empty optional overlay shows the layer below (pants → suspenders → buttons);
    /// 2. materials (<see cref="PaintLayers"/>): base parts and overlays alike. Texels without a part take their
    ///    <see cref="GarmentDef.Fixed"/> colour, else stay transparent. <paramref name="materialOf"/> gives a part's
    ///    material (null = empty: translucent grey on a required part, preview). Patterned cloth samples the pattern (offset
    ///    + turn, checker shade per cell, one cell per texel at the cloth's own size, <see cref="CellAt"/>); item parts tile the item texture 1:1
    ///    (<paramref name="textureOf"/> by item code; null or unknown → flat colour; turn only). Seam texels (next to
    ///    another visible part or the outside) are darkened a little;
    /// 3. shading (<see cref="GarmentShading.Apply"/>): parts with <see cref="PartDef.Shade"/> take the brightness of the
    ///    garment's <see cref="GarmentDef.ShadingTexture"/> (asked from <paramref name="textureOf"/> as a fixed-texture key);
    /// 4. embroidery (lane/mannequin-embroidery, <see cref="GarmentEmbroidery.Apply"/>): <paramref name="embroidery"/>'s
    ///    stitches on top of the shaded texture, only where the visible part holds cloth or leather, lightly shaded.
    /// </summary>
    /// <param name="wrap">lane/body-wrap: body-space coordinates + layout (null = UV space, garments sewn before).</param>
    public static int[] Compose(GarmentDef g, IReadOnlyList<PieceAssignment> placements, System.Func<int, SlotMaterial?> materialOf,
        System.Func<string, MaterialTexture?>? textureOf = null, Embroidery? embroidery = null, BodyWrap? wrap = null)
    {
        var mats = Materials(g, materialOf);
        int[] visible = VisibleOwners(g, p => Present(g, p, mats[p]));     // 1. layer order
        int[] px = PaintLayers(g, visible, placements, mats, textureOf, wrap); // 2. base parts + overlays
        int[]? unshaded = embroidery == null || embroidery.IsEmpty ? null : (int[])px.Clone();
        GarmentShading.Apply(g, visible, mats, px, textureOf);             // 3. shading
        if (unshaded != null) GarmentEmbroidery.Apply(g, visible, mats, unshaded, px, embroidery); // 4. embroidery
        return px;
    }

    /// <summary>Material per part for composition: fixed parts (premade, e.g. a brass buckle) always show their own texture.</summary>
    public static SlotMaterial?[] Materials(GarmentDef g, System.Func<int, SlotMaterial?> materialOf)
    {
        var mats = new SlotMaterial?[g.Parts.Length];
        for (int p = 0; p < mats.Length; p++) mats[p] = g.Parts[p].IsFixed ? SlotMaterial.OfFixed(g.Parts[p]) : materialOf(p);
        return mats;
    }

    /// <summary>Stage 2 of <see cref="Compose"/>: every texel painted with the material of its visible part.</summary>
    /// <summary>
    /// Stage 2 of <see cref="Compose"/>: every texel painted with the material of its visible part. With
    /// <paramref name="wrap"/> (lane/body-wrap) cloth and item textures are laid in body space (<see cref="WrapCell"/>):
    /// texels on a body segment take the cell at their segment coordinates, no UV seam darkening; texels without a
    /// segment and fixed parts stay in UV space.
    /// </summary>
    public static int[] PaintLayers(GarmentDef g, int[] visible, IReadOnlyList<PieceAssignment> placements, SlotMaterial?[] mats,
        System.Func<string, MaterialTexture?>? textureOf, BodyWrap? wrap = null)
    {
        int w = g.TextureWidth, h = g.TextureHeight;
        var px = new int[w * h];
        var textures = new Dictionary<string, MaterialTexture>();
        if (wrap != null && (wrap.Map.Width != w || wrap.Map.Height != h)) wrap = null; // recipe texture changed
        bool continues = wrap?.Layout.Continues(g) ?? false;
        int At(int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? -1 : visible[y * w + x];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = visible[y * w + x];
                if (p < 0)
                {
                    px[y * w + x] = FixedArgb(g, x, y);
                    continue;
                }
                var a = p < placements.Count ? placements[p] : PieceAssignment.None;
                SlotMaterial? mat = mats[p];
                if (mat == null)
                {
                    px[y * w + x] = UnassignedArgb;
                    continue;
                }
                int bu = 0, bv = 0, seg = -1;
                bool wrapped = wrap != null && mat.Kind != MaterialKind.Fixed && wrap.Map.Coords(y * w + x, continues, out seg, out bu, out bv);
                var sp = wrapped ? wrap!.Layout.For(seg, continues) : default;
                bool seam = !wrapped && (At(x - 1, y) != p || At(x + 1, y) != p || At(x, y - 1) != p || At(x, y + 1) != p);
                if (mat.Cloth != null)
                {
                    var (cx, cy) = wrapped ? WrapCell(bu, bv, sp, a, mat.Cloth.Width, mat.Cloth.Height) : CellAt(g, p, x, y, a, mat.Cloth.Width, mat.Cloth.Height);
                    float shade = (ClothUp(mat.Cloth, cx, cy) ? 1f : 0.93f) * (seam ? 0.86f : 1f);
                    px[y * w + x] = TwineColors.ToArgb(mat.Cloth.ColorAt(cx, cy), shade);
                    continue;
                }
                if (!textures.TryGetValue(mat.Code, out var tex))
                {
                    textures[mat.Code] = tex = textureOf?.Invoke(mat.Code) ?? MaterialTexture.Solid(mat.Kind == MaterialKind.Fixed
                        ? TwineColors.ToArgb(g.Parts[p].FixedColor) : FallbackArgb(mat.Kind));
                }
                var (tx, ty) = wrapped ? WrapTexel(bu, bv, sp, a.Rot, tex) : TextureTexelAt(g, p, x, y, mat.Kind == MaterialKind.Fixed ? 0 : a.Rot, tex);
                px[y * w + x] = Shade(tex.At(tx, ty), seam ? 0.86f : 1f);
            }
        }
        return px;
    }

    /// <summary>Opaque copy of an ARGB texel with RGB scaled by <paramref name="f"/>.</summary>
    /// <summary>Cell (cx, cy) takes the bright checker value: warp shows (draft cloth) or even parity.</summary>
    public static bool ClothUp(ClothPattern c, int cx, int cy) => c.WarpUp != null ? c.WarpUp[cy * c.Width + cx] : ((cx + cy) & 1) == 0;

    public static int Shade(int argb, float f)
    {
        int r = (int)(((argb >> 16) & 0xff) * f), gr = (int)(((argb >> 8) & 0xff) * f), b = (int)((argb & 0xff) * f);
        return unchecked((int)0xff000000) | (r << 16) | (gr << 8) | b;
    }

    /// <summary>Colour of a <see cref="GarmentDef.Fixed"/> fill at (x, y), 0 (transparent) when none; first fill wins.</summary>
    public static int FixedArgb(GarmentDef g, int x, int y)
    {
        foreach (var f in g.Fixed)
        {
            var r = f.Rect;
            if (r.Length == 4 && x >= r[0] && y >= r[1] && x < r[2] && y < r[3]) return TwineColors.ToArgb(f.Color);
        }
        return 0;
    }
}
