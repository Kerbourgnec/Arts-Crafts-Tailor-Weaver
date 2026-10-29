using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VsShape = Vintagestory.API.Common.Shape;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Client rendering: while a sewing dialog is open on this mannequin (any player), the mannequin wears the
/// in-progress garment. Same path as the vanilla mannequin's worn gear (dressable seraph shape + garment shape via
/// EntityBehaviorContainer.addGearToShape, block atlas, BlendNoCull, mannequin rotation) but the garment's texture
/// key points at a composed texture of the current layout. The opener's dialog pushes its local layout during a
/// drag (<see cref="SetLocalPreview"/>), others follow the server sync. One block-atlas texture per mannequin,
/// replaced on every change (freed, then inserted again).
/// Also: the vanilla mannequin only rebuilds its worn-gear mesh on the interacting client (Initialize and
/// OnBlockInteractStart), so a garment dressed by the server (cut) would stay invisible. When the synced
/// mannequin inventory changes we call its protected <c>regenMesh</c> through reflection (no Harmony).
/// </summary>
public partial class BEBehaviorMannequinTailoring
{
    static readonly MethodInfo? RegenMannequinMesh = typeof(BEBehaviorMannequin).GetMethod("regenMesh", BindingFlags.Instance | BindingFlags.NonPublic);
    static bool warnedNoRegen;

    PieceAssignment[]? localPieces;
    MeshData? previewMesh;
    int previewSubId;
    byte[]? lastMannequinInv;
    CompositeShape? dressableShape;
    Vec3f? shapeOffset;

    void InitPreview(ICoreClientAPI capi)
    {
        var props = Block.BlockEntityBehaviors?.FirstOrDefault(b => b.Name == "Mannequin")?.properties;
        dressableShape = props?["dressableEntityShape"].AsObject<CompositeShape>(null!);
        shapeOffset = props?["shapeOffset"].Exists == true ? props["shapeOffset"].AsObject<Vec3f>() : null;
        RegenPreviewClient();
    }

    /// <summary>Dialog → mannequin: show this layout now (drag preview), null = back to the synced layout.</summary>
    public void SetLocalPreview(PieceAssignment[]? pieces)
    {
        localPieces = pieces?.ToArray();
        RegenPreviewClient();
    }

    /// <summary>The session's garment shows while it is picked, dialog open or not (lane/playtest-fixes-5: walk around and look).</summary>
    bool PreviewActive => Garment != null;

    // Stitches on the mannequin (lane/mannequin-embroidery; lane/sewing-polish): what each target shows = the window's
    // stroke in progress (local override, this client), else the shared pending stitches (synced), else the applied ones.
    const int NoEmbroideryTarget = int.MinValue;
    int localEmbTarget = NoEmbroideryTarget;
    Embroidery? localEmbroidery;
    /// <summary>Worn garments drawn by the preview with their shown stitches (their own vanilla mesh hidden meanwhile).</summary>
    readonly Dictionary<int, (MeshData mesh, int subId)> wornPreviews = new();

    /// <summary>
    /// Window → mannequin: show <paramref name="layer"/> (the stroke being painted, before the server has it) on
    /// <paramref name="target"/> now; null = back to the synced pending / applied stitches.
    /// </summary>
    public void SetLocalEmbroidery(int target, Embroidery? layer, bool regen = true)
    {
        localEmbTarget = layer == null ? NoEmbroideryTarget : target;
        localEmbroidery = layer?.Clone();
        if (regen) RegenPreviewClient();
    }

    /// <summary>Stitches target <paramref name="target"/> shows on this client.</summary>
    Embroidery? ShownLayer(int target)
        => target == localEmbTarget && localEmbroidery != null ? localEmbroidery : PendingOf(target) ?? AppliedOf(target);

    /// <summary>Worn slots the preview draws: the window's stroke target, the in-world hover target, any with pending stitches.</summary>
    HashSet<int> WornPreviewSlots()
    {
        var set = new HashSet<int>();
        foreach (int s in WornTailoredSlots())
        {
            if (s == localEmbTarget || s == hoverTarget || PendingChanges(s) > 0) set.Add(s);
        }
        return set;
    }

    /// <summary>
    /// Client: the client-only hide flag (<see cref="ItemTailoredGarment.EmbroideringAttr"/>) on exactly the worn copies the
    /// preview draws; the vanilla mesh is rebuilt when a flag changed (synced stacks are new objects without it).
    /// </summary>
    void SyncWornHidden(HashSet<int> slots)
    {
        var inv = Mannequin?.Inventory;
        if (inv == null) return;
        bool changed = false;
        for (int i = 0; i < inv.Count; i++)
        {
            var stack = inv[i].Itemstack;
            if (stack == null) continue;
            bool want = slots.Contains(i), has = stack.Attributes.GetBool(ItemTailoredGarment.EmbroideringAttr);
            if (want == has) continue;
            if (want) stack.Attributes.SetBool(ItemTailoredGarment.EmbroideringAttr, true);
            else stack.Attributes.RemoveAttribute(ItemTailoredGarment.EmbroideringAttr);
            changed = true;
        }
        if (changed) RegenVanillaMesh();
    }

