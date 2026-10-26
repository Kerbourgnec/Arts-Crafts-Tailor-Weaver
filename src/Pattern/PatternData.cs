using System;
using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Pattern;

/// <summary>
/// A woven pattern: <see cref="Width"/> x <see cref="Height"/> palette indices (row-major, row 0 = first woven), palette
/// colours ("#rrggbb") and twine codes. Pure data (no client API), so it is testable offline and reusable by anything
/// that shows a cloth. Native size (lane/cloth-formats): a placed product keeps the cloth's own cells (16 x 16 when the
/// stack has no size); <see cref="Crop"/> cuts tiles out of a larger cloth. Never resampled (lane/pattern-density).
/// <para>
/// Attribute layout (same keys as <see cref="ItemWovenCloth"/>): bytes <c>pattern</c>, ints <c>clothWidth</c> /
/// <c>clothHeight</c>, string[] <c>paletteColors</c>, <c>paletteCodes</c>. A list of patterns (one per tile of a large
/// carpet, one for a chair) lives in a tree <c>patterns</c> with sub-trees <c>p0</c>, <c>p1</c>, ...; see <see cref="ReadList"/>.
/// </para>
/// </summary>
public sealed class PatternData
{
    public const string AttrList = "patterns";
    public const int Cells = WeavingState.DefaultWidth * WeavingState.DefaultRows;

