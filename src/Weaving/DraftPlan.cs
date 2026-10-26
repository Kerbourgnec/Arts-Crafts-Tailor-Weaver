using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// An imported draft on the loom (lane/warp-shuttles-import, chat command <c>/actailor draft &lt;code&gt;</c>): the warp
/// the cloth needs (twine code per end), the threading (shaft per end), the tie-up (lift mask per board treadle) and the
/// plan per row (treadle, weft twine code). The warp is a requirement: the player still loads each thread at the back.
/// Pure data (selftest), part of <see cref="DraftState"/> (BE tree <c>weaving.draft.plan</c>).
/// <para>
/// 6-treadle codes (designer "2/2 twill + tabby" etc.): accepted when the treadles the treadling uses lift at most 4
/// different shaft sets (those are put on the 4 board treadles, first used treadle first); else refused
/// (<c>draft-err-treadles</c>). 4-treadle codes keep their treadle numbers.
/// </para>
/// </summary>
public sealed class DraftPlan
{
    public string Code = "";
    public string[] Warp = Array.Empty<string>();
    public int[] Threading = Array.Empty<int>();
    /// <summary>Lift mask per board treadle (4; bit s = shaft s + 1).</summary>
    public int[] TieUp = new int[DraftPresets.Treadles];
    public int[] Treadling = Array.Empty<int>();
    public string[] Weft = Array.Empty<string>();
    /// <summary>Treadles of the source code (4 or 6); 6 = remapped onto the 4 board treadles.</summary>
    public int SourceTreadles = DraftPresets.Treadles;

    /// <summary>Warp ends = rows (square).</summary>
    public int Size => Warp.Length;

    /// <summary>Planned treadle of row <paramref name="row"/> (0-based), or -1 without a plan; a large carpet's 2N rows repeat the N-row plan (lane/linen-output).</summary>
    public int TreadleAt(int row) => row >= 0 && Treadling.Length > 0 ? Treadling[row % Treadling.Length] : -1;

    /// <summary>Planned weft twine of row <paramref name="row"/> (repeating, as <see cref="TreadleAt"/>), or null without a plan.</summary>
    public string? WeftAt(int row) => row >= 0 && Weft.Length > 0 ? Weft[row % Weft.Length] : null;

    /// <summary>
    /// The plan for a decoded code on a loom weaving <paramref name="clothSize"/> cloths (the world's size); null with
    /// <paramref name="error"/> when the size differs, it needs more than 4 shafts or 4 distinct treadles, or a treadle
    /// it uses lifts no end or every end (no shed).
    /// </summary>
    /// <para><paramref name="checkFromRow"/> (lane/draft-window): rows before it are already woven (their treadles were
    /// pressed under the tie-up of that time), so only later rows must open a shed.</para>
    public static DraftPlan? FromCode(DraftCodeData d, int clothSize, string code, out DraftCode.Error? error, int checkFromRow = 0)
    {
        error = null;
        if (d.N != clothSize) { error = DraftCode.Error.Of("draft-err-size", d.N, clothSize); return null; }
        if (d.S > DraftPresets.Shafts) { error = DraftCode.Error.Of("draft-err-shafts", d.S); return null; }
        int[] map = Enumerable.Range(0, d.T).ToArray();
        int[] tie = new int[DraftPresets.Treadles];
        if (d.T <= DraftPresets.Treadles)
        {
            for (int t = 0; t < d.T; t++) tie[t] = d.TieUp[t];
        }
        else
        {
            // Treadles the treadling uses, by number; equal lift masks share a board treadle.
            var masks = new List<int>();
            foreach (int t in d.Treadling.Distinct().OrderBy(t => t))
            {
                int k = masks.IndexOf(d.TieUp[t]);
                if (k < 0) { masks.Add(d.TieUp[t]); k = masks.Count - 1; }
                map[t] = k;
            }
            if (masks.Count > DraftPresets.Treadles) { error = DraftCode.Error.Of("draft-err-treadles", d.T, masks.Count); return null; }
            for (int k = 0; k < masks.Count; k++) tie[k] = masks[k];
        }
        // Every treadle the treadling uses must open a shed on this threading (the designer only warns, research §5.2).
        for (int y = System.Math.Max(0, checkFromRow); y < d.N; y++)
        {
            int mask = tie[map[d.Treadling[y]]];
            int up = d.Threading.Count(s => ((mask >> s) & 1) != 0);
            if (up == 0 || up == d.N) { error = DraftCode.Error.Of("draft-err-noshed", d.Treadling[y] + 1, y + 1); return null; }
        }
        return new DraftPlan
        {
            Code = code,
            Warp = d.Warp.Select(c => DraftCode.PaletteCodes[c]).ToArray(),
            Threading = (int[])d.Threading.Clone(),
            TieUp = tie,
            Treadling = d.Treadling.Select(t => map[t]).ToArray(),
            Weft = d.Weft.Select(c => DraftCode.PaletteCodes[c]).ToArray(),
            SourceTreadles = d.T,
        };
    }

