using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// The cloth on the loom: woven rows (read-only), the row being edited, and the cloth palette.
/// Cells hold indices into the cloth palette (one entry per twine code, colour frozen at first use),
/// so a twine whose colour attribute changes never recolours woven rows. Pure data: no game API calls.
/// Twine cost: <see cref="ClothCost"/>, charged at the cut.
/// <para>
/// Size (lane/square-cloth, owner): a cloth is square, 16 x 16 or 32 x 32 (<see cref="AllowedSizes"/>; 32 = the world's
/// "double precision" setting). The warp sets the <see cref="Width"/>; <see cref="Rows"/> = <see cref="Width"/>. The
/// cloth is cut when complete (<see cref="CanCut"/> = <see cref="Complete"/>: every row woven). Tree <c>width</c>/<c>height</c>,
/// 16 x 16 when missing; an old long cloth (height != width, lane/cloth-formats) loads as its first square of rows.
/// </para>
/// </summary>
public class WeavingState
{
    /// <summary>Default cloth size, and the size decor and tailoring work in (other sizes are resampled for them).</summary>
    public const int DefaultWidth = 16;
    public const int DefaultRows = 16;
    /// <summary>
    /// Square cloth sizes (lane/square-cloth): 16 (standard) or 32 (double precision; the cloth spans the loom's fixed
    /// width, so 32 cells are 0.76 px each, the narrowest still clickable).
    /// </summary>
    public static readonly int[] AllowedSizes = { 16, 32 };
    /// <summary>Most rows a cloth has (a 32-wide large carpet, 32 x 64; lane/linen-output): data cap of the draft row arrays.</summary>
    public const int MaxRows = 64;
    public const byte Empty = 255;
    public const int MaxPalette = 254;

    public int Width { get; private set; } = DefaultWidth;
    /// <summary>What the loom weaves (lane/linen-output): cloth, small carpet (both N x N) or large carpet (N x 2N).</summary>
    public ClothProduct Product { get; private set; } = ClothProduct.Cloth;
    /// <summary>Rows of the cloth: <see cref="Width"/> (square), twice that for a large carpet.</summary>
    public int Rows => Width * ClothProducts.LengthFactor(Product);
    /// <summary>Woven cells, row-major, <see cref="Width"/> x <see cref="Rows"/>; rows beyond <see cref="WovenRows"/> are 0.</summary>
    public byte[] Woven = new byte[DefaultWidth * DefaultRows];
    public byte[] Current = NewEmptyRow(DefaultWidth);
    public int WovenRows;
    public List<string> PaletteCodes = new();
    public List<string> PaletteColors = new();
    /// <summary>
    /// Shaft-weaving draft (lane/draft-mode, docs/weaving.md "Draft mode"): in <see cref="LoomMode.Draft"/> the current
    /// row is computed from the warp, threading, tie-up, the pressed treadle and the weft; cells are not painted
    /// (except the warp, on the fell row, before the first beat). Woven rows stay cells, so copy, roll back, cut,
    /// cost, decor and tailoring work as in tapestry mode.
    /// </summary>
    public readonly DraftState Draft = new();

    public WeavingState() { }

    public WeavingState(int size) => Reset(size);

    /// <summary>Every row of the square woven: only cutting is left.</summary>
    public bool Complete => WovenRows >= Rows;
    /// <summary>The cloth is cut when complete (lane/square-cloth; no free length any more).</summary>
    public bool CanCut => Complete;
    public bool CurrentRowFilled => Current.All(c => c != Empty);
    /// <summary>Cells of the cloth as it would be cut now.</summary>
    public int TotalCells => Width * WovenRows;

    public static bool IsAllowedSize(int n) => System.Array.IndexOf(AllowedSizes, n) >= 0;

    public static byte[] NewEmptyRow(int width) => Enumerable.Repeat(Empty, width).ToArray();
    public byte[] NewEmptyRow() => NewEmptyRow(Width);

    /// <summary>
    /// Weave <paramref name="product"/> from now on (lane/linen-output): only on an empty loom (no woven row); the product
    /// stays for the next cloths. False when rows are on the loom.
    /// </summary>
    public bool SetProduct(ClothProduct product)
    {
        if (WovenRows > 0) return false;
        Product = product;
        Woven = new byte[Width * Rows];
        return true;
    }

    /// <summary>Start a new, empty cloth (same size).</summary>
    public void Reset() => Reset(Width);

    /// <summary>Start a new, empty square cloth of <paramref name="size"/> x <paramref name="size"/> (unsupported sizes: 16 x 16).</summary>
    public void Reset(int size)
    {
        Width = IsAllowedSize(size) ? size : DefaultWidth;
        Woven = new byte[Width * Rows];
        Current = NewEmptyRow();
        WovenRows = 0;
        PaletteCodes.Clear();
        PaletteColors.Clear();
        // Draft: mode and presets are loom settings and stay; the warp (palette indices) and the treadling go.
        Draft.Warp = NewEmptyRow();
        Draft.ClearRows();
    }

