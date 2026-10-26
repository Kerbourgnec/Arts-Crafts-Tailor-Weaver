using System.Collections.Concurrent;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Client: decoded material textures (leather, pelt, vanilla cloth, hard items) for garment composition, by item code (thread-safe; worn gear may compose
/// off the main thread). Texture = <see cref="MaterialDef.Texture"/> PNG from the asset manager, cropped to
/// <see cref="MaterialDef.Crop"/>; a missing PNG falls back to <see cref="MaterialDef.Color"/> (warning logged once).
/// </summary>
public static class MaterialTextureCache
{
    const string CacheKey = TailorModSystem.Domain + "-materialtextures";

    static ConcurrentDictionary<string, MaterialTexture?> Cache(ICoreClientAPI capi)
        => ObjectCacheUtil.GetOrCreate(capi, CacheKey, () => new ConcurrentDictionary<string, MaterialTexture?>());

    public static System.Func<string, MaterialTexture?> For(ICoreClientAPI capi) => code => Get(capi, code);

    public static MaterialTexture? Get(ICoreClientAPI capi, string itemCode) => Cache(capi).GetOrAdd(itemCode, code =>
    {
        // Fixed part (GarmentLayout.FixedTextureKey): the recipe names the texture; missing → null (the part's fixedColor).
        if (GarmentLayout.TryParseFixedKey(code, out string fixedTex, out int[]? fixedCrop))
        {
            var floc = new AssetLocation(fixedTex).WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png");
            IAsset? fasset = capi.Assets.TryGet(floc);
            if (fasset == null)
            {
                capi.Logger.Warning("[actailor] garment fixed part texture {0} not found, flat colour used", floc);
                return null;
            }
            using var fbmp = fasset.ToBitmap(capi);
            return new MaterialTexture(fbmp.Pixels, fbmp.Width, fbmp.Height, fixedCrop);
        }
        var def = MaterialCatalog.Get(capi).Find(code);
        if (def == null) return null;
        var loc = def.TextureLocation(code);
        IAsset? asset = capi.Assets.TryGet(loc);
        if (asset == null)
        {
            capi.Logger.Warning("[actailor] tailoring material {0}: texture {1} not found, flat colour used", code, loc);
            return MaterialTexture.Solid(Weaving.TwineColors.ToArgb(def.Color));
        }
        using var bmp = asset.ToBitmap(capi);
        return new MaterialTexture(bmp.Pixels, bmp.Width, bmp.Height, def.Crop);
    });
}
