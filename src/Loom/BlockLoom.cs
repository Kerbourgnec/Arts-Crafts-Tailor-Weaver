using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ArtsAndCrafts.Tailor.Loom;

/// <summary>
/// Fly-shuttle loom (3x2x2 multiblock), woven in-world. The controller block implements the vanilla
/// multiblock interfaces so every filler block returns the controller's full box list (shifted) and
/// forwards clicks with the same SelectionBoxIndex; the ray then hits the nearest box of the whole loom
/// in whichever block it enters first. Collision: coarse frame boxes (LoomGeometry.CollisionBoxes).
/// Plain <see cref="Block"/> for now; an auto-loom would derive from BlockMPBase instead (see docs/loom.md).
/// </summary>
public class BlockLoom : Block, IMultiBlockColSelBoxes, IMultiBlockInteract
{
    const string InteractionsCacheKey = TailorModSystem.Domain + "-loom-interactions";

    BlockEntityLoom? LoomAt(IBlockAccessor ba, BlockPos pos) => ba.GetBlockEntity(pos) as BlockEntityLoom;

    #region Selection

    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        => LoomAt(blockAccessor, pos)?.GetSelectionBoxes(null) ?? base.GetSelectionBoxes(blockAccessor, pos);

    public override bool DoPartialSelection(IWorldAccessor world, BlockPos pos) => true;