    /// <summary>Palette index for a twine code, adding it with the given colour on first use; -1 if full.</summary>
    public int PaletteIndexFor(string code, string hex)
    {
        int idx = PaletteCodes.IndexOf(code);
        if (idx >= 0) return idx;
        if (PaletteCodes.Count >= MaxPalette) return -1;
        PaletteCodes.Add(code);
        PaletteColors.Add(hex);
        return PaletteCodes.Count - 1;
    }

    public string ColorOf(byte cell) => cell < PaletteColors.Count ? PaletteColors[cell] : TwineColors.PlainHex;

    /// <summary>Paint one cell (or the whole row) of the current row; false if the cloth is complete or the cell is out of range.</summary>
    public bool Paint(int cell, byte paletteIdx, bool fill)
    {
        if (Complete) return false;
        if (fill) { System.Array.Fill(Current, paletteIdx); return true; }
        if (cell < 0 || cell >= Width) return false;
        Current[cell] = paletteIdx;
        return true;
    }

    /// <summary>
    /// Cells a brush of <paramref name="size"/> paints when <paramref name="cell"/> is clicked: centred on it, an even
    /// size reaching one more cell toward higher columns (2 = cell, cell+1; 4 = cell-1..cell+2; 8 = cell-3..cell+4),
    /// clipped to the row. Returns [start, end).
    /// </summary>
    public static (int start, int end) BrushSpan(int cell, int size, int width = DefaultWidth)
    {
        size = System.Math.Max(1, size);
        int start = cell - (size - 1) / 2;
        return (System.Math.Max(0, start), System.Math.Min(width, start + size));
    }

    /// <summary>Paint the brush span around <paramref name="cell"/>; false if the loom is full or the cell is out of range.</summary>
    public bool PaintBrush(int cell, int size, byte paletteIdx)
    {
        if (Complete || cell < 0 || cell >= Width) return false;
        var (start, end) = BrushSpan(cell, size, Width);
        for (int c = start; c < end; c++) Current[c] = paletteIdx;
        return true;
    }

    /// <summary>Cells per twine code in the current row (empty cells skipped).</summary>
    public Dictionary<string, int> CurrentRowCellsByCode() => CellsByCode(Current);

    /// <summary>Cells per twine code of a row (empty cells skipped).</summary>
    public Dictionary<string, int> CellsByCode(IEnumerable<byte> row)
        => row.Where(c => c != Empty && c < PaletteCodes.Count)
            .GroupBy(c => PaletteCodes[c])
            .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>The woven cells so far (<see cref="WovenRows"/> rows).</summary>
    public IEnumerable<byte> WovenCells => Woven.Take(WovenRows * Width);

    /// <summary>Copy of woven row <paramref name="row"/> (0 = first woven).</summary>
    public byte[] WovenRow(int row) => Woven.Skip(row * Width).Take(Width).ToArray();

    /// <summary>Replace the current row with a copy of woven row <paramref name="row"/>; false if the loom is full or the row is not woven yet.</summary>
    public bool CopyRow(int row)
    {
        if (Complete || row < 0 || row >= WovenRows) return false;
        if (DraftMode)
        {
            // The copied row's treadle and weft, woven with the current tie-up (the same cells while the tie-up is
            // unchanged: warp and threading are fixed). A tapestry row (no treadle) cannot be copied in draft mode.
            byte t = Draft.Treadles[row];
            if (t == DraftState.NoTreadle || !Draft.ShedOpens(t, Width)) return false;
            Draft.CurrentTreadle = t;
            Draft.CurrentWeft = Draft.Wefts[row];
            Draft.SyncStep(t);
            RecomputeCurrent();
            return true;
        }
        System.Array.Copy(Woven, row * Width, Current, 0, Width);
        return true;
    }

    /// <summary>
    /// Undo the last woven row: it becomes the current row again (editable) and its cells per code are
    /// returned (informational: twine is only charged at the cut). The previous current row is discarded. False if nothing is woven.
    /// </summary>
    public bool Unweave(out Dictionary<string, int> refund)
    {
        refund = new Dictionary<string, int>();
        if (WovenRows <= 0) return false;
        WovenRows--;
        Current = WovenRow(WovenRows);
        System.Array.Clear(Woven, WovenRows * Width, Width);
        refund = CellsByCode(Current);
        byte t = Draft.Treadles[WovenRows], weft = Draft.Wefts[WovenRows];
        Draft.Treadles[WovenRows] = DraftState.NoTreadle;
        Draft.Wefts[WovenRows] = DraftState.NoTreadle;
        Draft.RowLifts[WovenRows] = 0;
        if (DraftMode)
        {
            // The rolled-back row's shed opens again with its weft (lane/draft-mode), under the current tie-up; a
            // tapestry row rolled back in draft mode waits for a treadle (lane/playtest-fixes-5).
            bool opens = t != DraftState.NoTreadle && Draft.ShedOpens(t, Width);
            Draft.CurrentTreadle = opens ? t : DraftState.NoTreadle;
            Draft.CurrentWeft = opens ? weft : Draft.CurrentWeft;
            if (Draft.Sequence.Length > 0) Draft.Step = System.Math.Max(0, Draft.Step - 1);
            if (opens) Draft.SyncStep(t);
            RecomputeCurrent();
        }
        return true;
    }

