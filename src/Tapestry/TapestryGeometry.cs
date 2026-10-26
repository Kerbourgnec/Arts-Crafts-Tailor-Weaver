using System;
using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.MathTools;

namespace ArtsAndCrafts.Tailor.Tapestry;

/// <summary>What a selection box of the tapestry loom is (docs/tapestry-loom.md "Interactions").</summary>
public enum TapestryPart : byte
{
    /// <summary>Posts and feet: no interaction (the block can be broken through them).</summary>
    Frame = 0,
    /// <summary>Cell of the current row (index = column).</summary>
    Cell = 1,
    /// <summary>A woven row (index = row, 0 = bottom): click copies it into the current row.</summary>
    Row = 2,
    /// <summary>Cloth beam (index 0, bottom) or warp beam (index 1, top): click beats the row in, sneak rolls back.</summary>
    Beam = 3,
    /// <summary>Bobbin on its peg (index 0..5).</summary>
    Bobbin = 4,
    /// <summary>Embroidery mode (lane/loom-embroidery): a cell of the cloth on the frame (index = y * size + x, row 0 at the bottom).</summary>
    EmbCell = 5,
}

public readonly record struct TapestryBoxPart(TapestryPart Part, int Index)
{
    public override string ToString() => $"{Part}#{Index}";
}

/// <summary>
/// Layout of the tapestry loom (lane/tapestry-loom): an upright two-beam frame, 2 wide x 2 tall x 1 deep. Pure data.
/// <para>
/// Frame of every number here = the shape frame of the north variant (rotateY 0), in px: controller block x 0..16, the
/// second block x 16..32, y 0..32, z 0..16; the weaver stands on the +z side and x grows to the weaver's right
/// (VS: a player facing north has +x on the right). Per facing, boxes and the cloth mesh turn by the block shape's
/// rotateY around the controller block centre (<see cref="Rotate"/>), like the shape. Shapes: scripts/tapestry-loom-shapes.py.
/// </para>
/// <para>
/// Cloth: square, <see cref="ClothX0"/>..<see cref="ClothX1"/> x <see cref="ClothY0"/>..<see cref="ClothY1"/> (24 px =
/// 1.5 blocks) in the warp plane z = <see cref="ClothZ"/>; rows build bottom → top (row 0 sits on the cloth beam, as on a
/// historical upright loom). 16 cells = 1.5 px each, 32 cells = 0.75 px. Above the current row only the bare warp shows.
/// </para>
/// </summary>
public static class TapestryGeometry
{
    public const float ClothX0 = 4f, ClothX1 = 28f, ClothY0 = 5.5f, ClothY1 = 29.5f, ClothZ = 8f;
    /// <summary>Cell / row selection plates: thin (2 x <see cref="PlateHalf"/>) around the drawn cells (an oblique ray must enter the plate where it meets the drawing).</summary>
    public const float PlateHalf = 0.02f;
    /// <summary>Drawn cells lie this far in front of the warp plane (warp threads run at <see cref="ClothZ"/>).</summary>
    public const float CellLift = 0.05f;

    public const int BobbinCount = 6;
    /// <summary>Bobbin column centres (x): left post, right post; bobbins 0..2 on the left (top → bottom), 3..5 on the right.</summary>
    public static readonly float[] BobbinX = { 1.75f, 30.25f };
    /// <summary>Peg heights (y) per tier, top → bottom; the bobbin hangs below its peg.</summary>
    public static readonly float[] PegY = { 26.2f, 20.2f, 14.2f };
    public const float BobbinDrop = 2.2f, BobbinHalfHeight = 2.0f;
    /// <summary>Bobbin selection box: z range in front of the posts (the pegs stick out to z 11.6).</summary>
    public const float BobbinZ0 = 9.3f, BobbinZ1 = 12.3f, BobbinHalfWidth = 1.25f;