    public Cuboidf[] MBGetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos, Vec3i offset)
        => LoomAt(blockAccessor, pos.AddCopy(offset))?.GetSelectionBoxes(offset) ?? new[] { Cuboidf.Default() };

    /// <summary>Coarse frame boxes (no full cubes: the seated weaver's eye must not be inside a collision box), per facing and offset.</summary>
    Cuboidf[] CollisionFor(Vec3i? offset)
    {
        // Physics asks from several threads: thread-safe cache per offset (this block = one facing).
        var key = offset == null ? (0, 0, 0) : (offset.X, offset.Y, offset.Z);
        return collisionByOffset.GetOrAdd(key, k =>
            LoomGeometry.CollisionBoxes(LoomGeometry.GeometryRotateY(Shape?.rotateY ?? 0f)).Select(b => b.OffsetCopy(k.Item1, k.Item2, k.Item3)).ToArray());
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, int), Cuboidf[]> collisionByOffset = new();

    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos) => CollisionFor(null);

    public Cuboidf[] MBGetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos, Vec3i offset) => CollisionFor(offset);

    public bool MBDoPartialSelection(IWorldAccessor world, BlockPos pos, Vec3i offset) => true;

    #endregion

    #region Interaction

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

    public ItemStack MBOnPickBlock(IWorldAccessor world, BlockPos pos, Vec3i offset)
        => OnPickBlock(world, pos.AddCopy(offset));

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
            var (twine, cutters) = ObjectCacheUtil.GetOrCreate(capi, InteractionsCacheKey, () => (
                world.Collectibles.Where(LoomTwine.IsTwine).Select(c => new ItemStack(c)).ToArray(),
                world.Items.Where(LoomTwine.IsCutter).Select(c => new ItemStack(c)).ToArray()));
            AddHelp(help, be, be.PartAt(selection.SelectionBoxIndex), twine, cutters);
        }
        return help.ToArray().Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
    }

    /// <summary>Help for the hovered part; mirrors <see cref="BlockEntityLoom.OnInteract"/>.</summary>
    static void AddHelp(List<WorldInteraction> help, BlockEntityLoom be, LoomBoxPart hovered, ItemStack[] twine, ItemStack[] cutters)
    {
        var part = hovered.Part;
        string d = TailorModSystem.Domain + ":";
        void Add(string key, ItemStack[]? stacks = null, string? hotkey = null, bool freeHand = false)
            => help.Add(new WorldInteraction { ActionLangCode = d + key, MouseButton = EnumMouseButton.Right, Itemstacks = stacks, HotKeyCode = hotkey, RequireFreeHand = freeHand });

        bool complete = be.Weaving.Complete;
        // Square cloth (lane/square-cloth): cut when complete.
        if (be.Weaving.CanCut && part is LoomPart.Cell or LoomPart.Cloth or LoomPart.Beater or LoomPart.ClothBeam or LoomPart.HangRow) Add("loom-help-cut", cutters);
        bool shuttle = be.Shuttles.SelectedCode != null;
        switch (part)
        {
            // Draft mode (lane/draft-mode): rows come from the treadles; the warp is loaded at the back (lane/warp-shuttles-import).
            case LoomPart.Cell:
                if (!be.Weaving.WarpLocked && !complete) help.Add(new WorldInteraction { ActionLangCode = d + "loom-help-warpatback", MouseButton = EnumMouseButton.None });
                // The F picker is always there (lane/playtest-fixes-5): product, draft presets, brush.
                help.Add(new WorldInteraction { ActionLangCode = d + "loom-help-format", MouseButton = EnumMouseButton.None, HotKeyCode = LoomClientSystem.ToolModeHotkey });
                break;
            case LoomPart.Treadle when !complete:
                Add("loom-help-treadle", twine);
                if (shuttle) Add("loom-help-treadleshuttle", freeHand: true);
                break;
            case LoomPart.WarpThread:
            {
                // Stock (lane/loom-feedback-8 item 4): this thread's own twine goes onto the warp beam (sneak: the stack).
                string own = be.Weaving.WarpCode(hovered.Index);
                ItemStack[]? ownStack = be.Api?.World.GetItem(new AssetLocation(own)) is { } ownItem ? new[] { new ItemStack(ownItem) } : null;
                Add("loom-help-warpstock", ownStack);
                Add("loom-help-warpstockall", ownStack, "shift");
                if (be.WarpStock.Count(own) > 0) Add("loom-help-warpstocktake", hotkey: "shift", freeHand: true);
                if (!be.Weaving.WarpLocked && !complete)
                {
                    Add("loom-help-warpload", twine);
                    Add("loom-help-warploadall", twine, "shift");
                    Add("loom-help-warpclear", hotkey: "shift", freeHand: true);
                    help.Add(new WorldInteraction { ActionLangCode = d + "loom-help-format", MouseButton = EnumMouseButton.None, HotKeyCode = LoomClientSystem.ToolModeHotkey });
                }
                else help.Add(new WorldInteraction { ActionLangCode = d + "loom-help-warpfixed", MouseButton = EnumMouseButton.None });
                break;
            }
            case LoomPart.Cloth when !complete:
                Add("loom-help-copy");
                break;
            case LoomPart.Beater:
                if (!complete) Add("loom-help-weave");
                if (be.Weaving.WovenRows > 0) Add("loom-help-unweave", hotkey: "shift");
                break;
            case LoomPart.Shuttle:
                Add("loom-help-shuttleload", twine);
                Add("loom-help-shuttleloadall", twine, "shift");
                Add("loom-help-shuttleselect", freeHand: true);
                Add("loom-help-shuttletake", hotkey: "shift", freeHand: true);
                break;
            case LoomPart.ClothBeam:
                Add(be.Unrolled ? "loom-help-rollup" : "loom-help-unroll", hotkey: "shift");
                break;
            case LoomPart.HangRow:
                if (!complete) Add("loom-help-copy");
                Add("loom-help-rollup", hotkey: "shift");
                break;
            case LoomPart.Seat:
                Add(be.MountedBy != null ? "loom-help-standup" : "loom-help-sit");
                break;
        }
    }

    /// <summary>Mountable instancer (api.RegisterMountable): the loom's block entity at the stored controller position.</summary>
    public static IMountableSeat? GetMountable(IWorldAccessor world, TreeAttribute tree)
    {
        var pos = new BlockPos(tree.GetInt("posx"), tree.GetInt("posy"), tree.GetInt("posz"));
        return world.BlockAccessor.GetBlockEntity(pos) as BlockEntityLoom;
    }

    #endregion
}