    /// <summary>
    /// Commit the current row (no-op when the loom is full). The next row starts as a copy of it (lane/loom-feedback-4,
    /// user: the client hides it only while the beat animation runs, so the progress shows); clicking a woven row still copies that row.
    /// </summary>
    public void CommitCurrentRow()
    {
        if (Complete) return;
        System.Array.Copy(Current, 0, Woven, WovenRows * Width, Width);
        // Rows on the loom lock width, threading and warp colours (WarpLocked); the cut frees them again (lane/loom-feedback-8).
        bool draftRow = DraftMode && Draft.CurrentTreadle != DraftState.NoTreadle;
        Draft.Treadles[WovenRows] = draftRow ? Draft.CurrentTreadle : DraftState.NoTreadle;
        Draft.Wefts[WovenRows] = draftRow ? Draft.CurrentWeft : DraftState.NoTreadle;
        Draft.RowLifts[WovenRows] = draftRow ? (byte)Draft.LiftMask(Draft.CurrentTreadle) : (byte)0;
        WovenRows++;
        if (DraftMode)
        {
            // Draft (lane/draft-mode): the beat closes the shed; the loom then opens the default one (OpenDefaultShed, lane/loom-feedback-8; the weft is kept: the
            // next treadle takes the active shuttle). Auto treadling (lane/playtest-fixes-5): the
            // preset's next treadle is pressed with the same weft.
            Draft.CurrentTreadle = DraftState.NoTreadle;
            Current = NewEmptyRow();
            if (draftRow && Draft.Sequence.Length > 0)
            {
                Draft.Step = (Draft.Step + 1) % Draft.Sequence.Length;
                AutoPress();
            }
            return;
        }
        Current = (byte[])Current.Clone();
    }

    #region Draft (lane/draft-mode)

    public bool DraftMode => Draft.Enabled;

    /// <summary>Palette index of plain flax twine (unpainted warp), added on first use; -1 if the palette is full.</summary>
    public int PlainIndex() => PaletteIndexFor(DraftState.PlainCode, TwineColors.PlainHex);

    /// <summary>Draft mode: every warp end has a twine (plain flax where none was painted or the save was odd).</summary>
    public void EnsureWarp()
    {
        if (!DraftMode) return;
        if (Draft.Warp.Length != Width) Draft.Warp = NewEmptyRow();
        for (int x = 0; x < Width; x++)
        {
            if (Draft.Warp[x] != Empty && Draft.Warp[x] < PaletteCodes.Count) continue;
            int p = PlainIndex();
            if (p >= 0) Draft.Warp[x] = (byte)p;
        }
    }

    /// <summary>
    /// The warp is woven into: rows on the loom (lane/loom-feedback-8, owner: no re-warp step; before, a "warp set" flag
    /// kept it locked across cuts until a re-warp). Locks the size, threading and warp colours; the cut frees them. The
    /// warp threads keep their colours for the next cloth (<see cref="StartNextCloth"/>).
    /// </summary>
    public bool WarpLocked => WovenRows > 0;

    /// <summary>A cloth of <paramref name="size"/> can be started: no row woven.</summary>
    public bool CanTakeSize(int size) => WovenRows == 0;

    public enum DraftResult { Ok, Invalid, ThreadingLocked }

    /// <summary>
    /// Loom settings from the F picker (lane/playtest-fixes-5: between any two rows). Mode, tie-up and treadling change
    /// anytime; the threading only while the warp is free (<see cref="WarpLocked"/>). Draft: the warp gets a twine per end;
    /// switching from tapestry drops the painted current row (rows then come from the treadles). Tapestry: the current
    /// row stays (now paintable). A pressed treadle stays pressed under a new tie-up when it still opens a shed.
    /// <para>
    /// Imported draft (lane/warp-shuttles-import): picking a threading (<paramref name="pickThreading"/>, or another index)
    /// or a tie-up preset (<paramref name="pickTieUp"/>, or another index) replaces the draft's own threading / tie-up and
    /// drops the plan (<see cref="DraftState.Plan"/>).
    /// </para>
    /// </summary>
    public DraftResult SetDraft(LoomMode mode, int threading, int tieUp, int treadling, bool pickThreading = false, bool pickTieUp = false)
    {
        if (mode is not (LoomMode.Tapestry or LoomMode.Draft) || !DraftPresets.ValidThreading(threading) || !DraftPresets.ValidTieUp(tieUp)
            || !DraftPresets.ValidTreadling(treadling)) return DraftResult.Invalid;
        bool newThreading = threading != Draft.Threading || (pickThreading && Draft.CustomThreading != null);
        bool newTieUp = tieUp != Draft.TieUp || (pickTieUp && Draft.CustomTieUp != null);
        if (newThreading && WarpLocked) return DraftResult.ThreadingLocked;
        bool wasDraft = DraftMode, newTreadling = treadling != Draft.Treadling;
        if (newThreading) { Draft.CustomThreading = null; Draft.Plan = null; }
        if (newTieUp) { Draft.CustomTieUp = null; Draft.Plan = null; }
        Draft.Mode = mode;
        Draft.Threading = threading;
        Draft.TieUp = tieUp;
        Draft.Treadling = treadling;
        if (newTreadling) Draft.Step = 0;
        if (!DraftMode)
        {
            Draft.CurrentTreadle = DraftState.NoTreadle;
            return DraftResult.Ok;
        }
        EnsureWarp();
        if (!wasDraft) Draft.CurrentTreadle = DraftState.NoTreadle;
        if (Draft.CurrentTreadle != DraftState.NoTreadle && !Draft.ShedOpens(Draft.CurrentTreadle, Width)) Draft.CurrentTreadle = DraftState.NoTreadle;
        if (newTreadling && Draft.CurrentTreadle != DraftState.NoTreadle) Draft.SyncStep(Draft.CurrentTreadle); // the sequence goes on from it
        else if (newTreadling) AutoPress();
        RecomputeCurrent();
        return DraftResult.Ok;
    }

