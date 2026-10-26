using System;
using System.Linq;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>How the loom makes each row (lane/draft-mode): paint cells (tapestry) or press treadles (shaft draft).</summary>
public enum LoomMode : byte
{
    /// <summary>Every cell painted by hand (max freedom; later the tapestry loom's only mode).</summary>
    Tapestry = 0,
    /// <summary>4-shaft draft: warp colours + threading + tie-up fixed per warp, one treadle and one weft per row.</summary>
    Draft = 1,
}

/// <summary>A threading preset: the shaft (0..3) of each warp end, repeated across the warp.</summary>
public sealed record ThreadingPreset(string Code, int[] Repeat);

/// <summary>A tie-up preset: per treadle (0..3) the bit mask of the shafts it lifts (bit 0 = shaft 1).</summary>
public sealed record TieUpPreset(string Code, int[] Lifts);

/// <summary>
/// A treadling preset (lane/playtest-fixes-5): the treadle (0..3) of each row, repeated. After each beat the loom presses
/// the next one with the last weft. Empty sequence = manual (the player presses every treadle).
/// </summary>
public sealed record TreadlingPreset(string Code, int[] Sequence)
{
    public bool Manual => Sequence.Length == 0;
}

/// <summary>
/// Draft presets (lane/draft-mode, docs/weaving.md "Draft mode"). 4 shafts, 4 treadles (Artisan loom).
/// Same rule as the owner's explainer: <c>cell = tieup[threading[x]][treadle[y]] ? warp[x] : weft[y]</c>.
/// </summary>
public static class DraftPresets
{
    public const int Shafts = 4, Treadles = 4;

    public static readonly ThreadingPreset[] Threadings =
    {
        new("straight", new[] { 0, 1, 2, 3 }),
        // Point (herringbone / goose-eye) threading: up and back, the end shafts once per repeat.
        new("point", new[] { 0, 1, 2, 3, 2, 1 }),
        // Two blocks of 2 shafts each (A = shafts 1+2, B = 3+4): with a twill tie-up, blocks show as squares.
        new("block", new[] { 0, 1, 0, 1, 2, 3, 2, 3 }),
        // Shafts 1..3 only: the threading the 2/1 twill tie-up needs (shaft 4 stays empty).
        new("straight3", new[] { 0, 1, 2 }),
    };

    public static readonly TieUpPreset[] TieUps =
    {
        // Treadles 3 and 4 repeat 1 and 2, so either foot pair weaves plain cloth.
        new("tabby", Masks(new[] { 0, 2 }, new[] { 1, 3 }, new[] { 0, 2 }, new[] { 1, 3 })),
        new("basket", Masks(new[] { 0, 1 }, new[] { 2, 3 }, new[] { 0, 1 }, new[] { 2, 3 })),
        new("twill22", Masks(new[] { 0, 1 }, new[] { 1, 2 }, new[] { 2, 3 }, new[] { 3, 0 })),
        // 2/1 twill over 3 shafts (with the straight3 threading); treadle 4 lifts shaft 4 only (a 1/3 twill pick
        // on a 4-shaft threading; refused on straight3, where it would lift no warp end).
        new("twill21", Masks(new[] { 0, 1 }, new[] { 1, 2 }, new[] { 2, 0 }, new[] { 3 })),
    };

    public static readonly TreadlingPreset[] Treadlings =
    {
        new("manual", Array.Empty<int>()),
        // 1, 2, 3, 4: with a twill tie-up a straight diagonal; with point threading herringbone.
        new("straight", new[] { 0, 1, 2, 3 }),
        // 1, 2, 3, 4, 3, 2: with point threading diamonds (goose-eye).
        new("point", new[] { 0, 1, 2, 3, 2, 1 }),
        // 1, 2 alternating: plain weave on the tabby tie-up, ribs on a twill.
        new("tabby", new[] { 0, 1 }),
    };

    /// <summary>
    /// Treadling sequences of the draft window (lane/draft-window chips, icons since lane/draft-polish): a click fills the
    /// treadling section's rows with the sequence. Lang <c>draftwin-treadling-{code}</c> (+ <c>-desc</c>).
    /// </summary>
    public static readonly TreadlingPreset[] WindowTreadlings =
    {
        new("tabby", new[] { 0, 1 }),
        new("straight", new[] { 0, 1, 2, 3 }),
        new("reverse", new[] { 3, 2, 1, 0 }),
        new("point", new[] { 0, 1, 2, 3, 2, 1 }),
        new("pairs", new[] { 0, 0, 1, 1 }),
    };

    public static int[] Masks(params int[][] shaftsPerTreadle)
        => shaftsPerTreadle.Select(s => s.Aggregate(0, (m, shaft) => m | (1 << shaft))).ToArray();

