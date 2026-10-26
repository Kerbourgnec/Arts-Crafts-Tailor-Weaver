using System;
using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ArtsAndCrafts.Tailor.Tapestry;

/// <summary>
/// Server-side loom basics shared by the simple looms (lane/tapestry-loom; meant for the hand looms too): twine names and
/// colours, the player's twine, cutting a finished <see cref="WeavingState"/> into woven linen with the pattern (or vanilla
/// linen for flax only; lane/linen-output, <see cref="ClothOutput"/>), charged by <see cref="ClothCost"/> from the loom's twine holders first, then hotbar + backpack.
/// Same rules as the Artisan loom's cut (src/Weaving/BlockEntityLoom.Weaving.cs), which keeps its own copy for now
/// (docs/tapestry-loom.md "Reuse"). Messages use the <c>loomcore-*</c> lang keys.
/// </summary>
public static class LoomCore
{
    public const string WovenClothCode = TailorModSystem.Domain + ":wovencloth";
    /// <summary>Squared distance (blocks) a player may be from the controller for a loom packet (as the Artisan loom).</summary>
    public const double MaxUseDistanceSq = 8 * 8;

    static string L(string langCode, string key, params object[] args) => Lang.GetL(langCode, TailorModSystem.Domain + ":" + key, args);

    /// <summary>Red in-game error for <paramref name="player"/> (lang key in our domain).</summary>
    public static void Fail(IServerPlayer player, string langKey, params object[] args)
        => player.SendIngameError("actailor-loom", L(player.LanguageCode, langKey, args));

    /// <summary>Chat notification for <paramref name="player"/> (lang key in our domain).</summary>
    public static void Tell(IServerPlayer player, string langKey, params object[] args)
        => player.SendMessage(GlobalConstants.GeneralChatGroup, L(player.LanguageCode, langKey, args), EnumChatType.Notification);

    /// <summary>Colour of a twine code as its item shows it (cells, bobbins).</summary>
    public static string TwineHex(IWorldAccessor world, string code)
    {
        var item = world.GetItem(new AssetLocation(code));
        return item != null ? TwineColors.HexOf(new ItemStack(item)) : TwineColors.PlainHex;
    }

    public static string TwineName(IWorldAccessor world, string code)
    {
        var item = world.GetItem(new AssetLocation(code));
        return item != null ? item.GetHeldItemName(new ItemStack(item)) : code;
    }