    /// <summary>
    /// Artisan loom (lane/loom-roles: draft weaving only): a state in tapestry mode (old save) switches to draft. Woven rows
    /// stay as they are (painted rows make a mixed cloth: cut as usual, no draft attribute); the painted current row goes,
    /// the next row waits for a treadle. True when the mode changed.
    /// </summary>
    public bool ForceDraftMode()
    {
        if (DraftMode) return false;
        if (SetDraft(LoomMode.Draft, Draft.Threading, Draft.TieUp, Draft.Treadling) != DraftResult.Ok)
        {
            Draft.Mode = LoomMode.Draft;
            Draft.CurrentTreadle = DraftState.NoTreadle;
            EnsureWarp();
            RecomputeCurrent();
        }
        return true;
    }

    /// <summary>Mode and presets, treadling unchanged (see <see cref="SetDraft"/>); false if refused.</summary>
    public bool SetMode(LoomMode mode, int threading, int tieUp) => SetDraft(mode, threading, tieUp, Draft.Treadling) == DraftResult.Ok;

    /// <summary>Auto treadling: press the sequence's treadle with the last weft (nothing when manual, no weft yet, or no shed).</summary>
    void AutoPress()
    {
        byte t = Draft.SequenceTreadle;
        if (t == DraftState.NoTreadle || Draft.CurrentWeft == Empty || Draft.CurrentWeft >= PaletteCodes.Count) return;
        if (Complete || !Draft.ShedOpens(t, Width)) return;
        Draft.CurrentTreadle = t;
        RecomputeCurrent();
    }

    /// <summary>Draft: the current row from the open shed (empty while no treadle is pressed).</summary>
    public void RecomputeCurrent()
    {
        if (!DraftMode) return;
        Current = Draft.CurrentTreadle == DraftState.NoTreadle ? NewEmptyRow() : Draft.Row(Width, Draft.CurrentTreadle, Draft.CurrentWeft);
    }

    public enum TreadleResult { Ok, NotDraft, Complete, NoShed }

    /// <summary>
    /// Treadle the row at the fell starts with (lane/loom-feedback-8 item 6, owner: the loom asked for a treadle before the
    /// first beat): the plan's treadle for this row, else the treadling preset's, else treadle 1; the first of those that
    /// opens a shed (else any treadle that does). -1 outside draft mode, when complete, or when no treadle opens a shed.
    /// </summary>
    public int DefaultTreadle
    {
        get
        {
            if (!DraftMode || Complete) return -1;
            var order = new List<int>();
            if (PlannedTreadle >= 0) order.Add(PlannedTreadle);
            if (Draft.SequenceTreadle != DraftState.NoTreadle) order.Add(Draft.SequenceTreadle);
            order.AddRange(Enumerable.Range(0, DraftPresets.Treadles));
            foreach (int t in order) if (Draft.ShedOpens(t, Width)) return t;
            return -1;
        }
    }

    /// <summary>
    /// Draft, shed closed (lane/loom-feedback-8 item 6): press <see cref="DefaultTreadle"/> with weft palette index
    /// <paramref name="weft"/>, so the row can be beaten at once. The loom calls it after every change (packet, load, import)
    /// with the resolved weft (<see cref="ResolveWeft"/>). False when a treadle is already pressed, without a weft, or no shed opens.
    /// </summary>
    public bool OpenDefaultShed(byte weft)
    {
        if (!DraftMode || Complete || Draft.CurrentTreadle != DraftState.NoTreadle || weft >= PaletteCodes.Count) return false;
        int t = DefaultTreadle;
        return t >= 0 && PressTreadle(t, weft) == TreadleResult.Ok;
    }

    /// <summary>Draft: press treadle <paramref name="t"/> with weft palette index <paramref name="weft"/>: the current row becomes that pick.</summary>
    public TreadleResult PressTreadle(int t, byte weft)
    {
        if (!DraftMode) return TreadleResult.NotDraft;
        if (Complete) return TreadleResult.Complete;
        if (!Draft.ShedOpens(t, Width)) return TreadleResult.NoShed;
        Draft.CurrentTreadle = (byte)t;
        Draft.CurrentWeft = weft;
        Draft.SyncStep(t);
        RecomputeCurrent();
        return TreadleResult.Ok;
    }

