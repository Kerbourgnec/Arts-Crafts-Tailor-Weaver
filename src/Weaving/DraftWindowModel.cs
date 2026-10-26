using System;
using System.Collections.Generic;
using System.Linq;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// State of the in-game draft window (lane/draft-window, docs/weaving.md "Draft window"): the whole draft of the loom's
/// cloth as the owner's designer shows it (docs/weaving-draft-designer.html): warp target colours, threading (4 shafts),
/// tie-up (4 treadles: the Artisan loom's board), treadling and weft per row. Colours are designer palette indices
/// (<see cref="DraftCode.PaletteCodes"/>). Pure data (selftest DraftWindowChecks), no game API.
/// <para>
/// Edit rules (same as the server, <see cref="DraftWindowRules.Apply"/>): the tie-up changes any time; treadling and weft
/// only for rows not woven yet; threading and warp colours only while the warp is free (<see cref="WeavingState.WarpLocked"/>).
/// Warp sync (lane/linen-output, owner 2026-10-20): the warp colours are the physical warp; a warp edit is a load request
/// to the server (<see cref="DraftWindowRules.LoadWarp"/>), not a separate target. Only an imported code's warp colours can
/// differ from the loaded twine: they are required colours until loaded. Woven rows show the loom's actual cells, read-only.
/// </para>
/// </summary>
public sealed class DraftWindowModel
{
    public const int Shafts = DraftPresets.Shafts, Treadles = DraftPresets.Treadles;
    /// <summary>Floats longer than this are flagged (the designer's FLOAT).</summary>
    public const int FloatLimit = 5;
    public static int PaletteSize => DraftCode.PaletteCodes.Length;

    public int N { get; private set; } = WeavingState.DefaultWidth;
    /// <summary>Warp colour per end (palette index): the loaded twine, or an imported draft's required colour not loaded yet.</summary>
    public int[] Warp = Array.Empty<int>();
    /// <summary>Shaft (0..3) per end.</summary>
    public int[] Threading = Array.Empty<int>();
    /// <summary>Lift mask per treadle (bit s = shaft s + 1).</summary>
    public int[] TieUp = new int[Treadles];
    /// <summary>Treadle (0..3) per row; for woven rows the treadle pressed (tapestry rows: 0, not used).</summary>
    public int[] Treadling = Array.Empty<int>();
    /// <summary>Weft colour per row (palette index).</summary>
    public int[] Weft = Array.Empty<int>();

    // From the loom (read-only in the window).
    public int WovenRows { get; private set; }
    /// <summary>Twine code per woven cell, row-major (<see cref="WovenRows"/> x <see cref="N"/>).</summary>
    public string[] WovenCells = Array.Empty<string>();
    /// <summary>Shafts each woven row lifted (-1: a tapestry row, no structure).</summary>
    public int[] WovenLifts = Array.Empty<int>();
    /// <summary>Twine code loaded on each warp thread at the back.</summary>
    public string[] LoadedWarp = Array.Empty<string>();
    /// <summary>The threading is fixed by the warp (rows on the loom; lane/loom-feedback-8: nothing else locks it).</summary>
    public bool ThreadingLocked { get; private set; }
    /// <summary>The lock comes from rows on the loom (always, since lane/loom-feedback-8).</summary>
    public bool LockedByRows { get; private set; }

    /// <summary>Palette index of a twine code, or -1 when the designer palette has no such twine.</summary>
    public static int PaletteIndex(string? code) => code == null ? -1 : Array.IndexOf(DraftCode.PaletteCodes, code);
    static int IndexOr0(string? code) => Math.Max(0, PaletteIndex(code));
    public static string CodeOf(int idx) => DraftCode.PaletteCodes[Math.Clamp(idx, 0, PaletteSize - 1)];

    #region From the loom

