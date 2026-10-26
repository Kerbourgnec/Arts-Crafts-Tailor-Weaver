using System;
using System.Collections.Generic;
using ArtsAndCrafts.Tailor.Tapestry;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArtsAndCrafts.Tailor.HandLoom;

/// <summary>
/// Client: the cloth, warp, heddle rods, pick-up stick, sword and stick shuttles of a hand loom as a chunk mesh (the
/// frame is the block shape). Laid out by <see cref="HandLoomGeometry"/> in the north shape frame and tesselated turned
/// by the shape's rotateY, like the boxes. Cells: one two-sided flat plate each (woven rows; the current row highlighted,
/// its empty ends show the bare warp). Bare warp: one thread per end from the fell to the warp beam, in the end's warp
/// colour, lifted ends drawn raised (the open shed). Rods: sticks across the warp, raised when lifted, with a leash down
/// to every end they carry. Wood parts use the block texture <c>rod</c>; cells use <see cref="ClothCellTextures"/>.
/// Build on the main thread (atlas insertion).
/// </summary>
public static class HandLoomMesh
{
    public const string RodTexture = "rod";
    const float ShedRaise = 0.8f;

    public static MeshData? Build(ICoreClientAPI capi, Block block, HandLoomWork work, System.Func<string, string> twineHex)
    {
        var tex = new ClothCellTextures(capi);
        var el = new List<ShapeElement>();
        var cloth = work.Cloth;
        int size = cloth.Width;
        float yc = HandLoomGeometry.ClothY + HandLoomGeometry.CellLift;
        int total = cloth.Rows;
        int rows = Math.Min(cloth.WovenRows, total);
        bool complete = cloth.Complete;
        // A large carpet (2N rows, lane/loom-roles) shows N rows on the warp; older ones are wound onto the cloth beam.
        int start = HandLoomGeometry.ViewStart(rows, total, size);
        int fellSlot = rows - start;

        // Woven rows and the current row.
        for (int r = start; r <= rows && r < total; r++)
        {
            bool current = r == rows;
            var (z0, z1) = HandLoomGeometry.RowSpan(r - start, size);
            for (int c = 0; c < size; c++)
            {
                byte cell = current ? cloth.Current[c] : cloth.Woven[r * size + c];
                if (cell == WeavingState.Empty) continue;
                var (x0, x1) = HandLoomGeometry.CellSpan(c, size);
                el.Add(Flat(x0, z0, x1, z1, yc, tex.Use(cloth.ColorOf(cell), current)));
            }
        }

        // Bare warp: from the current row (its empty ends included) to the warp beam; and over the cloth beam.
        float tw = HandLoomGeometry.ThreadWidth(size);
        float beamFar = HandLoomGeometry.WarpBeam.Z1 + 0.5f;
        for (int c = 0; c < size; c++)
        {
            var (x0, x1) = HandLoomGeometry.CellSpan(c, size);
            float xm = (x0 + x1) / 2;
            string warp = tex.Use(cloth.ColorOf(work.Warp[c]));
            float zStart = complete ? HandLoomGeometry.ClothZ1
                : cloth.Current[c] == WeavingState.Empty ? HandLoomGeometry.RowSpan(fellSlot, size).z1 : HandLoomGeometry.RowSpan(fellSlot, size).z0;
            bool up = !complete && work.Up(c);
            float y = HandLoomGeometry.ClothY + (up ? ShedRaise : 0f);
            if (zStart > beamFar) el.Add(Flat(xm - tw / 2, beamFar, xm + tw / 2, zStart, y, warp));
            el.Add(Flat(xm - tw / 2, HandLoomGeometry.ClothZ0, xm + tw / 2, HandLoomGeometry.ClothBeam.Z0 + 0.5f, HandLoomGeometry.ClothY, warp));
        }

        // Rows wound onto the cloth beam (large carpet): the newest wound row lies over the beam's top.
        if (start > 0)
        {
            var beam = HandLoomGeometry.ClothBeam;
            for (int c = 0; c < size; c++)
            {
                byte cell = cloth.Woven[(start - 1) * size + c];
                if (cell == WeavingState.Empty) continue;
                var (x0, x1) = HandLoomGeometry.CellSpan(c, size);
                el.Add(Flat(x0, HandLoomGeometry.ClothZ0, x1, beam.Z1, beam.Y1 + 0.05f, tex.Use(cloth.ColorOf(cell))));
            }
        }

        AddRods(work, el, tex, size);
        if (!complete) AddShedTools(work, el, tex, size, fellSlot);
        AddShuttles(work.Shuttles, twineHex, el, tex);

        if (el.Count == 0) return null;
        var shape = new Shape
        {
            Elements = el.ToArray(),
            Textures = new Dictionary<string, AssetLocation>(),
            TextureSizes = new Dictionary<string, int[]>(),
            TextureWidth = 16,
            TextureHeight = 16,
        };
        shape.ResolveReferences(capi.World.Logger, "actailor-handloom");
        var source = tex.Source(block);
        float rotY = block.Shape?.rotateY ?? 0f;
        capi.Tesselator.TesselateShape("actailor-handloom", shape, out MeshData mesh, source, new Vec3f(0, rotY, 0));
        return mesh;
    }