    void RegenVanillaMesh()
    {
        var m = Mannequin;
        if (m == null) return;
        if (RegenMannequinMesh == null)
        {
            if (!warnedNoRegen) Api.Logger.Warning("[actailor] BEBehaviorMannequin.regenMesh not found; dressed mannequins update on reload only");
            warnedNoRegen = true;
            return;
        }
        RegenMannequinMesh.Invoke(m, null);
        Blockentity.MarkDirty(true);
    }

    /// <summary>Client, before the main-thread refresh: vanilla worn-gear mesh when the mannequin inventory changed.</summary>
    void OnSyncedClient(ITreeAttribute tree)
    {
        byte[]? inv = (tree.GetTreeAttribute("inventory") as TreeAttribute)?.ToBytes();
        if (inv == null || (lastMannequinInv != null && inv.AsSpan().SequenceEqual(lastMannequinInv))) return;
        lastMannequinInv = inv;
        if (capi == null) return; // not initialized yet: the mannequin's Initialize builds its mesh
        capi.Event.EnqueueMainThreadTask(() =>
        {
            // The synced stacks are new objects: worn garments drawn by the preview get their client-only flag back.
            if (localEmbTarget >= 0 && WornGarment(localEmbTarget) == null) { localEmbTarget = NoEmbroideryTarget; localEmbroidery = null; }
            SyncWornHidden(WornPreviewSlots());
            RegenVanillaMesh();
            RegenPreviewClient();
        }, "actailor-mannequin-regen");
    }

    /// <summary>Main thread: rebuild (or drop) the preview mesh and redraw the block.</summary>
    void RegenPreviewClient()
    {
        if (capi == null) return;
        if (Environment.CurrentManagedThreadId != Vintagestory.API.Config.RuntimeEnv.MainThreadId)
        {
            capi.Event.EnqueueMainThreadTask(RegenPreviewClient, "actailor-mannequin-preview"); // atlas work: main thread only
            return;
        }
        var g = Garment;
        MeshData? mesh = null;
        var wornSlots = WornPreviewSlots();
        SyncWornHidden(wornSlots);
        var worn = new Dictionary<int, (MeshData mesh, int subId)>();
        try
        {
            if (PreviewActive && g != null)
            {
                var emb = ShownLayer(GarmentEmbroidery.TargetSession); // lane/sewing-polish: pending stitches show to everyone
                var px = GarmentLayout.Compose(g, localPieces ?? Session.Pieces, MaterialInSlot, MaterialTextureCache.For(capi), emb, SessionWrap(g));
                DrapeFlow.Ghost(g, px, MaterialInSlot, PartShown); // lane/drape-cut-sew: empty wanted parts are a faint outline
                TintHover(GarmentEmbroidery.TargetSession, px); // lane/mannequin-paint: brush footprint under the crosshair
                mesh = BuildPreviewMesh(capi, g, px, g.HiddenElements(), ref previewSubId);
            }
            // Worn garments with pending stitches, hovered or painted in the window: drawn here (their own mesh is hidden).
            foreach (int slot in wornSlots)
            {
                if (WornGarment(slot) is not { } w) continue;
                var px = GarmentLayout.Compose(w.def, w.data.Pieces, w.data.PartMaterial, MaterialTextureCache.For(capi), ShownLayer(slot),
                    BodyWrapCache.For(capi, w.def, w.data.Wrap));
                TintHover(slot, px);
                int subId = wornPreviews.TryGetValue(slot, out var old) ? old.subId : 0;
                var m = BuildPreviewMesh(capi, w.def, px, w.data.HiddenElements(w.def), ref subId);
                if (m != null) worn[slot] = (m, subId); // no mesh: the old texture (if any) is freed below
            }
        }
        catch (Exception e)
        {
            capi.Logger.Error("[actailor] mannequin preview failed: {0}", e);
        }
        if (mesh == null && previewSubId != 0)
        {
            capi.BlockTextureAtlas.FreeTextureSpace(previewSubId);
            previewSubId = 0;
        }
        foreach (var (slot, old) in wornPreviews)
        {
            if (!worn.TryGetValue(slot, out var now) && old.subId != 0) capi.BlockTextureAtlas.FreeTextureSpace(old.subId);
        }
        if (mesh == null && previewMesh == null && worn.Count == 0 && wornPreviews.Count == 0) return;
        previewMesh = mesh;
        wornPreviews.Clear();
        foreach (var (slot, v) in worn) wornPreviews[slot] = v;
        Blockentity.MarkDirty(true);
    }

