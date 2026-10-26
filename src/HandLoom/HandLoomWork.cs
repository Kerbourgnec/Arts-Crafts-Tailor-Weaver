using System;
using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.HandLoom;

/// <summary>
/// Heddle rod threadings of the hand looms (lane/hand-loom, docs/hand-loom.md): the rod (0..2) of each warp end,
/// repeated across the warp, and how many rods the preset needs. Crude loom: 2 rods (alternating only); plank loom: 3.
/// </summary>
public static class HandLoomPresets
{
    public const int MaxRods = 3;

    public static readonly ThreadingPreset[] Threadings =
    {
        // Odd ends on rod 1, even ends on rod 2: plain weave (tabby) by lifting the rods in turn.
        new("alternating", new[] { 0, 1 }),
        // 1, 2, 3: lifting one rod (or two) per row weaves a 1/2 (or 2/1) twill.
        new("straight", new[] { 0, 1, 2 }),
        // 1, 2, 3, 2: twill that turns back (small chevrons / diamonds).
        new("point", new[] { 0, 1, 2, 1 }),
    };

    /// <summary>Rods a threading uses (highest rod + 1).</summary>
    public static int RodsOf(int threading) => Threadings[Math.Clamp(threading, 0, Threadings.Length - 1)].Repeat.Max() + 1;

    public static bool Valid(int threading, int rods) => threading >= 0 && threading < Threadings.Length && RodsOf(threading) <= rods;

    /// <summary>Threadings a loom with <paramref name="rods"/> heddle rods offers (indices into <see cref="Threadings"/>).</summary>
    public static IEnumerable<int> For(int rods) => Enumerable.Range(0, Threadings.Length).Where(i => RodsOf(i) <= rods);
}

/// <summary>
/// What a hand loom holds (lane/hand-loom): the cloth (<see cref="WeavingState"/> kept in tapestry mode: this class
/// computes every row itself), the warp colours (<see cref="DraftState.Warp"/> of the cloth: palette index per end),
/// the heddle rod threading, the shed of the current row (rods lifted + pick-up overrides), and the stick shuttles
/// (<see cref="SpoolRack"/>). Pure data: no game API calls (selftest).
/// <para>
/// Rule (the drawdown of docs/weaving.md "Draft mode", with rods as shafts and a per-row lift):
/// <c>up(x) = pickUp(x) || (!pickDown(x) &amp;&amp; rods lifted has rod(x))</c>; <c>cell = up(x) ? warp[x] : weft</c>.
/// A row needs a shed (one end up, one down) and a weft (the active shuttle) to be beaten in. The beat closes the shed
/// (rods down, pick-up stick out); a roll back or a copy restores a row's shed and weft.
/// </para>
/// </summary>
public sealed class HandLoomWork
{
    public const int ShuttleCount = 2;
    public const byte NoWeft = WeavingState.Empty;

    public readonly WeavingState Cloth = new();
    public readonly SpoolRack Shuttles = new(ShuttleCount);

    /// <summary>Heddle rods of this loom (2 crude, 3 plank); set by the block entity from the block type.</summary>
    public int RodCount { get; private set; } = HandLoomPresets.MaxRods;
    public int Threading { get; private set; }

    /// <summary>Shed of the current row: rods lifted (bit mask, bit 0 = rod 1), pick-up overrides (bit per end).</summary>
    public int Rods { get; private set; }
    public uint PickUp { get; private set; }
    public uint PickDown { get; private set; }
    /// <summary>Weft of the current row (palette index), <see cref="NoWeft"/> before a shuttle is active.</summary>
    public byte Weft { get; private set; } = NoWeft;

    readonly byte[] rowRods = new byte[WeavingState.MaxRows];
    readonly uint[] rowUp = new uint[WeavingState.MaxRows], rowDown = new uint[WeavingState.MaxRows];
    readonly byte[] rowWeft = Enumerable.Repeat(NoWeft, WeavingState.MaxRows).ToArray();

