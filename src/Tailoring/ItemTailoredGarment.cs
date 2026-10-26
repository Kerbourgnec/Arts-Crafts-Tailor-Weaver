using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using VsShape = Vintagestory.API.Common.Shape;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Garment cut and sewn on a mannequin (<c>actailor:garment-*</c>). Stack attributes: <see cref="GarmentData"/>.
/// Worn: implements <see cref="IAttachableToEntity"/> so the vanilla gear tesselation
/// (EntityBehaviorContainer.addGearToShape) asks us for the textures; we insert the composed texture into the
/// entity atlas and point the shape's texture key at it. The per-stack texture prefix keeps two different
/// garments apart in the entity's texture dictionary.
/// Held / GUI: same composed texture in the item atlas on the full-body mesh (like CollectibleBehaviorWearableAttachment).
/// Without data (creative stack) it renders with its static JSON textures, like a vanilla garment.
/// Model: <see cref="IWearableShapeSupplier"/> builds the recipe's shape per stack (<see cref="LoadShape"/>: extra shape
/// merged in, fixed hidden elements and the elements of empty optional parts removed).
/// </summary>
public class ItemTailoredGarment : Item, IAttachableToEntity, IWearableShapeSupplier
{
    /// <summary>Stack attribute (string[]) of the mannequin preview's plain stack: elements to hide (no garment data yet).</summary>
    public const string PreviewHideAttr = "actailorPreviewHide";
    /// <summary>
    /// Client-only bool on the mannequin's worn stack while its embroidery is edited (lane/mannequin-embroidery): the worn
    /// mesh leaves it out and the live preview shows it with the unapplied stitches. Never set on the server.
    /// </summary>
    public const string EmbroideringAttr = "actailorEmbroidering";

