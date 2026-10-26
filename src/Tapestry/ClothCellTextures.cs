using System.Collections.Generic;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ArtsAndCrafts.Tailor.Tapestry;

/// <summary>
/// Client: tiny per-colour textures in the block atlas for loom meshes built in code (lane/tapestry-loom; meant for the
/// hand looms too). A key is <c>w#rrggbb</c> (woven), <c>h#rrggbb</c> (highlighted: current row, active bobbin) or
/// <see cref="EmptyKey"/> (an unpainted cell). Same 2x2 over/under look and atlas locations (<c>actailor:loomcell/...</c>)
/// as the Artisan loom's LoomClothMesh, so both share the inserted textures. Main thread only (atlas upload).
/// </summary>
public sealed class ClothCellTextures
{
    public const string EmptyKey = "e";
    const string CacheKey = TailorModSystem.Domain + "-loomcore-celltex";
    /// <summary>Highlight: mix this much white into the twine colour.</summary>
    public const float HighlightMix = 0.35f;

    readonly ICoreClientAPI capi;
    readonly Dictionary<string, TextureAtlasPosition> tex;
    readonly HashSet<string> used = new();

    public ClothCellTextures(ICoreClientAPI capi)
    {
        this.capi = capi;
        tex = ObjectCacheUtil.GetOrCreate(capi, CacheKey, () => new Dictionary<string, TextureAtlasPosition>());
    }

    public static string Key(string hex, bool highlight) => (highlight ? "h" : "w") + hex;

    /// <summary>Key for <paramref name="hex"/>, remembered so <see cref="Source"/> inserts its texture.</summary>
    public string Use(string hex, bool highlight = false) { string k = Key(hex, highlight); used.Add(k); return k; }

    public string UseEmpty() { used.Add(EmptyKey); return EmptyKey; }

    /// <summary>Insert every used texture and return a source that maps the keys (others fall back to the block's own textures).</summary>
    public ITexPositionSource Source(Block block)
    {
        foreach (string key in used) Ensure(key);
        return new Source_(tex, capi.Tesselator.GetTextureSource(block), capi.BlockTextureAtlas.Size);
    }

    void Ensure(string key)
    {
        if (tex.ContainsKey(key)) return;
        var loc = new AssetLocation(TailorModSystem.Domain, "loomcell/" + (key == EmptyKey ? "empty" : key.Substring(0, 1) + key.Substring(2)));
        if (capi.BlockTextureAtlas.GetOrInsertTexture(loc, out _, out TextureAtlasPosition pos, () => Bitmap(key))) tex[key] = pos;
    }

    /// <summary>2x2 px: two diagonal pixels shaded, the look of a plain over/under weave.</summary>
    static IBitmap Bitmap(string key)
    {
        string hex = key == EmptyKey ? "#3a342c" : key.Substring(1);
        float mix = key.StartsWith("h") ? HighlightMix : 0f;
        var px = new int[4];
        for (int i = 0; i < 4; i++) px[i] = TwineColors.ToArgb(hex, i == 0 || i == 3 ? 1f : 0.86f, mix);
        return new BakedBitmap { TexturePixels = px, Width = 2, Height = 2 };
    }

    sealed class Source_ : ITexPositionSource
    {
        readonly Dictionary<string, TextureAtlasPosition> tex;
        readonly ITexPositionSource fallback;
        public Size2i AtlasSize { get; }

        public Source_(Dictionary<string, TextureAtlasPosition> tex, ITexPositionSource fallback, Size2i atlasSize)
        {
            this.tex = tex;
            this.fallback = fallback;
            AtlasSize = atlasSize;
        }

        public TextureAtlasPosition this[string textureCode] => tex.TryGetValue(textureCode, out var pos) ? pos : fallback[textureCode]!;
    }
}
