namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// What a loom weaves (lane/linen-output, owner 2026-10-20; docs/weaving.md "Woven carpets"): chosen on an empty loom
/// (F picker), kept for the next cloths. N = the warp width (world cloth size 16 or 32).
/// </summary>
public enum ClothProduct
{
    /// <summary>N x N cloth → woven linen / vanilla linen (<see cref="ClothOutput"/>).</summary>
    Cloth = 0,
    /// <summary>N x N → <c>actailor:wovencarpet-small-north</c> with the pattern.</summary>
    SmallCarpet = 1,
    /// <summary>N wide x 2N long → <c>actailor:wovencarpet-large-north</c> (1x2 blocks), its two N x N halves as tiles.</summary>
    LargeCarpet = 2,
}

public static class ClothProducts
{
    public static readonly ClothProduct[] All = { ClothProduct.Cloth, ClothProduct.SmallCarpet, ClothProduct.LargeCarpet };

    public static bool Valid(int p) => p >= 0 && p < All.Length;

    /// <summary>Rows per warp end: 2 for the large carpet (twice as long, same width), else 1.</summary>
    public static int LengthFactor(ClothProduct p) => p == ClothProduct.LargeCarpet ? 2 : 1;

    /// <summary>Lang key (our domain) of the product's name.</summary>
    public static string LangKey(ClothProduct p) => "loomproduct-" + p switch
    {
        ClothProduct.SmallCarpet => "smallcarpet",
        ClothProduct.LargeCarpet => "largecarpet",
        _ => "cloth",
    };

    /// <summary>Block code a carpet cut gives (null for cloth).</summary>
    public static string? CarpetBlockCode(ClothProduct p) => p switch
    {
        ClothProduct.SmallCarpet => TailorModSystem.Domain + ":wovencarpet-small-north",
        ClothProduct.LargeCarpet => TailorModSystem.Domain + ":wovencarpet-large-north",
        _ => null,
    };
}