    public byte[] Pattern { get; }
    public string[] Colors { get; }
    public string[] Codes { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// Lifted-warp cells (row-major, true = warp shows) of a draft cloth, or null (parity shading, lane/cloth-cell-shade). Follows the
    /// pattern through crop, tiling, embroidery and the attributes (<see cref="ItemWovenCloth.AttrWarpUp"/>); part of <see cref="Hash"/>.
    /// </summary>
    public bool[]? WarpUp { get; }

    public PatternData(byte[] pattern, string[] colors, string[]? codes = null, int width = WeavingState.DefaultWidth, int height = WeavingState.DefaultRows, bool[]? warpUp = null)
    {
        WarpUp = warpUp?.Length == pattern.Length ? warpUp : null;
        Pattern = pattern;
        Colors = colors;
        Codes = codes ?? Array.Empty<string>();
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Stable key for texture/mesh caches (FNV-1a over width, cells and colours, as <see cref="ItemWovenCloth.PatternHash"/>;
    /// the width and the cell count fix the height). 16 x 16 keys are unchanged from before lane/cloth-formats.
    /// </summary>
    public string Hash => ItemWovenCloth.PatternHash(Pattern, Colors, Width) + ItemWovenCloth.ShadeKey(WarpUp);

    public static bool IsValid(byte[]? pattern, string[]? colors, int width = WeavingState.DefaultWidth, int height = WeavingState.DefaultRows)
        => width > 0 && height > 0 && pattern?.Length == width * height && colors is { Length: > 0 };

    /// <summary>
    /// The pattern stored directly in these attributes (a woven cloth stack, its own size), as shown: woven + embroidery
    /// (lane/loom-embroidery, <see cref="WovenTexture.Composite(ITreeAttribute)"/>), or null.
    /// </summary>
    public static PatternData? FromAttributes(ITreeAttribute? attrs)
        => WovenTexture.Composite(attrs) is { } p && IsValid(p.Pattern, p.Colors, p.Width, p.Height) ? p : null;

    /// <summary>Write the pattern directly into these attributes (woven cloth layout).</summary>
    public void ToAttributes(ITreeAttribute attrs)
    {
        attrs.SetBytes(ItemWovenCloth.AttrPattern, Pattern);
        attrs.SetInt(ItemWovenCloth.AttrWidth, Width);
        attrs.SetInt(ItemWovenCloth.AttrHeight, Height);
        attrs[ItemWovenCloth.AttrColors] = new StringArrayAttribute(Colors);
        attrs[ItemWovenCloth.AttrCodes] = new StringArrayAttribute(Codes);
        ItemWovenCloth.WriteWarpUp(attrs, WarpUp);
    }

    /// <summary>Cells <paramref name="x0"/>.. x <paramref name="w"/>, rows <paramref name="y0"/>.. x <paramref name="h"/> (clamped to the cloth).</summary>
    public PatternData Crop(int x0, int y0, int w, int h)
    {
        var o = new byte[w * h];
        var u = WarpUp == null ? null : new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int src = Math.Clamp(y0 + y, 0, Height - 1) * Width + Math.Clamp(x0 + x, 0, Width - 1);
                o[y * w + x] = Pattern[src];
                if (u != null) u[y * w + x] = WarpUp![src];
            }
        return new PatternData(o, Colors, Codes, w, h, u);
    }

    /// <summary>
    /// Cells <paramref name="x0"/>.. x <paramref name="w"/>, rows <paramref name="y0"/>.. x <paramref name="h"/>, the cloth
    /// repeating beyond its edges (one cell per cell, never resampled).
    /// </summary>
    public PatternData Tiled(int x0, int y0, int w, int h)
    {
        var o = new byte[w * h];
        var u = WarpUp == null ? null : new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int src = (((y0 + y) % Height + Height) % Height) * Width + ((x0 + x) % Width + Width) % Width;
                o[y * w + x] = Pattern[src];
                if (u != null) u[y * w + x] = WarpUp![src];
            }
        return new PatternData(o, Colors, Codes, w, h, u);
    }

    /// <summary>
    /// <paramref name="count"/> patterns from a <c>patterns</c> list; null entries where none is stored. Falls back to
    /// a pattern stored directly in the attributes (a woven cloth stack) as entry 0.
    /// </summary>
    public static PatternData?[] ReadList(ITreeAttribute? attrs, int count)
    {
        var list = new PatternData?[count];
        if (attrs == null || count <= 0) return list;
        if (attrs[AttrList] is ITreeAttribute tree)
        {
            for (int i = 0; i < count; i++) list[i] = FromAttributes(tree["p" + i] as ITreeAttribute);
        }
        else
        {
            list[0] = FromAttributes(attrs);
        }
        return list;
    }

    /// <summary>Store a pattern list (null entries are skipped); removes the list when every entry is null.</summary>
    public static void WriteList(ITreeAttribute attrs, IReadOnlyList<PatternData?> list)
    {
        attrs.RemoveAttribute(AttrList);
        if (list.All(p => p == null)) return;
        var tree = new TreeAttribute();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not { } p) continue;
            var sub = new TreeAttribute();
            p.ToAttributes(sub);
            tree["p" + i] = sub;
        }
        attrs[AttrList] = tree;
    }

    /// <summary>Cache key of a pattern list: hashes joined, "-" for an empty entry.</summary>
    public static string ListKey(IEnumerable<PatternData?> list) => string.Join(".", list.Select(p => p?.Hash ?? "-"));

    /// <summary>
    /// Patterns from the woven cloth inputs of a recipe, in grid order (row-major, as the crafting grid passes its
    /// slots): one entry per cloth, null for a blank cloth. A large carpet crafted "CC,CC" gets tile 0 = top left,
    /// 1 = top right, 2 = bottom left, 3 = bottom right.
    /// </summary>
    public static PatternData?[] FromInputs(IEnumerable<ITreeAttribute?> clothInputsInGridOrder, int count)
    {
        var list = new PatternData?[count];
        int n = 0;
        foreach (var attrs in clothInputsInGridOrder)
        {
            if (n >= count) break;
            list[n++] = FromAttributes(attrs);
        }
        return list;
    }

    /// <summary>
    /// One cloth split into tiles (lane/cloth-formats: a 32-wide cloth makes the whole large carpet): tile i = the
    /// <paramref name="tile"/> x <paramref name="tile"/> cells at <paramref name="tileCells"/>[i] = (column, row) in tiles.
    /// Cells map 1:1 (lane/pattern-density: no resample); a cloth smaller than <paramref name="cols"/> x <paramref name="rows"/>
    /// tiles repeats.
    /// </summary>
    public static PatternData?[] Split(PatternData cloth, int cols, int rows, IReadOnlyList<(int col, int row)> tileCells, int tile = WeavingState.DefaultWidth)
        => tileCells.Select(t => (PatternData?)cloth.Tiled(t.col * tile, t.row * tile, tile, tile)).ToArray();
}
