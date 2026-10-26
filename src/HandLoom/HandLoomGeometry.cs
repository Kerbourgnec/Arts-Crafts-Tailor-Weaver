using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.MathTools;

namespace ArtsAndCrafts.Tailor.HandLoom;

/// <summary>What a selection box of the hand loom is (docs/hand-loom.md "Interactions").</summary>
public enum HandLoomPart : byte
{
    /// <summary>Warp beam, side rails: no interaction (the block can be broken through them).</summary>
    Frame = 0,
    /// <summary>Warp end at the fell (current row cell, index = column): pick-up stick, or warp loading on a free warp.</summary>
    Thread = 1,
    /// <summary>A woven row (index = row, 0 = at the cloth beam): click copies its shed and weft.</summary>
    Row = 2,
    /// <summary>Cloth beam (near the weaver): beat with the sword; sneak rolls the last row back.</summary>
    Beam = 3,
    /// <summary>Heddle rod (index 0 = nearest the weaver): click lifts / lowers it.</summary>
    Rod = 4,
    /// <summary>Stick shuttle (index 0 = weaver's left, 1 = right).</summary>
    Shuttle = 5,
}

public readonly record struct HandLoomBoxPart(HandLoomPart Part, int Index)
{
    public override string ToString() => $"{Part}#{Index}";
}

/// <summary>
/// Layout of the hand looms (lane/hand-loom; lane/loom-feedback-8: one block, owner "hand looms are gigantic"): a low
/// horizontal ground loom in a single block (was 2 wide x 1 tall x 3 deep). Pure data.
/// <para>
/// Frame of every number here = the shape frame of the north variant (rotateY 0), in px: the block x 0..16, y 0..16,
/// z 0..16 (z 16 = near the weaver). The weaver stands on the +z side and x grows to the weaver's right. Per facing,
/// boxes and the meshes turn by the shape's rotateY around the block centre (<see cref="Rotate"/>). Shapes:
/// scripts/hand-loom-shapes.py.
/// </para>
/// <para>
/// The warp runs flat from the cloth beam (near, z 13.25..15.75) to the warp beam (far, z 0.1..1.2) in the plane
/// y = <see cref="ClothY"/>. Cloth x 2..14 (12 px): 16 cells = 0.75 px, 32 cells = 0.375 px, square. The warp shows a
/// window of <see cref="Window"/> = N / 2 rows (z 13 → 7, 6 px): from row N/2 + 1 on, the oldest rows wind onto the cloth
/// beam (<see cref="ViewStart"/>, as the large carpet did before), the current row stays in the far slot. Heddle rods lie
/// across the warp on forked supports, <see cref="RodY0"/>.. above it, beyond the window (z 1.25..6.75): a rod over the
/// cloth would hide the ends behind it from the weaver's eye (lane/hand-loom ray checks), so the window keeps the fell in front of them.
/// </para>
/// </summary>
public static class HandLoomGeometry
{
    public const float ClothX0 = 2f, ClothX1 = 14f, ClothZ0 = 13f, ClothZ1 = 7f, ClothY = 3.5f;
    /// <summary>Thread / row plates: thin (2 x <see cref="PlateHalf"/>) around the drawn cells.</summary>
    public const float PlateHalf = 0.02f, CellLift = 0.05f;

    public const int MaxRods = HandLoomPresets.MaxRods;
    /// <summary>Rod centre lines (z), rod 1 nearest the weaver.</summary>
    public static readonly float[] RodZ = { 6f, 4f, 2f };
    /// <summary>Rod box: covers the rod at rest (<see cref="RodY0"/>) and lifted (+<see cref="RodLift"/>).</summary>
    public const float RodY0 = 6f, RodThick = 1f, RodLift = 1.5f, RodHalfDepth = 0.75f, RodX0 = 0.5f, RodX1 = 15.5f;

    public const int ShuttleCount = HandLoomWork.ShuttleCount;
    /// <summary>Stick shuttles lie on the side rails beside the cloth window (rail top <see cref="RailTop"/>).</summary>
    public static readonly float[] ShuttleX = { 1f, 15f };
    public const float RailTop = 2f, ShuttleZ0 = 9f, ShuttleZ1 = 13f, ShuttleHalfWidth = 1f;

    public record struct Box(float X0, float Y0, float Z0, float X1, float Y1, float Z1)
    {
        public Cuboidf ToCuboid() => new(X0 / 16f, Y0 / 16f, Z0 / 16f, X1 / 16f, Y1 / 16f, Z1 / 16f);
    }

    public static readonly Box ClothBeam = new(0.25f, 0.5f, 13.25f, 15.75f, 3.5f, 15.75f);
    public static readonly Box WarpBeam = new(0.5f, 0.5f, 0.1f, 15.5f, 3f, 1.2f);
    public static readonly Box RailL = new(0f, 0f, 1.2f, 2f, RailTop, 13.25f), RailR = new(14f, 0f, 1.2f, 16f, RailTop, 13.25f);