    public record struct Box(float X0, float Y0, float Z0, float X1, float Y1, float Z1)
    {
        public Cuboidf ToCuboid() => new(X0 / 16f, Y0 / 16f, Z0 / 16f, X1 / 16f, Y1 / 16f, Z1 / 16f);
        public float Cx => (X0 + X1) / 2;
        public float Cy => (Y0 + Y1) / 2;
        public float Cz => (Z0 + Z1) / 2;
    }

    public static readonly Box BottomBeam = new(0.5f, 2.5f, 6.75f, 31.5f, 5f, 9.25f);
    public static readonly Box TopBeam = new(0.5f, 29.5f, 6.75f, 31.5f, 32f, 9.25f);
    public static Box Beam(int index) => index == 0 ? BottomBeam : TopBeam;
    /// <summary>Posts (frame boxes): the plank shape's bigger posts, the crude ones lie inside.</summary>
    public static readonly Box PostL = new(1f, 0f, 6.75f, 3.5f, 32f, 9.25f), PostR = new(28.5f, 0f, 6.75f, 31f, 32f, 9.25f);
    public static readonly Box FootL = new(0.5f, 0f, 1f, 4.5f, 2.4f, 15f), FootR = new(27.5f, 0f, 1f, 31.5f, 2.4f, 15f);

    public static float CellWidth(int size) => (ClothX1 - ClothX0) / Math.Max(1, size);
    public static float RowHeight(int size) => (ClothY1 - ClothY0) / Math.Max(1, size);

    /// <summary>x span of column <paramref name="col"/> (0 = the weaver's left).</summary>
    public static (float x0, float x1) CellSpan(int col, int size) => (ClothX0 + col * CellWidth(size), ClothX0 + (col + 1) * CellWidth(size));

    /// <summary>
    /// First cloth row shown on the frame (lane/linen-output): the frame holds <paramref name="size"/> rows; a longer cloth
    /// (large carpet, 2N rows) rolls its oldest rows onto the cloth beam so the current row stays on the frame (at the top
    /// once the frame is full). 0 for a square cloth.
    /// </summary>
    public static int ViewStart(int wovenRows, int rows, int size)
        => Math.Clamp(Math.Clamp(wovenRows, 0, rows) + 1 - size, 0, Math.Max(0, rows - size));

    /// <summary>y span of frame slot <paramref name="row"/> (0 = bottom, on the cloth beam; cloth row = slot + <see cref="ViewStart"/>).</summary>
    public static (float y0, float y1) RowSpan(int row, int size) => (ClothY0 + row * RowHeight(size), ClothY0 + (row + 1) * RowHeight(size));

    public static int BobbinSide(int i) => i / 3;
    public static int BobbinTier(int i) => i % 3;
    public static float BobbinCenterY(int i) => PegY[BobbinTier(i)] - BobbinDrop;

    /// <summary>Selection box of bobbin <paramref name="i"/> (peg and bobbin).</summary>
    public static Box BobbinBox(int i)
    {
        float cx = BobbinX[BobbinSide(i)], cy = BobbinCenterY(i);
        return new Box(cx - BobbinHalfWidth, cy - BobbinHalfHeight - 0.5f, BobbinZ0, cx + BobbinHalfWidth, PegY[BobbinTier(i)] + 0.6f, BobbinZ1);
    }

    /// <summary>Warp threads: one per column, centred, this wide (px).</summary>
    public static float ThreadWidth(int size) => Math.Min(0.4f, CellWidth(size) * 0.4f);