    /// <summary>Hotbar + backpack slots of the player (the inventories vanilla crafting draws from).</summary>
    public static IEnumerable<ItemSlot> PlayerSlots(IPlayer player)
    {
        foreach (string inv in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
        {
            var inventory = player.InventoryManager.GetOwnInventory(inv);
            if (inventory == null) continue;
            foreach (var slot in inventory) yield return slot;
        }
    }

    static bool Matches(ItemSlot slot, string code) => slot.Itemstack?.Collectible?.Code?.ToString() == code;

    public static int CountInInventory(IPlayer player, string code) => PlayerSlots(player).Where(s => Matches(s, code)).Sum(s => s.StackSize);

    public static void TakeFromInventory(IPlayer player, string code, int count)
    {
        foreach (var slot in PlayerSlots(player))
        {
            if (count <= 0) break;
            if (!Matches(slot, code)) continue;
            int take = Math.Min(count, slot.StackSize);
            slot.TakeOut(take);
            slot.MarkDirty();
            count -= take;
        }
    }

    /// <summary>Twine back to the player; whatever does not fit drops at their feet.</summary>
    public static void GiveTwine(IServerPlayer player, string code, int amount)
    {
        var item = player.Entity.World.GetItem(new AssetLocation(code));
        if (item == null || amount <= 0) return;
        GiveOrDrop(player, new ItemStack(item, amount));
    }

    public static void GiveOrDrop(IServerPlayer player, ItemStack stack)
    {
        player.InventoryManager.TryGiveItemstack(stack, true);
        if (stack.StackSize > 0) player.Entity.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
    }

    /// <summary>Twine items dropped at <paramref name="at"/> (a loom broken with twine on it).</summary>
    public static void DropTwine(IWorldAccessor world, Vec3d at, IEnumerable<(string code, int amount)> drops)
    {
        foreach (var (code, count) in drops)
        {
            var item = world.GetItem(new AssetLocation(code));
            if (item == null || count <= 0) continue;
            world.SpawnItemEntity(new ItemStack(item, count), at);
        }
    }

    /// <summary>Twine per code a cloth costs if cut now (<see cref="ClothCost"/>, flat per product, over the rows woven so far).</summary>
    public static Dictionary<string, int> CutCost(WeavingState cloth) => ClothCost.TwineByCode(cloth.CellsByCode(cloth.WovenCells), cloth.Product);

    /// <summary>
    /// What a cut of <paramref name="cloth"/> gives now (lane/linen-output, <see cref="ClothOutput.CutStack"/>): flax only →
    /// vanilla linen (x area); else <c>actailor:wovenlinen</c> with the pattern, size and draft. Null without the item.
    /// </summary>
    public static ItemStack? ClothStack(IWorldAccessor world, WeavingState cloth) => ClothOutput.CutStack(world, cloth);

    public enum ProductResult { Ok, Same, RowsOnLoom, Invalid }

    /// <summary>
    /// Server: weave <paramref name="product"/> on this loom from now on (lane/linen-output, F picker on an empty loom; the
    /// hand looms get it from here too). Only with no row woven; sends the error itself. The caller syncs (MarkDirty).
    /// </summary>
    public static ProductResult TrySetProduct(IServerPlayer player, WeavingState cloth, int product)
    {
        if (!ClothProducts.Valid(product)) return ProductResult.Invalid;
        var p = (ClothProduct)product;
        if (cloth.Product == p) return ProductResult.Same;
        if (!cloth.SetProduct(p)) { Fail(player, "loomcore-err-productrows"); return ProductResult.RowsOnLoom; }
        Tell(player, "loomcore-product-set", ProductName(player.LanguageCode, p), cloth.Width, cloth.Rows);
        return ProductResult.Ok;
    }

    /// <summary>Name of a product in <paramref name="langCode"/> (lang <c>loomproduct-*</c>).</summary>
    public static string ProductName(string langCode, ClothProduct p) => L(langCode, ClothProducts.LangKey(p));

    /// <summary>
    /// Server (lane/loom-embroidery): take <paramref name="cost"/> (twine per code) from <paramref name="holders"/> first,
    /// then hotbar + backpack, all or nothing (same stock rules as a cut). False (error sent, nothing taken) when any is short.
    /// </summary>
    public static bool TryCharge(IServerPlayer player, IReadOnlyDictionary<string, int> cost, SpoolRack? holders)
    {
        var world = player.Entity.World;
        var missing = cost.Select(kv => (code: kv.Key, need: kv.Value, have: (holders?.CountOf(kv.Key) ?? 0) + CountInInventory(player, kv.Key)))
            .Where(m => m.have < m.need).ToList();
        if (missing.Count > 0)
        {
            string list = string.Join(", ", missing.Select(m => L(player.LanguageCode, "loomcore-err-cutnotwine-line", TwineName(world, m.code), m.need, m.have)));
            Fail(player, "loomcore-err-chargenotwine", list);
            return false;
        }
        foreach (var (code, need) in cost)
        {
            int fromHolders = holders?.Take(code, need) ?? 0;
            TakeFromInventory(player, code, need - fromHolders);
        }
        return true;
    }

    public enum CutResult { Ok, NotComplete, NotCutter, MissingTwine, NoItem }

    /// <summary>
    /// Cut the complete cloth with the knife / shears in the active hotbar slot: charge its twine (holders first, then
    /// the player's hotbar + backpack; nothing is taken if any twine is short), give the cloth, wear the tool. Sends the
    /// error itself. The caller starts the next cloth on <see cref="CutResult.Ok"/>.
    /// </summary>
    public static CutResult TryCut(IServerPlayer player, WeavingState cloth, SpoolRack? holders)
    {
        if (!cloth.CanCut) { Fail(player, "loomcore-err-notcomplete", cloth.WovenRows, cloth.Rows); return CutResult.NotComplete; }
        ItemSlot slot = player.InventoryManager.ActiveHotbarSlot;
        if (!Loom.LoomTwine.IsCutter(slot)) return CutResult.NotCutter;
        var world = player.Entity.World;
        var stack = ClothStack(world, cloth);
        if (stack == null) return CutResult.NoItem;

        var cost = CutCost(cloth);
        var missing = cost.Select(kv => (code: kv.Key, need: kv.Value, have: (holders?.CountOf(kv.Key) ?? 0) + CountInInventory(player, kv.Key)))
            .Where(m => m.have < m.need).ToList();
        if (missing.Count > 0)
        {
            string list = string.Join(", ", missing.Select(m => L(player.LanguageCode, "loomcore-err-cutnotwine-line", TwineName(world, m.code), m.need, m.have)));
            Fail(player, "loomcore-err-cutnotwine", list);
            return CutResult.MissingTwine;
        }
        foreach (var (code, need) in cost)
        {
            int fromHolders = holders?.Take(code, need) ?? 0;
            TakeFromInventory(player, code, need - fromHolders);
        }
        GiveOrDrop(player, stack);
        slot.Itemstack?.Collectible.DamageItem(world, player.Entity, slot);
        slot.MarkDirty();
        return CutResult.Ok;
    }
}
