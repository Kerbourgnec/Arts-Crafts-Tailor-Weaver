using System;
using System.Collections.Generic;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ArtsAndCrafts.Tailor.Pattern;

/// <summary>
/// Client: a woven pattern as a texture in the block atlas, one per pattern hash (<c>actailor:wovencloth-placed/&lt;hash&gt;</c>,
/// 1 texel per cell at the cloth's own size (32 x 32 cloth: 32 x 32 px), built by <see cref="ItemWovenCloth.BuildBitmap"/>; a fitted pattern
/// <c>&lt;hash&gt;-fit&lt;rect&gt;</c> is <see cref="ItemWovenCloth.FitSize"/> px square, <see cref="ItemWovenCloth.BuildFitBitmap"/>). Atlas insertion uploads to the GPU, so it only
/// happens on the main thread; elsewhere it is queued and the given block position is re-tesselated afterwards.
/// Never evicted (bounded by the patterns seen in a session).
/// </summary>
public static class PatternTextures
{
    const string PendingKey = TailorModSystem.Domain + "-pattern-blockatlas-pending";

    public static AssetLocation BlockAtlasLoc(string hash) => new(TailorModSystem.Domain, "wovencloth-placed/" + hash);

    /// <summary>
    /// The pattern's block atlas texture at its own size (or drawn into <paramref name="fit"/>'s uv rectangle, see
    /// <see cref="PatternFit"/>), or null while it is not there yet (see class doc).
    /// </summary>
    public static TextureAtlasPosition? BlockAtlas(ICoreClientAPI capi, PatternData pattern, BlockPos? dirtyPos = null, PatternFit? fit = null)
    {
        byte[] p = (byte[])pattern.Pattern.Clone();
        string[] c = (string[])pattern.Colors.Clone();
        int w = pattern.Width, h = pattern.Height;
        if (fit != null && fit.Matches(pattern))
        {
            var f = fit;
            return BlockAtlas(capi, pattern.Hash + "-fit" + f.Key, () => ItemWovenCloth.BuildFitBitmap(p, c, w, h, f.Uv), dirtyPos);
        }
        var up = pattern.WarpUp;
        return BlockAtlas(capi, pattern.Hash, () => ItemWovenCloth.BuildBitmap(p, c, w, h, up), dirtyPos);
    }

    /// <summary>A <paramref name="w"/> x <paramref name="h"/> pattern (ground pile, shelves; the cloth's own size) under its hash.</summary>
    public static TextureAtlasPosition? BlockAtlas(ICoreClientAPI capi, string hash, byte[] pattern, string[] colors, int w, int h, BlockPos? dirtyPos, bool[]? warpUp = null)
    {
        byte[] p = (byte[])pattern.Clone();
        string[] c = (string[])colors.Clone();
        return BlockAtlas(capi, hash, () => ItemWovenCloth.BuildBitmap(p, c, w, h, warpUp), dirtyPos);
    }

    static TextureAtlasPosition? BlockAtlas(ICoreClientAPI capi, string hash, CreateTextureDelegate build, BlockPos? dirtyPos)
    {
        var loc = BlockAtlasLoc(hash);
        TextureAtlasPosition? pos = capi.BlockTextureAtlas[loc];
        if (pos != null) return pos;

        if (Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId)
        {
            return capi.BlockTextureAtlas.GetOrInsertTexture(loc, out _, out pos, build) ? pos : null;
        }

        var pending = ObjectCacheUtil.GetOrCreate(capi, PendingKey, () => new HashSet<string>());
        BlockPos? at = dirtyPos?.Copy();
        lock (pending)
        {
            if (!pending.Add(hash) && at == null) return null;
        }
        capi.Event.EnqueueMainThreadTask(() =>
        {
            capi.BlockTextureAtlas.GetOrInsertTexture(loc, out _, out _, build);
            lock (pending) pending.Remove(hash);
            if (at != null) capi.World.BlockAccessor.MarkBlockDirty(at);
        }, "actailor-pattern-tex");
        return null;
    }
}

/// <summary>Texture source that maps some texture keys to pattern textures and everything else to a fallback.</summary>
public sealed class PatternTexSource : ITexPositionSource
{
    readonly ITexPositionSource fallback;
    readonly IReadOnlyDictionary<string, TextureAtlasPosition> overrides;
    public Size2i AtlasSize { get; }

    public PatternTexSource(ITexPositionSource fallback, IReadOnlyDictionary<string, TextureAtlasPosition> overrides, Size2i atlasSize)
    {
        this.fallback = fallback;
        this.overrides = overrides;
        AtlasSize = atlasSize;
    }

    public TextureAtlasPosition this[string textureCode]
        => overrides.TryGetValue(textureCode, out var pos) ? pos : fallback[textureCode];
}
