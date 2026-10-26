using ArtsAndCrafts.Tailor.Pattern;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// What a woven cloth / linen stack looks like (lane/loom-embroidery): the woven pattern with its <see cref="Embroidery"/>
/// on top. Every renderer reads through here (item, ground pile, shelves, wall / floor panels, decor crafted from the cloth,
/// garments): <see cref="ItemWovenCloth.TryReadCloth(ITreeAttribute, out byte[], out string[], out int, out int)"/>,
/// and <see cref="PatternData.FromAttributes"/>
/// all return the composite. Only the tapestry loom's embroidery mode reads the layers apart (<see cref="Woven"/>,
/// <see cref="Embroidery.Read"/>). Pure (selftest).
/// </summary>
public static class WovenTexture
{
    /// <summary>The stack's pattern as shown (woven + embroidery, own size), or null when it carries no valid pattern.</summary>
    public static PatternData? Composite(ItemStack? stack) => Composite(stack?.Attributes);

    /// <summary>Same as <see cref="Composite(ItemStack)"/> for a cloth's attribute tree.</summary>
    public static PatternData? Composite(ITreeAttribute? attrs)
    {
        if (Woven(attrs) is not { } woven) return null;
        return Embroidery.Composite(woven, Embroidery.Read(attrs, woven.Width, woven.Height));
    }

    /// <summary>The woven layer alone (no embroidery), or null.</summary>
    public static PatternData? Woven(ITreeAttribute? attrs)
    {
        if (attrs == null || !ItemWovenCloth.TryReadWoven(attrs, out byte[] pattern, out string[] colors, out int w, out int h)) return null;
        string[] codes = (attrs[ItemWovenCloth.AttrCodes] as StringArrayAttribute)?.value ?? System.Array.Empty<string>();
        return new PatternData(pattern, colors, codes, w, h, ItemWovenCloth.ReadWarpUp(attrs, w, h));
    }
}
