using System;
using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.MathTools;

namespace ArtsAndCrafts.Tailor.Loom;

/// <summary>What a selection box of the loom stands for.</summary>
public enum LoomPart : byte
{
    Frame,
    /// <summary>A cell of the current row (at the fell); Index = column 0-15.</summary>
    Cell,
    /// <summary>A woven row still visible on the loom; Index = row.</summary>
    Cloth,
    /// <summary>Beat in or roll back a row. Index 2 = the reed (main target), 0/1 = the beater's left/right side post (its shuttle boxes are the shuttles).</summary>
    Beater,
    /// <summary>
    /// A warp thread at the back (lane/warp-shuttles-import; replaces the 6 spools): its roll on the warp beam;
    /// Index = warp end 0..N-1 (0 = the weaver's right, as the cells).
    /// </summary>
    WarpThread,
    /// <summary>A shuttle in the shuttle boxes at the beater ends (weft, tapestry palette); Index = shuttle 0-3 (0/1 = the weaver's left end).</summary>
    Shuttle,
    /// <summary>Unroll target (Index 0): cloth over the breast beam, cloth beam under the warp, its side wheels.</summary>
    ClothBeam,
    /// <summary>A row of the unrolled cloth hanging over the bench front; Index = woven row.</summary>
    HangRow,
    /// <summary>The weaver's bench: sit down / stand up.</summary>
    Seat,
    /// <summary>
    /// Draft mode (lane/draft-mode): one treadle of the tie-up board on the heddle frame; Index = treadle 0-3 (0 = the
    /// weaver's left). The model's own treadles lie under the warp and the bench, out of every weaver's sight.
    /// </summary>
    Treadle,
}

public readonly record struct LoomBoxPart(LoomPart Part, int Index);

/// <summary>
/// One drawn piece of the cloth on the loom (<see cref="LoomGeometry.ClothLayout"/>): woven row <paramref name="Row"/>
/// (-1 = plain leader cloth) along <paramref name="Strip"/>. <paramref name="OlderAtP0"/>: the strip's P0 end is the row's
/// older edge (toward row - 1; true on the warp slots, false over the breast beam, under the warp, on the hang path and
/// roll). <paramref name="F0"/>..<paramref name="F1"/>: the part of the row it covers (0 = older edge, 1 = newer edge;
/// hang rows split at the path's corners cover less than 0..1).
/// </summary>
public readonly record struct ClothPiece(int Row, Strip Strip, bool OlderAtP0, float F0, float F1, bool OnRoll = false)
{
    /// <summary>Point (y, z) at row fraction <paramref name="f"/> (0 = older edge), clamped to this piece.</summary>
    public (float y, float z) At(float f)
    {
        float span = F1 - F0;
        float u = span <= 0 ? 0 : Math.Clamp((f - F0) / span, 0f, 1f); // 0 at the F0 end
        if (!OlderAtP0) u = 1f - u; // P0 is the newer end
        return (Strip.Y0 + u * (Strip.Y1 - Strip.Y0), Strip.Z0 + u * (Strip.Z1 - Strip.Z0));
    }

    /// <summary>The sub-strip of row fractions <paramref name="f0"/>..<paramref name="f1"/> (clamped to this piece), P0 at f0.</summary>
    public Strip Sub(float f0, float f1)
    {
        var a = At(f0);
        var b = At(f1);
        return new Strip(a.y, a.z, b.y, b.z);
    }
}

/// <summary>
/// A flat strip across the loom (x ClothX0..ClothX1) from P0 to P1 in the (y, z) plane of the geometry frame, shape px.
/// The tesselator lays a cell element along local +z and rotates it by <see cref="RotationXDeg"/> around X
/// (VS: y' = y cos a - z sin a, z' = y sin a + z cos a).
/// </summary>
public readonly record struct Strip(float Y0, float Z0, float Y1, float Z1)
{
    public float Length => MathF.Sqrt((Y1 - Y0) * (Y1 - Y0) + (Z1 - Z0) * (Z1 - Z0));
    /// <summary>Rotation that maps local +z onto P0 → P1.</summary>
    public float RotationXDeg => MathF.Atan2(-(Y1 - Y0), Z1 - Z0) * 180f / MathF.PI;
    /// <summary>Unit normal (y, z) of the element's up face after rotation.</summary>
    public (float y, float z) Normal
    {
        get
        {
            float len = Length;
            return ((Z1 - Z0) / len, -(Y1 - Y0) / len);
        }
    }

    public Strip Lifted(float h)
    {
        var (ny, nz) = Normal;
        return new Strip(Y0 + ny * h, Z0 + nz * h, Y1 + ny * h, Z1 + nz * h);
    }
}

/// <summary>
/// Where the cloth, warp threads, shuttles and controls lie on the fly-shuttle loom shape (Immersive Fibercraft), and the selection
/// boxes built from them. All constants are shape pixels of the <b>geometry frame</b>, relative to the controller
/// block: the shape turned 180° around the controller block centre (x = 16 - x_shape, z = 16 - z_shape, y unchanged),
/// so the weaver sits at small z and faces +z, as in every earlier loom lane. Shape numbers in comments are the
/// unturned shape (scripts/shape-elements.py). Per facing, everything turns by <see cref="GeometryRotateY"/> around
/// the controller block centre, like the block shape.
/// <para>
/// Cloth path (the model's own plain cloth sheets are hidden, see scripts/loom-hide-elements.py): the fell and the
/// newest woven rows lie on the upper warp between the reed and the breast beam (<see cref="PlaneSlots"/>), the cloth
/// wraps over the breast beam front (<see cref="DropSlots"/>), runs back under the warp (<see cref="UnderSlots"/>)
/// and rolls onto the cloth beam. Slots without a woven row show plain leader cloth. Unrolled, the rows beyond the warp
/// run on over the bench and down its front (<see cref="HangPieces"/>). The reed (the model's beater) is the beat target.
/// </para>
/// <para>
/// Display order: index 0 = the row at the fell (the current row, or the last row once the cloth is complete),
/// then older rows: warp slots 1..3, breast beam, under the warp, then the roll
/// (face k shows index <c>PathSlots + k mod rolled</c>).
/// </para>
/// </summary>
public static class LoomGeometry
{
    /// <summary>Cloth width: the model's warp sheets (shape x -4.11..20.1).</summary>
    public const float ClothX0 = -4.1f, ClothX1 = 20.1f;
    /// <summary>Our warp-slot cloth floats this far above the cloth plane (px); the plane itself already clears the warp strings.</summary>
    public const float Lift = 0.05f;
    /// <summary>Half thickness (z) of a hanging row's box (px): thin, so a steep ray enters the row it is aimed at.</summary>
    const float HangBoxHalf = 0.01f;

