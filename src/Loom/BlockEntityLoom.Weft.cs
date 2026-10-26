using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using ArtsAndCrafts.Tailor.Weaving;

namespace ArtsAndCrafts.Tailor.Loom;

/// <summary>
/// The model's weft thread (lane/shuttle-fix, owner: it was white): the shape's <c>plain</c> texture (pirn
/// <c>shuttle12</c>, <c>shuttlethread*</c> to the fell; lane/loom-feedback-6: also the flying weft <c>shuttlestrings*obj*</c>,
/// scripts/loom-weft-strings.py) takes the active shuttle's twine colour, plain flax when none is
/// active. Client: the static loom mesh is tesselated by us with that colour (cached per block + colour) instead of
/// the chunk tesselator's default; the animator is (re)initialised with the same texture source.
/// </summary>
public partial class BlockEntityLoom
{
    /// <summary>Client: static loom model with the weft colour, built on the main thread, read by the tesselation thread.</summary>
    volatile MeshData? loomMesh;
    /// <summary>Client: weft colour the animator mesh was made with (null: not initialised).</summary>
    string? animatorWeftHex;

    /// <summary>Weft colour shown on the model: the active shuttle's twine, else plain flax.</summary>
    public string WeftHex => Shuttles.SelectedCode is { } code ? TwineHex(code) : TwineColors.PlainHex;

    ITexPositionSource WeftSource(ICoreClientAPI capi) => LoomClothMesh.WeftTexSource(capi, Block, WeftHex);

    /// <summary>Client, main thread: the static model in the current weft colour (cached per block code + colour).</summary>
    void RebuildLoomMesh()
    {
        if (Api is not ICoreClientAPI capi || Block?.Shape == null) return;
        string hex = WeftHex;
        string key = TailorModSystem.Domain + "-loommesh-" + Block.Code + "-" + hex;
        loomMesh = ObjectCacheUtil.GetOrCreate(capi, key, () =>
        {
            var shapeLoc = Block.Shape.Base.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
            var shape = Shape.TryGet(capi, shapeLoc);
            if (shape == null) return null!;
            shape.ResolveReferences(capi.World.Logger, key);
            var meta = new TesselationMetaData
            {
                TexSource = WeftSource(capi),
                TypeForLogging = key,
                Rotation = new Vec3f(Block.Shape.rotateX, Block.Shape.rotateY, Block.Shape.rotateZ),
                QuantityElements = Block.Shape.QuantityElements,
                SelectiveElements = Block.Shape.SelectiveElements,
                IgnoreElements = Block.Shape.IgnoreElements,
            };
            capi.Tesselator.TesselateShape(meta, shape, out MeshData mesh);
            return mesh;
        });
        UpdateAnimatorWeft(capi);
    }

    /// <summary>
    /// Client, main thread: re-make the animator mesh when the weft colour changed (also while a shed pose is held; the
    /// running animations carry over to the new renderer). Not during a cycle: the cycle end rebuilds.
    /// </summary>
    void UpdateAnimatorWeft(ICoreClientAPI capi)
    {
        if (!animatorReady || animatorWeftHex == WeftHex || Cycling) return;
        var animUtil = Animatable?.animUtil;
        if (animUtil == null) return;
        bool active = animUtil.activeAnimationsByAnimCode.Count > 0;
        InitAnimatorMesh(capi, animUtil);
        if (active && animUtil.renderer != null) animUtil.renderer.ShouldRender = true;
    }

    void InitAnimatorMesh(ICoreClientAPI capi, BlockEntityAnimationUtil animUtil)
    {
        animatorWeftHex = WeftHex;
        animUtil.InitializeAnimator(TailorModSystem.Domain + "-loom-" + Block?.Code, null, WeftSource(capi), new Vec3f(0, RotateYDeg, 0));
    }
}