    public static float CellWidth(int size) => (ClothX1 - ClothX0) / Math.Max(1, size);
    /// <summary>Rows shown on the warp: half the cloth width (8 of 16, 16 of 32), so the rows stay square in one block.</summary>
    public static int Window(int size) => Math.Max(1, size / 2);
    public static float RowDepth(int size) => (ClothZ0 - ClothZ1) / Window(size);

    /// <summary>x span of column <paramref name="col"/> (0 = the weaver's left).</summary>
    public static (float x0, float x1) CellSpan(int col, int size) => (ClothX0 + col * CellWidth(size), ClothX0 + (col + 1) * CellWidth(size));

    /// <summary>
    /// First cloth row shown on the warp (lane/loom-roles; window of <see cref="Window"/> rows since lane/loom-feedback-8):
    /// the oldest rows wind onto the cloth beam, so the current row stays on the warp (in the far slot once the window is full).
    /// </summary>
    public static int ViewStart(int wovenRows, int rows, int size)
        => Math.Clamp(Math.Clamp(wovenRows, 0, rows) + 1 - Window(size), 0, Math.Max(0, rows - Window(size)));

    /// <summary>z span of warp slot <paramref name="row"/> (0 = at the cloth beam; cloth row = slot + <see cref="ViewStart"/>): z0 far edge, z1 near edge.</summary>
    public static (float z0, float z1) RowSpan(int row, int size) => (ClothZ0 - (row + 1) * RowDepth(size), ClothZ0 - row * RowDepth(size));

    public static Box RodBox(int rod) => new(RodX0, RodY0 - 0.25f, RodZ[rod] - RodHalfDepth, RodX1, RodY0 + RodThick + RodLift + 0.25f, RodZ[rod] + RodHalfDepth);

    public static Box ShuttleBox(int i) => new(ShuttleX[i] - ShuttleHalfWidth, RailTop - 0.5f, ShuttleZ0, ShuttleX[i] + ShuttleHalfWidth, RailTop + 1.5f, ShuttleZ1);

    public static float ThreadWidth(int size) => Math.Min(0.4f, CellWidth(size) * 0.4f);

    /// <summary>
    /// Selection boxes (block units, controller-relative, turned by <paramref name="rotateYDeg"/>) and what each one is.
    /// Order: threads of the current row (none when complete), woven rows on the warp (index = cloth row), cloth beam, rods,
    /// shuttles, frame. <paramref name="clothRows"/>: the cloth's rows (default = <paramref name="size"/>, square; a large
    /// carpet has 2N, shown through <see cref="ViewStart"/>, lane/loom-roles).
    /// </summary>
    public static (Cuboidf[] boxes, HandLoomBoxPart[] parts) Build(int wovenRows, int size, int rods, float rotateYDeg, int clothRows = -1)
    {
        var boxes = new List<Box>();
        var parts = new List<HandLoomBoxPart>();
        void Add(Box b, HandLoomPart p, int i) { boxes.Add(b); parts.Add(new HandLoomBoxPart(p, i)); }
        int total = clothRows < 0 ? size : clothRows;
        int rows = Math.Clamp(wovenRows, 0, total);
        int start = ViewStart(rows, total, size);
        float y0 = ClothY + CellLift - PlateHalf, y1 = ClothY + CellLift + PlateHalf;
        if (rows < total)
        {
            var (z0, z1) = RowSpan(rows - start, size);
            for (int c = 0; c < size; c++)
            {
                var (x0, x1) = CellSpan(c, size);
                Add(new Box(x0, y0, z0, x1, y1, z1), HandLoomPart.Thread, c);
            }
        }
        for (int r = start; r < rows; r++)
        {
            var (z0, z1) = RowSpan(r - start, size);
            Add(new Box(ClothX0, y0, z0, ClothX1, y1, z1), HandLoomPart.Row, r);
        }
        Add(ClothBeam, HandLoomPart.Beam, 0);
        for (int k = 0; k < Math.Clamp(rods, 0, MaxRods); k++) Add(RodBox(k), HandLoomPart.Rod, k);
        for (int i = 0; i < ShuttleCount; i++) Add(ShuttleBox(i), HandLoomPart.Shuttle, i);
        Add(WarpBeam, HandLoomPart.Frame, 0);
        Add(RailL, HandLoomPart.Frame, 1);
        Add(RailR, HandLoomPart.Frame, 2);
        return (boxes.Select(b => Rotate(b.ToCuboid(), rotateYDeg)).ToArray(), parts.ToArray());
    }

    /// <summary>Collision: one low slab over the block (beams and rails; the player can step onto it).</summary>
    public static Cuboidf[] CollisionBoxes(float rotateYDeg)
        => new[] { Rotate(new Box(0f, 0f, 0.1f, 16f, ClothY, 15.75f).ToCuboid(), rotateYDeg) };

    public static Cuboidf Rotate(Cuboidf box, float rotateYDeg)
        => rotateYDeg % 360f == 0 ? box : box.RotatedCopy(0, rotateYDeg, 0, new Vec3d(0.5, 0.5, 0.5));
}