    /// <summary>Geometry frame → shape: the shape's own rotateY plus 180.</summary>
    public static float GeometryRotateY(float shapeRotateYDeg) => (shapeRotateYDeg + 180f) % 360f;

    /// <summary>
    /// Cloth plane on the upper warp (y, z), rest pose: a straight line from the breast beam top (cloth sheet, shape
    /// y 16.91 z 25.08) to the reed (warp strings, shape y 15.70 z 15.54). The model's cloth and warp kink at its own
    /// fell (shape z 21.94); the straight line floats up to ~0.3 px above the warp strings there, but keeps the slope
    /// (0.127) below the view slope of every weaver position, so each woven row stays visible and clickable.
    /// </summary>
    static readonly (float y, float z)[] Warp = { (16.91f, -9.08f), (15.70f, 0.46f) };
    public static float WarpFrontY => Warp[0].y;
    public static float WarpFrontZ => Warp[0].z;
    public static float WarpBackY => Warp[^1].y;
    public static float WarpBackZ => Warp[^1].z;
    /// <summary>Front of the warp beam (shape z 4.5): the upper warp runs from the breast beam to here.</summary>
    public const float WarpBeamFrontZ = 11.5f;
    /// <summary>Heddles (shape z 13.75..14.4).</summary>
    public const float HeddleZ0 = 1.6f, HeddleZ1 = 2.25f;

    /// <summary>Slots on the warp: fell + 3 woven rows (copyable). 16-wide cloth; see <see cref="PlaneSlotsFor"/>.</summary>
    public const int PlaneSlots = 4;
    /// <summary>Slots over the breast beam front, and back under the warp to the cloth beam (16-wide cloth).</summary>
    public const int DropSlots = 3, UnderSlots = 8;
    public const int PathSlots = PlaneSlots + DropSlots + UnderSlots;
    /// <summary>Row length (px) of a 16-wide cloth; see <see cref="RowLengthFor"/>.</summary>
    public const float RowLength = 1.5f;

    /// <summary>
    /// Fineness of a cloth <paramref name="width"/> cells wide (lane/loom-feedback-6, owner: a 32 x 32 cloth keeps the
    /// 16 x 16 cloth's physical size): 1 for 16 (and narrower), 2 for 32. Rows are 1 / k as long and every slot count of
    /// the cloth path is k times larger, so the same length of cloth lies on the warp, the breast beam and under the warp.
    /// </summary>
    public static int ScaleFor(int width) => Math.Max(1, width / WeavingState.DefaultWidth);
    /// <summary>Square default: a negative width means "as many cells as rows" (callers that pass only rows).</summary>
    static int W(int width, int rows) => width > 0 ? width : rows;
    public static float RowLengthFor(int width) => RowLength / ScaleFor(width);
    public static int PlaneSlotsFor(int width) => PlaneSlots * ScaleFor(width);
    public static int DropSlotsFor(int width) => DropSlots * ScaleFor(width);
    public static int UnderSlotsFor(int width) => UnderSlots * ScaleFor(width);
    public static int PathSlotsFor(int width) => PathSlots * ScaleFor(width);

    /// <summary>The reed (beat target, shape x -4.2..20.2, y 14.4..18.4, z 15.3..17.45 incl. the shuttle race).</summary>
    public const float BeaterX0 = -4.2f, BeaterX1 = 20.2f;
    const float ReedY0 = 14.4f, ReedY1 = 18.4f, ReedZ0 = -1.45f, ReedZ1 = 0.7f;

    /// <summary>Breast beam axis (shape y 15.8, z 25.5) and the radius the cloth wraps it at.</summary>
    public const float BreastY = 15.8f, BreastZ = -9.5f;
    const float DropRadius = 1.25f;
    /// <summary>Where the cloth leaves the breast beam bottom (shape y 14.76, z 25.98): angle on the breast beam.</summary>
    static readonly float DropEndAngle = MathF.Atan2(-1.04f, 0.48f);
    /// <summary>Under the warp: the model's cloth_warp_sheet1/2 path to the cloth beam's back top (shape z 17.07, 15.44).</summary>
    static readonly (float y, float z)[] UnderTail = { (12.88f, -1.07f), (10.3f, 0.6f) };

    /// <summary>Cloth beam axis (shape cloth_beam1/2, y 8.29, z 17.39) and its bare radius (wrapped in leader cloth).</summary>
    public const float RollY = 8.29f, RollZ = -1.39f;
    public const float BeamRadius = 1.95f;
    public const int RollFaces = 8;

    /// <summary>Unrolled cloth hangs down the bench front (shape bench z 30.35, top y 10.5): top edge, bottom, plane z.</summary>
    public const float HangTopY = 10.6f, HangZ = -14.7f, HangBottomY = 0.4f;

    /// <summary>
    /// Warp beam axis (shape warp_beam_obj y 14.6, z 2.6; the wound warp has radius 1.91). Since lane/warp-shuttles-import
    /// each warp end is wound there on its own: one thread roll per end (radius <see cref="ThreadRollRadius"/>, the end's
    /// colour), the click target to load that thread (box radius <see cref="ThreadBoxRadius"/>). Replaces the 6 spools.
    /// </summary>
    public const float BeamY = 14.6f, BeamZ = 13.4f;
    public const float ThreadRollRadius = 2.05f, ThreadBoxRadius = 2.15f;
    /// <summary>Gap between neighbouring thread rolls (px; drawn and box), so each end reads as its own thread.</summary>
    public const float ThreadRollGap = 0.08f;
    /// <summary>
    /// Imported draft (lane/warp-shuttles-import): a tag on top of the roll of every end whose loaded twine is not the
    /// one the draft needs, in the needed colour (mesh only).
    /// </summary>
    public const float TagY0 = 16.75f, TagY1 = 17.3f, TagZ0 = 12.9f, TagZ1 = 13.9f;