    const string MeshCacheKey = TailorModSystem.Domain + "-garment-meshes";
    ICoreClientAPI? capi;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        capi = api as ICoreClientAPI;
    }

    public GarmentDef? Def => api == null ? null : GarmentCatalog.Get(api).FindByItem(Code.ToString());

    public GarmentData? Data(ItemStack stack)
    {
        var def = Def;
        return def == null ? null : GarmentData.FromStack(stack, def);
    }

    /// <summary>
    /// The recipe's model: <see cref="GarmentDef.Shape"/> plus <see cref="GarmentDef.ExtraShape"/> elements (same texture
    /// units), minus <paramref name="hidden"/> elements (children go with them). Fresh copy each call; null when missing.
    /// </summary>
    public static VsShape? LoadShape(ICoreAPI api, GarmentDef def, string[] hidden)
    {
        VsShape? shape = VsShape.TryGet(api, ShapeLocation(def.Shape));
        if (shape == null) return null;
        if (!string.IsNullOrEmpty(def.ExtraShape))
        {
            VsShape? extra = VsShape.TryGet(api, ShapeLocation(def.ExtraShape));
            if (extra?.Elements != null) shape.Elements = (shape.Elements ?? System.Array.Empty<ShapeElement>()).Concat(extra.Elements).ToArray();
            else api.Logger.Warning("[actailor] garment {0}: extra shape {1} not found", def.Code, def.ExtraShape);
        }
        if (hidden.Length > 0) shape.RemoveElements(hidden);
        return shape;
    }

    public static AssetLocation ShapeLocation(string code) => new AssetLocation(code).WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");

    /// <summary>Elements a stack does not show: from its garment data, else the preview list, else every optional part's.</summary>
    public static string[] HiddenElements(ItemStack stack, GarmentDef def)
    {
        var data = GarmentData.FromStack(stack, def);
        if (data != null) return data.HiddenElements(def);
        if (stack.Attributes[PreviewHideAttr] is StringArrayAttribute preview) return preview.value ?? System.Array.Empty<string>();
        return def.HiddenElements(_ => false);
    }

    public VsShape? GetShape(ItemStack stack, Entity forEntity, string texturePrefixCode)
    {
        var def = Def;
        if (def == null || api == null) return null; // vanilla path: the item's JSON shape
        var shape = LoadShape(api, def, HiddenElements(stack, def));
        if (shape == null) return null;
        // Client-only flag on the mannequin's worn copy while it is being embroidered: the live preview draws it instead.
        if (stack.Attributes.GetBool(EmbroideringAttr)) shape.Elements = System.Array.Empty<ShapeElement>();
        shape.SubclassForStepParenting(texturePrefixCode);
        shape.ResolveReferences(api.Logger, def.Shape);
        return shape;
    }

    #region IAttachableToEntity

    public int RequiresBehindSlots { get; set; }
    public bool IsAttachable(Entity toEntity, ItemStack itemStack) => true;
    public string? GetCategoryCode(ItemStack stack) => Attributes?["clothescategory"].AsString();
    public CompositeShape? GetAttachedShape(ItemStack stack, string slotCode) => Shape;
    public string[]? GetDisableElements(ItemStack stack) => Attributes?["disableElements"].AsArray<string>()!;
    public string[]? GetKeepElements(ItemStack stack) => Attributes?["keepElements"].AsArray<string>()!;

    public string? GetTexturePrefixCode(ItemStack stack)
    {
        var data = Data(stack);
        return data == null ? null : TailorModSystem.Domain + "-" + data.Hash() + "-";
    }

    public void CollectTextures(ItemStack stack, VsShape shape, string texturePrefixCode, Dictionary<string, CompositeTexture> intoDict)
    {
        IAttachableToEntity.CollectTexturesFromCollectible(stack, texturePrefixCode, shape, intoDict);
        var def = Def;
        var data = def == null ? null : GarmentData.FromStack(stack, def);
        if (capi == null || def == null || data == null) return;
        var loc = EnsureTexture(capi, capi.EntityTextureAtlas, WornPrefix, def, data, out _);
        // The vanilla mannequin (BEBehaviorMannequin.regenMesh, main thread) runs the same gear path but looks the
        // texture up by name in the block atlas: insert it there too under the same name. Entities may tesselate
        // off the main thread, where atlas insertion is unsafe; they only need the entity atlas.
        if (System.Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId) EnsureTexture(capi, capi.BlockTextureAtlas, WornPrefix, def, data, out _);
        if (loc != null) shape.Textures[def.TextureKey] = loc;
    }

    /// <summary>Atlas name prefix of worn garment textures (entity atlas, and block atlas for mannequins).</summary>
    public const string WornPrefix = "garment-worn/";

    #endregion

    /// <summary>Composed texture in <paramref name="atlas"/> under <c>actailor:{prefix}{hash}</c>; null when the atlas refused it.</summary>
    public static AssetLocation? EnsureTexture(ICoreClientAPI capi, ITextureAtlasAPI atlas, string prefix, GarmentDef def, GarmentData data, out TextureAtlasPosition? texPos)
    {
        var loc = new AssetLocation(TailorModSystem.Domain, prefix + data.Hash());
        bool ok = atlas.GetOrInsertTexture(loc, out _, out TextureAtlasPosition pos, () => new BakedBitmap
        {
            TexturePixels = GarmentLayout.Compose(def, data.Pieces, data.PartMaterial, MaterialTextureCache.For(capi), data.Embroidery,
                BodyWrapCache.For(capi, def, data.Wrap)),
            Width = def.TextureWidth,
            Height = def.TextureHeight
        });
        texPos = ok ? pos : null;
        return ok ? loc : null;
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        var def = Def;
        var data = inSlot.Itemstack == null || def == null ? null : GarmentData.FromStack(inSlot.Itemstack, def);
        if (def == null || data == null)
        {
            dsc.AppendLine(Lang.Get(TailorModSystem.Domain + ":garment-plain"));
            return;
        }
        dsc.AppendLine(Lang.Get(TailorModSystem.Domain + ":garment-cloths", data.Materials.Count));
        for (int p = 0; p < def.Parts.Length && p < data.Pieces.Length; p++)
        {
            var a = data.Pieces[p];
            string part = Lang.Get(TailorModSystem.Domain + ":part-" + def.Parts[p].Code);
            var m = data.Material(a.Cloth);
            if (def.Parts[p].IsFixed)
            {
                dsc.AppendLine(Lang.Get(TailorModSystem.Domain + ":garment-partline-fixed", part));
                continue;
            }
            if (m == null)
            {
                dsc.AppendLine(Lang.Get(TailorModSystem.Domain + ":garment-partline-none", part));
                continue;
            }
            if (!m.IsPatterned)
            {
                var loc = new AssetLocation(m.Code);
                CollectibleObject? item = (CollectibleObject?)world.GetItem(loc) ?? world.GetBlock(loc); // vanilla linen is a block
                string name = item == null ? m.Code : item is Block b ? b.GetHeldItemName(new ItemStack(b)) : item.GetHeldItemName(new ItemStack((Item)item));
                dsc.AppendLine(Lang.Get(TailorModSystem.Domain + ":garment-partline-material", part, name, a.Rot * 90));
                continue;
            }
            dsc.AppendLine(Lang.Get(TailorModSystem.Domain + ":garment-partline", part, a.Dx, a.Dy, a.Rot * 90));
        }
        if (data.Embroidery is { IsEmpty: false } emb)
            dsc.AppendLine(Lang.Get(TailorModSystem.Domain + ":tailoring-garment-embroidered", emb.StitchCount, emb.UsedCodes().Count));
        if (withDebugInfo) dsc.AppendLine("garment " + data.Hash());
    }

    #region Held / GUI rendering (client)

    Dictionary<string, MultiTextureMeshRef> MeshRefs(ICoreClientAPI capi)
        => ObjectCacheUtil.GetOrCreate(capi, MeshCacheKey, () => new Dictionary<string, MultiTextureMeshRef>());

    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        base.OnBeforeRender(capi, itemstack, target, ref renderinfo); // Wearable behavior: static full-body mesh
        var def = Def;
        var data = def == null ? null : GarmentData.FromStack(itemstack, def);
        if (def == null || data == null) return;

        string hash = data.Hash();
        var refs = MeshRefs(capi);
        if (!refs.TryGetValue(hash, out var meshRef))
        {
            MeshData? mesh = GenHeldMesh(capi, def, data);
            if (mesh == null) return;
            refs[hash] = meshRef = capi.Render.UploadMultiTextureMesh(mesh);
        }
        renderinfo.ModelRef = meshRef;
    }

    /// <summary>Garment step-parented onto a bare player shape (seraph texture transparent), as vanilla wearables render.</summary>
    MeshData? GenHeldMesh(ICoreClientAPI capi, GarmentDef def, GarmentData data)
    {
        if (EnsureTexture(capi, capi.ItemTextureAtlas, "garment-item/", def, data, out var texPos) == null || texPos == null) return null;
        EntityProperties? props = capi.World.GetEntityType(new AssetLocation("player"));
        VsShape? entityShape = props?.Client.LoadedShape;
        if (entityShape == null) return null;

        var shapePath = ShapeLocation(def.Shape);
        VsShape? garmentShape = LoadShape(capi, def, data.HiddenElements(def));
        if (garmentShape == null) return null;

        var newShape = new VsShape
        {
            Elements = entityShape.CloneElements(),
            Animations = entityShape.CloneAnimations(),
            AnimationsByCrc32 = entityShape.AnimationsByCrc32,
            JointsById = entityShape.JointsById,
            TextureWidth = entityShape.TextureWidth,
            TextureHeight = entityShape.TextureHeight,
            Textures = null,
        };
        newShape.StepParentShape(garmentShape, shapePath.ToShortString(), props!.Client.Shape.Base.ToShortString(), capi.Logger, (_, _) => { });

        var texSource = new GarmentTexSource(capi.Tesselator.GetTextureSource(this), def.TextureKey, texPos, capi.ItemTextureAtlas.Size);
        capi.Tesselator.TesselateShapeWithJointIds("entity", newShape, out MeshData mesh, texSource, new Vec3f());
        return mesh;
    }

    public override void OnUnloaded(ICoreAPI api)
    {
        if (api is ICoreClientAPI c && api.ObjectCache.ContainsKey(MeshCacheKey))
        {
            foreach (var meshRef in MeshRefs(c).Values) meshRef.Dispose();
            ObjectCacheUtil.Delete(api, MeshCacheKey);
        }
        base.OnUnloaded(api);
    }

    class GarmentTexSource : ITexPositionSource
    {
        readonly ITexPositionSource fallback;
        readonly string key;
        readonly TextureAtlasPosition composed;
        public Size2i AtlasSize { get; }

        public GarmentTexSource(ITexPositionSource fallback, string key, TextureAtlasPosition composed, Size2i atlasSize)
        {
            this.fallback = fallback;
            this.key = key;
            this.composed = composed;
            AtlasSize = atlasSize;
        }

        public TextureAtlasPosition? this[string textureCode] => textureCode == key ? composed : fallback[textureCode];
    }

    #endregion
}
