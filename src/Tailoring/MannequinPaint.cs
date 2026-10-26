using System;
using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.MathTools;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Pure maths of painting and draping on the mannequin in world (lane/mannequin-paint, docs/embroidery.md "In-world
/// painting"; offline-tested): world ray → garment mesh space, brush footprint in body space, cloth anchor / turn /
/// edge nudge of a segment placement.
/// </summary>
public static class MannequinPaint
{
    /// <summary>
    /// Block-local → mesh space of the mannequin's garments (inverse of BEBehaviorMannequin.OnTesselation's
    /// T(0.5, 0, 0.5) RotY(angle) T(-0.5, 0, -0.5) after <c>mesh.Translate(shapeOffset)</c>).
    /// </summary>
    public static double[] MeshToBlock(double angleRad, V3 shapeOffset)
    {
        var m = Mat4d.Create();
        Mat4d.Translate(m, m, 0.5, 0, 0.5);
        Mat4d.RotateY(m, m, angleRad);
        Mat4d.Translate(m, m, -0.5, 0, -0.5);
        Mat4d.Translate(m, m, shapeOffset.X, shapeOffset.Y, shapeOffset.Z);
        return m;
    }

    /// <summary>A world ray (origin, direction) relative to the mannequin block origin, in garment mesh space.</summary>
    public static (V3 origin, V3 dir) RayToMesh(V3 originRelBlock, V3 dir, double angleRad, V3 shapeOffset)
    {
        var inv = Mat4d.Invert(Mat4d.Create(), MeshToBlock(angleRad, shapeOffset));
        return (V3.Point(inv, originRelBlock), V3.Dir(inv, dir));
    }

    /// <summary>Mesh-space point → block-local (for tests and highlight placement).</summary>
    public static V3 MeshPointToBlock(V3 mesh, double angleRad, V3 shapeOffset) => V3.Point(MeshToBlock(angleRad, shapeOffset), mesh);

    /// <summary>
    /// Texels under a square brush of <paramref name="brush"/> cells around texel (x, y): in body space (same box of the same segment,
    /// u / v within <see cref="WeavingState.BrushSpan"/> of the hovered cell, so a brush crosses the box's edges like the
    /// cloth does; other boxes of the segment, e.g. a button strip in front, are left alone), or a texture square for a texel without a segment. Row-major indices, sorted.
    /// </summary>
    public static int[] Footprint(GarmentSurface surf, BodyWrapMap map, int x, int y, int brush)
    {
        int w = map.Width, h = map.Height;
        if (x < 0 || y < 0 || x >= w || y >= h) return Array.Empty<int>();
        int i0 = y * w + x;
        brush = Math.Max(1, brush);
        int seg = map.Seg[i0];
        if (seg < 0)
        {
            var (x0, x1) = WeavingState.BrushSpan(x, brush, w);
            var (y0, y1) = WeavingState.BrushSpan(y, brush, h);
            var sq = new List<int>();
            for (int yy = y0; yy < y1; yy++) for (int xx = x0; xx < x1; xx++) sq.Add(yy * w + xx);
            return sq.ToArray();
        }
        int u0 = map.U[i0] - (brush - 1) / 2, v0 = map.V[i0] - (brush - 1) / 2;
        int el = surf.Faces[map.Face[i0]].Element;
        var list = new List<int>();
        for (int i = 0; i < map.Seg.Length; i++)
        {
            if (map.Seg[i] != seg || surf.Faces[map.Face[i]].Element != el) continue;
            if (map.U[i] >= u0 && map.U[i] < u0 + brush && map.V[i] >= v0 && map.V[i] < v0 + brush) list.Add(i);
        }
        if (!list.Contains(i0)) list.Add(i0);
        list.Sort();
        return list.ToArray();
    }

    /// <summary>
    /// Stitches (<paramref name="code"/>, <paramref name="hex"/>) or picks out (<paramref name="code"/> null) the
    /// <paramref name="texels"/> on a copy of <paramref name="applied"/>, only where <paramref name="mask"/> takes stitches
    /// (pick out: anywhere). Returns the working layer (unchanged = same content).
    /// </summary>
    public static Embroidery Stroke(Embroidery? applied, int width, int height, IEnumerable<int> texels, bool[] mask, string? code, string? hex)
    {
        var layer = applied?.Clone() ?? new Embroidery(width, height);
        foreach (int i in texels)
        {
            int x = i % width, y = i / width;
            if (code == null) layer.PickOut(x, y, 1);
            else if (mask[i]) GarmentEmbroidery.Paint(layer, mask, x, y, 1, code, hex ?? TwineColors.PlainHex);
        }
        return layer;
    }

    /// <summary>Segment whose placement moves texel <paramref name="i"/> (an arm's is the torso's while sleeves continue); -1 = none.</summary>
    public static int PlacementSegment(BodyWrapMap map, int i, bool continues)
    {
        int seg = i >= 0 && i < map.Seg.Length ? map.Seg[i] : -1;
        return seg >= 0 && continues && BodySegments.IsArm(seg) ? BodySegments.Torso : seg;
    }

    /// <summary>
    /// Placement for which cloth cell (<paramref name="cx"/>, <paramref name="cy"/>) shows at texel <paramref name="i"/>,
    /// keeping turn <paramref name="rot"/> (the part's own offset <paramref name="part"/> stays). Null for a texel
    /// without a segment.
    /// </summary>
    public static SegmentPlacement? Anchor(BodyWrapMap map, int i, bool continues, PieceAssignment part, int rot, int cw, int ch, int cx = 0, int cy = 0)
    {
        if (!map.Coords(i, continues, out _, out int u, out int v)) return null;
        for (int dv = 0; dv < GarmentLayout.OffsetPeriod; dv++)
            for (int du = 0; du < GarmentLayout.OffsetPeriod; du++)
            {
                var p = SegmentPlacement.Of(du, dv, rot);
                if (GarmentLayout.WrapCell(u, v, p, part, cw, ch) == (GarmentLayout.Mod(cx, cw), GarmentLayout.Mod(cy, ch))) return p;
            }
        return null;
    }

    /// <summary>A quarter turn of <paramref name="current"/> about texel <paramref name="i"/>: the cell under it stays.</summary>
    public static SegmentPlacement? TurnAbout(BodyWrapMap map, int i, bool continues, PieceAssignment part, SegmentPlacement current, int cw, int ch)
    {
        if (!map.Coords(i, continues, out _, out int u, out int v)) return null;
        var (cx, cy) = GarmentLayout.WrapCell(u, v, current, part, cw, ch);
        return Anchor(map, i, continues, part, current.Rot + 1, cw, ch, cx, cy);
    }

    /// <summary>Share of a face's width / height near each edge that nudges instead of anchoring.</summary>
    public const double EdgeZone = 0.2;

    /// <summary>
    /// Edge nudge (decision, docs/embroidery.md "In-world painting"): a click in the outer <see cref="EdgeZone"/> of the
    /// face along u (or v) moves the cloth one cell towards that edge; (0, 0) = the middle (anchor there instead).
    /// <paramref name="s"/>, <paramref name="t"/>: hit position on face <paramref name="face"/> (0..1).
    /// </summary>
    public static (int du, int dv) EdgeNudge(GarmentSurface surf, BodyWrapMap map, int face, int tx, int ty)
    {
        if (!map.Unfolds.TryGetValue(face, out var uf)) return (0, 0);
        var f = surf.Faces[face];
        double fx = (tx - f.X1 + 0.5) / Math.Max(1, f.X2 - f.X1), fy = (ty - f.Y1 + 0.5) / Math.Max(1, f.Y2 - f.Y1);
        double fu = uf.UAlongX ? fx : fy, fv = uf.VAlongX ? fx : fy;
        if (uf.USign < 0) fu = 1 - fu;
        if (uf.VSign < 0) fv = 1 - fv;
        int du = fu < EdgeZone ? -1 : fu > 1 - EdgeZone ? 1 : 0;
        int dv = fv < EdgeZone ? -1 : fv > 1 - EdgeZone ? 1 : 0;
        if (uf.Columns < 3) du = 0; // too narrow to have edges
        if (uf.Rows < 3) dv = 0;
        return (du, dv);
    }

    /// <summary>The part showing at texel <paramref name="i"/> (highest present layer), -1 = none.</summary>
    public static int PartAt(GarmentDef g, int i, Func<int, SlotMaterial?> materialOf)
    {
        var mats = GarmentLayout.Materials(g, materialOf);
        int[] visible = GarmentLayout.VisibleOwners(g, p => GarmentLayout.Present(g, p, mats[p]));
        return i >= 0 && i < visible.Length ? visible[i] : -1;
    }

    /// <summary>
    /// Part a held material drapes onto at texel <paramref name="i"/>: the visible part when it takes the material,
    /// else the topmost covering part (any layer) that takes it; -1 = none.
    /// </summary>
    public static int DrapePart(GarmentDef g, int i, Func<int, SlotMaterial?> materialOf, SlotMaterial held)
    {
        int vis = PartAt(g, i, materialOf);
        if (vis >= 0 && !g.Parts[vis].IsFixed && g.Parts[vis].Allows(held)) return vis;
        return Enumerable.Range(0, g.Parts.Length).Where(p => g.Coverage[p][i] && !g.Parts[p].IsFixed && g.Parts[p].Allows(held))
            .OrderByDescending(p => g.Parts[p].Layer).DefaultIfEmpty(-1).First();
    }
}