    /// <summary>
    /// The window for <paramref name="s"/>: the loom's threading and tie-up, the imported plan's warp / treadling / weft
    /// when it has one, else the loaded warp, the treadling preset (or straight 1-2-3-4) and the current weft (or flax).
    /// </summary>
    public static DraftWindowModel FromLoom(WeavingState s)
    {
        var m = new DraftWindowModel { N = s.Width };
        int n = m.N;
        var plan = s.Draft.Plan is { } p && p.Size == n ? p : null;
        m.Threading = Enumerable.Range(0, n).Select(s.Draft.ShaftOf).ToArray();
        m.TieUp = (int[])s.Draft.TieUpLifts.Clone();
        m.Warp = Enumerable.Range(0, n).Select(x => IndexOr0(plan != null ? plan.Warp[x] : s.WarpCode(x))).ToArray();
        var seq = s.Draft.Sequence.Length > 0 ? s.Draft.Sequence : new[] { 0, 1, 2, 3 };
        string? curWeft = s.Draft.CurrentWeft < s.PaletteCodes.Count ? s.PaletteCodes[s.Draft.CurrentWeft] : null;
        m.Treadling = Enumerable.Range(0, n).Select(y => plan != null ? plan.Treadling[y] : seq[y % seq.Length]).ToArray();
        m.Weft = Enumerable.Range(0, n).Select(y => IndexOr0(plan != null ? plan.Weft[y] : curWeft)).ToArray();
        m.RefreshFromLoom(s);
        return m;
    }

    /// <summary>
    /// The loom changed (sync): take its woven rows (their treadle and weft), loaded warp and lock; keep the plan edits of
    /// the rows not woven. A locked threading is the loom's. Another size: start over from the loom.
    /// </summary>
    public void RefreshFromLoom(WeavingState s)
    {
        if (s.Width != N)
        {
            var fresh = FromLoom(s);
            CopyFrom(fresh);
            return;
        }
        WovenRows = Math.Min(s.WovenRows, N);
        var oldLoaded = LoadedWarp;
        WovenCells = new string[WovenRows * N];
        WovenLifts = new int[WovenRows];
        for (int y = 0; y < WovenRows; y++)
        {
            for (int x = 0; x < N; x++)
            {
                byte c = s.Woven[y * N + x];
                WovenCells[y * N + x] = c < s.PaletteCodes.Count ? s.PaletteCodes[c] : DraftState.PlainCode;
            }
            byte t = s.Draft.Treadles[y];
            bool draftRow = t != DraftState.NoTreadle && t < Treadles;
            WovenLifts[y] = draftRow ? s.Draft.RowLifts[y] : -1;
            if (draftRow)
            {
                Treadling[y] = t;
                byte w = s.Draft.Wefts[y];
                if (w < s.PaletteCodes.Count && PaletteIndex(s.PaletteCodes[w]) is int wi and >= 0) Weft[y] = wi;
            }
        }
        LoadedWarp = Enumerable.Range(0, N).Select(s.WarpCode).ToArray();
        // Warp sync: a thread loaded since (at the back, or this window's request) shows what was loaded.
        if (oldLoaded.Length == N)
            for (int x = 0; x < N && x < Warp.Length; x++)
                if (oldLoaded[x] != LoadedWarp[x]) Warp[x] = IndexOr0(LoadedWarp[x]);
        ThreadingLocked = s.WarpLocked;
        LockedByRows = s.WovenRows > 0;
        if (ThreadingLocked) Threading = Enumerable.Range(0, N).Select(s.Draft.ShaftOf).ToArray();
    }

    /// <summary>Take the loom's whole draft, plan edits included (live draft: another player changed it).</summary>
    public void AdoptLoom(WeavingState s) => CopyFrom(FromLoom(s));

    void CopyFrom(DraftWindowModel o)
    {
        N = o.N; Warp = o.Warp; Threading = o.Threading; TieUp = o.TieUp; Treadling = o.Treadling; Weft = o.Weft;
        WovenRows = o.WovenRows; WovenCells = o.WovenCells; WovenLifts = o.WovenLifts; LoadedWarp = o.LoadedWarp;
        ThreadingLocked = o.ThreadingLocked; LockedByRows = o.LockedByRows;
    }

    #endregion

    #region Edits (false = refused by the rules)

    public bool Woven(int y) => y >= 0 && y < WovenRows;