    public HandLoomWork() : this(WeavingState.DefaultWidth, HandLoomPresets.MaxRods) { }

    public HandLoomWork(int size, int rods)
    {
        Cloth.Reset(size);
        SetRodCount(rods);
        ForceTapestry();
        EnsureWarp();
        Recompute();
    }

    public int Size => Cloth.Width;
    public int WovenRows => Cloth.WovenRows;
    public bool Complete => Cloth.Complete;
    public bool CanCut => Cloth.CanCut;
    /// <summary>The warp is free (threading, size and colours can change): no row woven (no re-warp step, lane/loom-feedback-8).</summary>
    public bool WarpFree => !Cloth.WarpLocked;

    /// <summary>Rod count from the block type; a threading that needs more rods falls back to alternating.</summary>
    public void SetRodCount(int rods)
    {
        RodCount = Math.Clamp(rods, 2, HandLoomPresets.MaxRods);
        if (!HandLoomPresets.Valid(Threading, RodCount)) Threading = 0;
        Rods &= (1 << RodCount) - 1;
        Recompute();
    }

    void ForceTapestry()
    {
        Cloth.Draft.Mode = LoomMode.Tapestry;
        Cloth.Draft.CurrentTreadle = DraftState.NoTreadle;
        Cloth.Draft.Plan = null;
        Cloth.Draft.CustomThreading = null;
        Cloth.Draft.CustomTieUp = null;
    }

    /// <summary>Every warp end holds a twine (plain flax where none is loaded).</summary>
    void EnsureWarp()
    {
        if (Cloth.Draft.Warp.Length != Size) Cloth.Draft.Warp = WeavingState.NewEmptyRow(Size);
        for (int x = 0; x < Size; x++)
        {
            byte w = Cloth.Draft.Warp[x];
            if (w != WeavingState.Empty && w < Cloth.PaletteCodes.Count) continue;
            int p = Cloth.PlainIndex();
            if (p >= 0) Cloth.Draft.Warp[x] = (byte)p;
        }
    }

    public byte[] Warp => Cloth.Draft.Warp;
    public string WarpCode(int x) => x >= 0 && x < Warp.Length && Warp[x] < Cloth.PaletteCodes.Count ? Cloth.PaletteCodes[Warp[x]] : DraftState.PlainCode;

    /// <summary>Rod (0-based) warp end <paramref name="x"/> hangs on.</summary>
    public int RodOf(int x)
    {
        var r = HandLoomPresets.Threadings[Threading].Repeat;
        return r[((x % r.Length) + r.Length) % r.Length];
    }

    static bool Bit(uint m, int x) => ((m >> x) & 1) != 0;

    /// <summary>End <paramref name="x"/> is lifted by the rods alone (no pick-up).</summary>
    public bool RodLifts(int x, int rods) => ((rods >> RodOf(x)) & 1) != 0;

    /// <summary>End <paramref name="x"/> is up in the current shed (rods and pick-up stick).</summary>
    public bool Up(int x) => Up(x, Rods, PickUp, PickDown);

    bool Up(int x, int rods, uint up, uint down) => Bit(up, x) || (!Bit(down, x) && RodLifts(x, rods));

    public int UpCount => Enumerable.Range(0, Size).Count(Up);
    /// <summary>The shed is open: at least one end up and one down (else the weft does not interlace).</summary>
    public bool ShedOpen => UpCount > 0 && UpCount < Size;
    public bool PickUsed => (PickUp | PickDown) != 0;
    public bool RodLifted(int rod) => ((Rods >> rod) & 1) != 0;

    /// <summary>
    /// Current row from the shed: lifted ends show their warp, the others the weft. Without a weft the lowered ends stay
    /// empty; with no end lifted the whole row stays empty (closed shed: bare warp).
    /// </summary>
    void Recompute()
    {
        var row = WeavingState.NewEmptyRow(Size);
        if (UpCount > 0 && !Complete)
            for (int x = 0; x < Size; x++) row[x] = Up(x) ? Warp[x] : Weft;
        Cloth.Current = row;
    }