    /// <summary>
    /// Shuttles (lane/shuttle-fix; were on the breast beam ends): 4, in the shuttle boxes at the two ends of the beater
    /// (batten), as on a fly-shuttle loom with drop boxes: 2 per end, one in front (on the box, lower) and one behind it
    /// on a riser (higher), so both top faces show from the seat and from the front. Shuttle boxes = the model's
    /// LauncherBaseL*/R* (shape x -9.3..-4.9 / 20.9..25.3, y 14.67..16.39, z 15.42..17.45 → frame x 20.9..25.3 /
    /// -9.3..-4.9, z -1.45..0.58). Shuttle 0/1 = the weaver's left end (high x; 0 front, 1 back), 2/3 = the right end.
    /// The selection boxes cover the whole shuttle box (no beater box there), so a click at a beater end hits a shuttle.
    /// The beater swings during the cycle (beater2 rotationX to -16°); our shuttles are a static mesh, hidden while
    /// the cycle runs (the model's own shuttle flies then), boxes stay at rest.
    /// </summary>
    public const int ShuttleCount = SpoolRack.ShuttleCount;
    public const float LauncherY0 = 14.67f, LauncherTopY = 16.39f, LauncherZ0 = -1.45f, LauncherZ1 = 0.58f;
    /// <summary>Front / back half of a shuttle box (z), the back riser's top, a shuttle body's height (px).</summary>
    public const float TierSplitZ = -0.45f, RiserTopY = 17.15f, ShuttleBodyHeight = 0.7f;
    /// <summary>Selection box tops (px): a clickable column over each shuttle, the front one above the reed top (18.4).</summary>
    public const float ShuttleFrontBoxTopY = 18.6f, ShuttleBackBoxTopY = 19.4f;
    /// <summary>Shuttle box x span per end: 0 = the weaver's left (high x), 1 = the right.</summary>
    static readonly (float x0, float x1)[] LauncherX = { (20.9f, 25.3f), (-9.3f, -4.9f) };
    /// <summary>A shuttle body is this much shorter than its box at each end, and this much narrower than its half (px).</summary>
    const float ShuttleInsetX = 0.3f, ShuttleInsetZ = 0.1f;
    /// <summary>The active shuttle is drawn this much higher (mesh only), so the choice reads from the seat.</summary>
    public const float ActiveShuttleLift = 0.35f;

    /// <summary>Bench (shape bench1/2): seat box.</summary>
    static readonly (float x1, float y1, float z1, float x2, float y2, float z2) BenchShape = (-9.6f, 8.5f, 26.35f, 25.6f, 10.5f, 30.35f);

    /// <summary>
    /// Seated player, controller-relative, north facing (shape rotateY 0), block units: feet (mount position) and
    /// eye = feet + the seat's LocalEyePos, as in the reference loom. Shape px: feet (8, 9.6, 22.4), eye (8, 25.6, 24.32).
    /// </summary>
    public static readonly Vec3d SeatFeetNorth = new(0.5, 0.6, 1.4);
    public static readonly Vec3f SeatEyeOffsetNorth = new(0f, 1f, 0.12f);
    /// <summary>Seated eye in the geometry frame (px).</summary>
    public static (float x, float y, float z) SeatedEyePx => (16f - (float)(SeatFeetNorth.X + SeatEyeOffsetNorth.X) * 16f,
        (float)(SeatFeetNorth.Y + SeatEyeOffsetNorth.Y) * 16f, 16f - (float)(SeatFeetNorth.Z + SeatEyeOffsetNorth.Z) * 16f);

    /// <summary>Cell width for a cloth <paramref name="width"/> cells wide (16: 1.51 px, 32: 0.76 px). Row length follows
    /// (lane/loom-feedback-6: <see cref="RowLengthFor"/>, 16: 1.5 px, 32: 0.75 px), so a 32 x 32 cloth is as large as a 16 x 16.</summary>
    public static float CellWidthFor(int width) => (ClothX1 - ClothX0) / width;
    public static float CellWidth => CellWidthFor(WeavingState.DefaultWidth);

    #region Paths

    static float PathLength((float y, float z)[] pts)
    {
        float len = 0;
        for (int i = 1; i < pts.Length; i++) len += Dist(pts[i - 1], pts[i]);
        return len;
    }

    static float Dist((float y, float z) a, (float y, float z) b) => MathF.Sqrt((b.y - a.y) * (b.y - a.y) + (b.z - a.z) * (b.z - a.z));

    /// <summary>Point <paramref name="t"/> px along a polyline (clamped to its ends).</summary>
    static (float y, float z) At((float y, float z)[] pts, float t)
    {
        for (int i = 1; i < pts.Length; i++)
        {
            float d = Dist(pts[i - 1], pts[i]);
            if (t <= d || i == pts.Length - 1)
            {
                float f = d <= 0 ? 0 : Math.Clamp(t / d, 0f, 1f);
                return (pts[i - 1].y + f * (pts[i].y - pts[i - 1].y), pts[i - 1].z + f * (pts[i].z - pts[i - 1].z));
            }
            t -= d;
        }
        return pts[^1];
    }

    static Strip Chord((float y, float z)[] pts, float t0, float t1)
    {
        var (y0, z0) = At(pts, t0);
        var (y1, z1) = At(pts, t1);
        return new Strip(y0, z0, y1, z1);
    }

    /// <summary>Point on the upper warp, <paramref name="t"/> px from the breast beam: (y, z).</summary>
    public static (float y, float z) WarpAt(float t) => At(Warp, t);

    /// <summary>Point on the breast beam at angle <paramref name="a"/> (rad; π/2 = top, 0 = the weaver's side).</summary>
    static (float y, float z) BreastAt(float a, float r = DropRadius) => (BreastY + r * MathF.Sin(a), BreastZ - r * MathF.Cos(a));

    static float DropStartAngle => MathF.Atan2(WarpFrontY - BreastY, BreastZ - WarpFrontZ);

    /// <summary>Breast beam wrap: from the warp front over the beam's weaver side to its bottom (one point per slot edge).</summary>
    static (float y, float z)[] DropPoints(int width = WeavingState.DefaultWidth)
    {
        int n = DropSlotsFor(width);
        var pts = new (float y, float z)[n + 1];
        pts[0] = (WarpFrontY, WarpFrontZ);
        for (int i = 1; i <= n; i++) pts[i] = BreastAt(DropStartAngle + (DropEndAngle - DropStartAngle) * i / n);
        return pts;
    }

    static (float y, float z)[] UnderPoints() => new[] { DropPoints()[^1] }.Concat(UnderTail).ToArray();

    #endregion

    /// <summary>
    /// Row numbers in display order (see class doc). Woven row numbers; <c>wovenRows</c> itself = the current row.
    /// Square cloth (lane/square-cloth): the fell shows the current row until the cloth is complete (<paramref name="capacity"/> = its rows),
    /// then the last woven row.
    /// </summary>
    public static int[] DisplayOrder(int wovenRows, int capacity = WeavingState.DefaultRows)
    {
        wovenRows = Math.Clamp(wovenRows, 0, capacity);
        bool complete = wovenRows >= capacity;
        var order = new List<int>();
        if (!complete) order.Add(wovenRows);
        for (int r = wovenRows - 1; r >= 0; r--) order.Add(r);
        return order.ToArray();
    }

