using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ArtsAndCrafts.Tailor.Weaving;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Embroidery on garments at the mannequin (lane/mannequin-embroidery, docs/embroidery.md "Mannequin"): twine stitches
/// painted on the garment texture grid (one stitch = one garment texel), stored apart from the materials as an
/// <see cref="Embroidery"/> layer of the garment texture's size, so picking a stitch out always shows the material below.
/// Pure (covered by tests/tailoring-selftest).
/// <list type="bullet">
/// <item>Where: only texels whose visible part (layer order, <see cref="GarmentLayout.VisibleOwners"/>) holds cloth or
/// leather (<see cref="Takes"/>); pelts (fur), hard items (buttons, pearls) and fixed parts never take stitches.</item>
/// <item>Look: drawn last in <see cref="GarmentLayout.Compose"/>, after shading, in the twine colour, taking
/// <see cref="ShadeShare"/> of the fold shading under it (lightly shaded); no seam darkening, no cell checker.</item>
/// <item>Cost: 1 twine per twine code with a new or changed stitch, charged when applying (<see cref="Embroidery.Cost"/>: codes not yet paid on the garment).</item>
/// <item>Network: the dialog sends a diff (<see cref="EncodeDiff"/>), the server applies it (<see cref="ApplyDiff"/>).</item>
/// </list>
/// </summary>
public static class GarmentEmbroidery
{
    /// <summary>Share of the fold shading a stitch takes (0 = flat twine colour, 1 = shaded like the material below).</summary>
    public const float ShadeShare = 0.5f;
    /// <summary>Diff target of the garment in progress (else the mannequin's worn slot index).</summary>
    public const int TargetSession = -1;
    /// <summary>Change value of a picked-out stitch in a diff.</summary>
    public const byte PickOutValue = Embroidery.None;
    /// <summary>Brush widths offered by the dialog (texels, square).</summary>
    public static readonly int[] Brushes = { 1, 2, 4 };

    /// <summary>Whether a part showing <paramref name="m"/> takes stitches: cloth or leather, not pelt, hard or fixed.</summary>
    public static bool Takes(PartDef part, SlotMaterial? m)
        => !part.IsFixed && m != null && (m.Kind == MaterialKind.Cloth || m.Kind == MaterialKind.Leather);

    /// <summary>A new, empty layer of the garment texture's size.</summary>
    public static Embroidery New(GarmentDef g) => new(g.TextureWidth, g.TextureHeight);

    /// <summary>Texels that take stitches (row-major, garment texture size), from the parts' materials and the layer order.</summary>
    public static bool[] Mask(GarmentDef g, Func<int, SlotMaterial?> materialOf)
    {
        var mats = GarmentLayout.Materials(g, materialOf);
        return Mask(g, GarmentLayout.VisibleOwners(g, p => GarmentLayout.Present(g, p, mats[p])), mats);
    }

    /// <summary>Same as <see cref="Mask(GarmentDef, Func{int, SlotMaterial})"/> from an already computed layer order.</summary>
    public static bool[] Mask(GarmentDef g, int[] visible, SlotMaterial?[] mats)
    {
        var mask = new bool[visible.Length];
        for (int i = 0; i < visible.Length; i++)
        {
            int p = visible[i];
            mask[i] = p >= 0 && Takes(g.Parts[p], mats[p]);
        }
        return mask;
    }

    /// <summary>Copy of <paramref name="layer"/> without stitches outside <paramref name="mask"/> (null when nothing is left).</summary>
    public static Embroidery? Prune(Embroidery? layer, bool[] mask)
    {
        if (layer == null || layer.Cells.Length != mask.Length) return null;
        var e = layer.Clone();
        for (int i = 0; i < e.Cells.Length; i++) if (!mask[i]) e.Cells[i] = Embroidery.None;
        e.Compact();
        return e.IsBlank ? null : e;
    }