    /// <summary>Tie-up: treadle <paramref name="t"/> lifts shaft <paramref name="shaft"/> or not (any time).</summary>
    public bool ToggleTieUp(int t, int shaft)
    {
        if (t < 0 || t >= Treadles || shaft < 0 || shaft >= Shafts) return false;
        TieUp[t] ^= 1 << shaft;
        return true;
    }

    /// <summary>Threading: end <paramref name="x"/> on <paramref name="shaft"/>; refused while the warp is locked.</summary>
    public bool SetThreading(int x, int shaft)
    {
        if (ThreadingLocked || x < 0 || x >= N || shaft < 0 || shaft >= Shafts) return false;
        Threading[x] = shaft;
        return true;
    }

    /// <summary>Treadling: row <paramref name="y"/> presses <paramref name="t"/>; refused for a woven row.</summary>
    public bool SetTreadle(int y, int t)
    {
        if (Woven(y) || y < 0 || y >= N || t < 0 || t >= Treadles) return false;
        Treadling[y] = t;
        return true;
    }

    /// <summary>Weft colour <paramref name="c"/> on rows <paramref name="from"/>..<paramref name="to"/> (0-based, inclusive) not woven yet; rows changed.</summary>
    public int SetWeft(int from, int to, int c)
    {
        if (c < 0 || c >= PaletteSize) return 0;
        int n = 0;
        for (int y = Math.Max(Math.Min(from, to), WovenRows); y <= Math.Min(Math.Max(from, to), N - 1); y++) { Weft[y] = c; n++; }
        return n;
    }

    /// <summary>Colour brush widths of the window (lane/draft-polish, owner): all, 8, 4, 2, 1 threads or rows per click (0 = all).</summary>
    public static readonly int[] ColourBrushes = { 0, 8, 4, 2, 1 };

    /// <summary>
    /// Threads (warp strip) or rows (weft column) a click on <paramref name="cell"/> colours with brush <paramref name="width"/>
    /// (0 = the whole strip), inclusive. Same span as the loom's brush at the back (<see cref="WeavingState.BrushSpan"/>:
    /// centred, even widths reach one further toward higher numbers, clipped at the ends).
    /// </summary>
    public static (int lo, int hi) BrushRange(int width, int cell, int n)
    {
        if (width <= 0) return (0, n - 1);
        var (start, end) = WeavingState.BrushSpan(Math.Clamp(cell, 0, n - 1), width, n);
        return (start, end - 1);
    }

    /// <summary>
    /// Warp colour <paramref name="c"/> on ends <paramref name="from"/>..<paramref name="to"/> as the window shows it after a
    /// load (free warp only, else 0); ends changed. The window itself sends a load request instead (warp sync); this is the
    /// local picture the server's load gives (selftest).
    /// </summary>
    public int SetWarp(int from, int to, int c)
    {
        if (ThreadingLocked || c < 0 || c >= PaletteSize) return 0;
        int n = 0;
        for (int x = Math.Max(0, Math.Min(from, to)); x <= Math.Min(Math.Max(from, to), N - 1); x++)
        {
            Warp[x] = c;
            if (x < LoadedWarp.Length) LoadedWarp[x] = CodeOf(c);
            n++;
        }
        return n;
    }

    /// <summary>
    /// Warp colour <paramref name="c"/> on ends <paramref name="from"/>..<paramref name="to"/> as a required colour while the load
    /// request is on its way (lane/loom-live-draft): the loaded twine stays as it is until the loom's sync says it was loaded, so a
    /// refused load shows as a mismatch (an import's required colour). Free warp only; ends changed.
    /// </summary>
    public int RequireWarp(int from, int to, int c)
    {
        if (ThreadingLocked || c < 0 || c >= PaletteSize) return 0;
        int n = 0;
        for (int x = Math.Max(0, Math.Min(from, to)); x <= Math.Min(Math.Max(from, to), N - 1); x++) { Warp[x] = c; n++; }
        return n;
    }

    /// <summary>Threading preset (shaft per end, repeated over the warp); refused while locked.</summary>
    public bool ApplyThreading(int[] repeat)
    {
        if (ThreadingLocked || repeat.Length == 0 || repeat.Any(s => s < 0 || s >= Shafts)) return false;
        for (int x = 0; x < N; x++) Threading[x] = repeat[x % repeat.Length];
        return true;
    }

