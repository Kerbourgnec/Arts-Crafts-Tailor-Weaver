using System;
using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.Pattern;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// Embroidery on a woven cloth / linen (lane/loom-embroidery, docs/embroidery.md): stitches painted over the woven pattern,
/// stored apart from it so picking a stitch out always shows the weave again. Pure data (selftest).
/// <para>
/// Stack attribute tree <c>embroidery</c> = { bytes <c>cells</c> (width x height, row-major like the woven pattern;
/// <see cref="None"/> = no stitch, else an index into the layer's own palette), ints <c>width</c> / <c>height</c>,
/// string[] <c>paletteColors</c>, <c>paletteCodes</c> (twine codes) }. A layer that does not match the cloth's size is ignored.
/// </para>
/// </summary>
public sealed class Embroidery
{
    public const string AttrTree = "embroidery";
    public const byte None = WeavingState.Empty;
    public const int MaxPalette = WeavingState.MaxPalette;

    public int Width { get; }
    public int Height { get; }
    public byte[] Cells { get; }
    public List<string> Codes { get; } = new();
    public List<string> Colors { get; } = new();
    /// <summary>
    /// Twine codes already paid for on this item (lane/embroidery-paid): a code in the set is free to stitch again, any number
    /// of times. Kept when every stitch of a colour is picked out. Stored in the tree as <c>paid</c>.
    /// </summary>
    public HashSet<string> Paid { get; } = new();

    public Embroidery(int width, int height)
    {
        Width = width;
        Height = height;
        Cells = Enumerable.Repeat(None, width * height).ToArray();
    }

    public Embroidery Clone()
    {
        var e = new Embroidery(Width, Height);
        Array.Copy(Cells, e.Cells, Cells.Length);
        e.Codes.AddRange(Codes);
        e.Colors.AddRange(Colors);
        e.Paid.UnionWith(Paid);
        return e;
    }

    public bool IsEmpty => Cells.All(c => c == None);
    public int StitchCount => Cells.Count(c => c != None);

