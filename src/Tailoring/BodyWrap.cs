using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using VsShape = Vintagestory.API.Common.Shape;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>Placement of the cloth on one body segment (lane/body-wrap): offset in cells along u / v, quarter turns.</summary>
public readonly record struct SegmentPlacement(int Du, int Dv, int Rot)
{
    public static SegmentPlacement Of(int du, int dv, int rot)
        => new(GarmentLayout.Mod(du, GarmentLayout.OffsetPeriod), GarmentLayout.Mod(dv, GarmentLayout.OffsetPeriod), GarmentLayout.Mod(rot, 4));
}

/// <summary>
/// How a garment's cloth is wrapped around the body (lane/body-wrap): one <see cref="SegmentPlacement"/> per
/// <see cref="BodySegments"/> entry and the sleeves mode ("" = the recipe's <see cref="GarmentDef.Sleeves"/>,
/// <see cref="SleevesContinue"/>, <see cref="SleevesSeparate"/>). Tree: int <c>wrap</c> = 1, string <c>sleeves</c>,
/// int[] <c>segDu</c>, <c>segDv</c>, <c>segRot</c>. A garment without it (sewn before this lane) composes in UV space.
/// </summary>
public sealed class WrapLayout
{
    public const string SleevesContinue = "continue", SleevesSeparate = "separate";

    public string Sleeves = "";
    public SegmentPlacement[] Segments = new SegmentPlacement[BodySegments.Count];

    public WrapLayout Clone() => new() { Sleeves = Sleeves, Segments = (SegmentPlacement[])Segments.Clone() };

    /// <summary>Sleeves continue the torso (arm segments use the torso's placement and coordinates) for recipe <paramref name="g"/>.</summary>
    public bool Continues(GarmentDef g) => (Sleeves.Length > 0 ? Sleeves : g.Sleeves) != SleevesSeparate;

    /// <summary>Placement that applies on segment <paramref name="seg"/> (an arm takes the torso's when sleeves continue).</summary>
    public SegmentPlacement For(int seg, bool continues)
        => seg < 0 || seg >= Segments.Length ? default : Segments[continues && BodySegments.IsArm(seg) ? BodySegments.Torso : seg];

    public bool Set(int seg, SegmentPlacement p)
    {
        if (seg < 0 || seg >= Segments.Length) return false;
        Segments[seg] = SegmentPlacement.Of(p.Du, p.Dv, p.Rot);
        return true;
    }

    public void ToTree(ITreeAttribute tree)
    {
        tree.SetInt("wrap", 1);
        tree.SetString("sleeves", Sleeves);
        tree["segDu"] = new IntArrayAttribute(Segments.Select(s => s.Du).ToArray());
        tree["segDv"] = new IntArrayAttribute(Segments.Select(s => s.Dv).ToArray());
        tree["segRot"] = new IntArrayAttribute(Segments.Select(s => s.Rot).ToArray());
    }

    /// <summary>The layout stored in <paramref name="tree"/>, null when it has none (UV-space garment).</summary>
    public static WrapLayout? FromTree(ITreeAttribute? tree)
    {
        if (tree == null || tree.GetInt("wrap") != 1) return null;
        var w = new WrapLayout { Sleeves = tree.GetString("sleeves", "") ?? "" };
        if (w.Sleeves != SleevesContinue && w.Sleeves != SleevesSeparate) w.Sleeves = "";
        int[] du = (tree["segDu"] as IntArrayAttribute)?.value ?? Array.Empty<int>();
        int[] dv = (tree["segDv"] as IntArrayAttribute)?.value ?? Array.Empty<int>();
        int[] rot = (tree["segRot"] as IntArrayAttribute)?.value ?? Array.Empty<int>();
        for (int s = 0; s < BodySegments.Count; s++)
            w.Segments[s] = SegmentPlacement.Of(s < du.Length ? du[s] : 0, s < dv.Length ? dv[s] : 0, s < rot.Length ? rot[s] : 0);
        return w;
    }

    /// <summary>Mixes the layout into a garment hash.</summary>
    public void Mix(Action<int> mixInt)
    {
        mixInt(0x77726170); // "wrap"
        foreach (char c in Sleeves) mixInt(c);
        foreach (var s in Segments) { mixInt(s.Du); mixInt(s.Dv); mixInt(s.Rot); }
    }
}

