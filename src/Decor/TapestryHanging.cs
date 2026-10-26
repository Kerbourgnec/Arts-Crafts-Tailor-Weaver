using ArtsAndCrafts.Tailor.Pattern;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ArtsAndCrafts.Tailor.Decor;

/// <summary>
/// Place woven cloth or woven linen on a block face as a thin patterned panel (<c>actailor:tapestry-{side}</c>):
/// <list type="bullet">
/// <item>woven linen (lane/loom-feedback-6, owner: like vanilla linen): right click any of the 6 faces, no tool; sneaking too
/// (lane/loom-feedback-8: vanilla linen is a Decor block with no ground pile, so neither has woven linen);</item>
/// <item>woven cloth: with a hammer in the other hand (lane/cloth-decor), now also on floors and ceilings.</item>
/// </list>
/// One item is used up; breaking the panel gives it back (pattern intact). Vanilla linen and wallpaper are decors
/// (BlockBehaviorDecor) and decors have no block entity, so they cannot carry a per-cloth pattern; the panel is a thin
/// block with a block entity instead, like vanilla's tapestry. Unlike a decor it takes the block space in front of the
/// face (one panel per block). Floor and ceiling panels turn to the placer like the small carpet (BE turnDeg).
/// </summary>
public static class TapestryHanging
{
    public const string BlockCodePrefix = TailorModSystem.Domain + ":tapestry-";

    public static bool IsHammer(ItemSlot? slot) => slot?.Itemstack?.Collectible?.Tool == EnumTool.Hammer;
    public static bool IsWovenCloth(ItemSlot? slot) => slot?.Itemstack?.Collectible is ItemWovenCloth;
    public static bool IsWovenLinen(ItemSlot? slot) => slot?.Itemstack?.Collectible is ItemWovenLinen;

    /// <summary>
    /// Where a panel goes for a click on <paramref name="clickedFace"/> of a block: the block in front of that face,
    /// variant side = towards the clicked block (up = on a ceiling, down = on a floor). Every face (lane/loom-feedback-6).
    /// </summary>
    public static (Vec3i offset, string side)? Target(BlockFacing clickedFace) => (clickedFace.Normali, clickedFace.Opposite.Code);

    /// <summary>
    /// Mesh turn (deg, around Y) of a floor / ceiling panel placed by a player facing <paramref name="playerFacing"/>
    /// (Block.SuggestedHVOrientation): the small carpet's rotations (north 180, east 90, south 0, west 270), so the
    /// first woven row lies nearest the player. Walls: 0 (the variant's shape rotation already faces out).
    /// </summary>
    public static int TurnFor(string side, BlockFacing playerFacing)
    {
        if (side != "up" && side != "down") return 0;
        return playerFacing.Code switch { "north" => 180, "east" => 90, "west" => 270, _ => 0 };
    }

    /// <summary>
    /// Try to place the cloth / linen in <paramref name="clothSlot"/> on the clicked face. True when the click is ours
    /// (client: always for a placeable item and a face, the server decides and reports errors).
    /// <paramref name="sneakPlaces"/>: also while sneaking (woven linen, as vanilla linen); else sneak is left to the ground pile (woven cloth).
    /// </summary>
    public static bool TryHang(EntityAgent byEntity, ItemSlot clothSlot, BlockSelection? blockSel, bool sneakPlaces = false)
    {
        if (blockSel == null || !IsWovenCloth(clothSlot) || (byEntity.Controls.ShiftKey && !sneakPlaces)) return false;
        if (Target(blockSel.Face) is not { } target) return false;
        var world = byEntity.World;
        var player = (byEntity as EntityPlayer)?.Player;
        if (player == null) return false;

        BlockPos wall = blockSel.Position;
        BlockPos at = wall.AddCopy(target.offset.X, target.offset.Y, target.offset.Z);
        Block? tapestry = world.GetBlock(new AssetLocation(BlockCodePrefix + target.side));
        if (tapestry == null) return false;
        if (world.Side == EnumAppSide.Client) return true;

        var sp = player as IServerPlayer;
        // One block per cloth (lane/square-cloth: cloths are square, 16 x 16 or 32 x 32; no tall tapestries any more).
        if (!world.Claims.TryAccess(player, at, EnumBlockAccessFlags.BuildOrBreak)) return true;
        if (!world.BlockAccessor.GetBlock(at).IsReplacableBy(tapestry))
        {
            sp?.SendIngameError("notreplaceable", Lang.GetL(sp.LanguageCode, "placefailure-notreplaceable"));
            return true;
        }
        if (!world.BlockAccessor.GetBlock(wall).CanAttachBlockAt(world.BlockAccessor, tapestry, wall, blockSel.Face))
        {
            string code = blockSel.Face.IsHorizontal ? "requirehorizontalattachable" : blockSel.Face == BlockFacing.UP ? "requiresolidground" : "requireattachable";
            sp?.SendIngameError(code, Lang.GetL(sp.LanguageCode, "placefailure-" + code));
            return true;
        }

        ItemStack one = clothSlot.Itemstack!.Clone();
        one.StackSize = 1;
        world.BlockAccessor.SetBlock(tapestry.BlockId, at, one); // BE reads the cloth's pattern (OnBlockPlaced)
        int turn = TurnFor(target.side, Block.SuggestedHVOrientation(player, blockSel)[0]);
        if (turn != 0 && world.BlockAccessor.GetBlockEntity(at) is BlockEntityPatterned be) be.SetTurn(turn);
        if (player.WorldData.CurrentGameMode != EnumGameMode.Creative)
        {
            clothSlot.TakeOut(1);
            clothSlot.MarkDirty();
        }
        world.PlaySoundAt(new AssetLocation("game:sounds/block/cloth"), at.X + 0.5, at.Y + 0.5, at.Z + 0.5, null, true, 16, 1f);
        return true;
    }
}

/// <summary>On hammers (patch <c>patches/hammer-tapestry.json</c>): right click a block face with woven cloth in the off hand.</summary>
public class CollectibleBehaviorHangTapestry : CollectibleBehavior
{
    public CollectibleBehaviorHangTapestry(CollectibleObject collObj) : base(collObj) { }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        if (!firstEvent || !TapestryHanging.TryHang(byEntity, byEntity.LeftHandItemSlot, blockSel)) return;
        handHandling = EnumHandHandling.PreventDefault;
        handling = EnumHandling.PreventSubsequent;
    }
}
