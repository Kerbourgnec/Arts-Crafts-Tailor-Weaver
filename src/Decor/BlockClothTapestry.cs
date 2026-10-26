using System.Collections.Generic;
using ArtsAndCrafts.Tailor.Loom;
using ArtsAndCrafts.Tailor.Pattern;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ArtsAndCrafts.Tailor.Decor;

/// <summary>
/// Woven cloth or linen on a block face (<c>actailor:tapestry-{side}</c>, side = direction from the panel to the block it
/// hangs on, lane/loom-feedback-6: also up = ceiling, down = floor; walls as vanilla
/// HorizontalAttachable / tapestry). A thin patterned panel without collision, like wallpaper; placed only by
/// <see cref="TapestryHanging"/> (woven linen, or hammer + woven cloth). Breaking it, pick block, or losing the block
/// behind return the item.
/// <para>
/// One block per cloth (lane/square-cloth; the multi-block tall tapestries of lane/cloth-formats are gone). Parts of an
/// old tall tapestry in a dev world load as separate one-block tapestries showing the whole cloth (no migration).
/// </para>
/// </summary>
public class BlockClothTapestry : BlockPatterned
{
    public BlockFacing WallSide => BlockFacing.FromCode(Variant["side"]) ?? BlockFacing.NORTH;

    /// <summary>Code of the cloth item a tapestry gives back: the item it was hung from, woven cloth for older saves.</summary>
    public static string DropItemCode(string? sourceCode) => string.IsNullOrEmpty(sourceCode) ? BlockEntityLoom.WovenClothCode : sourceCode;

    /// <summary>The woven cloth it was made from (pattern in the cloth's own attribute layout).</summary>
    public override ItemStack StackWith(IWorldAccessor world, IReadOnlyList<PatternData?> patterns) => StackWith(world, patterns, null);

    /// <summary>The cloth item (<paramref name="sourceCode"/>: woven linen or woven cloth, lane/linen-output) with the pattern.</summary>
    public ItemStack StackWith(IWorldAccessor world, IReadOnlyList<PatternData?> patterns, string? sourceCode, Vintagestory.API.Datastructures.ITreeAttribute? sourceAttrs = null)
    {
        var item = world.GetItem(new AssetLocation(DropItemCode(sourceCode))) ?? world.GetItem(new AssetLocation(BlockEntityLoom.WovenClothCode));
        if (item == null) return base.StackWith(world, patterns);
        var stack = new ItemStack(item);
        stack.Attributes = ReturnedAttributes(sourceAttrs, patterns);
        return stack;
    }

    /// <summary>
    /// Attributes of the item a tapestry gives back: the placed item's own attributes when known (identical stack, it stacks with
    /// the original; lane/cloth-fixes), else (older saves) the pattern keys alone. Pure (selftest).
    /// </summary>
    public static Vintagestory.API.Datastructures.ITreeAttribute ReturnedAttributes(Vintagestory.API.Datastructures.ITreeAttribute? sourceAttrs, IReadOnlyList<PatternData?> patterns)
    {
        if (sourceAttrs != null) return sourceAttrs.Clone();
        var attrs = new Vintagestory.API.Datastructures.TreeAttribute();
        if (patterns.Count > 0 && patterns[0] is { } p) p.ToAttributes(attrs);
        return attrs;
    }

    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityPatterned be) return new[] { StackWith(world, be.Patterns, be.SourceCode, be.SourceAttrs) };
        return base.GetDrops(world, pos, byPlayer, dropQuantityMultiplier);
    }

    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityPatterned be) return StackWith(world, be.Patterns, be.SourceCode, be.SourceAttrs);
        return base.OnPickBlock(world, pos);
    }

    public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
    {
        base.OnNeighbourBlockChange(world, pos, neibpos);
        BlockPos wall = pos.AddCopy(WallSide);
        if (!neibpos.Equals(wall)) return;
        if (!world.BlockAccessor.GetBlock(wall).CanAttachBlockAt(world.BlockAccessor, this, wall, WallSide.Opposite))
        {
            world.BlockAccessor.BreakBlock(pos, null);
        }
    }
}