    public static ThreadingPreset Threading(int i) => Threadings[Math.Clamp(i, 0, Threadings.Length - 1)];
    public static TieUpPreset TieUp(int i) => TieUps[Math.Clamp(i, 0, TieUps.Length - 1)];
    public static bool ValidThreading(int i) => i >= 0 && i < Threadings.Length;
    public static bool ValidTieUp(int i) => i >= 0 && i < TieUps.Length;
    public static TreadlingPreset Treadling(int i) => Treadlings[Math.Clamp(i, 0, Treadlings.Length - 1)];
    public static bool ValidTreadling(int i) => i >= 0 && i < Treadlings.Length;
}

/// <summary>
/// The draft of a cut cloth (item attribute tree <c>draft</c>, for later automation, e.g. pattern cards): preset codes,
/// warp twine per column, shaft per column, lift mask per treadle, treadle and weft twine per row (row 0 = first woven).
/// </summary>
/// <para>
/// <see cref="LiftPlan"/> (lane/playtest-fixes-5): the shafts each row lifted (bit mask), so a cloth whose tie-up changed
/// between rows still recomputes; <see cref="TieUpCode"/> is then <c>mixed</c> and <see cref="TieUp"/> the last tie-up.
/// </para>
public sealed record DraftExport(string ThreadingCode, string TieUpCode, string[] WarpCodes, int[] Threading, int[] TieUp, int[] Treadling, string[] WeftCodes,
    int[]? LiftPlan = null, string? SourceCode = null)
{
    /// <summary>Tie-up code of a cloth whose tie-up changed between rows.</summary>
    public const string MixedTieUp = "mixed";

    public void ToTree(ITreeAttribute tree)
    {
        tree.SetString("threadingPreset", ThreadingCode);
        tree.SetString("tieupPreset", TieUpCode);
        tree["warp"] = new StringArrayAttribute(WarpCodes);
        tree["threading"] = new IntArrayAttribute(Threading);
        tree["tieup"] = new IntArrayAttribute(TieUp);
        tree["treadling"] = new IntArrayAttribute(Treadling);
        tree["weft"] = new StringArrayAttribute(WeftCodes);
        if (LiftPlan != null) tree["liftplan"] = new IntArrayAttribute(LiftPlan);
        if (SourceCode != null) tree.SetString("code", SourceCode);
    }

    public static DraftExport? FromTree(ITreeAttribute? tree)
    {
        if (tree == null) return null;
        string[] S(string k) => (tree[k] as StringArrayAttribute)?.value ?? Array.Empty<string>();
        int[] I(string k) => (tree[k] as IntArrayAttribute)?.value ?? Array.Empty<int>();
        return new DraftExport(tree.GetString("threadingPreset", "") ?? "", tree.GetString("tieupPreset", "") ?? "",
            S("warp"), I("threading"), I("tieup"), I("treadling"), S("weft"), (tree["liftplan"] as IntArrayAttribute)?.value,
            tree.GetString("code", null));
    }

    /// <summary>Recompute the cloth's cells from the draft (codes per cell): the same rule the loom weaves with.</summary>
    public string[] Cells()
    {
        int w = WarpCodes.Length, h = Treadling.Length;
        var cells = new string[w * h];
        var up = WarpUp();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                cells[y * w + x] = up[y * w + x] ? WarpCodes[x] : (y < WeftCodes.Length ? WeftCodes[y] : "");
        return cells;
    }

    /// <summary>Per cell (row-major, warp count x treadling rows): true where the warp end is lifted (the warp shows), false where the weft does.</summary>
    public bool[] WarpUp()
    {
        int w = WarpCodes.Length, h = Treadling.Length;
        var up = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int t = Treadling[y];
                int lift = LiftPlan != null && y < LiftPlan.Length ? LiftPlan[y] : t >= 0 && t < TieUp.Length ? TieUp[t] : 0;
                up[y * w + x] = x < Threading.Length && ((lift >> Threading[x]) & 1) != 0;
            }
        return up;
    }
}

/// <summary>
/// Shaft-weaving draft of the cloth on the loom (lane/draft-mode). Pure data, part of <see cref="WeavingState"/>.
/// <para>
/// Loom settings (kept across cloths, changed only on an empty loom): <see cref="Mode"/>, <see cref="Threading"/>,
/// <see cref="TieUp"/> preset indices, and <see cref="Warp"/>: one palette index per column (the warp colours; painted
/// on the fell row before the first beat). Per cloth: <see cref="Treadles"/> / <see cref="Wefts"/> per woven row,
/// and the open shed of the current row (<see cref="CurrentTreadle"/>, <see cref="CurrentWeft"/>).
/// </para>
/// </summary>
public sealed class DraftState
{
    public const byte NoTreadle = 255;
    /// <summary>Warp twine where the warp is not painted: plain flax twine (LoomTwine.PlainTwineCode).</summary>
    public const string PlainCode = "game:flaxtwine";