    public enum Result { Ok, Complete, Invalid, WarpSet, NoShed, NoWeft, NothingWoven, PaletteFull }

    /// <summary>Lift / lower heddle rod <paramref name="rod"/> (several may be up together: any tie-up, by hand).</summary>
    public Result ToggleRod(int rod)
    {
        if (Complete) return Result.Complete;
        if (rod < 0 || rod >= RodCount) return Result.Invalid;
        Rods ^= 1 << rod;
        Recompute();
        return Result.Ok;
    }

    /// <summary>Pick-up stick: end <paramref name="x"/> flips (up ↔ down) for this row, whatever the rods do.</summary>
    public Result TogglePick(int x)
    {
        if (Complete) return Result.Complete;
        if (x < 0 || x >= Size) return Result.Invalid;
        bool nowUp = !Up(x);
        uint bit = 1u << x;
        PickUp &= ~bit;
        PickDown &= ~bit;
        // Store an override only where it differs from what the rods do.
        if (nowUp != RodLifts(x, Rods)) { if (nowUp) PickUp |= bit; else PickDown |= bit; }
        Recompute();
        return Result.Ok;
    }

    /// <summary>Pull the pick-up stick out: every end follows the rods again.</summary>
    public Result ClearPick()
    {
        if (Complete) return Result.Complete;
        PickUp = PickDown = 0;
        Recompute();
        return Result.Ok;
    }

    /// <summary>Weft of the current row (the active stick shuttle's twine; colour frozen at its first use in the cloth).</summary>
    public Result SetWeft(string? code, string hex)
    {
        if (code == null) { Weft = NoWeft; Recompute(); return Result.Ok; }
        int idx = Cloth.PaletteIndexFor(code, hex);
        if (idx < 0) return Result.PaletteFull;
        Weft = (byte)idx;
        Recompute();
        return Result.Ok;
    }

    public string? WeftCode => Weft < Cloth.PaletteCodes.Count ? Cloth.PaletteCodes[Weft] : null;

    /// <summary>Beat the row in with the sword: needs an open shed and a weft. The shed then closes (rods down, stick out).</summary>
    public Result Beat()
    {
        if (Complete) return Result.Complete;
        if (!ShedOpen) return Result.NoShed;
        if (Weft == NoWeft || Weft >= Cloth.PaletteCodes.Count) return Result.NoWeft;
        int r = WovenRows;
        rowRods[r] = (byte)Rods;
        rowUp[r] = PickUp;
        rowDown[r] = PickDown;
        rowWeft[r] = Weft;
        Cloth.CommitCurrentRow();
        Rods = 0;
        PickUp = PickDown = 0;
        Recompute();
        return Result.Ok;
    }

    /// <summary>Roll the last row back: its shed and weft open again (the current shed is dropped).</summary>
    public Result RollBack()
    {
        if (!Cloth.Unweave(out _)) return Result.NothingWoven;
        Restore(WovenRows);
        Clear(WovenRows);
        return Result.Ok;
    }

    /// <summary>Copy woven row <paramref name="row"/>: its rods, pick-up and weft become the current shed.</summary>
    public Result CopyRow(int row)
    {
        if (Complete) return Result.Complete;
        if (row < 0 || row >= WovenRows) return Result.Invalid;
        Restore(row);
        return Result.Ok;
    }

    void Restore(int row)
    {
        Rods = rowRods[row] & ((1 << RodCount) - 1);
        PickUp = rowUp[row];
        PickDown = rowDown[row];
        Weft = rowWeft[row];
        Recompute();
    }

    void Clear(int row)
    {
        rowRods[row] = 0;
        rowUp[row] = rowDown[row] = 0;
        rowWeft[row] = NoWeft;
    }

