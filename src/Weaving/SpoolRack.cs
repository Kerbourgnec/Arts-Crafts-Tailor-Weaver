using System;
using System.Collections.Generic;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// Twine holders of the loom, each holding whole twine items of one code (up to <see cref="Capacity"/>); one can be
/// selected (the active one). Since lane/warp-shuttles-import these are the loom's <see cref="ShuttleCount"/> shuttles
/// (weft of a draft row, palette of tapestry mode); before, 6 spools on the warp beam (<see cref="LegacySpoolCount"/>,
/// read from old saves). Cutting a cloth takes its twine (ClothCost) from the shuttles before the player's inventory.
/// Pure data: no game API calls.
/// </summary>
public class SpoolRack
{
    /// <summary>Shuttles on the Artisan loom (owner: 1 to 4 weft colours).</summary>
    public const int ShuttleCount = 4;
    /// <summary>Spools of the looms before lane/warp-shuttles-import (BE tree <c>spools</c>).</summary>
    public const int LegacySpoolCount = 6;
    public const int Capacity = 32;

    public int Count { get; }
    readonly string?[] codes;
    readonly int[] amounts;

    public SpoolRack(int count = ShuttleCount)
    {
        Count = Math.Max(1, count);
        codes = new string?[Count];
        amounts = new int[Count];
    }

    /// <summary>Selected (active) holder index, or -1.</summary>
    public int Selected { get; private set; } = -1;

    public string? CodeAt(int i) => InRange(i) && amounts[i] > 0 ? codes[i] : null;
    public int AmountAt(int i) => InRange(i) ? amounts[i] : 0;
    public bool IsEmpty(int i) => CodeAt(i) == null;

    /// <summary>Code of the selected holder, null if none is selected or it ran empty.</summary>
    public string? SelectedCode => CodeAt(Selected);

    bool InRange(int i) => i >= 0 && i < Count;

    /// <summary>First holder with twine of <paramref name="code"/>, or -1.</summary>
    public int IndexOf(string? code)
    {
        if (code == null) return -1;
        for (int i = 0; i < Count; i++) if (CodeAt(i) == code) return i;
        return -1;
    }

    /// <summary>Wind up to <paramref name="n"/> twine of <paramref name="code"/> onto holder i; returns how many fit (0: other code or full).</summary>
    public int Load(int i, string code, int n)
    {
        if (!InRange(i) || n <= 0) return 0;
        if (!IsEmpty(i) && codes[i] != code) return 0;
        int add = Math.Min(n, Capacity - amounts[i]);
        if (add <= 0) return 0;
        codes[i] = code;
        amounts[i] += add;
        return add;
    }

    /// <summary>First empty holder, or -1 when every holder holds twine.</summary>
    public int FirstEmpty()
    {
        for (int i = 0; i < Count; i++) if (IsEmpty(i)) return i;
        return -1;
    }

    /// <summary>
    /// Twine on no holder (lane/shuttle-fix): wind up to <paramref name="n"/> of <paramref name="code"/> onto the first
    /// empty holder and select it. Returns (holder, wound), or (-1, 0) when every holder holds other twine. A code
    /// already on a holder is not wound again: that holder is selected, (holder, 0).
    /// </summary>
    public (int index, int added) LoadIntoFree(string code, int n)
    {
        int i = IndexOf(code);
        if (i >= 0) { Selected = i; return (i, 0); }
        i = FirstEmpty();
        if (i < 0 || n <= 0) return (-1, 0);
        int added = Load(i, code, n);
        if (added > 0) Selected = i;
        return (added > 0 ? i : -1, added);
    }

    /// <summary>Take holder i off the loom; returns its content (code null if it was empty). Clears its selection.</summary>
    public (string? code, int amount) Unload(int i)
    {
        if (!InRange(i)) return (null, 0);
        var content = (CodeAt(i), amounts[i]);
        codes[i] = null;
        amounts[i] = 0;
        if (Selected == i) Selected = -1;
        return content;
    }

    /// <summary>Select holder i (false if it is empty).</summary>
    public bool Select(int i)
    {
        if (IsEmpty(i)) return false;
        Selected = i;
        return true;
    }

    public int CountOf(string code)
    {
        int n = 0;
        for (int i = 0; i < Count; i++) if (CodeAt(i) == code) n += amounts[i];
        return n;
    }

    /// <summary>Take up to <paramref name="n"/> twine of a code, holder 0 first; returns how many were taken.</summary>
    public int Take(string code, int n)
    {
        int taken = 0;
        for (int i = 0; i < Count && taken < n; i++)
        {
            if (CodeAt(i) != code) continue;
            int t = Math.Min(n - taken, amounts[i]);
            amounts[i] -= t;
            taken += t;
            if (amounts[i] == 0) codes[i] = null;
        }
        return taken;
    }

    /// <summary>Everything on the holders, per holder (for drops when the loom breaks).</summary>
    public IEnumerable<(string code, int amount)> Contents()
    {
        for (int i = 0; i < Count; i++)
        {
            string? c = CodeAt(i);
            if (c != null) yield return (c, amounts[i]);
        }
    }

    public void Clear()
    {
        for (int i = 0; i < Count; i++) { codes[i] = null; amounts[i] = 0; }
        Selected = -1;
    }

    public void ToTree(ITreeAttribute tree)
    {
        var c = new string[Count];
        for (int i = 0; i < Count; i++) c[i] = CodeAt(i) ?? "";
        tree["codes"] = new StringArrayAttribute(c);
        tree["amounts"] = new IntArrayAttribute((int[])amounts.Clone());
        tree.SetInt("selected", Selected);
    }

    /// <summary>
    /// Load. Entries beyond <see cref="Count"/> (an old 6-spool rack read into 4 shuttles) go to <paramref name="overflow"/>
    /// when given (the loom drops them as items), else they are lost.
    /// </summary>
    public void FromTree(ITreeAttribute? tree, List<(string code, int amount)>? overflow = null)
    {
        Clear();
        if (tree == null) return;
        string[] c = (tree["codes"] as StringArrayAttribute)?.value ?? Array.Empty<string>();
        int[] a = (tree["amounts"] as IntArrayAttribute)?.value ?? Array.Empty<int>();
        for (int i = 0; i < Math.Min(c.Length, a.Length); i++)
        {
            if (string.IsNullOrEmpty(c[i]) || a[i] <= 0) continue;
            int n = Math.Min(a[i], Capacity);
            if (i >= Count) { overflow?.Add((c[i], n)); continue; }
            codes[i] = c[i];
            amounts[i] = n;
        }
        int sel = tree.GetInt("selected", -1);
        Selected = IsEmpty(sel) ? -1 : sel;
    }
}
