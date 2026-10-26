using System;
using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.Loom;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// What a loom cut gives (lane/linen-output, owner proposal reviewed 2026-10-20; docs/weaving.md "Linen output"):
/// <list type="bullet">
/// <item>every cell plain flax twine → vanilla <c>game:linen-normal-down</c>, 4 per cut at any size (no pattern data);</item>
/// <item>anything else → <c>actailor:wovenlinen</c> with the pattern (same attributes as woven cloth), as many as the flax
/// cut gives vanilla linen (lane/loom-feedback-6): a stack of identical items, each carrying the whole pattern.</item>
/// </list>
/// The grid then finishes woven linen with a stick (as vanilla linen + stick → cloth): one dyed twine colour that vanilla
/// cloth has → <c>game:cloth-{color}</c> (1 per linen item, one stick); gold or mixed → <c>actailor:wovencloth</c> with the
/// pattern copied (<c>copyAttributesFrom</c>). Recipes: <c>recipes/grid/linen.json</c> (scripts/linen-recipes.py).
/// Woven carpets (<see cref="WeavingState.Product"/>, lane/linen-output) cut to the carpet block directly, whatever the twine.
/// The rules are pure (selftest LinenChecks, CarpetChecks); <see cref="CutStack"/> builds the item.
/// </summary>
public static class ClothOutput
{
    public const string WovenLinenCode = TailorModSystem.Domain + ":wovenlinen";
    public const string WovenClothCode = TailorModSystem.Domain + ":wovencloth";
    public const string VanillaLinenCode = "game:linen-normal-down";
    public const string VanillaClothPrefix = "game:cloth-";
    /// <summary>Our twine colours (<c>actailor:twine-{color}</c>) that vanilla cloth also has (<c>game:cloth-{color}</c>); gold has none.</summary>
    public static readonly string[] VanillaClothColors =
        { "mordant", "blue", "red", "yellow", "green", "purple", "pink", "orange", "brown", "gray", "black", "white" };
    /// <summary>Recipe attribute of the stick recipes: a colour of <see cref="VanillaClothColors"/>, or <see cref="CustomKey"/>.</summary>
    public const string RecipeAttr = "linenCloth";
    public const string CustomKey = "custom";

    public enum Kind { VanillaLinen, WovenLinen }

    /// <summary>Pieces one cut gives on the flying shuttle loom, whatever the cloth size (lane/cost-v2: 12 twine = 4 pieces).</summary>
    public const int PiecesPerCut = 4;
    /// <summary>Pieces one cut gives on the tapestry and hand looms (3 twine = 1 piece).</summary>
    public const int PiecesPerCutHandLoom = 1;

    /// <summary>
    /// Items a cut of <paramref name="cloth"/> gives (lane/loom-feedback-8 item 7, one rule for every loom and twine): a carpet
    /// product 1 block; vanilla linen (flax only) and woven linen (anything else) alike <see cref="PiecesPerCut"/> on the flying shuttle
    /// loom, <see cref="PiecesPerCutHandLoom"/> on the others (same at every cloth size).
    /// </summary>
    public static int CutCount(WeavingState cloth, bool flyingShuttle = false)
        => ClothProducts.CarpetBlockCode(cloth.Product) != null ? 1 : flyingShuttle ? PiecesPerCut : PiecesPerCutHandLoom;

    /// <summary>Every cell plain flax (codes = the twines the cloth uses).</summary>
    public static bool FlaxOnly(IReadOnlyCollection<string> codes) => codes.Count > 0 && codes.All(c => c == LoomTwine.PlainTwineCode);

    /// <summary>The cut's kind for a cloth using <paramref name="codes"/>.</summary>
    public static Kind ForCut(IReadOnlyCollection<string> codes) => FlaxOnly(codes) ? Kind.VanillaLinen : Kind.WovenLinen;

    /// <summary>The one vanilla cloth colour of a cloth using only <c>actailor:twine-{color}</c> of that colour, else null.</summary>
    public static string? SingleVanillaColor(IReadOnlyCollection<string> codes)
    {
        var distinct = codes.Distinct().ToList();
        if (distinct.Count != 1 || !distinct[0].StartsWith(LoomTwine.DyedTwinePrefix)) return null;
        string color = distinct[0].Substring(LoomTwine.DyedTwinePrefix.Length);
        return Array.IndexOf(VanillaClothColors, color) >= 0 ? color : null;
    }

