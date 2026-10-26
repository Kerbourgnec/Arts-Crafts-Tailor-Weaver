using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// Twine kept on the loom as stock (lane/loom-feedback-8 item 4, owner: there was no way to put more twine on the loom for a
/// cut): whole twine items per code, up to <see cref="CapacityPerCode"/> each. The flying shuttle loom keeps one on its warp
/// beam (twine of a warp thread's colour wound onto it); the cut takes from the shuttles, then this stock, then the
/// player's hotbar + backpack. Pure data (selftest), BE tree <c>warpStock</c>.
/// </summary>
public class TwineStock
{
    /// <summary>Most twine of one code the stock holds (one full twine stack).</summary>
    public const int CapacityPerCode = 64;

    readonly Dictionary<string, int> amounts = new();

    public int Count(string? code) => code != null && amounts.TryGetValue(code, out int n) ? n : 0;

    /// <summary>Add up to <paramref name="n"/> of <paramref name="code"/>; returns how many fit.</summary>
    public int Add(string code, int n)
    {
        if (string.IsNullOrEmpty(code) || n <= 0) return 0;
        int add = Math.Min(n, CapacityPerCode - Count(code));
        if (add <= 0) return 0;
        amounts[code] = Count(code) + add;
        return add;
    }

    /// <summary>Take up to <paramref name="n"/> of <paramref name="code"/>; returns how many were taken.</summary>
    public int Take(string code, int n)
    {
        int t = Math.Min(Math.Max(0, n), Count(code));
        if (t <= 0) return 0;
        int left = Count(code) - t;
        if (left > 0) amounts[code] = left;
        else amounts.Remove(code);
        return t;
    }

    /// <summary>Everything of <paramref name="code"/> (taken off the loom).</summary>
    public int TakeAll(string code) => Take(code, Count(code));

    /// <summary>Stock per code, by code (block info, drops).</summary>
    public IEnumerable<(string code, int amount)> Contents() => amounts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value));

    public bool IsEmpty => amounts.Count == 0;

    /// <summary>
    /// Codes of <paramref name="cost"/> the loom and the player cannot cover: (code, need, have), have = shuttles + this stock
    /// + <paramref name="inventory"/>(code). Empty = the cut can be paid.
    /// </summary>
    public static List<(string code, int need, int have)> Shortfall(IReadOnlyDictionary<string, int> cost, SpoolRack? shuttles, TwineStock? stock,
        Func<string, int> inventory)
        => cost.Select(kv => (code: kv.Key, need: kv.Value, have: (shuttles?.CountOf(kv.Key) ?? 0) + (stock?.Count(kv.Key) ?? 0) + inventory(kv.Key)))
            .Where(m => m.have < m.need).ToList();

    /// <summary>
    /// Pay <paramref name="cost"/> (check <see cref="Shortfall"/> first): per code from the shuttles, then this stock, then
    /// <paramref name="takeFromInventory"/>(code, rest).
    /// </summary>
    public static void Pay(IReadOnlyDictionary<string, int> cost, SpoolRack? shuttles, TwineStock? stock, Action<string, int> takeFromInventory)
    {
        foreach (var (code, need) in cost)
        {
            int rest = need - (shuttles?.Take(code, need) ?? 0);
            rest -= stock?.Take(code, rest) ?? 0;
            if (rest > 0) takeFromInventory(code, rest);
        }
    }

    public void Clear() => amounts.Clear();

    public void ToTree(ITreeAttribute tree)
    {
        var c = Contents().ToArray();
        tree["codes"] = new StringArrayAttribute(c.Select(x => x.code).ToArray());
        tree["amounts"] = new IntArrayAttribute(c.Select(x => x.amount).ToArray());
    }

    public void FromTree(ITreeAttribute? tree)
    {
        Clear();
        if (tree == null) return;
        string[] c = (tree["codes"] as StringArrayAttribute)?.value ?? Array.Empty<string>();
        int[] a = (tree["amounts"] as IntArrayAttribute)?.value ?? Array.Empty<int>();
        for (int i = 0; i < Math.Min(c.Length, a.Length); i++) Add(c[i], a[i]);
    }
}