    /// <summary>
    /// Selection boxes (block units, controller-relative, turned by <paramref name="rotateYDeg"/>) and what each one is.
    /// Order: current row cells (none when complete), woven rows on the frame (bottom → top; index = cloth row), beams,
    /// bobbins, frame. <paramref name="clothRows"/>: the cloth's rows (default = <paramref name="size"/>, square; a large
    /// carpet has 2N, shown through <see cref="ViewStart"/>).
    /// </summary>
    public static (Cuboidf[] boxes, TapestryBoxPart[] parts) Build(int wovenRows, int size, float rotateYDeg, int clothRows = -1)
    {
        var boxes = new List<Box>();
        var parts = new List<TapestryBoxPart>();
        void Add(Box b, TapestryPart p, int i) { boxes.Add(b); parts.Add(new TapestryBoxPart(p, i)); }
        int total = clothRows < 0 ? size : clothRows;
        int rows = Math.Clamp(wovenRows, 0, total);
        int start = ViewStart(rows, total, size);
        float z0 = ClothZ + CellLift - PlateHalf, z1 = ClothZ + CellLift + PlateHalf;
        if (rows < total)
        {
            var (y0, y1) = RowSpan(rows - start, size);
            for (int c = 0; c < size; c++)
            {
                var (x0, x1) = CellSpan(c, size);
                Add(new Box(x0, y0, z0, x1, y1, z1), TapestryPart.Cell, c);
            }
        }
        for (int r = start; r < rows; r++)
        {
            var (y0, y1) = RowSpan(r - start, size);
            Add(new Box(ClothX0, y0, z0, ClothX1, y1, z1), TapestryPart.Row, r);
        }
        Add(BottomBeam, TapestryPart.Beam, 0);
        Add(TopBeam, TapestryPart.Beam, 1);
        for (int i = 0; i < BobbinCount; i++) Add(BobbinBox(i), TapestryPart.Bobbin, i);
        Add(PostL, TapestryPart.Frame, 0);
        Add(PostR, TapestryPart.Frame, 1);
        Add(FootL, TapestryPart.Frame, 2);
        Add(FootR, TapestryPart.Frame, 3);
        return (boxes.Select(b => Rotate(b.ToCuboid(), rotateYDeg)).ToArray(), parts.ToArray());
    }

    /// <summary>
    /// Selection boxes in embroidery mode (lane/loom-embroidery): every cell of the <paramref name="size"/> x
    /// <paramref name="size"/> cloth (thin plates on the drawn plane, same layout as the woven rows), then beams, bobbins, frame.
    /// </summary>
    public static (Cuboidf[] boxes, TapestryBoxPart[] parts) BuildEmbroidery(int size, float rotateYDeg)
    {
        var boxes = new List<Box>();
        var parts = new List<TapestryBoxPart>();
        void Add(Box b, TapestryPart p, int i) { boxes.Add(b); parts.Add(new TapestryBoxPart(p, i)); }
        float z0 = ClothZ + CellLift - PlateHalf, z1 = ClothZ + CellLift + PlateHalf;
        for (int y = 0; y < size; y++)
        {
            var (y0, y1) = RowSpan(y, size);
            for (int x = 0; x < size; x++)
            {
                var (x0, x1) = CellSpan(x, size);
                Add(new Box(x0, y0, z0, x1, y1, z1), TapestryPart.EmbCell, y * size + x);
            }
        }
        Add(BottomBeam, TapestryPart.Beam, 0);
        Add(TopBeam, TapestryPart.Beam, 1);
        for (int i = 0; i < BobbinCount; i++) Add(BobbinBox(i), TapestryPart.Bobbin, i);
        Add(PostL, TapestryPart.Frame, 0);
        Add(PostR, TapestryPart.Frame, 1);
        Add(FootL, TapestryPart.Frame, 2);
        Add(FootR, TapestryPart.Frame, 3);
        return (boxes.Select(b => Rotate(b.ToCuboid(), rotateYDeg)).ToArray(), parts.ToArray());
    }

    /// <summary>Collision: the frame plane (posts, beams, cloth) and the two feet; turned like the boxes.</summary>
    public static Cuboidf[] CollisionBoxes(float rotateYDeg)
    {
        var list = new[]
        {
            new Box(0.5f, 0f, 6.5f, 31.5f, 32f, 9.5f),
            FootL, FootR,
        };
        return list.Select(b => Rotate(b.ToCuboid(), rotateYDeg)).ToArray();
    }

    /// <summary>Turn a controller-relative box like the block shape (around the controller block centre).</summary>
    public static Cuboidf Rotate(Cuboidf box, float rotateYDeg)
        => rotateYDeg % 360f == 0 ? box : box.RotatedCopy(0, rotateYDeg, 0, new Vec3d(0.5, 0.5, 0.5));
}