    /// <summary>
    /// Stitch twine <paramref name="code"/> on the square brush around (x, y) (<see cref="Embroidery.BrushCells"/>), only
    /// on texels of <paramref name="mask"/>. Unchanged when nothing under the brush takes stitches.
    /// </summary>
    public static Embroidery.Result Paint(Embroidery layer, bool[] mask, int x, int y, int brush, string code, string hex)
    {
        if (!layer.InRange(x, y)) return Embroidery.Result.OutOfRange;
        var cells = layer.BrushCells(x, y, brush).Where(c => mask[c.y * layer.Width + c.x]).ToList();
        if (cells.Count == 0) return Embroidery.Result.Unchanged;
        int idx = layer.PaletteIndexFor(code, hex);
        if (idx < 0) return Embroidery.Result.PaletteFull;
        bool changed = false;
        foreach (var (cx, cy) in cells)
        {
            int i = cy * layer.Width + cx;
            if (layer.Cells[i] == idx) continue;
            layer.Cells[i] = (byte)idx;
            changed = true;
        }
        layer.Compact();
        return changed ? Embroidery.Result.Ok : Embroidery.Result.Unchanged;
    }

    /// <summary>Pick the stitches out under the brush (anywhere: also stitches a material change left hidden).</summary>
    public static Embroidery.Result PickOut(Embroidery layer, int x, int y, int brush) => layer.PickOut(x, y, brush);

    /// <summary>Same stitches (codes and colours per texel) in both layers; null = empty.</summary>
    public static bool Same(Embroidery? a, Embroidery? b)
    {
        bool ea = a == null || a.IsEmpty, eb = b == null || b.IsEmpty;
        if (ea || eb) return ea && eb;
        if (a!.Width != b!.Width || a.Height != b.Height) return false;
        for (int i = 0; i < a.Cells.Length; i++)
        {
            int x = i % a.Width, y = i / a.Width;
            if (a.CodeAt(x, y) != b.CodeAt(x, y)) return false;
            if (a.CodeAt(x, y) != null && a.Colors[a.Cells[i]] != b.Colors[b.Cells[i]]) return false;
        }
        return true;
    }

    /// <summary>Stitches that differ between <paramref name="before"/> and <paramref name="after"/> (texel count).</summary>
    public static int Changed(Embroidery? before, Embroidery after)
    {
        int n = 0;
        for (int i = 0; i < after.Cells.Length; i++)
        {
            int x = i % after.Width, y = i / after.Width;
            if (after.CodeAt(x, y) != (before?.Width == after.Width && before.Height == after.Height ? before.CodeAt(x, y) : null)) n++;
        }
        return n;
    }

    /// <summary>
    /// Stage 4 of <see cref="GarmentLayout.Compose"/>: stitches over the shaded texels <paramref name="px"/> on embroiderable
    /// texels; <paramref name="unshaded"/> = the texels before shading (the stitch takes <see cref="ShadeShare"/> of the ratio).
    /// </summary>
    public static void Apply(GarmentDef g, int[] visible, SlotMaterial?[] mats, int[] unshaded, int[] px, Embroidery? layer)
    {
        if (layer == null || layer.Width != g.TextureWidth || layer.Height != g.TextureHeight || layer.IsEmpty) return;
        var mask = Mask(g, visible, mats);
        var rgb = layer.Colors.Select(h => { TwineColors.TryParse(h, out int v); return v; }).ToArray();
        for (int i = 0; i < px.Length; i++)
        {
            byte c = layer.Cells[i];
            if (c == Embroidery.None || c >= rgb.Length || !mask[i]) continue;
            double before = GarmentShading.Luma(unshaded[i]), after = GarmentShading.Luma(px[i]);
            float ratio = before < 8 ? 1f : (float)(after / before);
            float f = Math.Clamp(1f + ShadeShare * (ratio - 1f), GarmentShading.MinRatio, GarmentShading.MaxRatio);
            int r = Math.Clamp((int)(((rgb[c] >> 16) & 0xff) * f), 0, 255);
            int gr = Math.Clamp((int)(((rgb[c] >> 8) & 0xff) * f), 0, 255);
            int b = Math.Clamp((int)((rgb[c] & 0xff) * f), 0, 255);
            px[i] = unchecked((int)0xff000000) | (r << 16) | (gr << 8) | b;
        }
    }

    #region Diff (dialog → server)

    /// <summary>A decoded diff: target, the twine codes it uses, (texel, code index or <see cref="PickOutValue"/>) changes.</summary>
    public sealed class Diff
    {
        public int Target;
        public List<string> Codes = new();
        public List<(int cell, byte value)> Changes = new();
    }

