using System;
using System.Collections.Generic;
using System.Linq;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// Twine a finished cloth costs, charged when it is cut off (lane/cloth-cost-flat, owner rule: cost does NOT scale with
/// precision; 16 x 16, 32 x 32 and 64 x 64 only differ in detail, not in fabric). Per product a fixed total, split over the
/// colours by share of cells:
/// <list type="bullet">
/// <item>Flying shuttle (Artisan) loom: <see cref="FlyingShuttleTwine"/> = 12 twine, 4 pieces (3 twine per piece).</item>
/// <item>Tapestry and hand looms (lane/cost-v2): <see cref="TapestryTwine"/> = 3 twine, 1 piece.</item>
/// <item>Carpets, on any loom: small = 3 twine (one linen), large = 6 (two linen), 1 block.</item>
/// </list>
/// Rounding (<see cref="Split"/>, deterministic): largest remainder to the total, i.e. each colour gets floor(total * cells / all),
/// the leftover twine goes one each to the colours with the biggest remainder (ties: more cells, then code order); then every used
/// colour is raised to at least 1. The sum is the total, except when that raise is needed: the sum can then exceed the total
/// (at most 14 twine colours exist, so at most 14 on the flying shuttle loom).
/// Pure data: no game API calls.
/// </summary>
public static class ClothCost
{
    /// <summary>Total twine of one cut on the flying shuttle loom, any cloth size.</summary>
    public const int FlyingShuttleTwine = 12;
    /// <summary>Twine of one linen piece: tapestry and hand looms cut 1 piece for this much (lane/cost-v2).</summary>
    public const int TwinePerLinen = 3;
    /// <summary>Total twine of one cut on the tapestry and hand looms (1 piece), any cloth size.</summary>
    public const int TapestryTwine = TwinePerLinen;

    /// <summary>Total twine a cut of <paramref name="product"/> takes (all colours together, before the per-colour minimum).</summary>
    public static int Total(ClothProduct product, bool flyingShuttle)
        => ClothProducts.CarpetBlockCode(product) != null
            ? TwinePerLinen * ClothProducts.LengthFactor(product) // carpets, any loom: small = 1 linen = 3, large = 2 linen = 6
            : flyingShuttle ? FlyingShuttleTwine : TapestryTwine;

    /// <summary>Twine per code: <paramref name="total"/> split by cell share (see the class rule). Unused codes (0 cells) cost nothing.</summary>
    public static Dictionary<string, int> Split(IReadOnlyDictionary<string, int> cellsByCode, int total)
    {
        var used = cellsByCode.Where(kv => kv.Value > 0).ToList();
        var result = new Dictionary<string, int>();
        long all = used.Sum(kv => (long)kv.Value);
        if (all == 0) return result;
        var rem = new List<(string code, long rem, int cells)>();
        int given = 0;
        foreach (var (code, cells) in used)
        {
            long num = (long)cells * total;
            int whole = (int)(num / all);
            result[code] = whole;
            given += whole;
            rem.Add((code, num % all, cells));
        }
        foreach (var r in rem.OrderByDescending(r => r.rem).ThenByDescending(r => r.cells).ThenBy(r => r.code, StringComparer.Ordinal).Take(Math.Max(0, total - given)))
            result[r.code]++;
        foreach (var code in result.Keys.ToList()) if (result[code] < 1) result[code] = 1;
        return result;
    }

    /// <summary>Twine per code of a cut of <paramref name="product"/> on the given loom family.</summary>
    public static Dictionary<string, int> TwineByCode(IReadOnlyDictionary<string, int> cellsByCode, ClothProduct product, bool flyingShuttle = false)
        => Split(cellsByCode, Total(product, flyingShuttle));
}