    /// <summary>Rows on the roll (rolled up): everything after the warp, breast beam and under-warp slots.</summary>
    /// <remarks><paramref name="width"/> (cells across; default = rows, square) sets the path's slot counts (<see cref="ScaleFor"/>).</remarks>
    public static int RolledRows(int wovenRows, int rows = WeavingState.DefaultRows, int width = -1)
        => Math.Max(0, DisplayOrder(wovenRows, rows).Length - PathSlotsFor(W(width, rows)));

    /// <summary>Rows left on the roll while unrolled: those beyond the hang path's room (<see cref="MaxHangRowsFor"/>).</summary>
    public static int UnrolledRollRows(int wovenRows, int rows, int width = -1)
        => Math.Max(0, DisplayOrder(wovenRows, rows).Length - PlaneSlotsFor(W(width, rows)) - MaxHangRowsFor(rows, width));

    /// <summary>Most a roll grows (px): the 32-row cloth's full roll (17 rows x 0.06), so long cloths do not swell it further.</summary>
    public const float MaxRollGrowth = 1.02f;

    /// <summary>
    /// Apothem of the cloth roll (px): the bare beam's leader cloth, then 0.12 px per rolled row for a 16-row
    /// cloth (scaled by 16 / rows), at most <see cref="MaxRollGrowth"/>.
    /// </summary>
    public static float RollRadius(int rolledRows, int rows = WeavingState.DefaultRows)
        => BeamRadius + 0.1f + Math.Min(MaxRollGrowth, 0.12f * WeavingState.DefaultRows / rows * Math.Max(0, rolledRows));

    /// <summary>
    /// Warp slot (0 = the fell, PlaneSlots - 1 = at the breast beam): a chord of the upper warp, lifted off it. The cloth
    /// here slopes down away from the weaver (breast beam top → fell → reed), so rows are drawn tilted along the warp
    /// (no steps whose risers would hide the next row) and each selection box is a thin level plate at the row's far
    /// (lower) edge: a ray that can see the row's surface (steeper than the warp) then enters this row's plate first,
    /// passing above the plates of the rows in front of it (see <see cref="WarpBox"/>, selftest ray checks).
    /// The fell (slot 0, the cells) is drawn level at its near-edge height instead: its plate then matches the drawn
    /// cells exactly, so oblique rays from the side land in the cell they aim at (a tilted row's plate lies below
    /// its surface and shifts sideways hits into the neighbour cell).
    /// </summary>
    public static Strip PlaneStrip(int slot, int width = WeavingState.DefaultWidth)
    {
        float len = RowLengthFor(width);
        float t0 = (PlaneSlotsFor(width) - 1 - slot) * len;
        var s = Chord(Warp, t0, t0 + len).Lifted(Lift);
        return slot == 0 ? new Strip(s.Y0, s.Z0, s.Y0, s.Z1) : s;
    }

    /// <summary>Height of the drawn warp slots at <paramref name="z"/> (on the slot covering z; clamped to the warp ends).</summary>
    public static float DrawnWarpY(float z, int width = WeavingState.DefaultWidth)
    {
        int n = PlaneSlotsFor(width);
        for (int slot = 0; slot < n; slot++)
        {
            var s = PlaneStrip(slot, width);
            if (z >= s.Z0 - 1e-4f && z <= s.Z1 + 1e-4f) return s.Y0 + (s.Y1 - s.Y0) * (z - s.Z0) / (s.Z1 - s.Z0);
        }
        return z < PlaneStrip(n - 1, width).Z0 ? PlaneStrip(n - 1, width).Y0 : PlaneStrip(0, width).Y1;
    }

    /// <summary>The fell slot (current row): inner edge of the woven area.</summary>
    public static Strip FellStrip(int width = WeavingState.DefaultWidth) => PlaneStrip(0, width);

    /// <summary>Distance along the warp from the breast beam to the fell's inner edge (px); the same for every width.</summary>
    public static float FellT => PlaneSlots * RowLength;

    /// <summary>Breast beam slot (0 = at the warp front), over the beam's weaver side and down to its bottom.</summary>
    public static Strip DropStrip(int slot, int width = WeavingState.DefaultWidth)
    {
        var pts = DropPoints(width);
        return new Strip(pts[slot].y, pts[slot].z, pts[slot + 1].y, pts[slot + 1].z);
    }

    /// <summary>Under-warp slot (0 = at the breast beam bottom), back to the cloth beam.</summary>
    public static Strip UnderStrip(int slot, int width = WeavingState.DefaultWidth)
    {
        float len = RowLengthFor(width);
        return Chord(UnderPoints(), slot * len, (slot + 1) * len);
    }

    /// <summary>Path slot k (0..PathSlotsFor(width)-1): warp, breast beam, under the warp.</summary>
    public static Strip PathStrip(int k, int width = WeavingState.DefaultWidth)
    {
        int plane = PlaneSlotsFor(width), drop = DropSlotsFor(width);
        return k < plane ? PlaneStrip(k, width) : k < plane + drop ? DropStrip(k - plane, width) : UnderStrip(k - plane - drop, width);
    }

    /// <summary>Face k of the octagonal roll on the cloth beam: 0 faces the weaver, then going under (2 = bottom, 4 = back).</summary>
    public static Strip RollFace(int face, int rolledRows, int rows = WeavingState.DefaultRows)
    {
        float r = RollRadius(rolledRows, rows);
        double a = face * 2 * Math.PI / RollFaces;
        float ny = -(float)Math.Sin(a), nz = -(float)Math.Cos(a);
        float ty = nz, tz = -ny; // tangent: down at the front face
        float h = r * (float)Math.Tan(Math.PI / RollFaces);
        float cy = RollY + ny * r, cz = RollZ + nz * r;
        return new Strip(cy - ty * h, cz - tz * h, cy + ty * h, cz + tz * h);
    }

    /// <summary>The reed, beat target (Beater 2): (y0, y1, z0, z1); not drawn by us (the model's beater).</summary>
    public static (float y0, float y1, float z0, float z1) BeaterBar() => (ReedY0, ReedY1, ReedZ0, ReedZ1);