/// <summary>
/// Body-space cloth coordinates of every garment texel (lane/body-wrap, docs/garments.md "Body-space wrap"), from the
/// garment's <see cref="GarmentSurface"/>. Per element (box) of a segment, its four side faces are unfolded around the
/// segment's vertical axis: u = texel columns counted around the box (continuous across its vertical edges; u = 0 at
/// the front centre, the ring closes in the middle of the back face), v = texel rows down from the segment's top.
/// One cell per texel on both axes (isotropic copies: the global density, 1 cell = 1 texel). Boxes of one segment are
/// aligned by their front centre and top (in cells at the garment's texels per unit). Up / down faces fold over from
/// the front face. Tilted elements (skirt flaps) unfold in their own box frame. Arms also carry "continue" coordinates:
/// shift and height measured in the torso's frame, so the torso's pattern runs on across the sleeve fronts.
/// </summary>
public sealed class BodyWrapMap
{
    public readonly int Width, Height;
    /// <summary>Segment of each texel (row-major), -1 = no face (UV-space fallback).</summary>
    public readonly int[] Seg;
    public readonly int[] U, V;
    /// <summary>Arm texels: coordinates in the torso's frame (sleeves continue); else = U / V.</summary>
    public readonly int[] UC, VC;
    /// <summary>Face of the surface each texel takes its coordinates from (-1 = none).</summary>
    public readonly int[] Face;
    /// <summary>Per face: texel step along u and v in texture space (dx, dy each), for checks and tools.</summary>
    public readonly Dictionary<int, FaceUnfold> Unfolds = new();

    public sealed class FaceUnfold
    {
        /// <summary>Ring position 0 front, 1 right, 2 back, 3 left; 4 up cap, 5 down cap.</summary>
        public int Role;
        /// <summary>Texture axis of u (true = x) and its sign; same for v.</summary>
        public bool UAlongX, VAlongX;
        public int USign, VSign;
        /// <summary>Texel columns / rows of the face (u / v extents).</summary>
        public int Columns, Rows;
    }

    BodyWrapMap(int w, int h)
    {
        Width = w;
        Height = h;
        Seg = new int[w * h];
        U = new int[w * h];
        V = new int[w * h];
        UC = new int[w * h];
        VC = new int[w * h];
        Face = new int[w * h];
        Array.Fill(Seg, -1);
        Array.Fill(Face, -1);
    }

    /// <summary>Cloth coordinates of texel <paramref name="i"/> for the sleeves mode; false = no segment (UV space).</summary>
    public bool Coords(int i, bool continues, out int seg, out int u, out int v)
    {
        seg = Seg[i];
        if (seg < 0) { u = v = 0; return false; }
        bool c = continues && BodySegments.IsArm(seg);
        u = c ? UC[i] : U[i];
        v = c ? VC[i] : V[i];
        return true;
    }

    static readonly V3 SegUp = new(0, 1, 0), SegFront = new(-1, 0, 0);

