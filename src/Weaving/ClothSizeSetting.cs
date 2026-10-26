using Vintagestory.API.Common;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// Size of every new cloth (lane/square-cloth): 16 x 16, or 32 x 32 ("double precision"). A world setting: attribute
/// <see cref="Code"/> declared in the mod's root <c>worldconfig.json</c> (shown on the world creation screen, tab
/// "Arts &amp; Crafts"; later <c>/worldconfig actailorClothSize 32</c>), read from <see cref="IWorldAccessor.Config"/>
/// on both sides. A cloth already on a loom (rows woven) and every cut cloth keep their own size; the next cloth takes the setting (lane/loom-feedback-8).
/// </summary>
public static class ClothSizeSetting
{
    public const string Code = "actailorClothSize";

    /// <summary>Square size a new cloth gets in this world (16 when the world has no such setting, e.g. created before).</summary>
    public static int NewClothSize(IWorldAccessor? world)
    {
        string? value;
        try { value = world?.Config?.GetAsString(Code); }
        catch { value = null; } // a non-string value (hand-edited config) falls back like a missing one
        return Parse(value);
    }

    /// <summary>"16" / "32" (also as a number) → the size; anything else → 16. Pure (selftest).</summary>
    public static int Parse(string? value)
        => int.TryParse(value?.Trim(), out int n) && WeavingState.IsAllowedSize(n) ? n : WeavingState.DefaultWidth;
}