    /// <summary>
    /// Unrolled cloth path (lane/loom-feedback-3): from the warp front over the breast beam's weaver side, in a straight
    /// line down over the bench to its front top edge, then down the bench front. The rows beyond the warp slots lie on
    /// it in display order, one continuous cloth (no plain drape between the warp rows and the hanging rows: that was
    /// the white band of the earlier lanes). The cloth simply ends after its first woven row.
    /// </summary>
    public static (float y, float z)[] HangPoints()
    {
        var front = BreastAt(0);
        return new[] { (WarpFrontY, WarpFrontZ), BreastAt(DropStartAngle / 2), front, (HangTopY, HangZ), (HangBottomY, HangZ) };
    }

    /// <summary>Most rows on the hang path (data guard; a 32 x 32 cloth uses 28).</summary>
    public const int HangRowCap = 45;

    /// <summary>
    /// Room on the hang path, in rows: every row of the cloth but the fell and the 3 warp rows (lane/square-cloth: a
    /// complete cloth, its last row at the fell, reaches the bottom; 16 → 12 rows of 1.59 px, 32 → 28 of 0.68 px). Every
    /// row of a square cloth fits, so nothing stays on the roll while unrolled (that path is kept as a guard).
    /// </summary>
    public static int MaxHangRowsFor(int rows, int width = -1) => Math.Clamp(rows - PlaneSlotsFor(W(width, rows)), 1, HangRowCap);
    public static int MaxHangRows => MaxHangRowsFor(WeavingState.DefaultRows);

    /// <summary>Length of one row on the hang path (px).</summary>
    public static float HangRowLengthFor(int rows, int width = -1) => PathLength(HangPoints()) / MaxHangRowsFor(rows, width);
    public static float HangRowLength => HangRowLengthFor(WeavingState.DefaultRows);

    /// <summary>
    /// Hang row <paramref name="index"/> (0 = the row right after the warp slots, display index PlaneSlotsFor + index):
    /// its pieces along the hang path, split at the path's corners (each piece a flat strip).
    /// </summary>
    public static Strip[] HangPieces(int index, int rows = WeavingState.DefaultRows, int width = -1)
    {
        var pts = HangPoints();
        float len = HangRowLengthFor(rows, width);
        float t0 = index * len, t1 = (index + 1) * len;
        var pieces = new List<Strip>();
        float start = 0;
        for (int i = 1; i < pts.Length; i++)
        {
            float end = start + Dist(pts[i - 1], pts[i]);
            float a = Math.Max(t0, start), b = Math.Min(t1, end);
            if (b - a > 1e-4f) pieces.Add(Chord(pts, a, b));
            start = end;
        }
        return pieces.ToArray();
    }

    /// <summary>
    /// Everything the cloth mesh draws, in order: warp slots (fell first), then rolled up the breast beam and under-warp
    /// slots, or unrolled the hang path (rows that fit, <see cref="MaxHangRowsFor"/>), then the roll faces (rolled up
    /// always; unrolled only when rows are left on the roll). Row = woven row number (<c>wovenRows</c> = the current row),
    /// -1 = plain leader cloth. Pure: used by the mesh (client), the overlay and the selftest (element counts).
    /// </summary>
    public static List<ClothPiece> ClothLayout(int wovenRows, int rows, bool unrolled, int width = -1)
    {
        width = W(width, rows);
        var list = new List<ClothPiece>();
        int[] order = DisplayOrder(wovenRows, rows);
        int plane = PlaneSlotsFor(width), path = PathSlotsFor(width);
        int pathSlots = unrolled ? plane : path;
        for (int k = 0; k < pathSlots; k++)
            list.Add(new ClothPiece(k < order.Length ? order[k] : -1, PathStrip(k, width), k < plane, 0f, 1f));
        int hangEnd = Math.Min(order.Length, plane + MaxHangRowsFor(rows, width));
        if (unrolled)
        {
            float len = HangRowLengthFor(rows, width);
            for (int k = plane; k < hangEnd; k++)
            {
                // Pieces in path order from the row's newer edge; F0..F1 = the part of the row (0 = older edge) each covers.
                float d = 0;
                foreach (var piece in HangPieces(k - plane, rows, width))
                {
                    float d1 = d + piece.Length;
                    list.Add(new ClothPiece(order[k], piece, false, 1f - d1 / len, 1f - d / len));
                    d = d1;
                }
            }
        }
        int rolled = unrolled ? UnrolledRollRows(wovenRows, rows, width) : RolledRows(wovenRows, rows, width);
        int rollStart = unrolled ? hangEnd : path;
        if (!unrolled || rolled > 0)
        {
            // Outermost layer of the roll: the most recently rolled row faces the weaver; bare beam: leader cloth.
            for (int f = 0; f < RollFaces; f++)
                list.Add(new ClothPiece(rolled > 0 ? order[rollStart + f % rolled] : -1, RollFace(f, rolled, rows), false, 0f, 1f, OnRoll: true));
        }
        return list;
    }

    /// <summary>
    /// The fell piece of a row being woven (lane/loom-feedback-6, <see cref="Weaving.LoomLook.GrowFraction"/>): the woven part
    /// from the row's older edge to <paramref name="fraction"/> (null at 0) and the bare warp beyond it up to the newer
    /// (reed) edge (null at 1). The cloth grows toward the reed, where the beater lays each pick.
    /// </summary>
    public static (Strip? woven, Strip? bare) GrowStrips(ClothPiece piece, float fraction)
    {
        float f = Math.Clamp(fraction, 0f, 1f);
        return (f > 1e-4f ? piece.Sub(0f, f) : null, f < 1f - 1e-4f ? piece.Sub(f, 1f) : null);
    }

    /// <summary>Selection box of a hang piece: thin plate for a vertical piece, else the piece's own AABB (the path slopes toward the weaver, so a ray enters the row it sees first).</summary>
    static Cuboidf HangBox(Strip s)
    {
        float y0 = Math.Min(s.Y0, s.Y1), y1 = Math.Max(s.Y0, s.Y1), z0 = Math.Min(s.Z0, s.Z1), z1 = Math.Max(s.Z0, s.Z1);
        if (z1 - z0 < HangBoxHalf) { z0 -= HangBoxHalf; z1 += HangBoxHalf; }
        return new Cuboidf(ClothX0, y0, z0, ClothX1, y1, z1);
    }

    /// <summary>
    /// Warp strings (our own, lane/loom-feedback-3; the model's string sheets are hidden): from the fell's far edge
    /// along the upper warp to the reed, under the heddles and up to the top of the warp beam (shape warp_stringsL_obj9,
    /// obj1, obj5: y 15.57 at z 13.85, 16.37 at z 5.89, 16.51 at z 4.49). (y, z) points, fell first.
    /// </summary>
    public static (float y, float z)[] WarpStringPoints()
    {
        var fell = WarpAt(FellT);
        return new[] { fell, (WarpBackY, WarpBackZ), (15.57f, 2.15f), (16.37f, 10.11f), (16.51f, 11.51f) };
    }