    /// <summary>
    /// Draft, open shed (treadle pressed, not beaten; lane/shuttle-fix, owner): the weft becomes
    /// <paramref name="weft"/> and the row is drawn down again at once. False (nothing changes) without an open shed, or
    /// when the weft is already that one.
    /// </summary>
    public bool ChangeWeft(byte weft)
    {
        if (!DraftMode || Complete || Draft.CurrentTreadle == DraftState.NoTreadle || weft >= PaletteCodes.Count) return false;
        if (Draft.CurrentWeft == weft) return false;
        Draft.CurrentWeft = weft;
        RecomputeCurrent();
        return true;
    }

    /// <summary>Draft, before the first beat: paint the warp (brush span around <paramref name="cell"/>, or every end); false if refused.</summary>
    public bool PaintWarp(int cell, int size, byte paletteIdx, bool fill)
    {
        if (!DraftMode || WarpLocked || Complete) return false;
        if (fill) System.Array.Fill(Draft.Warp, paletteIdx);
        else
        {
            if (cell < 0 || cell >= Width) return false;
            var (start, end) = BrushSpan(cell, size, Width);
            for (int c = start; c < end; c++) Draft.Warp[c] = paletteIdx;
        }
        RecomputeCurrent();
        return true;
    }

    /// <summary>
    /// Warp ends a click on warp thread <paramref name="end"/> at the back loads (lane/warp-shuttles-import): the brush
    /// span of <paramref name="size"/> around it, or with <paramref name="all"/> (sneak) every end. Null when the click
    /// is out of range. <paramref name="code"/> = the twine being loaded.
    /// </summary>
    public List<int>? WarpTargets(int end, int size, string? code, bool all)
    {
        // With an imported draft, sneak loads the ends the draft wants in this twine (none for a twine it does not use).
        if (all && ActivePlan is { } plan) return Enumerable.Range(0, Width).Where(x => plan.Warp[x] == code).ToList();
        if (all) return Enumerable.Range(0, Width).ToList();
        if (end < 0 || end >= Width) return null;
        var (start, stop) = BrushSpan(end, size, Width);
        return Enumerable.Range(start, stop - start).ToList();
    }

    /// <summary>
    /// A twine click on warp thread <paramref name="end"/> stocks the twine instead of loading it (lane/loom-feedback-8 item 4):
    /// the thread already holds <paramref name="code"/> and loading would change nothing (the warp is locked by rows, or every
    /// end the click reaches already holds it). Then the twine goes onto the warp beam as stock (<see cref="TwineStock"/>).
    /// </summary>
    public bool WarpClickStocks(int end, int size, string code, bool all)
    {
        if (!DraftMode || end < 0 || end >= Width || WarpCode(end) != code) return false;
        if (WarpLocked) return true;
        var ends = WarpTargets(end, size, code, all);
        return ends == null || ends.All(x => WarpCode(x) == code);
    }

    /// <summary>
    /// Draft, warp free: load twine palette index <paramref name="paletteIdx"/> onto the warp threads a click on
    /// <paramref name="end"/> reaches (<see cref="WarpTargets"/>). Nothing is consumed (the cut charges the cloth).
    /// Returns the ends loaded, -1 if refused (tapestry, warp set, complete, out of range).
    /// </summary>
    public int LoadWarpThreads(int end, int size, byte paletteIdx, bool all)
    {
        if (!DraftMode || WarpLocked || Complete || paletteIdx >= PaletteCodes.Count) return -1;
        if (Draft.Warp.Length != Width) EnsureWarp();
        var ends = WarpTargets(end, size, PaletteCodes[paletteIdx], all);
        if (ends == null) return -1;
        foreach (int x in ends) Draft.Warp[x] = paletteIdx;
        SyncPlanWarp(ends);
        if (ends.Count > 0) RecomputeCurrent();
        return ends.Count;
    }

    /// <summary>
    /// Draft window (lane/linen-output, warp sync): load twine <paramref name="paletteIdx"/> onto ends
    /// <paramref name="from"/>..<paramref name="to"/> (inclusive, either order, clipped). Same rules as at the back
    /// (draft mode, free warp, not complete); the plan's warp follows. Returns the ends loaded, -1 if refused.
    /// </summary>
    public int LoadWarpEnds(int from, int to, byte paletteIdx)
    {
        if (!DraftMode || WarpLocked || Complete || paletteIdx >= PaletteCodes.Count) return -1;
        if (Draft.Warp.Length != Width) EnsureWarp();
        int lo = System.Math.Max(0, System.Math.Min(from, to)), hi = System.Math.Min(Width - 1, System.Math.Max(from, to));
        if (lo > hi) return -1;
        var ends = Enumerable.Range(lo, hi - lo + 1).ToList();
        foreach (int x in ends) Draft.Warp[x] = paletteIdx;
        SyncPlanWarp(ends);
        RecomputeCurrent();
        return ends.Count;
    }

