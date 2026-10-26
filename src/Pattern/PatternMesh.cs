using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ArtsAndCrafts.Tailor.Pattern;

/// <summary>
/// Client: tesselate a block shape with some of its texture keys replaced by woven patterns (block atlas).
/// Main thread only for patterns not yet in the atlas (see <see cref="PatternTextures"/>).
/// </summary>
public static class PatternMesh
{
    /// <summary>
    /// <paramref name="keys"/>[i] shows <paramref name="patterns"/>[i]; null patterns keep the block's own texture.
    /// Returns null if the shape is missing or a pattern texture is still queued (then <paramref name="dirtyPos"/> is
    /// re-tesselated once it is in, and the caller should rebuild).
    /// </summary>
    public static MeshData? Build(ICoreClientAPI capi, Block block, CompositeShape? cshape, IReadOnlyList<string> keys,
        IReadOnlyList<PatternData?> patterns, BlockPos? dirtyPos = null, PatternFit? fit = null)
    {
        if (cshape?.Base == null) return null;
        var overrides = new Dictionary<string, TextureAtlasPosition>();
        for (int i = 0; i < keys.Count && i < patterns.Count; i++)
        {
            if (patterns[i] is not { } p) continue;
            var pos = PatternTextures.BlockAtlas(capi, p, dirtyPos, fit);
            if (pos == null) return null;
            overrides[keys[i]] = pos;
        }

        Shape? shape = LoadShape(capi, cshape.Base);
        if (shape == null) return null;
        var source = new PatternTexSource(capi.Tesselator.GetTextureSource(block), overrides, capi.BlockTextureAtlas.Size);
        capi.Tesselator.TesselateShape(TailorModSystem.Domain + "-patterned-" + block.Code, shape, out MeshData mesh, source,
            new Vec3f(cshape.rotateX, cshape.rotateY, cshape.rotateZ));
        return mesh;
    }

    /// <summary>Shapes by location, loaded once per session (TesselateShape does not modify them).</summary>
    static Shape? LoadShape(ICoreClientAPI capi, AssetLocation baseLoc)
    {
        var cache = ObjectCacheUtil.GetOrCreate(capi, TailorModSystem.Domain + "-pattern-shapes", () => new Dictionary<string, Shape?>());
        var loc = baseLoc.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
        lock (cache)
        {
            if (!cache.TryGetValue(loc.ToString(), out Shape? shape))
            {
                shape = Shape.TryGet(capi, loc);
                cache[loc.ToString()] = shape;
            }
            return shape;
        }
    }
}
