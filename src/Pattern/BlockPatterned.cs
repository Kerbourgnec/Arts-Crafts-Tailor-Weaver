using System.Collections.Generic;
using System.Linq;
using System.Text;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ArtsAndCrafts.Tailor.Pattern;

/// <summary>
/// A block that shows woven patterns on some of its shape's texture keys (class <c>actailor.BlockPatterned</c>,
/// entity <c>actailor.Patterned</c>). The patterns travel in the stack (<see cref="PatternData.ReadList"/>) and live in
/// the block entity once placed; drops and pick block return a stack with them.
/// <para>
/// Blocktype attributes: <c>patternKeys</c> (string[], texture key per pattern, default ["pattern"]),
/// <c>dropCode</c> (block code to drop/pick, e.g. the "-north" variant; default this block),
/// <c>needsFloor</c> (bool: every footprint block needs solid ground below).
/// Crafted from woven cloth: the cloth inputs' patterns, in grid order, become the output's patterns.
/// </para>
/// </summary>
public class BlockPatterned : Block
{
    const string InvMeshCacheKey = TailorModSystem.Domain + "-patterned-invmeshes";

    public string[] PatternKeys { get; private set; } = { "pattern" };
    public int PatternCount => PatternKeys.Length;
    /// <summary>Attribute <c>patternFit</c>: a cloth of exactly this size fills the visible uv rectangle (lane/cloth-formats).</summary>
    public PatternFit? Fit { get; private set; }
    /// <summary>
    /// Attribute <c>splitCloth</c> <c>{ cols, rows, tiles: [[col, row], ...] }</c> (lane/cloth-formats): crafted from a
    /// single cloth, that cloth is cut into cols x rows tiles of (cloth width / cols) cells square, pattern i = tile tiles[i]
    /// (lane/linen-output: native size, a 32 x 64 carpet cloth gives two 32 x 32 tiles).
    /// </summary>
    public (int cols, int rows, (int col, int row)[] tiles)? SplitCloth { get; private set; }
    AssetLocation? dropCode;
    bool needsFloor;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        PatternKeys = Attributes?["patternKeys"].AsArray<string>(null) is { Length: > 0 } keys ? keys : new[] { "pattern" };
        string? drop = Attributes?["dropCode"].AsString(null);
        dropCode = drop != null ? AssetLocation.Create(drop, Code.Domain) : null;
        needsFloor = Attributes?["needsFloor"].AsBool(false) ?? false;
        Fit = PatternFit.From(Attributes?["patternFit"]);
        SplitCloth = ParseSplit(Attributes?["splitCloth"], PatternCount);
    }

    /// <summary><c>splitCloth</c> attribute → (cols, rows, tile per pattern); null when missing or not one tile per pattern key.</summary>
    public static (int cols, int rows, (int col, int row)[] tiles)? ParseSplit(JsonObject? attr, int patternCount)
    {
        if (attr == null || !attr.Exists) return null;
        int cols = attr["cols"].AsInt(0), rows = attr["rows"].AsInt(0);
        // Nested arrays: one JsonObject per tile (AsArray<int[]> does not read them, verified by the selftest).
        var tiles = attr["tiles"].AsArray()?.Select(t => t.AsArray<int>(null)).ToArray();
        if (cols <= 0 || rows <= 0 || tiles == null || tiles.Length != patternCount) return null;
        if (tiles.Any(t => t is not { Length: 2 } || t[0] < 0 || t[0] >= cols || t[1] < 0 || t[1] >= rows)) return null;
        return (cols, rows, tiles.Select(t => (t[0], t[1])).ToArray());
    }

    /// <summary>
    /// Patterns for a crafted stack from its woven cloth inputs (grid order): one cloth per pattern key; or, for a
    /// block with <see cref="SplitCloth"/> crafted from a single cloth, that cloth's tiles. Pure (selftest).
    /// </summary>
    public static PatternData?[] PatternsFromCloths(IReadOnlyList<ITreeAttribute?> cloths, int patternCount,
        (int cols, int rows, (int col, int row)[] tiles)? split)
    {
        if (split is { } s && cloths.Count == 1 && PatternData.FromAttributes(cloths[0]) is { } one)
            return PatternData.Split(one, s.cols, s.rows, s.tiles, System.Math.Max(1, one.Width / s.cols)); // native tile size (lane/linen-output)
        return PatternData.FromInputs(cloths, patternCount);
    }

    public PatternData?[] PatternsOf(ItemStack? stack) => PatternData.ReadList(stack?.Attributes, PatternCount);

    /// <summary>The stack this block drops and picks, carrying the placed patterns.</summary>
    public virtual ItemStack StackWith(IWorldAccessor world, IReadOnlyList<PatternData?> patterns)
    {
        Block block = (dropCode != null ? world.GetBlock(dropCode) : null) ?? this;
        var stack = new ItemStack(block);
        PatternData.WriteList(stack.Attributes, patterns);
        return stack;
    }

    #region Placement, drops

    public override bool CanPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref string failureCode)
    {
        if (!base.CanPlaceBlock(world, byPlayer, blockSel, ref failureCode)) return false;
        if (!needsFloor) return true;
        bool ok = true;
        void CheckFloor(BlockPos pos)
        {
            BlockPos below = pos.DownCopy();
            if (!world.BlockAccessor.GetBlock(below).CanAttachBlockAt(world.BlockAccessor, this, below, BlockFacing.UP)) ok = false;
        }
        var multiblock = GetBehavior<BlockBehaviorMultiblock>();
        if (multiblock != null) multiblock.IterateOverEach(blockSel.Position, p => { CheckFloor(p); return ok; });
        else CheckFloor(blockSel.Position);
        if (!ok) failureCode = "requiresolidground";
        return ok;
    }

    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityPatterned be) return new[] { StackWith(world, be.Patterns) };
        return base.GetDrops(world, pos, byPlayer, dropQuantityMultiplier);
    }

    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityPatterned be) return StackWith(world, be.Patterns);
        return base.OnPickBlock(world, pos);
    }

    /// <summary>Crafted from woven cloth: their patterns (grid order) go into the output stack.</summary>
    public override void OnCreatedByCrafting(ItemSlot[] allInputSlots, ItemSlot outputSlot, IRecipeBase byRecipe)
    {
        base.OnCreatedByCrafting(allInputSlots, outputSlot, byRecipe);
        if (outputSlot.Itemstack == null) return;
        var cloth = allInputSlots.Where(s => !s.Empty && s.Itemstack.Collectible is ItemWovenCloth).Select(s => (ITreeAttribute?)s.Itemstack.Attributes).ToList();
        PatternData.WriteList(outputSlot.Itemstack.Attributes, PatternsFromCloths(cloth, PatternCount, SplitCloth));
    }

    #endregion

    #region Client rendering

    /// <summary>Client, main thread: placed mesh (block shape with its rotation) showing the patterns.</summary>
    public virtual MeshData? GenPlacedMesh(ICoreClientAPI capi, IReadOnlyList<PatternData?> patterns, BlockPos pos)
        => PatternMesh.Build(capi, this, Shape, PatternKeys, patterns, pos, Fit);

    Dictionary<string, MultiTextureMeshRef> InvMeshes(ICoreClientAPI capi)
        => ObjectCacheUtil.GetOrCreate(capi, InvMeshCacheKey, () => new Dictionary<string, MultiTextureMeshRef>());

    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        var patterns = PatternsOf(itemstack);
        if (patterns.Any(p => p != null))
        {
            string key = Code + "@" + PatternData.ListKey(patterns);
            var meshes = InvMeshes(capi);
            if (!meshes.TryGetValue(key, out var meshRef))
            {
                MeshData? mesh = PatternMesh.Build(capi, this, ShapeInventory ?? Shape, PatternKeys, patterns, null, Fit);
                if (mesh != null) meshes[key] = meshRef = capi.Render.UploadMultiTextureMesh(mesh);
            }
            if (meshRef != null) renderinfo.ModelRef = meshRef;
        }
        base.OnBeforeRender(capi, itemstack, target, ref renderinfo);
    }

    public override void OnUnloaded(ICoreAPI api)
    {
        if (api is ICoreClientAPI capi && api.ObjectCache.ContainsKey(InvMeshCacheKey))
        {
            foreach (var meshRef in InvMeshes(capi).Values) meshRef.Dispose();
            ObjectCacheUtil.Delete(api, InvMeshCacheKey);
        }
        base.OnUnloaded(api);
    }

    #endregion

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        int n = PatternsOf(inSlot.Itemstack).Count(p => p != null);
        dsc.AppendLine(n == 0
            ? Lang.Get(TailorModSystem.Domain + ":wovencloth-nopattern")
            : Lang.Get(TailorModSystem.Domain + ":decor-patterns", n, PatternCount));
    }
}
