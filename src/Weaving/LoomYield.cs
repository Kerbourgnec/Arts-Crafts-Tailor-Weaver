using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.HandLoom;
using ArtsAndCrafts.Tailor.Loom;
using ArtsAndCrafts.Tailor.Tapestry;
using Vintagestory.API.Common;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// Cut yield evidence (lane/loom-feedback-8 item 7, owner: flax-only gave 4 vanilla linen, a custom weave 1 woven linen; both
/// must give the same count): complete cloths woven with each loom's own rules (flying shuttle draft, tapestry painting,
/// hand loom rods), flax only and with a second colour, at 16 and 32. Pure builders for the selftest; <see cref="Summary"/>
/// runs the real <see cref="ClothOutput.CutStack"/> on the server (smoke log line).
/// </summary>
public static class LoomYield
{
    public const string Flax = LoomTwine.PlainTwineCode, Red = LoomTwine.DyedTwinePrefix + "red";
    const string FlaxHex = TwineColors.PlainHex, RedHex = "#9b3028";

    public static readonly string[] Looms = { "flyingshuttle", "tapestry", "handloom" };

    /// <summary>A complete <paramref name="size"/> cloth from loom <paramref name="loom"/> (see <see cref="Looms"/>); flax only, or a red weft / red cells.</summary>
    public static WeavingState Woven(string loom, int size, bool custom)
    {
        string weft = custom ? Red : Flax, weftHex = custom ? RedHex : FlaxHex;
        switch (loom)
        {
            case "tapestry":
            {
                var t = new TapestryWork(size);
                while (!t.Complete)
                {
                    t.Paint(0, Flax, FlaxHex, 1, fill: true);
                    if (custom) t.Paint(t.WovenRows % size, Red, RedHex, 1, fill: false);
                    t.Beat();
                }
                return t.Cloth;
            }
            case "handloom":
            {
                var h = new HandLoomWork(size, 2);
                h.SetWeft(weft, weftHex);
                while (!h.Complete)
                {
                    if (!h.RodLifted(h.WovenRows % 2)) h.ToggleRod(h.WovenRows % 2);
                    h.Beat();
                }
                return h.Cloth;
            }
            default:
            {
                var w = new WeavingState(size);
                w.EnsureWarp();
                byte idx = (byte)w.PaletteIndexFor(weft, weftHex);
                while (!w.Complete)
                {
                    w.OpenDefaultShed(idx);
                    if (w.Draft.CurrentTreadle == DraftState.NoTreadle) break;
                    w.CommitCurrentRow();
                }
                return w;
            }
        }
    }

    /// <summary>
    /// Server: the real cut stack of each loom x size x (flax / custom), as one line: "flyingshuttle 16 flax 1 x game:linen-normal-down, ...".
    /// </summary>
    public static string Summary(IWorldAccessor world)
    {
        var parts = new List<string>();
        foreach (string loom in Looms)
            foreach (int size in WeavingState.AllowedSizes)
                foreach (bool custom in new[] { false, true })
                {
                    var cloth = Woven(loom, size, custom);
                    var stack = ClothOutput.CutStack(world, cloth, loom == "flyingshuttle");
                    parts.Add($"{loom} {size} {(custom ? "custom" : "flax")} {(cloth.Complete ? "" : "INCOMPLETE ")}{stack?.StackSize ?? 0} x {stack?.Collectible?.Code}");
                }
        return string.Join(", ", parts);
    }

    /// <summary>Every flax and custom cut of one loom gives the same count at every size (selftest + smoke).</summary>
    public static bool SameCounts(IEnumerable<(string loom, int size, bool custom, int count)> cuts)
        => cuts.GroupBy(c => c.loom).All(g => g.Select(c => c.count).Distinct().Count() == 1);
}