    /// <summary>Threads per cell column and their width (px): two thin threads per column read as strings, not a sheet.</summary>
    public const int ThreadsPerColumn = 2;
    public const float ThreadWidth = 0.3f;

    /// <summary>Thread j of column c: its x span.</summary>
    public static (float x0, float x1) ThreadSpan(int column, int thread, int width = WeavingState.DefaultWidth, int threads = ThreadsPerColumn)
    {
        float cx = ClothX0 + (column + (thread + 0.5f) / threads) * CellWidthFor(width);
        return (cx - ThreadWidth / 2, cx + ThreadWidth / 2);
    }

    /// <summary>Thread roll of warp end <paramref name="end"/> on the warp beam: its x span (the end's cell column, minus a gap).</summary>
    public static (float x0, float x1) WarpThreadSpan(int end, int width = WeavingState.DefaultWidth)
    {
        float w = CellWidthFor(width);
        return (ClothX0 + end * w + ThreadRollGap / 2, ClothX0 + (end + 1) * w - ThreadRollGap / 2);
    }

    /// <summary>Selection box of warp end <paramref name="end"/>'s thread roll (px).</summary>
    static Cuboidf WarpThreadBox(int end, int width)
    {
        var (x0, x1) = WarpThreadSpan(end, width);
        const float r = ThreadBoxRadius;
        return new Cuboidf(x0, BeamY - r, BeamZ - r, x1, BeamY + r, BeamZ + r);
    }

    /// <summary>Beater end of shuttle <paramref name="i"/>: 0 = the weaver's left (high x), 1 = the right.</summary>
    public static int ShuttleEnd(int i) => Math.Clamp(i, 0, ShuttleCount - 1) / 2;
    /// <summary>Tier of shuttle <paramref name="i"/> in its box: 0 = front (lower, on the box), 1 = back (on the riser).</summary>
    public static int ShuttleTier(int i) => Math.Clamp(i, 0, ShuttleCount - 1) % 2;

    /// <summary>Shuttle box at beater end <paramref name="end"/> (0 = left): x span.</summary>
    public static (float x0, float x1) LauncherSpan(int end) => LauncherX[Math.Clamp(end, 0, 1)];

    /// <summary>Shuttle <paramref name="i"/>'s drawn body at rest (px, unlifted), lying along the weft (x) in its box.</summary>
    public static (float x0, float y0, float z0, float x1, float y1, float z1) ShuttleBody(int i)
    {
        var (bx0, bx1) = LauncherSpan(ShuttleEnd(i));
        bool back = ShuttleTier(i) == 1;
        float y0 = back ? RiserTopY : LauncherTopY;
        float z0 = back ? TierSplitZ : LauncherZ0, z1 = back ? LauncherZ1 : TierSplitZ;
        return (bx0 + ShuttleInsetX, y0, z0 + ShuttleInsetZ, bx1 - ShuttleInsetX, y0 + ShuttleBodyHeight, z1 - ShuttleInsetZ);
    }

    /// <summary>Riser under the back shuttle of beater end <paramref name="end"/> (px, mesh): back half of the box, up to <see cref="RiserTopY"/>.</summary>
    public static (float x0, float y0, float z0, float x1, float y1, float z1) ShuttleRiser(int end)
    {
        var (x0, x1) = LauncherSpan(end);
        return (x0, LauncherTopY, TierSplitZ, x1, RiserTopY, LauncherZ1);
    }

    /// <summary>Shuttle <paramref name="i"/>: x span of its drawn body.</summary>
    public static (float x0, float x1) ShuttleSpan(int i)
    {
        var b = ShuttleBody(i);
        return (b.x0, b.x1);
    }

    /// <summary>
    /// Selection box of shuttle <paramref name="i"/> (px): its half of the shuttle box from the box bottom to above the
    /// (lifted) body, the whole box width, so the beater end is all shuttle.
    /// </summary>
    public static Cuboidf ShuttleBox(int i)
    {
        var (x0, x1) = LauncherSpan(ShuttleEnd(i));
        var b = ShuttleBody(i);
        bool back = ShuttleTier(i) == 1;
        // Tops above the reed top (y 18.4, front) and above the front box (back), so the reed never covers a shuttle's top.
        float top = back ? ShuttleBackBoxTopY : ShuttleFrontBoxTopY;
        return new Cuboidf(x0, LauncherY0, back ? TierSplitZ : LauncherZ0, x1, Math.Max(top, b.y1 + ActiveShuttleLift + 0.05f), back ? LauncherZ1 : TierSplitZ);
    }

    #region Tie-up board (draft mode)

    /// <summary>
    /// Tie-up board (lane/draft-mode): drawn by us inside the heddle top frame (shape cube96-101: uprights x -0.3..0.1 and
    /// 15.9..16.3, bars y 23.5..23.9 and 30.9..31.4, front face z 14.6 → frame z 1.4), on its weaver side, at the seated
    /// eye's height. One column per treadle (treadle 0 = the weaver's left = high x), 4 shaft squares each (shaft 1 at the
    /// bottom, filled = lifted by that treadle), the pressed treadle highlighted. Why here: the model's 2 treadles
    /// (cube125/126, y 0.8..3.6) lie under the warp cloth and behind the bench, hidden from the seat and the front.
    /// </summary>
    public const float BoardX0 = 0.5f, BoardX1 = 15.5f, BoardY0 = 24.2f, BoardY1 = 30.6f, BoardZ = 1.3f;
    /// <summary>Gap between treadle columns, shaft squares drawn this far in front of the board (no z-fight).</summary>
    public const float TreadleGap = 0.4f, SquareLift = 0.04f;
    public const int BoardTreadles = DraftPresets.Treadles, BoardShafts = DraftPresets.Shafts;

    /// <summary>Treadle column <paramref name="t"/> (0 = the weaver's left): x span.</summary>
    public static (float x0, float x1) TreadleSpan(int t)
    {
        float pitch = (BoardX1 - BoardX0) / BoardTreadles;
        float x1 = BoardX1 - t * pitch;
        return (x1 - pitch + TreadleGap / 2, x1 - TreadleGap / 2);
    }

