using System;
using System.Collections.Generic;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArtsAndCrafts.Tailor.Tapestry;

/// <summary>
/// Client: the cloth, warp and bobbins of a tapestry loom as a chunk mesh (the frame is the block shape). Laid out by
/// <see cref="TapestryGeometry"/> in the north shape frame and tesselated turned by the shape's rotateY, like the boxes.
/// Woven rows and the current row: one two-sided plate per cell in the warp plane (row 0 at the bottom); the current
/// row highlighted, unpainted cells dark. Above it the bare warp: one flax thread per column up to the warp beam (the
/// painted weft hides the warp: tapestry is weft-faced). Bobbins: a wooden spindle under each peg, with its twine
/// (highlighted when active). Build on the main thread (atlas insertion).
/// </summary>
public static class TapestryMesh
{
    public const string BobbinWoodHex = "#8a6a45";

    public static MeshData? Build(ICoreClientAPI capi, Block block, TapestryWork work, System.Func<string, string> twineHex)
    {
        var tex = new ClothCellTextures(capi);
        var elements = new List<ShapeElement>();
        var cloth = work.Cloth;
        int size = cloth.Width;
        float zc = TapestryGeometry.ClothZ + TapestryGeometry.CellLift;

        if (work.Hoop is { } hoop)
        {
            // Embroidery mode (lane/loom-embroidery): the whole cloth on the frame, woven + stitches; the weaving waits.
            AddHoop(hoop, elements, tex, zc);
            AddBobbins(work.Bobbins, twineHex, elements, tex);
            return Tesselate(capi, block, elements, tex);
        }

        // A large carpet (2N rows, lane/linen-output) shows N rows; older ones are rolled onto the cloth beam.
        int start = TapestryGeometry.ViewStart(cloth.WovenRows, cloth.Rows, size);
        for (int r = start; r < cloth.Rows && r < start + size; r++)
        {
            bool current = r == cloth.WovenRows;
            if (r > cloth.WovenRows) break;
            var (y0, y1) = TapestryGeometry.RowSpan(r - start, size);
            for (int c = 0; c < size; c++)
            {
                byte cell = current ? cloth.Current[c] : cloth.Woven[r * size + c];
                string key = cell == WeavingState.Empty ? tex.UseEmpty() : tex.Use(cloth.ColorOf(cell), current);
                var (x0, x1) = TapestryGeometry.CellSpan(c, size);
                elements.Add(Plate(x0, y0, x1, y1, zc, key));
            }
        }

        // Bare warp above the cloth (from the top of the current row, or of the last row) to the warp beam.
        int topRow = Math.Min(cloth.WovenRows + 1 - start, size);
        float warpY0 = TapestryGeometry.RowSpan(topRow - 1, size).y1, warpY1 = TapestryGeometry.TopBeam.Y0 + 0.5f;
        float tw = TapestryGeometry.ThreadWidth(size);
        string flax = tex.Use(TwineColors.PlainHex);
        for (int c = 0; c < size; c++)
        {
            var (x0, x1) = TapestryGeometry.CellSpan(c, size);
            float xm = (x0 + x1) / 2;
            if (warpY1 > warpY0) elements.Add(Plate(xm - tw / 2, warpY0, xm + tw / 2, warpY1, TapestryGeometry.ClothZ, flax));
            // Below row 0: the warp from the cloth beam into the first row.
            elements.Add(Plate(xm - tw / 2, TapestryGeometry.BottomBeam.Y1 - 0.5f, xm + tw / 2, TapestryGeometry.ClothY0, TapestryGeometry.ClothZ, flax));
        }

        AddBobbins(work.Bobbins, twineHex, elements, tex);
        return Tesselate(capi, block, elements, tex);
    }