    /// <summary>Tie-up preset (lift mask per treadle).</summary>
    public bool ApplyTieUp(int[] lifts)
    {
        if (lifts.Length != Treadles || lifts.Any(m => m < 0 || m >= 1 << Shafts)) return false;
        TieUp = (int[])lifts.Clone();
        return true;
    }

    /// <summary>
    /// Treadling section: rows <paramref name="from"/>..<paramref name="to"/> (0-based, inclusive) repeat
    /// <paramref name="seq"/> (0-based treadles) from <paramref name="from"/> on; woven rows are skipped. Rows changed.
    /// </summary>
    public int ApplyTreadling(int from, int to, int[] seq)
    {
        if (seq.Length == 0 || seq.Any(t => t < 0 || t >= Treadles)) return 0;
        int lo = Math.Max(0, Math.Min(from, to)), hi = Math.Min(N - 1, Math.Max(from, to)), n = 0;
        for (int y = Math.Max(lo, WovenRows); y <= hi; y++) { Treadling[y] = seq[(y - lo) % seq.Length]; n++; }
        return n;
    }

    /// <summary>Copy-square sizes of the window (lane/loom-live-draft).</summary>
    public static readonly int[] SquareSizes = { 2, 4, 8, 16 };

    /// <summary>
    /// Top-left cell (column or row) of a <paramref name="size"/> square centred on <paramref name="cell"/> (even sizes reach one
    /// further toward higher numbers, like the brush), clamped so the square lies inside the cloth of <paramref name="n"/>.
    /// </summary>
    public static int SquareStart(int size, int cell, int n)
    {
        size = Math.Clamp(size, 1, n);
        return Math.Clamp(cell - (size - 1) / 2, 0, n - size);
    }

    /// <summary>
    /// Copy square (lane/loom-live-draft): the threading of source columns <paramref name="srcX"/>.. and the treadling and weft of
    /// source rows <paramref name="srcY"/>.. (<paramref name="size"/> each) go onto the target columns / rows
    /// <paramref name="dstX"/>.. / <paramref name="dstY"/>.. (clipped at the cloth's edge). The threading only while the warp is
    /// free; treadling and weft only on rows not woven. Source and target may overlap. Warp colours are not part of the model
    /// edit (they are loads, <see cref="CopySquareWarpRuns"/>). False when nothing changed.
    /// </summary>
    public bool CopySquare(int size, int srcX, int srcY, int dstX, int dstY)
    {
        if (size < 1 || size > N || srcX < 0 || srcY < 0 || srcX + size > N || srcY + size > N || dstX < 0 || dstY < 0 || dstX >= N || dstY >= N) return false;
        var before = TakeSnapshot();
        int cols = Math.Min(size, N - dstX), rows = Math.Min(size, N - dstY);
        var th = Enumerable.Range(0, size).Select(i => Threading[srcX + i]).ToArray();
        var tr = Enumerable.Range(0, size).Select(j => Treadling[srcY + j]).ToArray();
        var wf = Enumerable.Range(0, size).Select(j => Weft[srcY + j]).ToArray();
        if (!ThreadingLocked) for (int i = 0; i < cols; i++) Threading[dstX + i] = th[i];
        for (int j = 0; j < rows; j++)
        {
            if (Woven(dstY + j)) continue;
            Treadling[dstY + j] = tr[j];
            Weft[dstY + j] = wf[j];
        }
        return !TakeSnapshot().SameAs(before);
    }