    static readonly V3[] Axes = { new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1) };

    /// <summary>Mesh-space point → segment frame (inverse of the frame bone's matrix).</summary>
    sealed class Frame
    {
        public readonly double[] Inv;
        public readonly V3 Size;
        public Frame(double[] m, V3 size)
        {
            Inv = Mat4d.Invert(Mat4d.Create(), m);
            Size = size;
        }
        public V3 Local(V3 p) => V3.Point(Inv, p) * 16; // shape units
        public V3 Dir(V3 d) => V3.Dir(Inv, d);
        public double CentreLateral => Size.Z / 2;
        public double Top => Size.Y;
    }

    public static BodyWrapMap Build(GarmentSurface surf)
    {
        var map = new BodyWrapMap(surf.Width, surf.Height);
        var frames = new Frame?[BodySegments.Count];
        for (int s = 0; s < BodySegments.Count; s++)
            frames[s] = surf.Bones.TryGetValue(BodySegments.FrameBones[s], out var b) ? new Frame(b.m, b.size) : null;
        double tpu = surf.TexelsPerUnit;
        var byElement = surf.Faces.GroupBy(f => f.Element);
        // Per face: (u, v) per texel in its own segment frame and the torso frame (arms).
        var faceCoords = new Dictionary<int, Dictionary<int, (int u, int v, int uc, int vc)>>();
        foreach (var grp in byElement)
        {
            var el = surf.Elements[grp.Key];
            var frame = frames[el.Segment] ?? frames[BodySegments.Torso];
            var torso = frames[BodySegments.Torso] ?? frame;
            if (frame == null || torso == null) continue;
            UnfoldElement(map, surf, el, grp.ToList(), frame, torso, tpu, faceCoords);
        }
        // Texel owner: the first face covering it (shared UVs, e.g. mirrored sleeves, move together).
        for (int y = 0; y < surf.Height; y++)
        {
            for (int x = 0; x < surf.Width; x++)
            {
                foreach (int f in surf.FacesOf(x, y))
                {
                    if (!faceCoords.TryGetValue(f, out var fc) || !fc.TryGetValue(y * surf.Width + x, out var c)) continue;
                    int i = y * surf.Width + x;
                    map.Seg[i] = surf.Faces[f].Segment;
                    map.Face[i] = f;
                    (map.U[i], map.V[i], map.UC[i], map.VC[i]) = c;
                    break;
                }
            }
        }
        return map;
    }

    /// <summary>Texture axis + sign of mesh-space direction <paramref name="dir"/> on face <paramref name="f"/>.</summary>
    static (bool alongX, int sign) TexAxis(SurfaceFace f, V3 dir)
    {
        double ds = dir.Dot(f.S) / f.S.Length, dt = dir.Dot(f.T) / f.T.Length;
        double gx, gy;
        if (Math.Abs(ds) >= Math.Abs(dt)) { gx = Math.Sign(ds) * f.Us; gy = Math.Sign(ds) * f.Vs; }
        else { gx = Math.Sign(dt) * f.Ut; gy = Math.Sign(dt) * f.Vt; }
        return Math.Abs(gx) >= Math.Abs(gy) ? (true, Math.Sign(gx)) : (false, Math.Sign(gy));
    }

    static int Raw(int x, int y, (bool alongX, int sign) a) => a.sign * (a.alongX ? x : y);

    static void UnfoldElement(BodyWrapMap map, GarmentSurface surf, SurfaceElement el, List<SurfaceFace> faces, Frame frame, Frame torso,
        double tpu, Dictionary<int, Dictionary<int, (int, int, int, int)>> outCoords)
    {
        // Element-local axes closest to the segment's up and front (a tilted flap keeps its own box frame).
        V3 InSeg(V3 a) => frame.Dir(V3.Dir(el.Matrix, a)).Normalized;
        V3 up = Axes.OrderByDescending(a => InSeg(a).Dot(SegUp)).First();
        V3 front = Axes.Where(a => Math.Abs(a.Dot(up)) < 0.5).OrderByDescending(a => InSeg(a).Dot(SegFront)).First();
        V3 right = (-front).Cross(up);
        V3[] ringNormal = { front, right, -front, -right };
        V3[] ringTangent = { right, -front, -right, front };
        V3 Ext(V3 a) => new(Math.Abs(a.X) * el.Size.X, Math.Abs(a.Y) * el.Size.Y, Math.Abs(a.Z) * el.Size.Z);
        double Len(V3 a) { var e = Ext(a); return e.X + e.Y + e.Z; }
        V3 MeshDir(V3 local) => V3.Dir(el.Matrix, local).Normalized;

        // Role of each face; texel index ranges per face along its u / v directions.
        var info = new Dictionary<int, (int role, (bool, int) ua, (bool, int) va, int i0, int j0, int cols, int rows, int uOff, int vOff)>();
        var inv = Mat4d.Invert(Mat4d.Create(), el.Matrix);
        // Distance (shape units) of a mesh point from the box side where direction d starts.
        double From(V3 mesh, V3 d)
        {
            var l = V3.Point(inv, mesh) * 16;
            double min = Math.Min(0, d.X * el.Size.X) + Math.Min(0, d.Y * el.Size.Y) + Math.Min(0, d.Z * el.Size.Z);
            return l.Dot(d) - min;
        }
        foreach (var f in faces)
        {
            var n = GarmentSurface.LocalNormal(f.Facing);
            int role = -1;
            for (int k = 0; k < 4; k++) if (n.Dot(ringNormal[k]) > 0.5) role = k;
            if (n.Dot(up) > 0.5) role = 4;
            if (n.Dot(up) < -0.5) role = 5;
            if (role < 0) continue;
            // u runs along the ring tangent (caps: along the front's), v down (caps: away from the front edge).
            V3 uDir = role < 4 ? ringTangent[role] : right;
            V3 vDir = role < 4 ? -up : -front;
            var ua = TexAxis(f, MeshDir(uDir));
            var va = TexAxis(f, MeshDir(vDir));
            int i0 = int.MaxValue, i1 = int.MinValue, j0 = int.MaxValue, j1 = int.MinValue;
            (int x, int y) ti = default, tj = default;
            for (int y = f.Y1; y < f.Y2; y++)
            {
                for (int x = f.X1; x < f.X2; x++)
                {
                    int ri = Raw(x, y, ua), rj = Raw(x, y, va);
                    if (ri < i0) ti = (x, y);
                    if (rj < j0) tj = (x, y);
                    i0 = Math.Min(i0, ri); i1 = Math.Max(i1, ri);
                    j0 = Math.Min(j0, rj); j1 = Math.Max(j1, rj);
                }
            }
            if (i0 == int.MaxValue) continue;
            // Offset of the first row (columns chain exactly around the box): the face's MIDDLE row sits where the garment density puts it from
            // the box edge (exact on faces spanning the whole box side; a side the anisotropy fix shrank by up to 7 % then
            // differs by at most one cell at each end instead of drifting from one end).
            int im = (i0 + i1) / 2, jm = (j0 + j1) / 2;
            (int x, int y) mi = ti, mj = tj;
            for (int y = f.Y1; y < f.Y2; y++)
                for (int x = f.X1; x < f.X2; x++)
                {
                    if (Raw(x, y, ua) == im) mi = (x, y);
                    if (Raw(x, y, va) == jm) mj = (x, y);
                }
            int uOff = (int)Math.Round(From(surf.TexelPoint(f.Index, mi.x, mi.y), uDir) * tpu - 0.5) - (im - i0);
            int vOff = (int)Math.Round(From(surf.TexelPoint(f.Index, mj.x, mj.y), vDir) * tpu - 0.5) - (jm - j0);
            info[f.Index] = (role, ua, va, i0, j0, i1 - i0 + 1, j1 - j0 + 1, uOff, vOff);
            map.Unfolds[f.Index] = new FaceUnfold
            {
                Role = role, UAlongX = ua.alongX, USign = ua.sign, VAlongX = va.alongX, VSign = va.sign, Columns = i1 - i0 + 1, Rows = j1 - j0 + 1,
            };
        }
        // Ring widths (texel columns); a missing face counts its box edge at the garment density.
        var widths = new int[4];
        for (int k = 0; k < 4; k++)
        {
                        // Texel columns of the face (exact chaining around the box), the box edge at the garment density when missing.
            var present = info.Values.Where(v => v.role == k).ToList();
            widths[k] = present.Count > 0 ? present.Max(v => v.cols) : (int)Math.Round(Len(ringTangent[k]) * tpu);
        }
        int frontRows = (int)Math.Round(Len(up) * tpu);
        int perimeter = widths.Sum();
        int c = widths[0] / 2;
        int split = widths[0] + widths[1] + widths[2] / 2;

        // Alignment within the segment: front-centre lateral position and top, in cells, in the segment's frame and the torso's.
        var centreLocal = el.Size * 0.5;
        V3 topLocal = centreLocal + new V3(up.X * el.Size.X, up.Y * el.Size.Y, up.Z * el.Size.Z) * 0.5;
        V3 ToMesh(V3 localUnits) => V3.Point(el.Matrix, localUnits * (1.0 / 16));
        (int shift, int vTop) Align(Frame fr)
        {
            var centre = fr.Local(ToMesh(centreLocal));
            var top = fr.Local(ToMesh(topLocal));
            return ((int)Math.Round((centre.Z - fr.CentreLateral) * tpu), (int)Math.Round((fr.Top - top.Y) * tpu));
        }
        var (shiftS, vTopS) = Align(frame);
        var (shiftT, vTopT) = Align(torso);

        foreach (var f in faces)
        {
            if (!info.TryGetValue(f.Index, out var fi)) continue;
            var coords = new Dictionary<int, (int, int, int, int)>();
            for (int y = f.Y1; y < f.Y2; y++)
            {
                for (int x = f.X1; x < f.X2; x++)
                {
                    int i = Raw(x, y, fi.ua) - fi.i0, j = Raw(x, y, fi.va) - fi.j0;
                    int u, v; // relative to the box: u from its front centre, v from its top
                    if (fi.role < 4)
                    {
                        int lin = widths.Take(fi.role).Sum() + i;
                        u = lin - c - (lin >= split ? perimeter : 0);
                        v = fi.vOff + j;
                    }
                    else
                    {
                        u = i - c;
                        v = fi.role == 4 ? -1 - (fi.vOff + j) : frontRows + fi.vOff + j;
                    }
                    coords[y * surf.Width + x] = (u + shiftS, v + vTopS, u + shiftT, v + vTopT);
                }
            }
            outCoords[f.Index] = coords;
        }
    }
}

