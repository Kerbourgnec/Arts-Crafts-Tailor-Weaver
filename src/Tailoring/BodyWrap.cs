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
/// <see cref="SleevesContinue"/>, <see cref="SleevesSeparate"/>). Tree: int <c>wrap</c> = version (1, 2), string <c>sleeves</c>,
/// int[] <c>segDu</c>, <c>segDv</c>, <c>segRot</c>. A garment without it (sewn before this lane) composes in UV space.
/// </summary>
public sealed class WrapLayout
{
    public const string SleevesContinue = "continue", SleevesSeparate = "separate";

    /// <summary>
    /// Layout version (lane/wrap-closure). 1 = stacks sewn in 0.1.0-dev.4 (ring closes in the middle of the back, texel owner = first face,
    /// only arms continue the torso's cloth); 2 = ring closes in a hidden place, head and legs continue the torso's cloth too, texels shared by
    /// several faces take the coordinates of the most visible one. Tree key <c>wrap</c>; new layouts are 2, a tree stored with 1 keeps its look.
    /// </summary>
    public const int CurrentVersion = 2;
    public int Version = CurrentVersion;

    public string Sleeves = "";
    public SegmentPlacement[] Segments = new SegmentPlacement[BodySegments.Count];

    public WrapLayout Clone() => new() { Version = Version, Sleeves = Sleeves, Segments = (SegmentPlacement[])Segments.Clone() };

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
        tree.SetInt("wrap", Version);
        tree.SetString("sleeves", Sleeves);
        tree["segDu"] = new IntArrayAttribute(Segments.Select(s => s.Du).ToArray());
        tree["segDv"] = new IntArrayAttribute(Segments.Select(s => s.Dv).ToArray());
        tree["segRot"] = new IntArrayAttribute(Segments.Select(s => s.Rot).ToArray());
    }

    /// <summary>The layout stored in <paramref name="tree"/>, null when it has none (UV-space garment).</summary>
    public static WrapLayout? FromTree(ITreeAttribute? tree)
    {
        int version = tree?.GetInt("wrap") ?? 0;
        if (tree == null || version < 1) return null;
        var w = new WrapLayout { Version = Math.Min(version, CurrentVersion), Sleeves = tree.GetString("sleeves", "") ?? "" };
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
        if (Version >= 2) mixInt(Version); // version 1 hashes as before
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
    /// <summary>Layout version this map was built for (<see cref="WrapLayout.Version"/>).</summary>
    public readonly int Version;
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

    BodyWrapMap(int w, int h, int version)
    {
        Width = w;
        Height = h;
        Version = version;
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
        bool c = (continues && BodySegments.IsArm(seg)) || (Version >= 2 && BodySegments.IsJoined(seg));
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

    /// <summary>
    /// Map of <paramref name="surf"/> for layout <paramref name="version"/>; <paramref name="seams"/> = the recipe's <see cref="GarmentDef.WrapSeam"/>
    /// (version 2: where a ring closes, by segment code or element name: front, back, left, right, inner, outer); <paramref name="owner"/> = the
    /// recipe's <see cref="GarmentDef.WrapOwner"/> (version 2: "first" = a shared texel keeps the first face's coordinates).
    /// </summary>
    public static BodyWrapMap Build(GarmentSurface surf, int version = 1, IReadOnlyDictionary<string, string>? seams = null, string? owner = null)
    {
        var map = new BodyWrapMap(surf.Width, surf.Height, version);
        bool ownerFirst = owner == WrapOwnerFirst;
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
            UnfoldElement(map, surf, el, grp.ToList(), frame, torso, tpu, faceCoords, seams);
        }
        // Texel owner (shared UVs, e.g. mirrored sleeves, move together): the first face covering it (version 1); version 2: the most visible
        // one (front-facing first, FaceWeight), then the first.
        double midZ = surf.Bones.TryGetValue("UpperTorso", out var tb) ? tb.m[14] + tb.size.Z / 32 : 0.5;
        var weights = new Dictionary<int, double>();
        double Weight(int fi)
        {
            if (!weights.TryGetValue(fi, out double w))
            {
                var sf = surf.Faces[fi];
                weights[fi] = w = FaceWeight(sf.Normal, sf.C0 + (sf.S + sf.T) * 0.5, midZ);
            }
            return w;
        }
        for (int y = 0; y < surf.Height; y++)
        {
            for (int x = 0; x < surf.Width; x++)
            {
                int i = y * surf.Width + x, best = -1;
                double bw = -1;
                foreach (int f in surf.FacesOf(x, y))
                {
                    if (!faceCoords.TryGetValue(f, out var fc) || !fc.ContainsKey(i)) continue;
                    if (version < 2 || ownerFirst) { best = f; break; }
                    double w = Weight(f);
                    if (w > bw) { best = f; bw = w; }
                }
                if (best < 0) continue;
                map.Seg[i] = surf.Faces[best].Segment;
                map.Face[i] = best;
                (map.U[i], map.V[i], map.UC[i], map.VC[i]) = faceCoords[best][i];
            }
        }
        return map;
    }

    /// <summary>
    /// How visible a face is on the dressed seraph (version 2: texel owner; the audit's "visible break" metric): from its outward unit normal and
    /// centre (mesh space), <paramref name="midZ"/> = lateral centre of the torso. Front-facing 1.0, back / outer side / top 0.3,
    /// flank between the arms / inner side of a limb 0.1, underside 0.05.
    /// </summary>
    public static double FaceWeight(V3 n, V3 c, double midZ)
    {
        if (n.Y > 0.6) return 0.3;
        if (n.Y < -0.6) return 0.05;
        double front = -n.X, zc = c.Z - midZ;
        if (front > 0.5) return 1;
        if (front < -0.5) return 0.3;
        return Math.Abs(zc) < 0.14 || n.Z * zc < 0 ? 0.1 : 0.3;
    }

    /// <summary>
    /// Ring role (0 front, 1 +z = the wearer's left, 2 back, 3 -z = right) where an element's ring closes (version 2): a hidden place. Torso and limbs:
    /// the inner side (under the arm, inside of the leg; the torso's own centre: its left flank), the head: the back. <paramref name="lat"/> =
    /// z component (torso frame) of each ring role's normal; <paramref name="word"/> = the recipe's override.
    /// </summary>
    public const string WrapOwnerVisible = "visible", WrapOwnerFirst = "first";

    public static readonly string[] SeamWords = { "front", "back", "left", "right", "inner", "outer" };

    static int SeamRole(int segment, string? word, int shiftT, double[] lat)
    {
        int Towards(int sign)
        {
            int best = 2;
            double bk = 0.3;
            for (int k = 1; k < 4; k += 2) if (sign * lat[k] > bk) { best = k; bk = sign * lat[k]; }
            return best;
        }
        int Inner() => Math.Abs(shiftT) > 2 ? Towards(shiftT > 0 ? -1 : 1) : Towards(1);
        return word switch
        {
            "front" => 0,
            "back" => 2,
            "left" => Towards(1),
            "right" => Towards(-1),
            "inner" => Inner(),
            "outer" => Math.Abs(shiftT) > 2 ? Towards(shiftT > 0 ? 1 : -1) : Towards(-1),
            _ => segment == BodySegments.Head ? 2 : Inner(),
        };
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
        double tpu, Dictionary<int, Dictionary<int, (int, int, int, int)>> outCoords, IReadOnlyDictionary<string, string>? seams)
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
        int split = widths[0] + widths[1] + widths[2] / 2; // version 1: the ring closes in the middle of the back face

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
        if (map.Version >= 2)
        {
            string? word = null;
            if (seams != null && (seams.TryGetValue(el.Name, out word) || seams.TryGetValue(BodySegments.Codes[el.Segment], out word))) { }
            var lat = ringNormal.Select(n => torso.Dir(MeshDir(n)).Z).ToArray();
            int role = SeamRole(el.Segment, word, shiftT, lat);
            // middle of the face; a face narrower than 4 texels closes at its end towards the back (a thin strap would otherwise tear at its front edge)
            int before = widths.Take(role).Sum();
            split = widths[role] >= 4 ? before + widths[role] / 2 : role == 3 ? before : before + widths[role];
        }

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

    /// <summary>Map of the recipe for layout <paramref name="version"/> (<see cref="WrapLayout.Version"/>; default the current one).</summary>
    public static BodyWrapMap? Map(ICoreAPI api, GarmentDef def, int version = WrapLayout.CurrentVersion)
        => Cache(api).GetOrAdd(def.Code + "#" + version, _ => Surface(api, def) is { } s ? BodyWrapMap.Build(s, version, def.WrapSeam, def.WrapOwner) : null);

    /// <summary>Composition input for <paramref name="layout"/> (null = UV space: no layout, or the map failed).</summary>
    public static BodyWrap? For(ICoreAPI? api, GarmentDef def, WrapLayout? layout)
        => api == null || layout == null || Map(api, def, layout.Version) is not { } m ? null : new BodyWrap(m, layout);
}