    /// <summary>
    /// The warp loads a copy square needs: runs of target ends (from, to, colour) whose colour differs from the source ends'
    /// (warp colours are the loaded twine, so each run is a load request). Empty while the warp is locked.
    /// </summary>
    public List<(int from, int to, int colour)> CopySquareWarpRuns(int size, int srcX, int dstX)
    {
        var runs = new List<(int, int, int)>();
        if (ThreadingLocked || size < 1 || srcX < 0 || srcX + size > N || dstX < 0 || dstX >= N) return runs;
        int cols = Math.Min(size, N - dstX);
        var src = Enumerable.Range(0, size).Select(i => Warp[srcX + i]).ToArray(); // before any load
        for (int i = 0; i < cols; i++)
        {
            if (Warp[dstX + i] == src[i]) continue;
            if (runs.Count > 0 && runs[^1].Item2 == dstX + i - 1 && runs[^1].Item3 == src[i]) runs[^1] = (runs[^1].Item1, dstX + i, src[i]);
            else runs.Add((dstX + i, dstX + i, src[i]));
        }
        return runs;
    }

    /// <summary>"1 2 3 4" (1-based treadles, spaces / commas / dashes) → 0-based; null when empty or out of range.</summary>
    public static int[]? ParseSequence(string? text)
    {
        var parts = (text ?? "").Split(new[] { ' ', ',', '-', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var seq = new List<int>();
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out int t) || t < 1 || t > Treadles) return null;
            seq.Add(t - 1);
        }
        return seq.Count > 0 ? seq.ToArray() : null;
    }

    #endregion

    #region Drawdown and checks

    /// <summary>Shafts row <paramref name="y"/> lifts: a woven row's record (-1 for a tapestry row), else the plan's treadle.</summary>
    public int LiftOf(int y) => Woven(y) ? WovenLifts[y] : TieUp[Math.Clamp(Treadling[y], 0, Treadles - 1)];

    /// <summary>Structure of cell (x, y): 1 warp up, 0 weft on top, 2 unknown (tapestry row).</summary>
    public int Up(int x, int y)
    {
        int lift = LiftOf(y);
        return lift < 0 ? 2 : (lift >> Threading[x]) & 1;
    }

    /// <summary>Twine code of a cell: woven rows as woven, plan rows from the rule with the target warp.</summary>
    public string CellCode(int x, int y)
        => Woven(y) ? WovenCells[y * N + x] : Up(x, y) == 1 ? CodeOf(Warp[x]) : CodeOf(Weft[y]);

    /// <summary>Treadle <paramref name="t"/> lifts no end or every end on this threading: no shed (refused by the loom).</summary>
    public bool NoShed(int t)
    {
        if (t < 0 || t >= Treadles) return true;
        int up = Enumerable.Range(0, N).Count(x => ((TieUp[t] >> Threading[x]) & 1) != 0);
        return up == 0 || up == N;
    }

    /// <summary>Rows not woven yet whose treadle opens no shed (Apply is refused while there are any).</summary>
    public List<int> NoShedRows() => Enumerable.Range(WovenRows, Math.Max(0, N - WovenRows)).Where(y => NoShed(Treadling[y])).ToList();

    public readonly record struct Float(int Length, int Index, bool Back);

    /// <summary>
    /// Floats (the designer's floatMap): runs of the same structure along a row (weft float; <see cref="Float.Index"/> = row)
    /// and down a column (warp float; index = end). Mask: cells in a run longer than <see cref="FloatLimit"/>.
    /// Tapestry rows break the runs.
    /// </summary>
    public (bool[] mask, Float weft, Float warp) Floats()
    {
        var mask = new bool[N * N];
        Float h = default, v = default;
        for (int y = 0; y < N; y++)
        {
            int s = 0;
            for (int x = 1; x <= N; x++)
            {
                if (x < N && Up(x, y) == Up(s, y)) continue;
                int len = x - s, u = Up(s, y);
                if (u != 2)
                {
                    if (len > h.Length) h = new Float(len, y, u == 1);
                    if (len > FloatLimit) for (int k = s; k < x; k++) mask[y * N + k] = true;
                }
                s = x;
            }
        }
        for (int x = 0; x < N; x++)
        {
            int s = 0;
            for (int y = 1; y <= N; y++)
            {
                if (y < N && Up(x, y) == Up(x, s)) continue;
                int len = y - s, u = Up(x, s);
                if (u != 2)
                {
                    if (len > v.Length) v = new Float(len, x, u == 0);
                    if (len > FloatLimit) for (int k = s; k < y; k++) mask[k * N + x] = true;
                }
                s = y;
            }
        }
        return (mask, h, v);
    }

    /// <summary>Cells per twine code of the whole cloth (woven rows as woven, the rest as planned).</summary>
    public Dictionary<string, int> CellsByCode()
    {
        var d = new Dictionary<string, int>();
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                string c = CellCode(x, y);
                d[c] = d.TryGetValue(c, out int k) ? k + 1 : 1;
            }
        return d;
    }

    /// <summary>Twine the cloth costs when cut (the flying shuttle loom's rule, <see cref="ClothCost"/>: 12 per cut, any size).</summary>
    public Dictionary<string, int> Cost(ClothProduct product = ClothProduct.Cloth) => ClothCost.TwineByCode(CellsByCode(), product, flyingShuttle: true);

    /// <summary>Ends whose loaded twine differs from the required colour (an imported draft not loaded yet).</summary>
    public List<int> WarpMismatches() => Enumerable.Range(0, N).Where(x => x < LoadedWarp.Length && LoadedWarp[x] != CodeOf(Warp[x])).ToList();

    #endregion

    #region Undo snapshots (lane/draft-polish)

    /// <summary>
    /// The plan edits of the window at one moment: threading, tie-up, treadling, weft, and the warp colours that differ from
    /// the loaded twine (an import's required colours; -1 = "as loaded"). Loaded warp colours are the physical warp (loads are
    /// server actions), so undo never reloads a thread; it only restores required colours.
    /// </summary>
    public sealed record Snapshot(int N, int[] Threading, int[] TieUp, int[] Treadling, int[] Weft, int[] Required)
    {
        public bool SameAs(Snapshot o) => N == o.N && Threading.SequenceEqual(o.Threading) && TieUp.SequenceEqual(o.TieUp)
            && Treadling.SequenceEqual(o.Treadling) && Weft.SequenceEqual(o.Weft) && Required.SequenceEqual(o.Required);
    }

    public Snapshot TakeSnapshot()
        => new(N, (int[])Threading.Clone(), (int[])TieUp.Clone(), (int[])Treadling.Clone(), (int[])Weft.Clone(),
            Enumerable.Range(0, N).Select(x => x < LoadedWarp.Length && LoadedWarp[x] == CodeOf(Warp[x]) ? -1 : Warp[x]).ToArray());

    /// <summary>
    /// Put <paramref name="s"/> back, within the edit rules of now: woven rows keep the loom's treadle and weft, a locked
    /// threading stays the loom's (and its warp the loaded one). False when the snapshot is of another cloth size.
    /// </summary>
    public bool Restore(Snapshot s)
    {
        if (s.N != N) return false;
        TieUp = (int[])s.TieUp.Clone();
        for (int y = WovenRows; y < N; y++)
        {
            Treadling[y] = s.Treadling[y];
            Weft[y] = s.Weft[y];
        }
        if (!ThreadingLocked)
        {
            Threading = (int[])s.Threading.Clone();
            for (int x = 0; x < N; x++) Warp[x] = s.Required[x] >= 0 ? s.Required[x] : IndexOr0(x < LoadedWarp.Length ? LoadedWarp[x] : null);
        }
        return true;
    }

    #endregion

    #region Code

    public DraftCodeData ToData()
        => new(N, Shafts, Treadles, (int[])Warp.Clone(), (int[])Threading.Clone(), (int[])TieUp.Clone(),
            Treadling.Select(t => Math.Clamp(t, 0, Treadles - 1)).ToArray(), (int[])Weft.Clone());

    /// <summary>The designer's export code of the window state (also what Apply sends).</summary>
    public string ToCode() => DraftCode.Encode(ToData());

    /// <summary>
    /// Import a designer code into the window (same validation as <c>/actailor draft</c>: <see cref="DraftCode.Decode"/> +
    /// <see cref="DraftPlan.FromCode"/>, rows from <see cref="WovenRows"/> must open a shed). Woven rows stay; a locked
    /// threading must match (<c>draftwin-err-threadinglocked</c>). False with <paramref name="error"/> on any problem.
    /// </summary>
    public bool Import(string? code, out DraftCode.Error? error)
    {
        var d = DraftCode.Decode(code, out error);
        if (d == null) return false;
        var plan = DraftPlan.FromCode(d, N, (code ?? "").Trim(), out error, WovenRows);
        if (plan == null)
        {
            if (error?.Key == "draft-err-size") error = DraftCode.Error.Of("draft-err-loomsize", d.N, N);
            return false;
        }
        if (ThreadingLocked && !plan.Threading.SequenceEqual(Threading)) { error = DraftCode.Error.Of("draftwin-err-threadinglocked"); return false; }
        // A set warp cannot be reloaded: an imported code must want the loaded colours (as /actailor draft on a set warp).
        if (ThreadingLocked && Enumerable.Range(0, N).Any(x => x < LoadedWarp.Length && plan.Warp[x] != LoadedWarp[x]))
        {
            error = DraftCode.Error.Of("draftwin-err-warplocked");
            return false;
        }
        Warp = plan.Warp.Select(IndexOr0).ToArray();
        Threading = (int[])plan.Threading.Clone();
        TieUp = (int[])plan.TieUp.Clone();
        for (int y = WovenRows; y < N; y++)
        {
            Treadling[y] = plan.Treadling[y];
            Weft[y] = IndexOr0(plan.Weft[y]);
        }
        return true;
    }

    #endregion
}