    /// <summary>
    /// Garment mesh on the mannequin's dressable shape with <paramref name="pixels"/> as its texture (block atlas; replaces
    /// the texture in <paramref name="subIdSlot"/>), <paramref name="hidden"/> elements left out.
    /// </summary>
    MeshData? BuildPreviewMesh(ICoreClientAPI capi, GarmentDef g, int[] pixels, string[] hidden, ref int subIdSlot)
    {
        if (dressableShape == null) return null;
        var item = capi.World.GetItem(new AssetLocation(g.Item));
        if (item is not ItemTailoredGarment garmentItem) return null;
        var shapeLoc = dressableShape.Base.CopyWithPathPrefixAndAppendixOnce("shapes/", ".json");
        VsShape? shape = VsShape.TryGet(capi, shapeLoc);
        if (shape == null) return null;

        // Composed texture of the current layout; replaces the previous one of this mannequin.
        var bmp = new BakedBitmap { TexturePixels = pixels, Width = g.TextureWidth, Height = g.TextureHeight };
        if (!capi.BlockTextureAtlas.InsertTexture(bmp, out int subId, out TextureAtlasPosition texPos)) return null;
        if (subIdSlot != 0) capi.BlockTextureAtlas.FreeTextureSpace(subIdSlot);
        subIdSlot = subId;

        // A plain stack (no tailoring data): null texture prefix and the item's static textures, like vanilla gear. It only
        // carries the elements to hide (parts left off), read by ItemTailoredGarment.GetShape.
        var collected = new Dictionary<string, CompositeTexture>();
        string[] ignore = Array.Empty<string>();
        var stack = new ItemStack(garmentItem);
        stack.Attributes[ItemTailoredGarment.PreviewHideAttr] = new StringArrayAttribute(hidden);
        EntityBehaviorContainer.addGearToShape(capi, null, capi.BlockTextureAtlas, shape, stack, garmentItem, "default",
            shapeLoc.ToShortString(), ref ignore, collected);

        capi.BlockTextureAtlas.GetOrInsertTexture(new AssetLocation("block/transparent"), out _, out TextureAtlasPosition transparent);
        var texSource = new PreviewTexSource(capi.BlockTextureAtlas, collected, g.TextureKey, texPos, transparent);
        capi.Tesselator.TesselateShape(new TesselationMetaData { TexSource = texSource, IgnoreElements = ignore }, shape, out MeshData mesh);
        for (int i = 0; i < mesh.RenderPassCount; i++) mesh.RenderPassesAndExtraBits[i] = (int)EnumChunkRenderPass.BlendNoCull;
        if (shapeOffset != null) mesh.Translate(shapeOffset);
        return mesh;
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        var mesh = previewMesh;
        var worn = wornPreviews.Values.Select(v => v.mesh).ToList(); // copy: tesselation may run off the main thread
        if (mesh != null || worn.Count > 0)
        {
            // Same transform as BEBehaviorMannequin.OnTesselation.
            float angle = (Mannequin?.MeshAngleRad ?? 0) + (Block.GetBehavior<BlockBehaviorRotateablePlaceable>()?.OffsetRad ?? 0);
            var tf = new Matrixf().Translate(0.5f, 0, 0.5f).RotateY(angle).Translate(-0.5f, 0, -0.5f).Values;
            if (mesh != null) mesher.AddMeshData(mesh, tf);
            foreach (var w in worn) mesher.AddMeshData(w, tf);
        }
        return base.OnTesselation(mesher, tessThreadTesselator);
    }

    void DisposePreview()
    {
        previewMesh = null;
        if (capi != null && previewSubId != 0) capi.BlockTextureAtlas.FreeTextureSpace(previewSubId);
        if (capi != null) foreach (var v in wornPreviews.Values) if (v.subId != 0) capi.BlockTextureAtlas.FreeTextureSpace(v.subId);
        previewSubId = 0;
        wornPreviews.Clear();
    }

    sealed class PreviewTexSource : ITexPositionSource
    {
        readonly ITextureAtlasAPI atlas;
        readonly IDictionary<string, CompositeTexture> collected;
        readonly string garmentKey;
        readonly TextureAtlasPosition garment, transparent;

        public PreviewTexSource(ITextureAtlasAPI atlas, IDictionary<string, CompositeTexture> collected, string garmentKey,
            TextureAtlasPosition garment, TextureAtlasPosition transparent)
        {
            this.atlas = atlas;
            this.collected = collected;
            this.garmentKey = garmentKey;
            this.garment = garment;
            this.transparent = transparent;
        }

        public Size2i AtlasSize => atlas.Size;

        public TextureAtlasPosition this[string textureCode]
        {
            get
            {
                if (textureCode == garmentKey) return garment;
                if (textureCode == "seraph") return transparent; // the mannequin's own body is the block shape
                return collected.TryGetValue(textureCode, out var ct) && ct.Baked != null ? atlas.Positions[ct.Baked.TextureSubId] : transparent;
            }
        }
    }
}
