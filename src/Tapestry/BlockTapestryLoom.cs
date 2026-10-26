using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.Loom;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ArtsAndCrafts.Tailor.Tapestry;

/// <summary>
/// Tapestry loom (lane/tapestry-loom): upright 2x2x1 frame, crude (sticks + logs) or plank (13 woods). Same multiblock
/// pattern as the Artisan loom: the controller implements the vanilla multiblock interfaces, so every filler returns
/// the controller's box list (shifted) and forwards clicks with the same SelectionBoxIndex. Help per hovered part.
/// </summary>
public class BlockTapestryLoom : Block, IMultiBlockColSelBoxes, IMultiBlockInteract
{
    const string HelpCacheKey = TailorModSystem.Domain + "-tapestryloom-interactions";

    static BlockEntityTapestryLoom? LoomAt(IBlockAccessor ba, BlockPos pos) => ba.GetBlockEntity(pos) as BlockEntityTapestryLoom;

    float RotY => Shape?.rotateY ?? 0f;

    readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, int), Cuboidf[]> collisionByOffset = new();

    Cuboidf[] CollisionFor(Vec3i? offset)
    {
        var key = offset == null ? (0, 0, 0) : (offset.X, offset.Y, offset.Z);
        return collisionByOffset.GetOrAdd(key, k => TapestryGeometry.CollisionBoxes(RotY).Select(b => b.OffsetCopy(k.Item1, k.Item2, k.Item3)).ToArray());
    }

    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        => LoomAt(blockAccessor, pos)?.GetSelectionBoxes(null) ?? base.GetSelectionBoxes(blockAccessor, pos);

    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos) => CollisionFor(null);

    public override bool DoPartialSelection(IWorldAccessor world, BlockPos pos) => true;

    public Cuboidf[] MBGetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos, Vec3i offset)
        => LoomAt(blockAccessor, pos.AddCopy(offset))?.GetSelectionBoxes(offset) ?? new[] { Cuboidf.Default() };

    public Cuboidf[] MBGetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos, Vec3i offset) => CollisionFor(offset);

    public bool MBDoPartialSelection(IWorldAccessor world, BlockPos pos, Vec3i offset) => true;

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (blockSel == null) return false;
        if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.Use)) return false;
        var be = LoomAt(world.BlockAccessor, blockSel.Position);
        if (be != null && be.OnInteract(byPlayer, blockSel)) return true;
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    public bool MBOnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, Vec3i offset)
        => OnBlockInteractStart(world, byPlayer, blockSel.AddPosCopy(offset));

    public bool MBOnBlockInteractStep(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, Vec3i offset)
        => OnBlockInteractStep(secondsUsed, world, byPlayer, blockSel.AddPosCopy(offset));

    public void MBOnBlockInteractStop(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, Vec3i offset)
        => OnBlockInteractStop(secondsUsed, world, byPlayer, blockSel.AddPosCopy(offset));

    public bool MBOnBlockInteractCancel(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, EnumItemUseCancelReason cancelReason, Vec3i offset)
        => OnBlockInteractCancel(secondsUsed, world, byPlayer, blockSel.AddPosCopy(offset), cancelReason);

    public ItemStack MBOnPickBlock(IWorldAccessor world, BlockPos pos, Vec3i offset) => OnPickBlock(world, pos.AddCopy(offset));

    public WorldInteraction[] MBGetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection blockSel, IPlayer forPlayer, Vec3i offset)
        => GetPlacedBlockInteractionHelp(world, blockSel.AddPosCopy(offset), forPlayer);

    public BlockSounds MBGetSounds(IBlockAccessor blockAccessor, BlockSelection blockSel, ItemStack stack, Vec3i offset)
        => GetSounds(blockAccessor, blockSel.AddPosCopy(offset), stack);

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        var be = LoomAt(world.BlockAccessor, selection.Position);
        var help = new List<WorldInteraction>();
        if (be != null && world.Api is ICoreClientAPI capi)
        {
            var (twine, cutters) = ObjectCacheUtil.GetOrCreate(capi, HelpCacheKey, () => (
                world.Collectibles.Where(LoomTwine.IsTwine).Select(c => new ItemStack(c)).ToArray(),
                world.Items.Where(LoomTwine.IsCutter).Select(c => new ItemStack(c)).ToArray()));
            var cloths = ObjectCacheUtil.GetOrCreate(capi, HelpCacheKey + "-cloths", () => new[] { "actailor:wovenlinen", "actailor:wovencloth", "game:linen-normal-down", "game:cloth-plain" }
                .Select(c => (CollectibleObject?)world.GetItem(new AssetLocation(c)) ?? world.GetBlock(new AssetLocation(c))).Where(c => c != null).Select(c => new ItemStack(c)).ToArray());
            AddHelp(help, be, be.PartAt(selection.SelectionBoxIndex).Part, twine, cutters, cloths);
        }
        return help.ToArray().Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
    }

    /// <summary>Lang keys of the help per part (mirrors <see cref="BlockEntityTapestryLoom.OnInteract"/>); public for the selftest.</summary>
    public static IEnumerable<string> HelpKeys(TapestryPart part, bool complete, bool canCut, bool woven, bool bobbinActive, bool embroidering = false, bool canMount = false)
    {
        // Embroidery (lane/loom-embroidery): a cloth on the frame; its cells only exist then.
        if (embroidering || part == TapestryPart.EmbCell)
        {
            switch (part)
            {
                case TapestryPart.EmbCell:
                    yield return "tapestryloom-help-embpaint";
                    if (bobbinActive) yield return "tapestryloom-help-embpaintbobbin";
                    yield return "tapestryloom-help-embpickout";
                    yield return "tapestryloom-help-brush";
                    yield return "tapestryloom-help-embtakeoff";
                    break;
                case TapestryPart.Beam:
                    yield return "tapestryloom-help-embtakeoff";
                    yield return "tapestryloom-help-embtakeoffhand";
                    break;
                case TapestryPart.Bobbin:
                    foreach (var k in HelpKeys(part, complete, canCut, woven, bobbinActive)) yield return k;
                    break;
            }
            yield break;
        }
        if (canMount && part is TapestryPart.Cell or TapestryPart.Row or TapestryPart.Beam) yield return "tapestryloom-help-embmount";
        if (canCut && part is TapestryPart.Cell or TapestryPart.Row or TapestryPart.Beam) yield return "tapestryloom-help-cut";
        switch (part)
        {
            case TapestryPart.Cell when !complete:
                yield return "tapestryloom-help-paint";
                yield return "tapestryloom-help-fill";
                if (bobbinActive) { yield return "tapestryloom-help-paintbobbin"; yield return "tapestryloom-help-fillbobbin"; }
                yield return "tapestryloom-help-brush";
                break;
            case TapestryPart.Row when !complete:
                yield return "tapestryloom-help-copy";
                break;
            case TapestryPart.Beam:
                if (!complete) yield return "tapestryloom-help-beat";
                if (woven) yield return "tapestryloom-help-rollback";
                break;
            case TapestryPart.Bobbin:
                yield return "tapestryloom-help-bobbinload";
                yield return "tapestryloom-help-bobbinloadall";
                yield return "tapestryloom-help-bobbinselect";
                yield return "tapestryloom-help-bobbintake";
                break;
        }
    }

    static void AddHelp(List<WorldInteraction> help, BlockEntityTapestryLoom be, TapestryPart part, ItemStack[] twine, ItemStack[] cutters, ItemStack[] cloths)
    {
        var cloth = be.Cloth;
        string d = TailorModSystem.Domain + ":";
        foreach (string key in HelpKeys(part, cloth.Complete, cloth.CanCut, cloth.WovenRows > 0, be.Work.Bobbins.SelectedCode != null, be.Work.Hoop != null, be.Work.CanMount))
        {
            var wi = new WorldInteraction { ActionLangCode = d + key, MouseButton = EnumMouseButton.Right };
            switch (key)
            {
                case "tapestryloom-help-embmount": wi.Itemstacks = cloths; break;
                case "tapestryloom-help-embpaint": wi.Itemstacks = twine; break;
                case "tapestryloom-help-embpaintbobbin": case "tapestryloom-help-embtakeoffhand": wi.RequireFreeHand = true; break;
                case "tapestryloom-help-embpickout": wi.HotKeyCode = "shift"; break;
                case "tapestryloom-help-embtakeoff": wi.Itemstacks = cutters; break;
                case "tapestryloom-help-cut": wi.Itemstacks = cutters; break;
                case "tapestryloom-help-paint": case "tapestryloom-help-copy": case "tapestryloom-help-bobbinload": wi.Itemstacks = twine; break;
                case "tapestryloom-help-fill": case "tapestryloom-help-bobbinloadall": wi.Itemstacks = twine; wi.HotKeyCode = "shift"; break;
                case "tapestryloom-help-paintbobbin": case "tapestryloom-help-bobbinselect": wi.RequireFreeHand = true; break;
                case "tapestryloom-help-rollback": wi.HotKeyCode = "shift"; break;
                case "tapestryloom-help-fillbobbin": case "tapestryloom-help-bobbintake":
                    wi.RequireFreeHand = true; wi.HotKeyCode = "shift"; break;
                case "tapestryloom-help-brush": wi.MouseButton = EnumMouseButton.None; wi.HotKeyCode = LoomClientSystem.ToolModeHotkey; break;
            }
            help.Add(wi);
        }
    }
}