    /// <summary>Rods across the warp (raised when lifted) and a leash from each rod down to every end it carries.</summary>
    static void AddRods(HandLoomWork work, List<ShapeElement> el, ClothCellTextures tex, int size)
    {
        string leash = tex.Use(TwineColors.PlainHex);
        float lw = Math.Min(0.18f, HandLoomGeometry.CellWidth(size) * 0.2f);
        for (int k = 0; k < work.RodCount; k++)
        {
            bool lifted = work.RodLifted(k);
            float y0 = HandLoomGeometry.RodY0 + (lifted ? HandLoomGeometry.RodLift : 0f), zc = HandLoomGeometry.RodZ[k];
            el.Add(Box(HandLoomGeometry.RodX0, y0, zc - 0.4f, HandLoomGeometry.RodX1, y0 + HandLoomGeometry.RodThick, zc + 0.4f, RodTexture));
            float warpY = HandLoomGeometry.ClothY + (lifted ? ShedRaise : 0f);
            for (int c = 0; c < size; c++)
            {
                if (work.RodOf(c) != k) continue;
                var (x0, x1) = HandLoomGeometry.CellSpan(c, size);
                float xm = (x0 + x1) / 2;
                el.Add(Box(xm - lw / 2, warpY, zc - lw / 2, xm + lw / 2, y0 + 0.2f, zc + lw / 2, leash));
            }
        }
    }

    /// <summary>The weaving sword lies in the shed beyond the fell; the pick-up stick next to it while it lifts or holds ends.</summary>
    static void AddShedTools(HandLoomWork work, List<ShapeElement> el, ClothCellTextures tex, int size, int fellSlot)
    {
        float fell = HandLoomGeometry.RowSpan(fellSlot, size).z0;
        float y = HandLoomGeometry.ClothY + 0.2f;
        // Sword: a flat board across the warp, a little beyond the current row (one-block loom, lane/loom-feedback-8).
        el.Add(Box(HandLoomGeometry.ClothX0 - 1.5f, y, fell - 1.4f, HandLoomGeometry.ClothX1 + 1.5f, y + 0.3f, fell - 0.6f, RodTexture));
        if (work.PickUsed) el.Add(Box(HandLoomGeometry.ClothX0 - 0.5f, y + 0.4f, fell - 2.2f, HandLoomGeometry.ClothX1 + 0.5f, y + 0.7f, fell - 1.6f, RodTexture));
    }

    /// <summary>Stick shuttles on the side rails: a flat notched stick, its twine wound round the middle (highlighted when active).</summary>
    static void AddShuttles(SpoolRack shuttles, System.Func<string, string> twineHex, List<ShapeElement> el, ClothCellTextures tex)
    {
        for (int i = 0; i < shuttles.Count && i < HandLoomGeometry.ShuttleCount; i++)
        {
            bool active = i == shuttles.Selected && shuttles.CodeAt(i) != null;
            float cx = HandLoomGeometry.ShuttleX[i], y0 = HandLoomGeometry.RailTop + (active ? 0.35f : 0f);
            float z0 = HandLoomGeometry.ShuttleZ0 + 0.3f, z1 = HandLoomGeometry.ShuttleZ1 - 0.3f;
            el.Add(Box(cx - 0.45f, y0, z0, cx + 0.45f, y0 + 0.3f, z1, RodTexture));
            if (shuttles.CodeAt(i) is not { } code) continue;
            float fill = 0.4f + 0.6f * Math.Min(1f, shuttles.AmountAt(i) / (float)SpoolRack.Capacity);
            float zm = (z0 + z1) / 2, half = (z1 - z0) * 0.32f, h = 0.25f + 0.45f * fill;
            el.Add(Box(cx - 0.6f, y0 - 0.05f, zm - half, cx + 0.6f, y0 + 0.3f + h, zm + half, tex.Use(twineHex(code), active)));
        }
    }

    /// <summary>Zero-thickness flat plate at height <paramref name="y"/>, up and down faces textured.</summary>
    static ShapeElement Flat(float x0, float z0, float x1, float z1, float y, string key)
    {
        var e = new ShapeElement { Name = "c", From = new double[] { x0, y, Math.Min(z0, z1) }, To = new double[] { x1, y, Math.Max(z0, z1) } };
        e.FacesResolved![BlockFacing.UP.Index] = Face(key);
        e.FacesResolved![BlockFacing.DOWN.Index] = Face(key);
        return e;
    }

    static ShapeElement Box(float x0, float y0, float z0, float x1, float y1, float z1, string key)
    {
        var e = new ShapeElement { Name = "b", From = new double[] { x0, y0, z0 }, To = new double[] { x1, y1, z1 } };
        for (int f = 0; f < 6; f++) e.FacesResolved![f] = Face(key);
        return e;
    }

    static ShapeElementFace Face(string key) => new() { Texture = key, Uv = new float[] { 0, 0, 16, 16 } };
}
