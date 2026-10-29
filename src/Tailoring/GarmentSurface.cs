using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VsShape = Vintagestory.API.Common.Shape;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Body segments of the seraph skeleton (lane/body-wrap): the cloth of a part is laid around each segment's vertical
/// axis (<see cref="BodyWrapMap"/>), and the player places it per segment (<see cref="WrapLayout"/>).
/// </summary>
public static class BodySegments
{
    public const int Torso = 0, Head = 1, UpperArmR = 2, UpperArmL = 3, LowerArmR = 4, LowerArmL = 5,
        UpperLegR = 6, UpperLegL = 7, LowerLegR = 8, LowerLegL = 9;
    public const int Count = 10;

    /// <summary>Codes (lang <c>segment-{code}</c>), indexed by segment.</summary>
    public static readonly string[] Codes =
        { "torso", "head", "upperarm-right", "upperarm-left", "forearm-right", "forearm-left", "thigh-right", "thigh-left", "shin-right", "shin-left" };

    /// <summary>Skeleton element whose frame (front = -x, up = +y, right = +z) a segment unfolds in.</summary>
    public static readonly string[] FrameBones =
        { "UpperTorso", "Head", "UpperArmR", "UpperArmL", "LowerArmR", "LowerArmL", "UpperFootR", "UpperFootL", "LowerFootR", "LowerFootL" };

    /// <summary>Skeleton elements that start a segment; every other element (attachments, item anchors) takes its parent's.</summary>
    static readonly Dictionary<string, int> Starts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["root"] = Torso, ["LowerTorso"] = Torso, ["UpperTorso"] = Torso, ["Neck"] = Torso, ["Head"] = Head,
        ["UpperArmR"] = UpperArmR, ["UpperArmL"] = UpperArmL, ["LowerArmR"] = LowerArmR, ["LowerArmL"] = LowerArmL,
        ["UpperFootR"] = UpperLegR, ["UpperFootL"] = UpperLegL, ["LowerFootR"] = LowerLegR, ["LowerFootL"] = LowerLegL,
    };

    public static int StartOf(string? bone, int parent) => bone != null && Starts.TryGetValue(bone, out int s) ? s : parent;

    public static bool IsArm(int s) => s >= UpperArmR && s <= LowerArmL;

    /// <summary>Segments that take the torso's cloth coordinates whatever the sleeves mode, in layout version 2 (head, legs).</summary>
    public static bool IsJoined(int s) => s == Head || (s >= UpperLegR && s <= LowerLegL);

    public static int Index(string code) => Array.IndexOf(Codes, code);
}