    /// <summary>Default mode of a new loom and of a save without a stored mode (lane/playtest-fixes-5, owner): draft.</summary>
    public const LoomMode DefaultMode = LoomMode.Draft;

    public LoomMode Mode = DefaultMode;
    public int Threading;
    public int TieUp = 2; // 2/2 twill: the explainer's default
    public byte[] Warp = WeavingState.NewEmptyRow(WeavingState.DefaultWidth);
    /// <summary>Treadling preset index (<see cref="DraftPresets.Treadlings"/>, 0 = manual); a loom setting, changeable between rows.</summary>
    public int Treadling;
    /// <summary>Position of the current row in the treadling sequence (auto treadling).</summary>
    public int Step;
    // No "warp set" flag since lane/loom-feedback-8 (owner: re-warp step removed): only rows on the loom lock the warp
    // (WeavingState.WarpLocked). Old saves' "warpSet" is ignored.
    public byte[] Treadles = NewRows();
    public byte[] Wefts = NewRows();
    /// <summary>Shafts lifted per woven row (bit mask; 0 for a tapestry row): the tie-up may change between rows.</summary>
    public byte[] RowLifts = new byte[WeavingState.MaxRows];
    public byte CurrentTreadle = NoTreadle;
    public byte CurrentWeft = WeavingState.Empty;

    /// <summary>
    /// Imported draft (lane/warp-shuttles-import, <c>/actailor draft</c>): the required warp, the treadling and weft plan.
    /// Kept across cloths; dropped by picking a threading or tie-up preset (F) or <c>/actailor draft clear</c>.
    /// </summary>
    public DraftPlan? Plan;
    /// <summary>Threading of an imported draft (shaft per end), instead of the <see cref="Threading"/> preset; null = preset.</summary>
    public byte[]? CustomThreading;
    /// <summary>Tie-up of an imported draft (lift mask per treadle), instead of the <see cref="TieUp"/> preset; null = preset.</summary>
    public int[]? CustomTieUp;

    public bool Enabled => Mode == LoomMode.Draft;

    static byte[] NewRows() => Enumerable.Repeat(NoTreadle, WeavingState.MaxRows).ToArray();

    /// <summary>Shaft (0..3) warp end <paramref name="x"/> is threaded on.</summary>
    public int ShaftOf(int x)
    {
        if (CustomThreading is { Length: > 0 } c) return c[((x % c.Length) + c.Length) % c.Length];
        var r = DraftPresets.Threading(Threading).Repeat;
        return r[((x % r.Length) + r.Length) % r.Length];
    }

    /// <summary>Lift mask per treadle of the tie-up in use (imported or preset).</summary>
    public int[] TieUpLifts => CustomTieUp is { Length: DraftPresets.Treadles } c ? c : DraftPresets.TieUp(TieUp).Lifts;

    /// <summary>Bit mask of the shafts treadle <paramref name="t"/> lifts (0 for an unknown treadle).</summary>
    public int LiftMask(int t)
    {
        var lifts = TieUpLifts;
        return t >= 0 && t < lifts.Length ? lifts[t] : 0;
    }

    /// <summary>Warp end <paramref name="x"/> is up (shows its warp colour) when treadle <paramref name="t"/> is pressed.</summary>
    public bool Raised(int x, int t) => ((LiftMask(t) >> ShaftOf(x)) & 1) != 0;

    /// <summary>
    /// Treadle <paramref name="t"/> opens a shed on a warp <paramref name="width"/> ends wide: at least one end up and
    /// one down (otherwise the weft does not interlace, research §5.2).
    /// </summary>
    public bool ShedOpens(int t, int width)
    {
        if (t < 0 || t >= DraftPresets.Treadles) return false;
        int up = 0;
        for (int x = 0; x < width; x++) if (Raised(x, t)) up++;
        return up > 0 && up < width;
    }

    /// <summary>The drawdown row for treadle <paramref name="t"/> and weft palette index <paramref name="weft"/>.</summary>
    public byte[] Row(int width, int t, byte weft)
    {
        var row = new byte[width];
        for (int x = 0; x < width; x++) row[x] = Raised(x, t) && x < Warp.Length ? Warp[x] : weft;
        return row;
    }

    /// <summary>Clear the per-cloth part (treadling, wefts, lifts, open shed, sequence step); loom settings and warp stay.</summary>
    public void ClearRows()
    {
        Array.Fill(Treadles, NoTreadle);
        Array.Fill(Wefts, NoTreadle);
        Array.Clear(RowLifts);
        CurrentTreadle = NoTreadle;
        CurrentWeft = WeavingState.Empty;
        Step = 0;
    }

    /// <summary>The treadling preset's sequence (empty = manual).</summary>
    public int[] Sequence => DraftPresets.Treadling(Treadling).Sequence;

