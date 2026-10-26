using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Common;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Sewing slot i of a mannequin = part i of the selected garment recipe. Holds one item (a filled part consumes 1)
/// of a material kind that part takes; no garment selected, or i beyond its parts: holds nothing.
/// </summary>
public class ItemSlotTailorMaterial : ItemSlot
{
    readonly int part;
    readonly System.Func<int, SlotMaterial?, bool> accepts;

    public ItemSlotTailorMaterial(InventoryBase inventory, int part, System.Func<int, SlotMaterial?, bool> accepts) : base(inventory)
    {
        this.part = part;
        this.accepts = accepts;
        MaxSlotStackSize = 1;
    }

    public override bool CanHold(ItemSlot sourceSlot)
        => sourceSlot.Itemstack != null && inventory?.Api != null && accepts(part, TailorMaterials.Read(inventory.Api, sourceSlot.Itemstack))
            && base.CanHold(sourceSlot);

    public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
        => CanHold(sourceSlot) && base.CanTakeFrom(sourceSlot, priority);
}

/// <summary>Reads what a stack is as a sewing material (both sides).</summary>
public static class TailorMaterials
{
    /// <summary>
    /// Patterned woven cloth or woven linen (lane/garment-families: linen is sewn too) → patterned cloth; catalog item
    /// or block (leather, pelt, vanilla linen / cloth-{color}, wood, metal, bone...) → its family + item code; anything
    /// else null.
    /// </summary>
    public static SlotMaterial? Read(ICoreAPI api, ItemStack? stack)
    {
        if (stack?.Collectible?.Code == null) return null;
        // lane/loom-embroidery: the cloth as shown (woven + embroidery). lane/pattern-density: at its own size, never
        // resampled (a 32 x 32 tweed read as 16 x 16 kept every other cell: a one-cell weave turned into stripes).
        if (WovenTexture.Composite(stack) is { } shown)
            return SlotMaterial.OfCloth(new ClothPattern(shown.Pattern, shown.Colors, shown.Width, shown.Height, shown.WarpUp));
        return MaterialCatalog.Get(api).ItemMaterial(stack.Collectible.Code.ToString());
    }
}
