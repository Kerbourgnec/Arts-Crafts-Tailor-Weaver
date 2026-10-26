using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// Patterned linen cut from a loom (<c>actailor:wovenlinen</c>, lane/linen-output): same stack attributes, rendering, shelf
/// and panel placing as <see cref="ItemWovenCloth"/>; no ground pile (lane/loom-feedback-8, as vanilla linen). Not a decor or tailoring material: a stick in the grid finishes
/// it (<see cref="ClothOutput"/>): one vanilla cloth colour → <c>game:cloth-{color}</c>, else woven cloth with the pattern.
/// Stick recipes filter on ingredient <c>recipeAttributes.linenCloth</c> (colour or "custom"), so exactly one matches.
/// </summary>
public class ItemWovenLinen : ItemWovenCloth
{
    /// <summary>Recipe filter: <c>linenCloth</c> must equal <see cref="ClothOutput.StickKey"/>; then the size ranges (base).</summary>
    public override bool MatchesForCrafting(ItemStack inputStack, IRecipeBase recipe, IRecipeIngredient ingredient)
    {
        var ra = ingredient.RecipeAttributes;
        if (ra != null && ra.Exists && ra[ClothOutput.RecipeAttr].AsString(null) is { } want && want != ClothOutput.StickKey(inputStack.Attributes))
            return false;
        return base.MatchesForCrafting(inputStack, recipe, ingredient);
    }

    /// <summary>
    /// Right click any block face: a thin patterned panel there, like vanilla linen (lane/loom-feedback-6;
    /// <see cref="Decor.TapestryHanging"/>, no hammer). Sneaking too, and no ground pile (lane/loom-feedback-8: vanilla
    /// linen is a Decor block without GroundStorable; the item type has no GroundStorable behaviour either).
    /// </summary>
    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        if (firstEvent && Decor.TapestryHanging.TryHang(byEntity, slot, blockSel, sneakPlaces: true))
        {
            handling = EnumHandHandling.PreventDefault;
            return;
        }
        base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
    }

    protected override string PlaceHelpLangCode => TailorModSystem.Domain + ":decor-help-placelinen";

    protected override void AppendProducts(ItemStack stack, int cw, int ch, StringBuilder dsc)
    {
        string key = ClothOutput.StickKey(stack.Attributes);
        if (key == ClothOutput.CustomKey) dsc.AppendLine(Lang.Get(TailorModSystem.Domain + ":wovenlinen-stick-custom"));
        else dsc.AppendLine(Lang.Get(TailorModSystem.Domain + ":wovenlinen-stick-vanilla", 1, // 1 cloth per linen item, any size (lane/loom-feedback-6)
            Lang.Get("game:item-cloth-" + key)));
    }
}