    public int RowRods(int row) => row >= 0 && row < WovenRows ? rowRods[row] : 0;
    public uint RowPickUp(int row) => row >= 0 && row < WovenRows ? rowUp[row] : 0;
    public uint RowPickDown(int row) => row >= 0 && row < WovenRows ? rowDown[row] : 0;
    public byte RowWeft(int row) => row >= 0 && row < WovenRows ? rowWeft[row] : NoWeft;

    /// <summary>Lift mask of woven row <paramref name="row"/> per end (true = up), for checks and later automation.</summary>
    public bool RowUp(int row, int x) => Up(x, RowRods(row), RowPickUp(row), RowPickDown(row));

    /// <summary>Pick a threading preset: only on a free warp, and only one this loom has the rods for.</summary>
    public Result SetThreading(int threading)
    {
        if (!WarpFree) return Result.WarpSet;
        if (!HandLoomPresets.Valid(threading, RodCount)) return Result.Invalid;
        Threading = threading;
        Recompute();
        return Result.Ok;
    }

    /// <summary>Warp ends a click on <paramref name="end"/> loads: the brush span, or every end (<paramref name="all"/>); null out of range.</summary>
    public List<int>? WarpTargets(int end, int brush, bool all)
    {
        if (all) return Enumerable.Range(0, Size).ToList();
        if (end < 0 || end >= Size) return null;
        var (s0, s1) = WeavingState.BrushSpan(end, brush, Size);
        return Enumerable.Range(s0, s1 - s0).ToList();
    }

    /// <summary>Load twine <paramref name="code"/> on the warp ends a click reaches (warp free only); nothing is consumed (cut charges).</summary>
    public Result LoadWarp(int end, int brush, bool all, string code, string hex)
    {
        if (!WarpFree) return Result.WarpSet;
        if (Complete) return Result.Complete;
        var ends = WarpTargets(end, brush, all);
        if (ends == null) return Result.Invalid;
        int idx = Cloth.PaletteIndexFor(code, hex);
        if (idx < 0) return Result.PaletteFull;
        foreach (int x in ends) Warp[x] = (byte)idx;
        Recompute();
        return Result.Ok;
    }

    /// <summary>Put the warp ends a click reaches back to plain flax (warp free only).</summary>
    public Result ClearWarp(int end, int brush, bool all)
    {
        if (!WarpFree) return Result.WarpSet;
        var ends = WarpTargets(end, brush, all);
        if (ends == null) return Result.Invalid;
        int plain = Cloth.PlainIndex();
        if (plain < 0) return Result.PaletteFull;
        foreach (int x in ends) Warp[x] = (byte)plain;
        Recompute();
        return Result.Ok;
    }

    /// <summary>Twine per code the cloth costs if cut now (<see cref="ClothCost"/>, same rule as every loom).</summary>
    public Dictionary<string, int> CutCost() => ClothCost.TwineByCode(Cloth.CellsByCode(Cloth.WovenCells), Cloth.Product);

    /// <summary>Next cloth after a cut (or a world size change on an empty loom): warp colours kept (resampled), shed closed.</summary>
    public void StartNextCloth(int size)
    {
        string? weftCode = WeftCode;
        string weftHex = Weft < Cloth.PaletteColors.Count ? Cloth.PaletteColors[Weft] : TwineColors.PlainHex;
        Cloth.StartNextCloth(size);
        ForceTapestry();
        for (int r = 0; r < WeavingState.MaxRows; r++) Clear(r);
        Rods = 0;
        PickUp = PickDown = 0;
        // The palette starts over with the next cloth: the weft keeps its twine (and frozen colour).
        Weft = NoWeft;
        if (weftCode != null && Cloth.PaletteIndexFor(weftCode, weftHex) is int wi and >= 0) Weft = (byte)wi;
        EnsureWarp();
        Recompute();
    }

    #region Shuttles (stick shuttles: same rules as the tapestry bobbins)

    public enum ShuttleAction { WindOne = 0, WindStack = 1, Select = 2, Unwind = 3 }

    public enum ShuttleResult { Ok, Invalid, OtherTwine, Full, Empty }