    public bool InRange(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    /// <summary>Twine code of the stitch at (x, y), or null.</summary>
    public string? CodeAt(int x, int y) => InRange(x, y) && Cells[y * Width + x] is var c && c != None && c < Codes.Count ? Codes[c] : null;

    /// <summary>Palette index for a twine code (colour frozen at first use); -1 when full.</summary>
    public int PaletteIndexFor(string code, string hex)
    {
        int i = Codes.IndexOf(code);
        if (i >= 0) return i;
        if (Codes.Count >= MaxPalette) return -1;
        Codes.Add(code);
        Colors.Add(hex);
        return Codes.Count - 1;
    }

    /// <summary>
    /// Cells a square brush of <paramref name="brush"/> covers around (x, y): <see cref="WeavingState.BrushSpan"/> on both
    /// axes (1, 2, 4, 8 cells wide and tall), clipped to the cloth.
    /// </summary>
    public IEnumerable<(int x, int y)> BrushCells(int x, int y, int brush)
    {
        var (x0, x1) = WeavingState.BrushSpan(x, brush, Width);
        var (y0, y1) = WeavingState.BrushSpan(y, brush, Height);
        for (int yy = y0; yy < y1; yy++)
            for (int xx = x0; xx < x1; xx++)
                yield return (xx, yy);
    }

    public enum Result { Ok, OutOfRange, PaletteFull, Unchanged }

    /// <summary>Stitch twine <paramref name="code"/> on the brush area around (x, y).</summary>
    public Result Paint(int x, int y, int brush, string code, string hex)
    {
        if (!InRange(x, y)) return Result.OutOfRange;
        int idx = PaletteIndexFor(code, hex);
        if (idx < 0) return Result.PaletteFull;
        bool changed = false;
        foreach (var (cx, cy) in BrushCells(x, y, brush))
        {
            int i = cy * Width + cx;
            if (Cells[i] == idx) continue;
            Cells[i] = (byte)idx;
            changed = true;
        }
        Compact();
        return changed ? Result.Ok : Result.Unchanged;
    }

    /// <summary>Pick the stitches out of the brush area around (x, y): the woven cells show again.</summary>
    public Result PickOut(int x, int y, int brush)
    {
        if (!InRange(x, y)) return Result.OutOfRange;
        bool changed = false;
        foreach (var (cx, cy) in BrushCells(x, y, brush))
        {
            int i = cy * Width + cx;
            if (Cells[i] == None) continue;
            Cells[i] = None;
            changed = true;
        }
        Compact();
        return changed ? Result.Ok : Result.Unchanged;
    }

    /// <summary>Drop palette entries no stitch uses (keeps the palette bounded while painting over and over).</summary>
    public void Compact()
    {
        var used = new HashSet<byte>(Cells.Where(c => c != None));
        if (used.Count == Codes.Count) return;
        var remap = new byte[Codes.Count];
        var codes = new List<string>();
        var colors = new List<string>();
        for (int i = 0; i < Codes.Count; i++)
        {
            if (!used.Contains((byte)i)) continue;
            remap[i] = (byte)codes.Count;
            codes.Add(Codes[i]);
            colors.Add(Colors[i]);
        }
        for (int i = 0; i < Cells.Length; i++) if (Cells[i] != None) Cells[i] = remap[Cells[i]];
        Codes.Clear(); Codes.AddRange(codes);
        Colors.Clear(); Colors.AddRange(colors);
    }

    /// <summary>Twine codes with at least one stitch.</summary>
    public HashSet<string> UsedCodes() => Cells.Where(c => c != None && c < Codes.Count).Select(c => Codes[c]).ToHashSet();

    /// <summary>
    /// Twine the stitches cost (owner rule, lane/embroidery-paid): 1 per twine code used that is not in <see cref="Paid"/>.
    /// Re-stitching an already paid colour is free; so is picking out.
    /// </summary>
    public Dictionary<string, int> Cost() => UsedCodes().Where(c => !Paid.Contains(c)).ToDictionary(c => c, _ => 1);

    /// <summary>Marks every code in use as paid (call once the cost has been charged).</summary>
    public void MarkPaid() => Paid.UnionWith(UsedCodes());

    /// <summary>Nothing to keep: no stitch and no paid code (such a layer is stored as no tree).</summary>
    public bool IsBlank => IsEmpty && Paid.Count == 0;

    #region Attributes

    public void ToTree(ITreeAttribute tree)
    {
        tree.SetBytes("cells", Cells);
        tree.SetInt("width", Width);
        tree.SetInt("height", Height);
        tree[ItemWovenCloth.AttrColors] = new StringArrayAttribute(Colors.ToArray());
        tree[ItemWovenCloth.AttrCodes] = new StringArrayAttribute(Codes.ToArray());
        tree["paid"] = new StringArrayAttribute(Paid.OrderBy(c => c, StringComparer.Ordinal).ToArray()); // always written: absent = layer from before paid sets
    }

    public static Embroidery? FromTree(ITreeAttribute? tree)
    {
        if (tree == null) return null;
        int w = tree.GetInt("width"), h = tree.GetInt("height");
        byte[]? cells = tree.GetBytes("cells");
        if (w <= 0 || h <= 0 || cells == null || cells.Length != w * h) return null;
        var e = new Embroidery(w, h);
        string[] colors = (tree[ItemWovenCloth.AttrColors] as StringArrayAttribute)?.value ?? Array.Empty<string>();
        string[] codes = (tree[ItemWovenCloth.AttrCodes] as StringArrayAttribute)?.value ?? Array.Empty<string>();
        int n = Math.Min(colors.Length, codes.Length);
        e.Codes.AddRange(codes.Take(n));
        e.Colors.AddRange(colors.Take(n));
        for (int i = 0; i < cells.Length; i++) e.Cells[i] = cells[i] < n ? cells[i] : None;
        // A layer saved before paid sets existed was charged per code already: its codes count as paid.
        if (tree["paid"] is StringArrayAttribute paid) e.Paid.UnionWith(paid.value);
        else e.Paid.UnionWith(e.UsedCodes());
        return e;
    }

    /// <summary>The layer stored on a cloth stack's attributes, or null (none, or another size than <paramref name="w"/> x <paramref name="h"/>).</summary>
    public static Embroidery? Read(ITreeAttribute? attrs, int w, int h)
        => FromTree(attrs?[AttrTree] as ITreeAttribute) is { } e && e.Width == w && e.Height == h ? e : null;

    /// <summary>
    /// Like <see cref="Read"/>, but a layer of the same width and fewer rows (a garment texture that grew rows below its layout,
    /// lane/modular-parts) is padded with empty rows instead of dropped: the stitches keep their texels.
    /// </summary>
    public static Embroidery? ReadGrown(ITreeAttribute? attrs, int w, int h)
    {
        var e = FromTree(attrs?[AttrTree] as ITreeAttribute);
        if (e == null || e.Width != w || e.Height > h) return null;
        if (e.Height == h) return e;
        var grown = new Embroidery(w, h);
        grown.Codes.AddRange(e.Codes);
        grown.Colors.AddRange(e.Colors);
        grown.Paid.UnionWith(e.Paid);
        System.Array.Copy(e.Cells, grown.Cells, e.Cells.Length);
        return grown;
    }

    /// <summary>Store the layer on cloth attributes; an empty (or null) layer removes the tree.</summary>
    public static void Write(ITreeAttribute attrs, Embroidery? layer)
    {
        attrs.RemoveAttribute(AttrTree);
        if (layer == null || layer.IsBlank) return;
        var t = new TreeAttribute();
        layer.Compact();
        layer.ToTree(t);
        attrs[AttrTree] = t;
    }

    #endregion

    /// <summary>
    /// Woven pattern + stitches as one pattern (what every renderer shows): a stitched cell takes the stitch colour, the
    /// rest the woven cell. Palette = the woven palette, then each stitch code not already there (same code and colour
    /// reuse the woven entry), so a cloth without stitches composites to itself (same hash).
    /// </summary>
    public static PatternData Composite(PatternData woven, Embroidery? layer)
    {
        if (layer == null || layer.Width != woven.Width || layer.Height != woven.Height || layer.IsEmpty) return woven;
        var codes = woven.Codes.ToList();
        var colors = woven.Colors.ToList();
        while (codes.Count < colors.Count) codes.Add("");
        var map = new byte[layer.Codes.Count];
        for (int i = 0; i < layer.Codes.Count; i++)
        {
            int at = -1;
            for (int j = 0; j < colors.Count; j++)
                if (codes[j] == layer.Codes[i] && colors[j] == layer.Colors[i]) { at = j; break; }
            if (at < 0)
            {
                at = colors.Count;
                codes.Add(layer.Codes[i]);
                colors.Add(layer.Colors[i]);
            }
            map[i] = (byte)Math.Min(at, 254);
        }
        var pattern = (byte[])woven.Pattern.Clone();
        for (int i = 0; i < pattern.Length; i++)
            if (layer.Cells[i] != None && layer.Cells[i] < map.Length) pattern[i] = map[layer.Cells[i]];
        return new PatternData(pattern, colors.ToArray(), codes.ToArray(), woven.Width, woven.Height, woven.WarpUp);
    }
}