    /// <summary>
    /// Plan and physical warp are one state (owner 2026-10-20): loading a thread (back or draft window) makes the plan's
    /// warp colour for that end the loaded twine; the plan's designer code is re-encoded. Required colours of an imported
    /// draft stay only on ends not loaded since.
    /// </summary>
    void SyncPlanWarp(IEnumerable<int> ends)
    {
        if (ActivePlan is not { } plan) return;
        bool changed = false;
        foreach (int x in ends)
        {
            if (x < 0 || x >= plan.Warp.Length) continue;
            string code = WarpCode(x);
            if (plan.Warp[x] == code) continue;
            plan.Warp[x] = code;
            changed = true;
        }
        if (changed && plan.ToCodeData() is { } data) plan.Code = DraftCode.Encode(data);
    }

    /// <summary>
    /// Draft, warp free (lane/shuttle-fix): put the warp threads a click on <paramref name="end"/> reaches back to plain
    /// flax (<paramref name="all"/>: every thread). Returns the ends cleared (already plain ones count), -1 if refused.
    /// </summary>
    public int ClearWarpThreads(int end, int size, bool all)
    {
        if (!DraftMode || WarpLocked || Complete || end < 0 || end >= Width) return -1;
        int plain = PlainIndex();
        if (plain < 0) return -1;
        if (Draft.Warp.Length != Width) EnsureWarp();
        var (s0, s1) = all ? (0, Width) : BrushSpan(end, size, Width);
        var ends = Enumerable.Range(s0, s1 - s0).ToList();
        foreach (int x in ends) Draft.Warp[x] = (byte)plain;
        SyncPlanWarp(ends);
        if (ends.Count > 0) RecomputeCurrent();
        return ends.Count;
    }

    /// <summary>
    /// The row the fell shows: the current row; in draft mode while the warp is free (<see cref="WarpLocked"/>) and no
    /// treadle is pressed, the warp (as loaded at the back).
    /// </summary>
    public byte[] FellRow => DraftMode && !WarpLocked && Draft.CurrentTreadle == DraftState.NoTreadle ? Draft.Warp : Current;

    /// <summary>
    /// The draft of the cloth as cut now (twine codes, not palette indices), or null unless every woven row came from a
    /// treadle (a tapestry cloth, or one with tapestry rows, has no draft). Lift plan per row; tie-up code
    /// <see cref="DraftExport.MixedTieUp"/> when rows used another tie-up than the current one.
    /// </summary>
    public DraftExport? ExportDraft()
    {
        if (WovenRows == 0 || Draft.Warp.Length != Width || Draft.Treadles.Take(WovenRows).Any(t => t == DraftState.NoTreadle)) return null;
        string CodeOf(byte p) => p < PaletteCodes.Count ? PaletteCodes[p] : DraftState.PlainCode;
        var lifts = Draft.RowLifts.Take(WovenRows).Select(l => (int)l).ToArray();
        bool oneTieUp = Enumerable.Range(0, WovenRows).All(y => lifts[y] == Draft.LiftMask(Draft.Treadles[y]));
        // Imported draft (lane/warp-shuttles-import): codes "custom"; the designer code travels when the cloth followed it.
        string threadingCode = Draft.CustomThreading != null ? CustomCode : DraftPresets.Threading(Draft.Threading).Code;
        string tieUpCode = !oneTieUp ? DraftExport.MixedTieUp : Draft.CustomTieUp != null ? CustomCode : DraftPresets.TieUp(Draft.TieUp).Code;
        return new DraftExport(
            threadingCode, tieUpCode,
            Draft.Warp.Take(Width).Select(CodeOf).ToArray(),
            Enumerable.Range(0, Width).Select(Draft.ShaftOf).ToArray(),
            (int[])Draft.TieUpLifts.Clone(),
            Draft.Treadles.Take(WovenRows).Select(t => (int)t).ToArray(),
            Draft.Wefts.Take(WovenRows).Select(CodeOf).ToArray(),
            lifts,
            PlanFollowed ? Draft.Plan!.Code : null);
    }

    /// <summary>Preset code of an imported threading / tie-up (lang <c>draft-threading-custom</c>, <c>draft-tieup-custom</c>).</summary>
    public const string CustomCode = "custom";

    #endregion

    #region Imported draft and guided weaving (lane/warp-shuttles-import)

    /// <summary>The imported draft, while the loom weaves it: draft mode and the draft's size (null otherwise).</summary>
    public DraftPlan? ActivePlan => DraftMode && Draft.Plan is { } p && p.Size == Width ? p : null;

    public enum ImportResult { Ok, Rows, Size }

    /// <summary>
    /// Put an imported draft on the loom: draft mode, its threading and tie-up, manual treadling, and the plan (required
    /// warp, treadle and weft per row). Refused with rows on the loom (<see cref="ImportResult.Rows"/>) or another size
    /// (<see cref="ImportResult.Size"/>; the loom takes the world size first). The warp is not loaded: the player loads
    /// each thread at the back (the loom shows what each needs).
    /// </summary>
    public ImportResult ApplyPlan(DraftPlan plan)
    {
        if (WovenRows > 0) return ImportResult.Rows;
        if (plan.Size != Width) return ImportResult.Size;
        Draft.Mode = LoomMode.Draft;
        Draft.CustomThreading = plan.Threading.Select(s => (byte)s).ToArray();
        Draft.CustomTieUp = (int[])plan.TieUp.Clone();
        Draft.Treadling = 0; // manual: the plan guides, the player presses
        Draft.Step = 0;
        Draft.Plan = plan;
        Draft.CurrentTreadle = DraftState.NoTreadle;
        EnsureWarp();
        RecomputeCurrent();
        return ImportResult.Ok;
    }

