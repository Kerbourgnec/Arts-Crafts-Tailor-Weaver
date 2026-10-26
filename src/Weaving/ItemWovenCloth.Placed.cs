using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using ArtsAndCrafts.Tailor.Pattern;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// Woven cloth placed in the world: ground pile (GroundStorable "Stacking"), shelves and display cases.
/// <para>
/// Shelves/display cases call <see cref="GenMesh"/> per stack. The ground pile (1.22 BlockEntityGroundStorage,
/// Stacking layout) never calls GenMesh: it tesselates the stacking shape with the item's static textures and
/// caches the mesh in <c>ObjectCache["meshesDisplay-groundstorage"]</c> under
/// <c>ItemModelCount + "x" + GetMeshCacheKey(slot)</c>. So <see cref="GetMeshCacheKey"/> returns a per-pattern key
/// and primes that cache entry with a patterned pile mesh before the ground storage looks it up.
/// </para>
/// The pattern texture goes into the block atlas on the main thread; until it exists the plain key is returned
/// (plain linen pile) and the block is marked dirty once the texture is in, which re-tesselates it.
/// </summary>
public partial class ItemWovenCloth
{
    const string GroundStorageInvClass = "groundstorage";
    const string GroundStorageMeshCacheKey = "meshesDisplay-" + GroundStorageInvClass;
    public string GetMeshCacheKey(ItemSlot slot)
    {
        var stack = slot.Itemstack;
        if (stack == null || api is not ICoreClientAPI capi || !TryReadCloth(stack, out byte[] pattern, out string[] colors, out int w, out int h))
        {
            return Code.ToString();
        }
        var warpUp = WarpUpOf(stack, w, h);
        string hash = PatternHash(pattern, colors, w) + ShadeKey(warpUp);
        TextureAtlasPosition? texPos = BlockAtlasPattern(capi, hash, pattern, colors, w, h, slot.Inventory?.Pos, warpUp);
        if (texPos == null) return Code.ToString();

        string key = Code + "-" + hash;
        if (slot.Inventory?.ClassName == GroundStorageInvClass) PrimePileMesh(capi, stack, key, texPos);
        return key;
    }

    public MeshData GenMesh(ItemSlot slot, ITextureAtlasAPI targetAtlas, BlockPos atBlockPos)
    {
        var capi = (ICoreClientAPI)api;
        ITexPositionSource source = new AtlasTexSource(this, targetAtlas);
        var stack = slot.Itemstack;
        if (stack != null && targetAtlas == capi.BlockTextureAtlas && TryReadCloth(stack, out byte[] pattern, out string[] colors, out int w, out int h))
        {
            var texPos = BlockAtlasPattern(capi, PatternHash(pattern, colors, w) + ShadeKey(WarpUpOf(stack, w, h)), pattern, colors, w, h, atBlockPos, WarpUpOf(stack, w, h));
            if (texPos != null) source = new PatternTexSource(source, texPos, targetAtlas.Size);
        }
        capi.Tesselator.TesselateItem(this, out MeshData mesh, source);
        return mesh;
    }

    /// <summary>Pattern texture in the block atlas at the cloth's own size, or null while it is not there yet (see <see cref="PatternTextures"/>).</summary>
    static TextureAtlasPosition? BlockAtlasPattern(ICoreClientAPI capi, string hash, byte[] pattern, string[] colors, int w, int h, BlockPos? dirtyPos, bool[]? warpUp)
        => PatternTextures.BlockAtlas(capi, hash, pattern, colors, w, h, dirtyPos, warpUp);

    /// <summary>Put a patterned pile mesh into the ground storage mesh cache under the key it is about to look up.</summary>
    void PrimePileMesh(ICoreClientAPI capi, ItemStack stack, string key, TextureAtlasPosition texPos)
    {
        var props = GetBehavior<CollectibleBehaviorGroundStorable>()?.StorageProps;
        if (props?.Layout != EnumGroundStorageLayout.Stacking || props.StackingModel == null) return;

        int models = (int)Math.Ceiling(stack.StackSize / (float)Math.Max(1, props.ItemsPerModel));
        string fullKey = models + "x" + key;
        var cache = ObjectCacheUtil.GetOrCreate(capi, GroundStorageMeshCacheKey, () => new Dictionary<string, MeshData>());
        lock (cache)
        {
            if (cache.ContainsKey(fullKey)) return;
        }

        var loc = props.StackingModel.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
        Vintagestory.API.Common.Shape? shape = Vintagestory.API.Common.Shape.TryGet(capi, loc);
        if (shape == null) return;
        var source = new PatternTexSource(new AtlasTexSource(this, capi.BlockTextureAtlas), texPos, capi.BlockTextureAtlas.Size);
        capi.Tesselator.TesselateShape("actailor-wovencloth-pile", shape, out MeshData mesh, source, null, 0, 0, 0, props.CuboidsPerModel * models);
        lock (cache)
        {
            cache[fullKey] = mesh;
        }
    }

    /// <summary>The item's own textures, looked up in the given atlas (block atlas for placed cloth).</summary>
    class AtlasTexSource : ITexPositionSource
    {
        readonly Item item;
        readonly ITextureAtlasAPI atlas;
        public Size2i AtlasSize => atlas.Size;

        public AtlasTexSource(Item item, ITextureAtlasAPI atlas)
        {
            this.item = item;
            this.atlas = atlas;
        }

        public TextureAtlasPosition this[string textureCode]
        {
            get
            {
                if (item.Textures.TryGetValue(textureCode, out CompositeTexture? tex) && tex.Baked?.BakedName != null)
                {
                    return atlas[tex.Baked.BakedName] ?? atlas.UnknownTexturePosition;
                }
                return atlas.UnknownTexturePosition;
            }
        }
    }
}