    /// <summary>Cloth on the frame for embroidery: one plate per cell in the composite colour, the warp from the cloth beam into row 0.</summary>
    static void AddHoop(EmbroideryHoop hoop, List<ShapeElement> elements, ClothCellTextures tex, float zc)
    {
        var shown = hoop.Shown;
        int n = shown.Width;
        for (int y = 0; y < n; y++)
        {
            var (y0, y1) = TapestryGeometry.RowSpan(y, n);
            for (int x = 0; x < n; x++)
            {
                byte cell = shown.Pattern[y * n + x];
                string hex = cell < shown.Colors.Length ? shown.Colors[cell] : TwineColors.PlainHex;
                var (x0, x1) = TapestryGeometry.CellSpan(x, n);
                elements.Add(Plate(x0, y0, x1, y1, zc, tex.Use(hex)));
            }
        }
        float tw = TapestryGeometry.ThreadWidth(n);
        string flax = tex.Use(TwineColors.PlainHex);
        for (int x = 0; x < n; x++)
        {
            var (x0, x1) = TapestryGeometry.CellSpan(x, n);
            float xm = (x0 + x1) / 2;
            elements.Add(Plate(xm - tw / 2, TapestryGeometry.BottomBeam.Y1 - 0.5f, xm + tw / 2, TapestryGeometry.ClothY0, TapestryGeometry.ClothZ, flax));
        }
    }

    static MeshData? Tesselate(ICoreClientAPI capi, Block block, List<ShapeElement> elements, ClothCellTextures tex)
    {
        if (elements.Count == 0) return null;
        var shape = new Shape
        {
            Elements = elements.ToArray(),
            Textures = new Dictionary<string, AssetLocation>(),
            TextureSizes = new Dictionary<string, int[]>(),
            TextureWidth = 16,
            TextureHeight = 16,
        };
        shape.ResolveReferences(capi.World.Logger, "actailor-tapestrycloth");
        var source = tex.Source(block);
        float rotY = block.Shape?.rotateY ?? 0f;
        capi.Tesselator.TesselateShape("actailor-tapestrycloth", shape, out MeshData mesh, source, new Vec3f(0, rotY, 0));
        return mesh;
    }

    /// <summary>Bobbins: a spindle under each peg (bare wood when empty), twine wound on a loaded one, thicker as it holds more.</summary>
    static void AddBobbins(SpoolRack bobbins, System.Func<string, string> twineHex, List<ShapeElement> elements, ClothCellTextures tex)
    {
        string wood = tex.Use(BobbinWoodHex);
        for (int i = 0; i < bobbins.Count && i < TapestryGeometry.BobbinCount; i++)
        {
            float cx = TapestryGeometry.BobbinX[TapestryGeometry.BobbinSide(i)], cy = TapestryGeometry.BobbinCenterY(i);
            float h = TapestryGeometry.BobbinHalfHeight, zc = 10.8f;
            // The spindle hangs from the peg tip (peg: shape element peg{i}, z 9..11.6).
            elements.Add(BoxEl(cx - 0.35f, cy - h, zc - 0.35f, cx + 0.35f, TapestryGeometry.PegY[TapestryGeometry.BobbinTier(i)] + 0.1f, zc + 0.35f, wood));
            if (bobbins.CodeAt(i) is not { } code) continue;
            bool active = i == bobbins.Selected;
            float fill = 0.5f + 0.5f * Math.Min(1f, bobbins.AmountAt(i) / (float)SpoolRack.Capacity);
            float r = 0.55f + 0.55f * fill, hh = h * 0.7f;
            elements.Add(BoxEl(cx - r, cy - hh, zc - r, cx + r, cy + hh, zc + r, tex.Use(twineHex(code), active)));
        }
    }

    /// <summary>Zero-thickness plate in the plane z = <paramref name="z"/>, front and back faces textured.</summary>
    static ShapeElement Plate(float x0, float y0, float x1, float y1, float z, string key)
    {
        var el = new ShapeElement { Name = "c", From = new double[] { x0, y0, z }, To = new double[] { x1, y1, z } };
        el.FacesResolved![BlockFacing.NORTH.Index] = Face(key);
        el.FacesResolved![BlockFacing.SOUTH.Index] = Face(key);
        return el;
    }

    static ShapeElement BoxEl(float x0, float y0, float z0, float x1, float y1, float z1, string key)
    {
        var el = new ShapeElement { Name = "b", From = new double[] { x0, y0, z0 }, To = new double[] { x1, y1, z1 } };
        for (int f = 0; f < 6; f++) el.FacesResolved![f] = Face(key);
        return el;
    }

    static ShapeElementFace Face(string key) => new() { Texture = key, Uv = new float[] { 0, 0, 16, 16 } };
}