/// <summary>
/// Server rules of the draft window's Apply (lane/draft-window, packet <c>SetPlan</c>): the whole plan in one code,
/// validated like <c>/actailor draft</c>. Pure (selftest).
/// </summary>
public static class DraftWindowRules
{
    public enum Result { Ok, Size, ThreadingLocked }

    public enum LoadResult { Ok, NotDraft, WarpLocked, Complete, Range, MissingTwine, PaletteFull }

    /// <summary>
    /// Server rule of a draft-window warp edit (packet <c>LoadWarp</c>, warp sync, lane/linen-output): load the twine of
    /// designer palette index <paramref name="paletteIdx"/> onto ends <paramref name="from"/>..<paramref name="to"/>, as at
    /// the back. Plain flax needs nothing (as sneak + empty hand at the back); any other twine must be in the player's
    /// inventory (<paramref name="playerHas"/>: count in hotbar + backpack; <paramref name="hexOf"/>: the twine's colour). Nothing is consumed: the cut charges the cloth
    /// (owner decision 2026-10-19 "warp cost at cut time"). The plan's warp follows (<see cref="WeavingState.LoadWarpEnds"/>).
    /// </summary>
    public static LoadResult LoadWarp(WeavingState s, int from, int to, int paletteIdx, System.Func<string, int> playerHas,
        System.Func<string, string> hexOf, out int loaded)
    {
        loaded = 0;
        if (!s.DraftMode) return LoadResult.NotDraft;
        if (s.WarpLocked) return LoadResult.WarpLocked;
        if (s.Complete) return LoadResult.Complete;
        if (paletteIdx < 0 || paletteIdx >= DraftCode.PaletteCodes.Length || Math.Max(from, to) < 0 || Math.Min(from, to) >= s.Width) return LoadResult.Range;
        string code = DraftCode.PaletteCodes[paletteIdx];
        if (code != DraftState.PlainCode && playerHas(code) < 1) return LoadResult.MissingTwine;
        int idx = s.PaletteIndexFor(code, hexOf(code));
        if (idx < 0) return LoadResult.PaletteFull;
        loaded = s.LoadWarpEnds(from, to, (byte)idx);
        return loaded < 0 ? LoadResult.Range : LoadResult.Ok;
    }