    /// <summary>Treadle the sequence asks for at <see cref="Step"/>, or <see cref="NoTreadle"/> (manual).</summary>
    public byte SequenceTreadle => Sequence.Length == 0 ? NoTreadle : (byte)Sequence[((Step % Sequence.Length) + Sequence.Length) % Sequence.Length];

    /// <summary>A treadle was pressed by hand: the sequence goes on from it (first match at or after <see cref="Step"/>).</summary>
    public void SyncStep(int t)
    {
        var seq = Sequence;
        for (int i = 0; i < seq.Length; i++)
        {
            int k = (Step + i) % seq.Length;
            if (seq[k] == t) { Step = k; return; }
        }
    }

    public void ToTree(ITreeAttribute tree, int wovenRows)
    {
        tree.SetInt("mode", (int)Mode);
        tree.SetInt("threading", Threading);
        tree.SetInt("tieup", TieUp);
        tree.SetBytes("warp", Warp);
        tree.SetBytes("treadles", Treadles.Take(wovenRows).ToArray());
        tree.SetBytes("wefts", Wefts.Take(wovenRows).ToArray());
        tree.SetInt("curTreadle", CurrentTreadle);
        tree.SetInt("curWeft", CurrentWeft);
        tree.SetInt("treadling", Treadling);
        tree.SetInt("step", Step);
        tree.SetBytes("lifts", RowLifts.Take(wovenRows).ToArray());
        if (CustomThreading != null) tree.SetBytes("customThreading", CustomThreading);
        if (CustomTieUp != null) tree["customTieUp"] = new IntArrayAttribute(CustomTieUp);
        if (Plan != null)
        {
            var ptree = new TreeAttribute();
            Plan.ToTree(ptree);
            tree["plan"] = ptree;
        }
    }

    /// <summary>
    /// Load (missing tree or no stored mode: <see cref="DefaultMode"/>, defaults). Warp of another width is fixed by the
    /// caller (<see cref="WeavingState"/>).
    /// </summary>
    public void FromTree(ITreeAttribute? tree, int width, int wovenRows)
    {
        Mode = tree?.HasAttribute("mode") != true ? DefaultMode
            : tree.GetInt("mode") == (int)LoomMode.Draft ? LoomMode.Draft : LoomMode.Tapestry;
        int th = tree?.GetInt("threading", 0) ?? 0, tu = tree?.GetInt("tieup", 2) ?? 2;
        Threading = DraftPresets.ValidThreading(th) ? th : 0;
        TieUp = DraftPresets.ValidTieUp(tu) ? tu : 2;
        byte[]? warp = tree?.GetBytes("warp");
        Warp = warp?.Length == width ? warp : WeavingState.NewEmptyRow(width);
        ClearRows();
        byte[] tr = tree?.GetBytes("treadles") ?? Array.Empty<byte>(), wf = tree?.GetBytes("wefts") ?? Array.Empty<byte>();
        Array.Copy(tr, Treadles, Math.Min(Math.Min(tr.Length, wovenRows), Treadles.Length));
        Array.Copy(wf, Wefts, Math.Min(Math.Min(wf.Length, wovenRows), Wefts.Length));
        int ct = tree?.GetInt("curTreadle", NoTreadle) ?? NoTreadle, cw = tree?.GetInt("curWeft", WeavingState.Empty) ?? WeavingState.Empty;
        CurrentTreadle = ct >= 0 && ct < DraftPresets.Treadles ? (byte)ct : NoTreadle;
        CurrentWeft = cw >= 0 && cw <= 255 ? (byte)cw : WeavingState.Empty;
        int tl = tree?.GetInt("treadling", 0) ?? 0;
        Treadling = DraftPresets.ValidTreadling(tl) ? tl : 0;
        Step = Math.Max(0, tree?.GetInt("step", 0) ?? 0);
        // Imported draft (lane/warp-shuttles-import): before the lift masks below, which use the tie-up in use.
        byte[]? cth = tree?.GetBytes("customThreading");
        CustomThreading = cth is { Length: > 0 } && cth.All(s => s < DraftPresets.Shafts) ? cth : null;
        int[]? cu = (tree?["customTieUp"] as IntArrayAttribute)?.value;
        CustomTieUp = cu is { Length: DraftPresets.Treadles } && cu.All(m => m >= 0 && m < 1 << DraftPresets.Shafts) ? cu : null;
        Plan = DraftPlan.FromTree(tree?.GetTreeAttribute("plan"));
        byte[]? lifts = tree?.GetBytes("lifts");
        int rows = Math.Min(wovenRows, RowLifts.Length);
        for (int y = 0; y < rows; y++)
            RowLifts[y] = lifts != null && y < lifts.Length ? lifts[y] : Treadles[y] == NoTreadle ? (byte)0 : (byte)LiftMask(Treadles[y]);
    }
}
