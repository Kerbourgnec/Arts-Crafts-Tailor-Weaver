using Vintagestory.API.Common;

namespace ArtsAndCrafts.Tailor.Loom;

/// <summary>What the loom accepts as weft twine, and which tools cut the cloth off.</summary>
public static class LoomTwine
{
    public const string PlainTwineCode = "game:flaxtwine";
    public const string DyedTwinePrefix = TailorModSystem.Domain + ":twine-";

    public static bool IsTwine(CollectibleObject? obj)
    {
        if (obj?.Code == null) return false;
        string code = obj.Code.ToString();
        return code == PlainTwineCode || code.StartsWith(DyedTwinePrefix);
    }

    public static bool IsTwine(ItemStack? stack) => IsTwine(stack?.Collectible);

    /// <summary>Vanilla knives (<c>tool: "knife"</c>) and shears (<c>tool: "shears"</c>).</summary>
    public static bool IsCutter(CollectibleObject? obj) => obj?.Tool is EnumTool.Knife or EnumTool.Shears;

    public static bool IsCutter(ItemSlot? slot)
    {
        var obj = slot?.Itemstack?.Collectible;
        if (obj == null) return false;
        return obj.GetTool(slot!) is EnumTool.Knife or EnumTool.Shears;
    }
}