    /// <summary>Drop the plan (guidance); the imported threading and tie-up stay the loom's setup.</summary>
    public bool ClearPlan()
    {
        if (Draft.Plan == null) return false;
        Draft.Plan = null;
        return true;
    }

    /// <summary>Twine code loaded on warp end <paramref name="x"/> (plain flax when none is).</summary>
    public string WarpCode(int x)
        => x >= 0 && x < Draft.Warp.Length && Draft.Warp[x] < PaletteCodes.Count ? PaletteCodes[Draft.Warp[x]] : DraftState.PlainCode;

    /// <summary>Twine the imported draft needs on end <paramref name="x"/> when it differs from the loaded one, else null.</summary>
    public string? WarpNeeded(int x) => ActivePlan is { } p && x >= 0 && x < Width && p.Warp[x] != WarpCode(x) ? p.Warp[x] : null;

    /// <summary>Ends loaded as the imported draft needs (0 without a plan).</summary>
    public int WarpMatches => ActivePlan is { } p ? Enumerable.Range(0, Width).Count(x => p.Warp[x] == WarpCode(x)) : 0;

    /// <summary>Guided weaving: the plan's treadle for the next row (the row at the fell), or -1 (no plan, complete).</summary>
    public int PlannedTreadle => ActivePlan is { } p && !Complete ? p.TreadleAt(WovenRows) : -1;

    /// <summary>Guided weaving: the plan's weft twine for the next row, or null.</summary>
    public string? PlannedWeft => ActivePlan is { } p && !Complete ? p.WeftAt(WovenRows) : null;

    /// <summary>
    /// Every woven row followed the plan (treadle, weft, lifts) on the warp the plan needs: the cut cloth then carries
    /// the designer code (<see cref="DraftExport.SourceCode"/>).
    /// </summary>
    public bool PlanFollowed => ActivePlan is { } p && WovenRows > 0 && WarpMatches == Width
        && Enumerable.Range(0, WovenRows).All(y => Draft.Treadles[y] == p.TreadleAt(y) && Draft.Wefts[y] < PaletteCodes.Count
            && PaletteCodes[Draft.Wefts[y]] == p.WeftAt(y) && Draft.RowLifts[y] == p.TieUp[p.TreadleAt(y)]);

    /// <summary>
    /// Guided weaving: make the shuttle holding the plan's weft for the next row active (if one holds it). The player may
    /// pick another shuttle afterwards. True when the selection changed.
    /// </summary>
    public bool AutoSelectShuttle(SpoolRack shuttles)
    {
        int i = shuttles.IndexOf(PlannedWeft);
        if (i < 0 || i == shuttles.Selected) return false;
        return shuttles.Select(i);
    }

    /// <summary>
    /// Weft for the row at the fell with no twine in hand (lane/loom-feedback-8 item 5, owner: a weft colour added in the draft
    /// window vanished although a shuttle held it): the plan's weft for this row when a shuttle holds that twine code (that
    /// shuttle becomes active), else the active shuttle. Null when no shuttle holds twine. Plan and shuttles match by twine
    /// code (the plan's designer palette maps to the same <c>actailor:twine-*</c> / <c>game:flaxtwine</c> codes).
    /// </summary>
    public string? ResolveWeft(SpoolRack shuttles)
    {
        AutoSelectShuttle(shuttles);
        return shuttles.SelectedCode;
    }

    /// <summary>
    /// Twine was wound onto shuttle <paramref name="spool"/> (lane/loom-feedback-8 item 5): the plan's weft shuttle becomes
    /// active (before: only when no shuttle was active, so a later-loaded plan colour stayed unused); else the first filled
    /// shuttle is active.
    /// </summary>
    public void ShuttleLoaded(SpoolRack shuttles, int spool)
    {
        if (!AutoSelectShuttle(shuttles) && shuttles.Selected < 0) shuttles.Select(spool);
    }

    #endregion