    public ShuttleResult Wind(int i, string code, int n, out int added)
    {
        added = 0;
        if (i < 0 || i >= Shuttles.Count || n <= 0) return ShuttleResult.Invalid;
        if (Shuttles.CodeAt(i) is { } other && other != code) return ShuttleResult.OtherTwine;
        added = Shuttles.Load(i, code, n);
        if (added <= 0) return ShuttleResult.Full;
        if (Shuttles.SelectedCode == null) Shuttles.Select(i);
        return ShuttleResult.Ok;
    }

    public ShuttleResult Select(int i)
    {
        if (i < 0 || i >= Shuttles.Count) return ShuttleResult.Invalid;
        return Shuttles.Select(i) ? ShuttleResult.Ok : ShuttleResult.Empty;
    }

    public (string? code, int amount) Unwind(int i) => Shuttles.Unload(i);

    /// <summary>After a copy / roll back: the shuttle holding the row's weft becomes active (if one does).</summary>
    public bool SelectShuttleForWeft()
    {
        int i = Shuttles.IndexOf(WeftCode);
        return i >= 0 && i != Shuttles.Selected && Shuttles.Select(i);
    }

    #endregion

    public void ToTree(ITreeAttribute tree)
    {
        var c = new TreeAttribute();
        Cloth.ToTree(c);
        tree["cloth"] = c;
        var s = new TreeAttribute();
        Shuttles.ToTree(s);
        tree["shuttles"] = s;
        int n = WovenRows;
        tree.SetInt("threading", Threading);
        tree.SetInt("rods", Rods);
        tree.SetLong("pickUp", PickUp);
        tree.SetLong("pickDown", PickDown);
        tree.SetInt("weft", Weft);
        tree.SetBytes("rowRods", rowRods.Take(n).ToArray());
        tree["rowUp"] = new LongArrayAttribute(rowUp.Take(n).Select(v => (long)v).ToArray());
        tree["rowDown"] = new LongArrayAttribute(rowDown.Take(n).Select(v => (long)v).ToArray());
        tree.SetBytes("rowWeft", rowWeft.Take(n).ToArray());
    }

    public void FromTree(ITreeAttribute? tree)
    {
        Cloth.FromTree(tree?.GetTreeAttribute("cloth"));
        ForceTapestry();
        Shuttles.FromTree(tree?.GetTreeAttribute("shuttles"));
        int th = tree?.GetInt("threading") ?? 0;
        Threading = HandLoomPresets.Valid(th, RodCount) ? th : 0;
        uint mask = Size >= 32 ? uint.MaxValue : (1u << Size) - 1;
        Rods = (tree?.GetInt("rods") ?? 0) & ((1 << RodCount) - 1);
        PickUp = (uint)(tree?.GetLong("pickUp") ?? 0) & mask;
        PickDown = (uint)(tree?.GetLong("pickDown") ?? 0) & mask;
        int w = tree?.GetInt("weft", NoWeft) ?? NoWeft;
        Weft = w >= 0 && w < Cloth.PaletteCodes.Count ? (byte)w : NoWeft;
        byte[] rr = tree?.GetBytes("rowRods") ?? Array.Empty<byte>(), rw = tree?.GetBytes("rowWeft") ?? Array.Empty<byte>();
        long[] ru = (tree?["rowUp"] as LongArrayAttribute)?.value ?? Array.Empty<long>(), rd = (tree?["rowDown"] as LongArrayAttribute)?.value ?? Array.Empty<long>();
        for (int r = 0; r < WeavingState.MaxRows; r++)
        {
            bool woven = r < WovenRows;
            rowRods[r] = woven && r < rr.Length ? rr[r] : (byte)0;
            rowUp[r] = woven && r < ru.Length ? (uint)ru[r] & mask : 0;
            rowDown[r] = woven && r < rd.Length ? (uint)rd[r] & mask : 0;
            rowWeft[r] = woven && r < rw.Length ? rw[r] : NoWeft;
        }
        EnsureWarp();
        Recompute();
    }
}