    /// <summary>Decode + map in one go (the chat command); null with <paramref name="error"/> on any problem.</summary>
    public static DraftPlan? Parse(string? code, int clothSize, out DraftCode.Error? error)
    {
        var d = DraftCode.Decode(code, out error);
        return d == null ? null : FromCode(d, clothSize, (code ?? "").Trim(), out error);
    }

    /// <summary>
    /// The plan as designer data (4 shafts, 4 treadles; lane/draft-window), or null when a warp or weft twine is not in the
    /// designer palette (<see cref="DraftCode.PaletteCodes"/>).
    /// </summary>
    public DraftCodeData? ToCodeData()
    {
        int[] Idx(string[] codes) => codes.Select(c => System.Array.IndexOf(DraftCode.PaletteCodes, c)).ToArray();
        int[] warp = Idx(Warp), weft = Idx(Weft);
        if (warp.Any(i => i < 0) || weft.Any(i => i < 0)) return null;
        return new DraftCodeData(Size, DraftPresets.Shafts, DraftPresets.Treadles, warp, (int[])Threading.Clone(), (int[])TieUp.Clone(),
            (int[])Treadling.Clone(), weft);
    }

    public void ToTree(ITreeAttribute tree)
    {
        tree.SetString("code", Code);
        tree["warp"] = new StringArrayAttribute(Warp);
        tree["threading"] = new IntArrayAttribute(Threading);
        tree["tieup"] = new IntArrayAttribute(TieUp);
        tree["treadling"] = new IntArrayAttribute(Treadling);
        tree["weft"] = new StringArrayAttribute(Weft);
        tree.SetInt("sourceTreadles", SourceTreadles);
    }

    /// <summary>Null when the tree is missing or inconsistent (lengths, ranges).</summary>
    public static DraftPlan? FromTree(ITreeAttribute? tree)
    {
        if (tree == null) return null;
        string[] S(string k) => (tree[k] as StringArrayAttribute)?.value ?? Array.Empty<string>();
        int[] I(string k) => (tree[k] as IntArrayAttribute)?.value ?? Array.Empty<int>();
        var p = new DraftPlan
        {
            Code = tree.GetString("code", "") ?? "",
            Warp = S("warp"), Threading = I("threading"), TieUp = I("tieup"), Treadling = I("treadling"), Weft = S("weft"),
            SourceTreadles = tree.GetInt("sourceTreadles", DraftPresets.Treadles),
        };
        int n = p.Warp.Length;
        bool ok = WeavingState.IsAllowedSize(n) && p.Threading.Length == n && p.Treadling.Length == n && p.Weft.Length == n
            && p.TieUp.Length == DraftPresets.Treadles && p.Threading.All(s => s >= 0 && s < DraftPresets.Shafts)
            && p.Treadling.All(t => t >= 0 && t < DraftPresets.Treadles) && p.TieUp.All(m => m >= 0 && m < 1 << DraftPresets.Shafts);
        return ok ? p : null;
    }
}