/// <summary>Body-space composition input (lane/body-wrap): the garment's texel map and the layout that places the cloth.</summary>
public sealed record BodyWrap(BodyWrapMap Map, WrapLayout Layout);

/// <summary>
/// <see cref="BodyWrapMap"/> per garment recipe (both sides; built once from the recipe's model minus
/// <see cref="GarmentDef.HideElements"/> on the mannequin's dressable skeleton <see cref="SkeletonShape"/>).
/// </summary>
public static class BodyWrapCache
{
    /// <summary>The mannequin's dressable shape (vanilla mannequin.json <c>dressableEntityShape</c>), static pose.</summary>
    public const string SkeletonShape = "game:entity/humanoid/seraph-faceless";
    const string CacheKey = TailorModSystem.Domain + "-bodywrap";

    static ConcurrentDictionary<string, BodyWrapMap?> Cache(ICoreAPI api)
        => ObjectCacheUtil.GetOrCreate(api, CacheKey, () => new ConcurrentDictionary<string, BodyWrapMap?>());

    static ConcurrentDictionary<string, GarmentSurface?> Surfaces(ICoreAPI api)
        => ObjectCacheUtil.GetOrCreate(api, CacheKey + "-surfaces", () => new ConcurrentDictionary<string, GarmentSurface?>());

