using System;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>State of a preset icon button (frame colour, dimming).</summary>
public enum PresetIconState { Normal, Hover, Selected, Disabled }

/// <summary>
/// Pixel icons of the draft presets (lane/draft-polish, owner: "small images instead of names"). Each icon draws the preset's
/// own grid the way the draft window draws it: threading = shaft per warp end (shaft 4 on top, 8 ends), tie-up = shafts per
/// treadle (shaft 4 on top), treadling = treadle per pick (row 1 on top, 8 picks). Pure (selftest DraftPolishChecks:
/// every icon's pixels equal its preset); the window uploads <see cref="Render"/> as an RGBA texture.
/// </summary>
public static class PresetIcons
{
    /// <summary>Warp ends shown by a threading icon, picks shown by a treadling icon.</summary>
    public const int Ends = 8, Picks = 8;

    // RGBA ints as VS packs them (r | g << 8 | b << 16 | a << 24), opaque.
    public static readonly int Background = Rgba(0x14, 0x17, 0x1a), On = Rgba(0xe7, 0xe6, 0xe1), Off = Rgba(0x3a, 0x3f, 0x45);
    public static readonly int FrameNormal = Rgba(0x55, 0x5c, 0x63), FrameHover = Rgba(0x8f, 0xa8, 0xd8), FrameSelected = Rgba(0xc9, 0xa2, 0x27);

    public static int Rgba(int r, int g, int b, int a = 255) => r | g << 8 | b << 16 | a << 24;

    /// <summary>Threading grid [row, end]: row 0 = shaft 4 (top) .. row 3 = shaft 1; on where the end runs through that shaft.</summary>
    public static bool[,] Threading(int[] repeat)
    {
        var m = new bool[DraftPresets.Shafts, Ends];
        if (repeat.Length == 0) return m;
        for (int x = 0; x < Ends; x++)
        {
            int s = repeat[x % repeat.Length];
            if (s >= 0 && s < DraftPresets.Shafts) m[DraftPresets.Shafts - 1 - s, x] = true;
        }
        return m;
    }

    /// <summary>Tie-up grid [row, treadle]: row 0 = shaft 4 (top); on where the treadle lifts that shaft.</summary>
    public static bool[,] TieUp(int[] lifts)
    {
        var m = new bool[DraftPresets.Shafts, DraftPresets.Treadles];
        for (int t = 0; t < DraftPresets.Treadles && t < lifts.Length; t++)
            for (int s = 0; s < DraftPresets.Shafts; s++)
                m[DraftPresets.Shafts - 1 - s, t] = ((lifts[t] >> s) & 1) != 0;
        return m;
    }

    /// <summary>Treadling grid [pick, treadle]: pick 0 on top; on at the treadle the sequence presses (repeated over <see cref="Picks"/>).</summary>
    public static bool[,] Treadling(int[] sequence)
    {
        var m = new bool[Picks, DraftPresets.Treadles];
        if (sequence.Length == 0) return m;
        for (int y = 0; y < Picks; y++)
        {
            int t = sequence[y % sequence.Length];
            if (t >= 0 && t < DraftPresets.Treadles) m[y, t] = true;
        }
        return m;
    }

    /// <summary>
    /// Cell layout of a <paramref name="rows"/> x <paramref name="cols"/> grid in a square icon of <paramref name="size"/> px:
    /// square cells (pitch = the largest that fits inside the 2 px frame), the grid centred, 1 px gap between cells
    /// when the pitch is at least 3 px. Returns the cell's top-left and its filled size.
    /// </summary>
    public static (int x, int y, int fill) Cell(int rows, int cols, int size, int r, int c)
    {
        int inner = size - 2 * Inset;
        int pitch = Math.Max(1, inner / Math.Max(rows, cols));
        int ox = Inset + (inner - pitch * cols) / 2, oy = Inset + (inner - pitch * rows) / 2;
        int fill = pitch >= 3 ? pitch - 1 : pitch;
        return (ox + c * pitch, oy + r * pitch, fill);
    }

    /// <summary>Frame width and inset of the cell area (frame + margin), px.</summary>
    public const int Frame = 2, Inset = 2;

    /// <summary>
    /// The icon as <paramref name="size"/> x <paramref name="size"/> RGBA ints (row-major): background, frame in the state's
    /// colour, then the grid (<see cref="On"/> / <see cref="Off"/> cells). Disabled: everything at a third of the brightness.
    /// </summary>
    public static int[] Render(bool[,] mask, int size, PresetIconState state = PresetIconState.Normal)
    {
        size = Math.Max(2 * Inset + 1, size);
        var px = new int[size * size];
        Array.Fill(px, Background);
        int frame = state switch { PresetIconState.Hover => FrameHover, PresetIconState.Selected => FrameSelected, _ => FrameNormal };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (x < Frame || y < Frame || x >= size - Frame || y >= size - Frame) px[y * size + x] = frame;
        int rows = mask.GetLength(0), cols = mask.GetLength(1);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var (cx, cy, fill) = Cell(rows, cols, size, r, c);
                int colour = mask[r, c] ? On : Off;
                for (int y = cy; y < cy + fill && y < size; y++)
                    for (int x = cx; x < cx + fill && x < size; x++) px[y * size + x] = colour;
            }
        if (state == PresetIconState.Disabled)
            for (int i = 0; i < px.Length; i++) px[i] = Dim(px[i]);
        return px;
    }

    /// <summary>A third of the brightness, opaque (a disabled icon: the threading while a cloth is on the warp).</summary>
    public static int Dim(int rgba) => Rgba((rgba & 0xff) / 3, ((rgba >> 8) & 0xff) / 3, ((rgba >> 16) & 0xff) / 3);

    /// <summary>Read the grid back from rendered pixels (centre pixel of each cell: <see cref="On"/> → true). Selftest helper.</summary>
    public static bool[,] ReadBack(int[] px, int size, int rows, int cols, PresetIconState state = PresetIconState.Normal)
    {
        var m = new bool[rows, cols];
        int on = state == PresetIconState.Disabled ? Dim(On) : On;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var (cx, cy, fill) = Cell(rows, cols, size, r, c);
                m[r, c] = px[(cy + fill / 2) * size + cx + fill / 2] == on;
            }
        return m;
    }
}