    /// <summary>
    /// Weft colours a live plan edit brings in (lane/loom-live-draft item 4): the colours of rows <paramref name="fromRow"/>.. in
    /// <paramref name="after"/> that <paramref name="before"/> did not have there, in order of first row. Pure (selftest).
    /// </summary>
    public static List<string> NewWeftColours(IEnumerable<string> before, IEnumerable<string> after, int fromRow)
    {
        var had = new HashSet<string>(before.Skip(fromRow));
        return after.Skip(fromRow).Where(c => !had.Contains(c)).Distinct().ToList();
    }

    /// <summary>The weft colours of the plan before an edit: the plan's, else the loom's current weft (or flax) on every row, as the window shows them.</summary>
    public static string[] WeftBefore(WeavingState s)
    {
        if (s.Draft.Plan is { } p && p.Size == s.Width) return p.Weft;
        string cur = s.Draft.CurrentWeft < s.PaletteCodes.Count ? s.PaletteCodes[s.Draft.CurrentWeft] : DraftState.PlainCode;
        return Enumerable.Repeat(cur, s.Width).ToArray();
    }

    /// <summary>Decode + validate a window code for <paramref name="s"/> (size = the warp's width; woven rows need no shed).</summary>
    public static DraftPlan? Parse(WeavingState s, string? code, out DraftCode.Error? error, bool live = false)
    {
        var d = DraftCode.Decode(code, out error);
        if (d == null) return null;
        // Live draft (lane/loom-live-draft): the window sends every edit, also a state with a treadle that opens no shed (the
        // window flags it; the loom refuses to weave such a row), so no row is checked.
        var plan = DraftPlan.FromCode(d, s.Width, (code ?? "").Trim(), out error, live ? s.Width : s.WovenRows);
        if (plan == null && error?.Key == "draft-err-size") error = DraftCode.Error.Of("draft-err-loomsize", d.N, s.Width);
        return plan;
    }