    /// <summary>
    /// Start the next cloth of <paramref name="size"/> x <paramref name="size"/>, keeping the colours (lane/loom-feedback-4, user): its
    /// current row is the last woven row of this cloth (the current row when nothing is woven), so several cloths can
    /// be made in a row. Palette entries of that row carry over (code + frozen colour); another width resamples it (nearest cell).
    /// </summary>
    public void StartNextCloth(int size)
    {
        byte[] seed = WovenRows > 0 ? WovenRow(WovenRows - 1) : (byte[])Current.Clone();
        byte[] warp = (byte[])Draft.Warp.Clone();
        int oldWidth = Width;
        var codes = PaletteCodes.ToList();
        var colors = PaletteColors.ToList();
        Reset(size);
        byte Carry(byte s)
        {
            if (s == Empty || s >= codes.Count) return Empty;
            int idx = PaletteIndexFor(codes[s], colors[s]);
            return idx >= 0 ? (byte)idx : Empty;
        }
        // Draft (lane/draft-mode): the warp stays on the loom for the next cloth (same twine codes, resampled to the width).
        if (warp.Length == oldWidth)
            for (int c = 0; c < Width; c++) Draft.Warp[c] = Carry(warp[c * oldWidth / Width]);
        if (DraftMode)
        {
            // The next cloth's rows come from the treadles: nothing is carried into its current row.
            EnsureWarp();
            RecomputeCurrent();
            return;
        }
        for (int c = 0; c < Width; c++) Current[c] = Carry(seed[c * oldWidth / Width]);
    }

    /// <summary>The cloth as cut now (<see cref="Width"/> x <see cref="WovenRows"/>), palette compacted to entries actually used.</summary>
    public (byte[] pattern, string[] codes, string[] colors) ExportCompacted()
    {
        var remap = new Dictionary<byte, byte>();
        var codes = new List<string>();
        var colors = new List<string>();
        var pattern = new byte[Width * WovenRows];
        for (int i = 0; i < pattern.Length; i++)
        {
            byte c = Woven[i];
            if (!remap.TryGetValue(c, out byte n))
            {
                n = (byte)codes.Count;
                remap[c] = n;
                codes.Add(c < PaletteCodes.Count ? PaletteCodes[c] : "");
                colors.Add(ColorOf(c));
            }
            pattern[i] = n;
        }
        return (pattern, codes.ToArray(), colors.ToArray());
    }

    public void ToTree(ITreeAttribute tree)
    {
        tree.SetBytes("woven", Woven.Take(WovenRows * Width).ToArray()); // woven rows only (sync size)
        tree.SetBytes("current", Current);
        tree.SetInt("rows", WovenRows);
        tree.SetInt("width", Width);
        tree.SetInt("height", Rows);
        tree.SetInt("product", (int)Product);
        tree["palCodes"] = new StringArrayAttribute(PaletteCodes.ToArray());
        tree["palColors"] = new StringArrayAttribute(PaletteColors.ToArray());
        var dtree = new TreeAttribute();
        Draft.ToTree(dtree, WovenRows);
        tree["draft"] = dtree;
    }

    public void FromTree(ITreeAttribute? tree)
    {
        if (tree == null) { Reset(); return; }
        // Square only (lane/square-cloth): the width is the size; "height" is written for older readers and ignored.
        // An old long cloth (lane/cloth-formats, e.g. 16 x 48) keeps its first square of rows (dev worlds, no migration).
        int w = tree.GetInt("width", DefaultWidth);
        Width = IsAllowedSize(w) ? w : DefaultWidth;
        int product = tree.GetInt("product", 0);
        Product = ClothProducts.Valid(product) ? (ClothProduct)product : ClothProduct.Cloth;
        byte[]? woven = tree.GetBytes("woven");
        byte[]? current = tree.GetBytes("current");
        // Older saves stored the whole cloth (width x height); now only the woven rows. Both fit the buffer.
        Woven = new byte[Width * Rows];
        // A width we no longer weave (e.g. a 12-wide cushion cloth) drops its rows: the bytes do not fit the new layout.
        bool fits = IsAllowedSize(w) && woven != null && woven.Length % Width == 0;
        if (fits) System.Array.Copy(woven!, Woven, System.Math.Min(woven!.Length, Woven.Length));
        Current = current?.Length == Width ? current : NewEmptyRow();
        WovenRows = fits ? System.Math.Clamp(tree.GetInt("rows"), 0, System.Math.Min(Rows, woven!.Length / Width)) : 0;
        PaletteCodes = ((tree["palCodes"] as StringArrayAttribute)?.value ?? System.Array.Empty<string>()).ToList();
        PaletteColors = ((tree["palColors"] as StringArrayAttribute)?.value ?? System.Array.Empty<string>()).ToList();
        // Keep the palette lists aligned even if a save was tampered with.
        int n = System.Math.Min(PaletteCodes.Count, PaletteColors.Count);
        PaletteCodes = PaletteCodes.Take(n).ToList();
        PaletteColors = PaletteColors.Take(n).ToList();
        for (int i = 0; i < Current.Length; i++) if (Current[i] != Empty && Current[i] >= n) Current[i] = Empty;
        // Draft (lane/draft-mode): no tree or no stored mode = draft (lane/playtest-fixes-5; before: tapestry).
        Draft.FromTree(tree.GetTreeAttribute("draft"), Width, WovenRows);
        for (int i = 0; i < Draft.Warp.Length; i++) if (Draft.Warp[i] != Empty && Draft.Warp[i] >= n) Draft.Warp[i] = Empty;
        if (Draft.CurrentWeft != Empty && Draft.CurrentWeft >= n) Draft.CurrentTreadle = DraftState.NoTreadle;
        EnsureWarp();
        // Draft: the current row is the open shed's drawdown (an old tapestry save's painted row is dropped).
        RecomputeCurrent();
    }
}