    /// <summary>Shaft square <paramref name="shaft"/> (0 = shaft 1, bottom) inside treadle column <paramref name="t"/>: (x0, x1, y0, y1).</summary>
    public static (float x0, float x1, float y0, float y1) ShaftSquare(int t, int shaft)
    {
        var (x0, x1) = TreadleSpan(t);
        const float margin = 0.45f, gap = 0.3f;
        float h = (BoardY1 - BoardY0 - 2 * margin - (BoardShafts - 1) * gap) / BoardShafts;
        float y0 = BoardY0 + margin + shaft * (h + gap);
        float inset = Math.Max(0.3f, (x1 - x0 - 1.8f) / 2);
        return (x0 + inset, x1 - inset, y0, y0 + h);
    }

    /// <summary>Selection box of treadle column <paramref name="t"/>: a thin plate on the board (px).</summary>
    static Cuboidf TreadleBox(int t)
    {
        var (x0, x1) = TreadleSpan(t);
        return new Cuboidf(x0, BoardY0, BoardZ - SquareLift - 0.01f, x1, BoardY1, BoardZ + 0.01f);
    }

    #endregion

    #region Fixed parts and collision

    /// <summary>A box given in (unturned) shape px, converted to the geometry frame.</summary>
    static (float x1, float y1, float z1, float x2, float y2, float z2) S(float x1, float y1, float z1, float x2, float y2, float z2)
        => (16f - x2, y1, 16f - z2, 16f - x1, y2, 16f - z1);

    /// <summary>One side frame of the shape (x ranges: posts, rails, bottom rail): bottom rail, front post, top rail, back post, mid rail, mid post.</summary>
    static IEnumerable<(float, float, float, float, float, float)> SideFrame(float p1, float p2, float r1, float r2, float b1, float b2)
    {
        yield return S(b1, 1.5f, 1.6f, b2, 5f, 31.6f);
        yield return S(p1, 5f, 22.4f, p2, 29f, 30.6f);
        yield return S(r1, 29f, 10.6f, r2, 32f, 30.95f);
        yield return S(p1, 5f, 3.3f, p2, 22.8f, 9.4f);
        yield return S(p1, 10f, 9.4f, p2, 13.5f, 22.4f);
        yield return S(p1, 4f, 9.4f, p2, 10f, 18.9f);
    }

    static IEnumerable<(float, float, float, float, float, float)> Sides()
        => SideFrame(-12.5f, -9.5f, -12.8f, -9.2f, -13f, -9f).Concat(SideFrame(25.5f, 28.5f, 25.2f, 28.8f, 25f, 29f));

    /// <summary>Static parts of the loom (geometry frame px). Shape element names in comments.</summary>
    static readonly (LoomPart part, int index, float x1, float y1, float z1, float x2, float y2, float z2)[] FixedBoxes = BuildFixedBoxes();

    static (LoomPart, int, float, float, float, float, float, float)[] BuildFixedBoxes()
    {
        var list = new List<(LoomPart, int, float, float, float, float, float, float)>();
        void Add(LoomPart p, int i, (float x1, float y1, float z1, float x2, float y2, float z2) b) => list.Add((p, i, b.x1, b.y1, b.z1, b.x2, b.y2, b.z2));

        Add(LoomPart.Frame, 0, S(-14.5f, 0f, 27.1f, 30.5f, 3f, 30.6f));        // front floor beam (cube81-84)
        Add(LoomPart.Frame, 0, S(-14.5f, 0f, 2.6f, 30.5f, 3f, 6.1f));          // back floor beam (cube85-88)
        foreach (var side in Sides()) Add(LoomPart.Frame, 0, side);              // side frames (cube1-80, 89-95, 102, 127)
        Add(LoomPart.Frame, 0, S(4.18f, 0.8f, 6.1f, 11.82f, 3.63f, 21.64f));    // treadles (cube109-111, 125, 126)
        Add(LoomPart.Frame, 0, S(-9.55f, 14.8f, 24.5f, -4.2f, 16.8f, 26.5f));   // breast beam ends
        Add(LoomPart.Frame, 0, S(20.2f, 14.8f, 24.5f, 25.65f, 16.8f, 26.5f));
        Add(LoomPart.Frame, 0, S(-4.2f, 14.6f, 24.5f, 20.2f, 16.6f, 25.08f));   // breast beam under the woven rows
        Add(LoomPart.Frame, 0, S(-8.4f, 12.7f, 0.7f, -4.1f, 16.5f, 4.5f));      // warp beam ends (warp_beam_obj1/2)
        Add(LoomPart.Frame, 0, S(20.1f, 12.7f, 0.7f, 24.4f, 16.5f, 4.5f));
        Add(LoomPart.Frame, 0, S(-14.55f, 11.5f, 0.3f, -12.8f, 16.6f, 13.25f)); // ratchet + gear
        Add(LoomPart.Frame, 0, S(-12.8f, 13f, 0.8f, -8.4f, 16.9f, 4.5f));       // warp beam mount + axle (left)
        Add(LoomPart.Frame, 0, S(24.4f, 13f, 0.8f, 29.95f, 16.9f, 4.5f));       // (right)
        Add(LoomPart.Frame, 0, S(-4.2f, 11.8f, 13.75f, 20.2f, 19f, 14.4f));     // heddles (headle1/2)
        Add(LoomPart.Frame, 0, S(-9.2f, 23.5f, 13.65f, 25.2f, 31.8f, 14.6f));   // heddle top frame (cube96-101, 107, 108)
        // Beater side posts only: the shuttle boxes at the beater ends (LauncherBase*) are the shuttles' boxes (lane/shuttle-fix).
        Add(LoomPart.Beater, 0, S(-4.9f, 14f, 15.1f, -4.1f, 32f, 15.9f));       // beater side post (cube119/120)
        Add(LoomPart.Beater, 1, S(20.1f, 14f, 15.1f, 20.9f, 32f, 15.9f));       // (cube121/122)
        Add(LoomPart.ClothBeam, 0, S(-7.2f, 5.9f, 15.0f, 22.8f, 10.7f, 19.8f));  // cloth beam + roll (cloth_beam1/2)
        Add(LoomPart.ClothBeam, 0, S(-14.9f, 5.25f, 14.25f, -12.8f, 11.55f, 20.55f)); // cloth beam wheel + axle (cloth_beam_wheelL)
        Add(LoomPart.ClothBeam, 0, S(28.8f, 5.25f, 14.25f, 30.9f, 11.55f, 20.55f));   // (cloth_beam_wheelR)
        var b = BenchShape;
        Add(LoomPart.Seat, 0, S(b.x1, b.y1, b.z1, b.x2, b.y2, b.z2));         // bench1/2
        return list.ToArray();
    }