    /// <summary>
    /// Put the window's plan on the loom, between any two rows: draft mode, the plan's threading (only while the warp is
    /// free, or unchanged), its tie-up (any time), manual treadling guided by the plan, and the plan. Woven rows keep what
    /// was woven: the plan's rows before <see cref="WeavingState.WovenRows"/> become the recorded treadle and weft (the
    /// code is re-encoded to match). Warp sync (lane/linen-output): on a set warp the plan takes the loaded warp (it cannot
    /// be reloaded); on a free warp the code's colours are the loaded ones or an import's required ones (shown on the threads
    /// until loaded). The shed closes when the plan has a treadle for the current row (lane/loom-feedback-8 item 6: the loom
    /// then opens that treadle with the plan's weft, <see cref="WeavingState.OpenDefaultShed"/>); else an open shed stays when it still opens.
    /// </summary>
    public static Result Apply(WeavingState s, DraftPlan plan)
    {
        if (plan.Size != s.Width) return Result.Size;
        var draft = s.Draft;
        if (s.WarpLocked && Enumerable.Range(0, s.Width).Any(x => draft.ShaftOf(x) != plan.Threading[x])) return Result.ThreadingLocked;
        for (int y = 0; y < Math.Min(s.WovenRows, plan.Size); y++)
        {
            byte t = draft.Treadles[y], w = draft.Wefts[y];
            if (t == DraftState.NoTreadle || t >= DraftPresets.Treadles) continue; // a tapestry row: nothing recorded
            plan.Treadling[y] = t;
            if (w < s.PaletteCodes.Count) plan.Weft[y] = s.PaletteCodes[w];
        }
        if (s.WarpLocked) for (int x = 0; x < plan.Size; x++) plan.Warp[x] = s.WarpCode(x);
        if (plan.ToCodeData() is { } data) plan.Code = DraftCode.Encode(data);
        bool wasDraft = s.DraftMode;
        draft.Mode = LoomMode.Draft;
        draft.CustomThreading = plan.Threading.Select(v => (byte)v).ToArray();
        draft.CustomTieUp = (int[])plan.TieUp.Clone();
        draft.Treadling = 0; // manual: the plan guides, the player presses
        draft.Step = 0;
        draft.Plan = plan;
        if (!wasDraft || (draft.CurrentTreadle != DraftState.NoTreadle && !draft.ShedOpens(draft.CurrentTreadle, s.Width))
            || (s.PlannedTreadle >= 0 && draft.ShedOpens(s.PlannedTreadle, s.Width)))
            draft.CurrentTreadle = DraftState.NoTreadle;
        s.EnsureWarp();
        s.RecomputeCurrent();
        return Result.Ok;
    }
}