    /// <summary>
    /// Packet payload of the stitches that differ from <paramref name="before"/> (null = none) to <paramref name="after"/>:
    /// int32 target, byte code count + codes (length-prefixed UTF-8), int32 change count + per change ushort texel and
    /// byte value (code index, <see cref="PickOutValue"/> = picked out). Colours are not sent: the server takes them from the twine item.
    /// </summary>
    public static byte[] EncodeDiff(int target, Embroidery? before, Embroidery after)
    {
        var codes = new List<string>();
        var changes = new List<(int, byte)>();
        for (int i = 0; i < after.Cells.Length; i++)
        {
            int x = i % after.Width, y = i / after.Width;
            string? now = after.CodeAt(x, y);
            string? was = before != null && before.Width == after.Width && before.Height == after.Height ? before.CodeAt(x, y) : null;
            if (now == was) continue;
            if (now == null) { changes.Add((i, PickOutValue)); continue; }
            int k = codes.IndexOf(now);
            if (k < 0) { codes.Add(now); k = codes.Count - 1; }
            changes.Add((i, (byte)k));
        }
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write(target);
            w.Write((byte)codes.Count);
            foreach (string c in codes) w.Write(c);
            w.Write(changes.Count);
            foreach (var (cell, v) in changes) { w.Write((ushort)cell); w.Write(v); }
        }
        return ms.ToArray();
    }

    /// <summary>Reads a diff payload; null when malformed (or more changes than <paramref name="maxCells"/>).</summary>
    public static Diff? DecodeDiff(byte[]? data, int maxCells = ushort.MaxValue + 1)
    {
        if (data == null || data.Length < 9) return null;
        try
        {
            using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            var d = new Diff { Target = r.ReadInt32() };
            int nc = r.ReadByte();
            for (int i = 0; i < nc; i++) d.Codes.Add(r.ReadString());
            int n = r.ReadInt32();
            if (n < 0 || n > maxCells) return null;
            for (int i = 0; i < n; i++) d.Changes.Add((r.ReadUInt16(), r.ReadByte()));
            return d;
        }
        catch (EndOfStreamException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>Result of <see cref="ApplyDiff"/>: the new layer (null = empty), changes applied / skipped, refusal (lang key).</summary>
    public readonly record struct ApplyResult(Embroidery? Layer, int Applied, int Skipped, string? Error);

    /// <summary>
    /// Server: <paramref name="diff"/> on a copy of <paramref name="current"/> (null = none) for garment <paramref name="g"/>.
    /// Stitches outside <paramref name="mask"/> or past the texture are skipped; pick-outs apply anywhere. A code that is not
    /// twine (<paramref name="hexOf"/> null) refuses the whole diff (<c>tailor-err-emb-twine</c>); so does a full palette
    /// (<c>tailor-err-emb-palette</c>). Colours come from <paramref name="hexOf"/>, never from the client.
    /// </summary>
    public static ApplyResult ApplyDiff(GarmentDef g, Embroidery? current, Diff diff, bool[] mask, Func<string, string?> hexOf)
    {
        var layer = current != null && current.Width == g.TextureWidth && current.Height == g.TextureHeight ? current.Clone() : New(g);
        var hex = new string?[diff.Codes.Count];
        for (int k = 0; k < hex.Length; k++)
        {
            hex[k] = hexOf(diff.Codes[k]);
            if (hex[k] == null) return new ApplyResult(current, 0, diff.Changes.Count, "tailor-err-emb-twine");
        }
        int applied = 0, skipped = 0;
        foreach (var (cell, v) in diff.Changes)
        {
            if (cell < 0 || cell >= layer.Cells.Length) { skipped++; continue; }
            if (v == PickOutValue)
            {
                if (layer.Cells[cell] != Embroidery.None) { layer.Cells[cell] = Embroidery.None; applied++; }
                continue;
            }
            if (v >= hex.Length || !mask[cell]) { skipped++; continue; }
            int idx = layer.PaletteIndexFor(diff.Codes[v], hex[v]!);
            if (idx < 0) return new ApplyResult(current, 0, diff.Changes.Count, "tailor-err-emb-palette");
            if (layer.Cells[cell] == idx) continue;
            layer.Cells[cell] = (byte)idx;
            applied++;
        }
        layer.Compact();
        return new ApplyResult(layer.IsBlank ? null : layer, applied, skipped, null);
    }

    #endregion
}
