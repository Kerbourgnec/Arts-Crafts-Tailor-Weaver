using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.Loom;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ArtsAndCrafts.Tailor.HandLoom;

/// <summary>
/// Hand loom (lane/hand-loom): low horizontal ground loom in one block (lane/loom-feedback-8; was 2 x 1 x 3); crude
/// (sticks + logs, 2 heddle rods) or plank (13 woods, 3 rods). The vanilla multiblock interfaces stay implemented so the
/// filler blocks of a loom placed before (dev worlds) still return the controller's boxes and forward clicks. Help per hovered part.
/// </summary>
public class BlockHandLoom : Block, IMultiBlockColSelBoxes, IMultiBlockInteract
{
    const string HelpCacheKey = TailorModSystem.Domain + "-handloom-interactions";

    static BlockEntityHandLoom? LoomAt(IBlockAccessor ba, BlockPos pos) => ba.GetBlockEntity(pos) as BlockEntityHandLoom;

    float RotY => Shape?.rotateY ?? 0f;

    readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, int), Cuboidf[]> collisionByOffset = new();

    Cuboidf[] CollisionFor(Vec3i? offset)
    {
        var key = offset == null ? (0, 0, 0) : (offset.X, offset.Y, offset.Z);
        return collisionByOffset.GetOrAdd(key, k => HandLoomGeometry.CollisionBoxes(RotY).Select(b => b.OffsetCopy(k.Item1, k.Item2, k.Item3)).ToArray());
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
            var w = be.Work;
            foreach (string key in HelpKeys(be.PartAt(selection.SelectionBoxIndex).Part, w.Complete, w.CanCut, w.WovenRows > 0, w.WarpFree, w.PickUsed))
                help.Add(Interaction(key, twine, cutters));
        }
        return help.ToArray().Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
    }

    /// <summary>Lang keys of the help per part (mirrors <see cref="BlockEntityHandLoom.OnInteract"/>); public for the selftest.</summary>
    public static IEnumerable<string> HelpKeys(HandLoomPart part, bool complete, bool canCut, bool woven, bool warpFree, bool pickUsed)
    {
        if (canCut && part is HandLoomPart.Thread or HandLoomPart.Row or HandLoomPart.Beam) yield return "handloom-help-cut";
        switch (part)
        {
            case HandLoomPart.Thread when !complete:
                yield return "handloom-help-pick";
                if (pickUsed && !warpFree) yield return "handloom-help-pickclear";
                if (warpFree)
                {
                    yield return "handloom-help-warpload";
                    yield return "handloom-help-warploadall";
                    yield return "handloom-help-warpclear";
                    yield return "handloom-help-brush";
                }
                break;
            case HandLoomPart.Row when !complete:
                yield return "handloom-help-copy";
                break;
            case HandLoomPart.Beam:
                if (!complete) yield return "handloom-help-beat";
                if (woven) yield return "handloom-help-rollback";
                break;
            case HandLoomPart.Rod when !complete:
                yield return "handloom-help-rod";
                break;
            case HandLoomPart.Shuttle:
                yield return "handloom-help-shuttleload";
                yield return "handloom-help-shuttleloadall";
                yield return "handloom-help-shuttleselect";
                yield return "handloom-help-shuttletake";
                break;
            case HandLoomPart.Frame:
                break;
        }
        if (part != HandLoomPart.Frame && part != HandLoomPart.Shuttle) yield return "handloom-help-settings";
    }

    static WorldInteraction Interaction(string key, ItemStack[] twine, ItemStack[] cutters)
    {
        var wi = new WorldInteraction { ActionLangCode = TailorModSystem.Domain + ":" + key, MouseButton = EnumMouseButton.Right };
        switch (key)
        {
            case "handloom-help-cut": wi.Itemstacks = cutters; break;
            case "handloom-help-warpload": case "handloom-help-shuttleload": wi.Itemstacks = twine; break;
            case "handloom-help-warploadall": case "handloom-help-shuttleloadall": wi.Itemstacks = twine; wi.HotKeyCode = "shift"; break;
            case "handloom-help-pick": case "handloom-help-shuttleselect": wi.RequireFreeHand = true; break;
            case "handloom-help-pickclear": case "handloom-help-rollback": wi.HotKeyCode = "shift"; break;
            case "handloom-help-warpclear": case "handloom-help-shuttletake": wi.RequireFreeHand = true; wi.HotKeyCode = "shift"; break;
            case "handloom-help-brush": wi.MouseButton = EnumMouseButton.None; wi.HotKeyCode = LoomClientSystem.ToolModeHotkey; break;
            case "handloom-help-settings": wi.MouseButton = EnumMouseButton.None; wi.HotKeyCode = LoomClientSystem.ToolModeHotkey; wi.RequireFreeHand = true; break;
        }
        return wi;
    }
}