/// <summary>Small double vector for the surface maths (mesh space, block units like the tesselator).</summary>
public readonly record struct V3(double X, double Y, double Z)
{
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator -(V3 a) => new(-a.X, -a.Y, -a.Z);
    public static V3 operator *(V3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public double Dot(V3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public V3 Cross(V3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double Length => Math.Sqrt(Dot(this));
    public V3 Normalized => Length < 1e-12 ? this : this * (1 / Length);
    // Not the record default: it would print Normalized, itself a V3, forever.
    public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";

    /// <summary>Point through a column-major 4x4 (Mat4d layout).</summary>
    public static V3 Point(double[] m, V3 p) => new(
        m[0] * p.X + m[4] * p.Y + m[8] * p.Z + m[12],
        m[1] * p.X + m[5] * p.Y + m[9] * p.Z + m[13],
        m[2] * p.X + m[6] * p.Y + m[10] * p.Z + m[14]);

    /// <summary>Direction through a column-major 4x4 (no translation).</summary>
    public static V3 Dir(double[] m, V3 d) => new(
        m[0] * d.X + m[4] * d.Y + m[8] * d.Z,
        m[1] * d.X + m[5] * d.Y + m[9] * d.Z,
        m[2] * d.X + m[6] * d.Y + m[10] * d.Z);
}

/// <summary>One element (box) of a garment model, placed on the skeleton in the mannequin's static pose.</summary>
public sealed class SurfaceElement
{
    public string Name = "";
    /// <summary>Skeleton element its root hangs on (step parent).</summary>
    public string Bone = "";
    public int Segment;
    /// <summary>Element → mesh space (column-major 4x4, block units; the box spans 0..Size/16 locally).</summary>
    public double[] Matrix = Array.Empty<double>();
    /// <summary>Box size in shape units.</summary>
    public V3 Size;
    /// <summary>Index of the garment element it is a child of (-1 = a root hung on <see cref="Bone"/>).</summary>
    public int Parent = -1;
}

/// <summary>
/// One textured face of a garment element: mesh-space rectangle P(s, t) = C0 + s S + t T and texel mapping
/// (x, y) = (U0, V0) + s (Us, Vs) + t (Ut, Vt), s, t in [0, 1] (the tesselator's corner order, lane/body-wrap).
/// </summary>
public sealed class SurfaceFace
{
    public int Index;
    public int Element;
    /// <summary>BlockFacing index in the element's frame (N E S W U D).</summary>
    public int Facing;
    public int Segment;
    public V3 C0, S, T, Normal;
    public double U0, V0, Us, Vs, Ut, Vt;
    /// <summary>Texel bounds (half-open) of the face's UV rectangle.</summary>
    public int X1, Y1, X2, Y2;

    public V3 At(double s, double t) => C0 + S * s + T * t;
    public (double x, double y) UvAt(double s, double t) => (U0 + s * Us + t * Ut, V0 + s * Vs + t * Vt);

    /// <summary>Face parameters of texture point (x, y), false when the UV rectangle is degenerate.</summary>
    public bool ParamsOf(double x, double y, out double s, out double t)
    {
        double det = Us * Vt - Ut * Vs;
        s = t = 0;
        if (Math.Abs(det) < 1e-9) return false;
        double dx = x - U0, dy = y - V0;
        s = (dx * Vt - Ut * dy) / det;
        t = (Us * dy - dx * Vs) / det;
        return true;
    }
}

/// <summary>Nearest face hit of a ray (<see cref="GarmentSurface.Raycast"/>).</summary>
public readonly record struct SurfaceHit(int Face, double Distance, V3 Point, double S, double T, int TexelX, int TexelY);

/// <summary>
/// Geometry of a garment model on the mannequin (lane/body-wrap; pure, offline-testable): every face of the garment's
/// texture key in mesh space (the dressable seraph skeleton in its static pose: the mannequin tesselates without
/// animation), texel → 3D point and face, and the reverse (3D ray → face → texel). Mesh space = what
/// <c>Tesselator.TesselateShape</c> outputs (block units); the mannequin adds its <c>shapeOffset</c> and rotation.
/// Matrices follow ShapeTesselator: T(origin) Rx Ry Rz S T(from - origin) per element, children inside, garment roots
/// appended to their step parent (Shape.StepParentShape).
/// </summary>
public sealed class GarmentSurface
{
    public readonly List<SurfaceElement> Elements = new();
    public readonly List<SurfaceFace> Faces = new();
    public readonly int Width, Height;
    /// <summary>Garment texels per shape unit (texture width / shape texture width of the key; vanilla clothing 2).</summary>
    public readonly double TexelsPerUnit;
    /// <summary>Skeleton element matrices (mesh space) and their segment, by name.</summary>
    public readonly Dictionary<string, (double[] m, int seg, V3 size)> Bones = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Faces whose UV rectangle covers each texel centre (row-major; first = composition owner).</summary>
    readonly List<int>?[] texelFaces;

    GarmentSurface(int w, int h, double tpu)
    {
        Width = w;
        Height = h;
        TexelsPerUnit = tpu;
        texelFaces = new List<int>?[w * h];
    }

    /// <summary>Faces covering texel (x, y) (centre inside their UV rectangle), empty when none.</summary>
    public IReadOnlyList<int> FacesOf(int x, int y)
        => x < 0 || y < 0 || x >= Width || y >= Height ? Array.Empty<int>() : (IReadOnlyList<int>?)texelFaces[y * Width + x] ?? Array.Empty<int>();

    /// <summary>
    /// The surface of <paramref name="garment"/> (unprefixed, not step-parented: <see cref="ItemTailoredGarment.LoadShape"/>)
    /// on <paramref name="skeleton"/> (the mannequin's dressable shape, e.g. seraph-faceless), faces textured
    /// <paramref name="textureKey"/> only, texels of a <paramref name="width"/> x <paramref name="height"/> garment texture.
    /// </summary>
    public static GarmentSurface Build(VsShape skeleton, VsShape garment, string textureKey, int width, int height)
    {
        int[] ts = garment.TextureSizes != null && garment.TextureSizes.TryGetValue(textureKey, out var sz) && sz is { Length: 2 }
            ? sz : new[] { garment.TextureWidth, garment.TextureHeight };
        double kx = (double)width / ts[0], ky = (double)height / ts[1];
        var surf = new GarmentSurface(width, height, kx);
        var id = Mat4d.Create();
        WalkSkeleton(surf, skeleton.Elements, id, BodySegments.Torso);
        foreach (var e in garment.Elements ?? Array.Empty<ShapeElement>()) WalkGarment(surf, e, null, -1, "", BodySegments.Torso, textureKey, kx, ky);
        return surf;
    }

    /// <summary>Element matrix as ShapeTesselator.TesselateShapeElements builds it (parent * local).</summary>
    public static double[] LocalMatrix(double[] parent, ShapeElement e)
    {
        var m = (double[])parent.Clone();
        double ox = 0, oy = 0, oz = 0;
        if (e.RotationOrigin is { Length: 3 } ro)
        {
            ox = ro[0]; oy = ro[1]; oz = ro[2];
            Mat4d.Translate(m, m, ox / 16, oy / 16, oz / 16);
        }
        if (e.RotationX != 0) Mat4d.Rotate(m, m, e.RotationX * GameMath.DEG2RAD, 1, 0, 0);
        if (e.RotationY != 0) Mat4d.Rotate(m, m, e.RotationY * GameMath.DEG2RAD, 0, 1, 0);
        if (e.RotationZ != 0) Mat4d.Rotate(m, m, e.RotationZ * GameMath.DEG2RAD, 0, 0, 1);
        if (e.ScaleX != 1 || e.ScaleY != 1 || e.ScaleZ != 1) Mat4d.Scale(m, e.ScaleX, e.ScaleY, e.ScaleZ);
        var from = e.From ?? new double[3];
        Mat4d.Translate(m, m, (from[0] - ox) / 16, (from[1] - oy) / 16, (from[2] - oz) / 16);
        return m;
    }

    static V3 SizeOf(ShapeElement e)
    {
        var f = e.From ?? new double[3];
        var t = e.To ?? new double[3];
        return new V3(t[0] - f[0], t[1] - f[1], t[2] - f[2]);
    }

    static void WalkSkeleton(GarmentSurface surf, ShapeElement[]? elems, double[] parent, int parentSeg)
    {
        foreach (var e in elems ?? Array.Empty<ShapeElement>())
        {
            var m = LocalMatrix(parent, e);
            int seg = BodySegments.StartOf(e.Name, parentSeg);
            if (e.Name != null && !surf.Bones.ContainsKey(e.Name)) surf.Bones[e.Name] = (m, seg, SizeOf(e));
            WalkSkeleton(surf, e.Children, m, seg);
        }
    }

    static void WalkGarment(GarmentSurface surf, ShapeElement e, double[]? parent, int parentIndex, string bone, int seg, string key, double kx, double ky)
    {
        if (e.StepParentName != null)
        {
            // Step parented: hangs on that skeleton element (Shape.StepParentShape appends it to its children).
            if (!surf.Bones.TryGetValue(e.StepParentName, out var b)) return; // VS: "Will not be visible"
            parent = b.m;
            parentIndex = -1;
            bone = e.StepParentName;
            seg = b.seg;
        }
        else if (parent == null)
        {
            return; // a root element without step parent is not drawn on an entity
        }
        var m = LocalMatrix(parent, e);
        var size = SizeOf(e);
        int ei = surf.Elements.Count;
        surf.Elements.Add(new SurfaceElement { Name = e.Name ?? "", Bone = bone, Segment = seg, Matrix = m, Size = size, Parent = parentIndex });
        var faces = e.FacesResolved;
        if (faces != null)
        {
            for (int f = 0; f < 6 && f < faces.Length; f++)
            {
                var face = faces[f];
                if (face == null || !face.Enabled || face.Uv is not { Length: 4 } || face.Texture != key) continue;
                AddFace(surf, ei, f, face, m, size, kx, ky);
            }
        }
        foreach (var c in e.Children ?? Array.Empty<ShapeElement>()) WalkGarment(surf, c, m, ei, bone, seg, key, kx, ky);
    }

    static readonly V3[] LocalNormals = { new(0, 0, -1), new(1, 0, 0), new(0, 0, 1), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0) };

    /// <summary>Outward normal of BlockFacing index <paramref name="facing"/> in the element frame.</summary>
    public static V3 LocalNormal(int facing) => LocalNormals[facing];

    static void AddFace(GarmentSurface surf, int ei, int facing, ShapeElementFace face, double[] m, V3 size, double kx, double ky)
    {
        // Corners and UVs exactly as ModelCubeUtilExt.AddFace: corner j = CubeVertices, uv = CubeUvCoords[(rot + j) % 4].
        var corners = new V3[4];
        var uvs = new (double x, double y)[4];
        int rot = ((int)(face.Rotation / 90) % 4 + 4) % 4;
        var uv = face.Uv!;
        double ou = uv[0], ov = uv[3], su = uv[2] - uv[0], sv = uv[1] - uv[3];
        var half = size * (0.5 / 16);
        for (int j = 0; j < 4; j++)
        {
            int ci = facing * 12 + 3 * j;
            var local = new V3(half.X + half.X * CubeMeshUtil.CubeVertices[ci], half.Y + half.Y * CubeMeshUtil.CubeVertices[ci + 1],
                half.Z + half.Z * CubeMeshUtil.CubeVertices[ci + 2]);
            corners[j] = V3.Point(m, local);
            int ui = facing * 8 + 2 * ((rot + j) % 4);
            uvs[j] = ((ou + su * CubeMeshUtil.CubeUvCoords[ui]) * kx, (ov + sv * CubeMeshUtil.CubeUvCoords[ui + 1]) * ky);
        }
        var sf = new SurfaceFace
        {
            Index = surf.Faces.Count,
            Element = ei,
            Facing = facing,
            Segment = surf.Elements[ei].Segment,
            C0 = corners[0],
            S = corners[1] - corners[0],
            T = corners[3] - corners[0],
            Normal = V3.Dir(m, LocalNormals[facing]).Normalized,
            U0 = uvs[0].x, V0 = uvs[0].y,
            Us = uvs[1].x - uvs[0].x, Vs = uvs[1].y - uvs[0].y,
            Ut = uvs[3].x - uvs[0].x, Vt = uvs[3].y - uvs[0].y,
        };
        double minX = Math.Min(Math.Min(uvs[0].x, uvs[1].x), Math.Min(uvs[2].x, uvs[3].x)), maxX = Math.Max(Math.Max(uvs[0].x, uvs[1].x), Math.Max(uvs[2].x, uvs[3].x));
        double minY = Math.Min(Math.Min(uvs[0].y, uvs[1].y), Math.Min(uvs[2].y, uvs[3].y)), maxY = Math.Max(Math.Max(uvs[0].y, uvs[1].y), Math.Max(uvs[2].y, uvs[3].y));
        // Texels whose centre lies inside the rectangle (x + 0.5 in [min, max)).
        sf.X1 = Math.Max(0, (int)Math.Ceiling(minX - 0.5)); sf.X2 = Math.Min(surf.Width, (int)Math.Ceiling(maxX - 0.5));
        sf.Y1 = Math.Max(0, (int)Math.Ceiling(minY - 0.5)); sf.Y2 = Math.Min(surf.Height, (int)Math.Ceiling(maxY - 0.5));
        if (Math.Abs(sf.Us * sf.Vt - sf.Ut * sf.Vs) < 1e-9) return; // degenerate UV
        surf.Faces.Add(sf);
        for (int y = sf.Y1; y < sf.Y2; y++)
        {
            for (int x = sf.X1; x < sf.X2; x++)
            {
                (surf.texelFaces[y * surf.Width + x] ??= new List<int>()).Add(sf.Index);
            }
        }
    }

    /// <summary>
    /// Faces drawn when <paramref name="hidden"/> elements are left out (empty optional parts; children go with their
    /// parent, as Shape.RemoveElements), as a filter for <see cref="Raycast"/>.
    /// </summary>
    public System.Func<int, bool> ShownFaces(IEnumerable<string> hidden)
    {
        var set = new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase);
        var shown = new bool[Elements.Count];
        for (int i = 0; i < Elements.Count; i++)
        {
            int p = Elements[i].Parent;
            shown[i] = !set.Contains(Elements[i].Name) && (p < 0 || shown[p]); // parents come first
        }
        return f => f >= 0 && f < Faces.Count && shown[Faces[f].Element];
    }

    /// <summary>Mesh-space centre of texel (x, y) on face <paramref name="face"/>.</summary>
    public V3 TexelPoint(int face, int x, int y)
    {
        var f = Faces[face];
        f.ParamsOf(x + 0.5, y + 0.5, out double s, out double t);
        return f.At(s, t);
    }

    /// <summary>
    /// Nearest face of the garment a mesh-space ray hits (both sides count: the mannequin draws garments without
    /// culling), with its texel; null on a miss. <paramref name="maxDistance"/> in block units.
    /// </summary>
    public SurfaceHit? Raycast(V3 origin, V3 dir, double maxDistance = 16, System.Func<int, bool>? faceFilter = null)
    {
        SurfaceHit? best = null;
        foreach (var f in Faces)
        {
            if (faceFilter != null && !faceFilter(f.Index)) continue;
            var n = f.S.Cross(f.T);
            double denom = dir.Dot(n);
            if (Math.Abs(denom) < 1e-12) continue;
            double lambda = (f.C0 - origin).Dot(n) / denom;
            if (lambda < 0 || lambda > maxDistance || (best != null && lambda >= best.Value.Distance)) continue;
            var p = origin + dir * lambda;
            var d = p - f.C0;
            double s = d.Dot(f.S) / f.S.Dot(f.S), t = d.Dot(f.T) / f.T.Dot(f.T);
            if (s < -1e-9 || s > 1 + 1e-9 || t < -1e-9 || t > 1 + 1e-9) continue;
            var (ux, uy) = f.UvAt(s, t);
            int tx = Math.Clamp((int)Math.Floor(ux), Math.Max(0, f.X1), Math.Max(f.X1, f.X2 - 1));
            int ty = Math.Clamp((int)Math.Floor(uy), Math.Max(0, f.Y1), Math.Max(f.Y1, f.Y2 - 1));
            best = new SurfaceHit(f.Index, lambda, p, s, t, tx, ty);
        }
        return best;
    }
}
