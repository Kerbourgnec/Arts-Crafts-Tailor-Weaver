using System.Collections.Generic;
using System.Globalization;
using ArtsAndCrafts.Tailor.Loom;
using Vintagestory.API.Common;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// Colour of a twine stack: collectible attribute <c>tailorColor</c> ("#rrggbb") when present
/// (set by the dyed twine items), else a per-code fallback so weaving works without them.
/// </summary>
public static class TwineColors
{
    public const string AttributeKey = "tailorColor";
    public const string PlainHex = "#d8c9a6";

    /// <summary>Fallback per colour suffix of <c>actailor:twine-{color}</c>; approximates vanilla linen hues.</summary>
    static readonly Dictionary<string, string> Fallback = new()
    {
        ["plain"] = PlainHex,
        ["mordant"] = "#cdbf98",
        ["blue"] = "#3d5b8f",
        ["red"] = "#9b3028",
        ["yellow"] = "#d4b03c",
        ["green"] = "#557a36",
        ["purple"] = "#6a3f7c",
        ["pink"] = "#d48aa2",
        ["orange"] = "#cf7a2f",
        ["brown"] = "#6e4b2f",
        ["gray"] = "#7c7a76",
        ["black"] = "#2b2a2a",
        ["white"] = "#ebe7dc",
        ["gold"] = "#e8af2c",
    };

    public static string HexOf(ItemStack? stack)
    {
        if (stack?.Collectible == null) return PlainHex;
        string? attr = stack.Collectible.Attributes?[AttributeKey].AsString(null);
        if (TryParse(attr, out _)) return attr!.ToLowerInvariant();

        string code = stack.Collectible.Code.ToString();
        if (code == LoomTwine.PlainTwineCode) return PlainHex;
        if (code.StartsWith(LoomTwine.DyedTwinePrefix))
        {
            string color = code.Substring(LoomTwine.DyedTwinePrefix.Length);
            if (Fallback.TryGetValue(color, out string? hex)) return hex;
        }
        return PlainHex;
    }

    public static bool TryParse(string? hex, out int rgb)
    {
        rgb = 0;
        if (hex == null || hex.Length != 7 || hex[0] != '#') return false;
        return int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
    }

    /// <summary>0xAARRGGBB, the layout of SKColor / BakedBitmap pixels. <paramref name="whiteMix"/> 0..1 lightens.</summary>
    public static int ToArgb(string hex, float shade = 1f, float whiteMix = 0f)
    {
        TryParse(hex, out int rgb);
        int r = Scale((rgb >> 16) & 0xff, shade, whiteMix), g = Scale((rgb >> 8) & 0xff, shade, whiteMix), b = Scale(rgb & 0xff, shade, whiteMix);
        return unchecked((int)0xff000000) | (r << 16) | (g << 8) | b;
    }

    static int Scale(int c, float f, float mix) => System.Math.Clamp((int)((c + (255 - c) * mix) * f), 0, 255);
}