    /// <summary>Twine codes a woven linen / cloth stack's pattern uses (palette entries with at least one cell; embroidery included).</summary>
    public static string[] UsedCodes(ITreeAttribute attrs)
    {
        // As shown (lane/loom-embroidery): stitches count, so an embroidered one-colour linen is custom.
        var shown = WovenTexture.Composite(attrs);
        string[] codes = shown?.Codes ?? (attrs[ItemWovenCloth.AttrCodes] as StringArrayAttribute)?.value ?? Array.Empty<string>();
        byte[] pattern = shown?.Pattern ?? attrs.GetBytes(ItemWovenCloth.AttrPattern) ?? Array.Empty<byte>();
        var used = new HashSet<byte>(pattern);
        return codes.Where((_, i) => used.Contains((byte)i)).ToArray();
    }

    /// <summary>Stick recipe key of a woven linen stack: its vanilla cloth colour, or <see cref="CustomKey"/>.</summary>
    public static string StickKey(ITreeAttribute attrs) => SingleVanillaColor(UsedCodes(attrs)) ?? CustomKey;

    /// <summary>Item code woven linen + stick gives: <c>game:cloth-{color}</c> or <c>actailor:wovencloth</c>.</summary>
    public static string StickOutputCode(ITreeAttribute attrs)
        => StickKey(attrs) is var k && k != CustomKey ? VanillaClothPrefix + k : WovenClothCode;

    /// <summary>
    /// Patterns of a carpet woven as one cloth: the block's <c>splitCloth</c> tiles at native size (large carpet: tile0 =
    /// rows N..2N-1, far), else the whole cloth as its one pattern. Pure (selftest).
    /// </summary>
    public static Pattern.PatternData?[] CarpetPatterns(Pattern.PatternData whole, int patternCount,
        (int cols, int rows, (int col, int row)[] tiles)? split)
    {
        if (split is { } sp && patternCount > 1) return Pattern.PatternData.Split(whole, sp.cols, sp.rows, sp.tiles, Math.Max(1, whole.Width / sp.cols));
        return new Pattern.PatternData?[] { whole };
    }

    /// <summary>
    /// The stack a cut of <paramref name="cloth"/> gives (see the class rules), or null when the item is missing.
    /// Woven linen carries the pattern, size and draft like woven cloth did before lane/linen-output.
    /// </summary>
    public static ItemStack? CutStack(IWorldAccessor world, WeavingState cloth, bool flyingShuttle = false)
    {
        var (pattern, codes, colors) = cloth.ExportCompacted();
        // Woven carpets (lane/linen-output): the carpet block itself, its patterns from the cloth (large: two N x N halves).
        if (ClothProducts.CarpetBlockCode(cloth.Product) is { } carpetCode)
        {
            if (world.GetBlock(new AssetLocation(carpetCode)) is not Pattern.BlockPatterned carpet) return null;
            var whole = new Pattern.PatternData(pattern, colors, codes, cloth.Width, cloth.Rows, cloth.ExportDraft()?.WarpUp());
            var carpetStack = new ItemStack(carpet);
            Pattern.PatternData.WriteList(carpetStack.Attributes, CarpetPatterns(whole, carpet.PatternCount, carpet.SplitCloth));
            return carpetStack;
        }
        if (ForCut(codes) == Kind.VanillaLinen)
        {
            Block? linen = world.GetBlock(new AssetLocation(VanillaLinenCode));
            return linen == null ? null : new ItemStack(linen, CutCount(cloth, flyingShuttle));
        }
        Item? item = world.GetItem(new AssetLocation(WovenLinenCode));
        if (item == null) return null;
        // Same yield as the flax-only cut (lane/loom-feedback-6, owner): one woven linen per vanilla piece, each with the whole pattern.
        var stack = new ItemStack(item, CutCount(cloth, flyingShuttle));
        ItemWovenCloth.WritePattern(stack, pattern, codes, colors, null, cloth.Width, cloth.Rows);
        if (cloth.ExportDraft() is { } draft) ItemWovenCloth.WriteDraft(stack, draft);
        return stack;
    }
}