    /// <summary>Cloth over the breast beam front (shape x -4.2..20.2, y 14.4..16.9, z 25.1..26.8): unroll target while rolled up.</summary>
    static readonly (float x1, float y1, float z1, float x2, float y2, float z2) BreastFrontUnrollBox = S(-4.2f, 14.4f, 25.1f, 20.2f, 16.9f, 26.8f);

    /// <summary>Coarse collision (geometry frame px): floor beams, side frames, bench, beams, heddles, reed. No full cubes: the seated eye must stay outside every box (BehaviorBreathe).</summary>
    static readonly (float x1, float y1, float z1, float x2, float y2, float z2)[] CollisionPx = new[]
    {
        S(-14.5f, 0f, 27.1f, 30.5f, 3f, 30.6f), S(-14.5f, 0f, 2.6f, 30.5f, 3f, 6.1f),
        S(BenchShape.x1, BenchShape.y1, BenchShape.z1, BenchShape.x2, BenchShape.y2, BenchShape.z2),
        S(-9.55f, 14.8f, 24.5f, 25.65f, 16.8f, 26.5f),   // breast beam
        S(-8.4f, 12.7f, 0.7f, 24.4f, 16.5f, 4.5f),        // warp beam
        S(-7.2f, 6.4f, 15.5f, 22.8f, 10.2f, 19.3f),       // cloth beam
        S(-9.2f, 11.8f, 13.65f, 25.2f, 31.8f, 14.6f),     // heddles + top frame
        S(-4.9f, 14.4f, 15.1f, 20.9f, 18.4f, 17.45f),     // reed
        S(4.18f, 0.8f, 6.1f, 11.82f, 3.63f, 21.64f),      // treadles
    }.Concat(Sides()).ToArray();

    /// <summary>Collision boxes (block units, controller-relative) for a facing.</summary>
    public static Cuboidf[] CollisionBoxes(float rotateYDeg)
        => CollisionPx.Select(c => Turn(new Cuboidf(c.x1, c.y1, c.z1, c.x2, c.y2, c.z2), rotateYDeg)).ToArray();

    #endregion

    /// <summary>
    /// Selection boxes (block units, relative to the controller block) and what each one is. <paramref name="rotateYDeg"/>
    /// is the geometry rotation (<see cref="GeometryRotateY"/>); <paramref name="rows"/> = the cloth's rows (= width, square; hang path scale). Order: fell slot (cells, or the last row when complete),
    /// woven rows on the warp, hanging rows (unrolled), warp threads (back), reed, shuttles, (draft) treadles, fixed parts (incl. unroll targets, seat).
    /// </summary>
    public static (Cuboidf[] boxes, LoomBoxPart[] parts) Build(int wovenRows, float rotateYDeg, bool unrolled = false,
        int width = WeavingState.DefaultWidth, int rows = WeavingState.DefaultRows, bool draft = false)
    {
        var boxes = new List<Cuboidf>();
        var parts = new List<LoomBoxPart>();
        void Add(Cuboidf box, LoomPart part, int index) { boxes.Add(box); parts.Add(new LoomBoxPart(part, index)); }

        float w = CellWidthFor(width);
        int[] order = DisplayOrder(wovenRows, rows);
        bool complete = wovenRows >= rows;

        Strip fell = FellStrip(width);
        int plane = PlaneSlotsFor(width);
        if (!complete)
        {
            for (int c = 0; c < width; c++) Add(WarpBox(fell, ClothX0 + c * w, ClothX0 + (c + 1) * w), LoomPart.Cell, c);
        }
        else Add(WarpBox(fell, ClothX0, ClothX1), LoomPart.Cloth, order[0]);

        for (int i = 1; i < plane && i < order.Length; i++) Add(WarpBox(PlaneStrip(i, width), ClothX0, ClothX1), LoomPart.Cloth, order[i]);
        if (unrolled)
        {
            // Rows beyond the warp slots, along the hang path (one box per piece), so any row can be copied.
            int hangEnd = Math.Min(order.Length, plane + MaxHangRowsFor(rows, width));
            for (int k = plane; k < hangEnd; k++)
                foreach (var piece in HangPieces(k - plane, rows, width)) Add(HangBox(piece), LoomPart.HangRow, order[k]);
        }

        // Warp threads at the back (lane/warp-shuttles-import): one thread roll per warp end on the warp beam.
        for (int x = 0; x < width; x++) Add(WarpThreadBox(x, width), LoomPart.WarpThread, x);
        var bar = BeaterBar();
        Add(new Cuboidf(BeaterX0, bar.y0, bar.z0, BeaterX1, bar.y1, bar.z1), LoomPart.Beater, 2);
        for (int i = 0; i < ShuttleCount; i++) Add(ShuttleBox(i), LoomPart.Shuttle, i);
        // Draft mode: the tie-up board's treadle columns (in front of the heddle top frame's box).
        if (draft)
            for (int t = 0; t < BoardTreadles; t++) Add(TreadleBox(t), LoomPart.Treadle, t);

        foreach (var f in FixedBoxes) Add(new Cuboidf(f.x1, f.y1, f.z1, f.x2, f.y2, f.z2), f.part, f.index);
        // Unroll target on the cloth over the breast beam front; unrolled, the hanging rows lie there instead (sneak = roll up too).
        if (!unrolled)
        {
            var bf = BreastFrontUnrollBox;
            Add(new Cuboidf(bf.x1, bf.y1, bf.z1, bf.x2, bf.y2, bf.z2), LoomPart.ClothBeam, 0);
        }

        return (boxes.Select(b => Turn(b, rotateYDeg)).ToArray(), parts.ToArray());
    }

    /// <summary>Px box → block units, turned around the controller block centre.</summary>
    static Cuboidf Turn(Cuboidf b, float rotateYDeg)
    {
        var unit = new Cuboidf(b.X1 / 16f, b.Y1 / 16f, b.Z1 / 16f, b.X2 / 16f, b.Y2 / 16f, b.Z2 / 16f);
        return rotateYDeg == 0 ? unit : unit.RotatedCopy(0, rotateYDeg, 0, new Vec3d(0.5, 0.5, 0.5));
    }

    /// <summary>Thin level plate of a warp row at its far (lower, larger z) edge height (see <see cref="PlaneStrip"/>).</summary>
    static Cuboidf WarpBox(Strip s, float x1, float x2)
    {
        const float half = 0.004f; // thin: a ray grazing the next row must pass above its plate
        float y = s.Z1 >= s.Z0 ? s.Y1 : s.Y0;
        return new Cuboidf(x1, y - half, Math.Min(s.Z0, s.Z1), x2, y + half, Math.Max(s.Z0, s.Z1));
    }
}