    /// <summary>Surface of the recipe's model (every element but <see cref="GarmentDef.HideElements"/>), null when a shape is missing.</summary>
    public static GarmentSurface? Surface(ICoreAPI api, GarmentDef def) => Surfaces(api).GetOrAdd(def.Code, _ =>
    {
        try
        {
            var skeleton = VsShape.TryGet(api, ItemTailoredGarment.ShapeLocation(SkeletonShape));
            var shape = ItemTailoredGarment.LoadShape(api, def, def.HideElements);
            if (skeleton == null || shape == null)
            {
                api.Logger.Warning("[actailor] body wrap of {0}: shape missing (skeleton {1}, model {2})", def.Code, skeleton != null, shape != null);
                return null;
            }
            return GarmentSurface.Build(skeleton, shape, def.TextureKey, def.TextureWidth, def.TextureHeight);
        }
        catch (Exception e)
        {
            api.Logger.Error("[actailor] body wrap of {0} failed: {1}", def.Code, e);
            return null;
        }
    });

    public static BodyWrapMap? Map(ICoreAPI api, GarmentDef def) => Cache(api).GetOrAdd(def.Code, _ => Surface(api, def) is { } s ? BodyWrapMap.Build(s) : null);

    /// <summary>Composition input for <paramref name="layout"/> (null = UV space: no layout, or the map failed).</summary>
    public static BodyWrap? For(ICoreAPI? api, GarmentDef def, WrapLayout? layout)
        => api == null || layout == null || Map(api, def) is not { } m ? null : new BodyWrap(m, layout);
}
