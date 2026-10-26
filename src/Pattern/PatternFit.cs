using System.Globalization;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Pattern;

/// <summary>
/// Blocktype attribute <c>patternFit</c> (lane/cloth-formats): a cloth of exactly <see cref="Width"/> x <see cref="Rows"/>
/// cells is drawn into the shape's visible uv rectangle <see cref="Uv"/> (texture units 0..16) instead of over the whole
/// texture, so the product shows the whole cloth (chair: 12 x 12 cells into the cushion's uv 2.5..14.5). Any other size
/// keeps the old look (the cloth over the whole texture; the chair then shows its middle 12/16). Pure.
/// </summary>
public sealed class PatternFit
{
    public int Width { get; init; }
    public int Rows { get; init; }
    /// <summary>u0, v0, u1, v1 in texture units (0..16).</summary>
    public float[] Uv { get; init; } = { 0, 0, 16, 16 };

    public bool Matches(PatternData p) => p.Width == Width && p.Height == Rows;
    public bool Matches(int width, int rows) => width == Width && rows == Rows;

    /// <summary>Texture key part: the rectangle.</summary>
    public string Key => string.Join("_", System.Array.ConvertAll(Uv, u => u.ToString("0.##", CultureInfo.InvariantCulture)));

    /// <summary><c>{ width, rows, uv: [u0, v0, u1, v1] }</c>, or null when missing / malformed.</summary>
    public static PatternFit? From(JsonObject? attr)
    {
        if (attr == null || !attr.Exists) return null;
        int w = attr["width"].AsInt(0), h = attr["rows"].AsInt(0);
        float[]? uv = attr["uv"].AsArray<float>(null);
        if (w <= 0 || h <= 0 || uv is not { Length: 4 } || uv[0] >= uv[2] || uv[1] >= uv[3]) return null;
        return new PatternFit { Width = w, Rows = h, Uv = uv };
    }
}
