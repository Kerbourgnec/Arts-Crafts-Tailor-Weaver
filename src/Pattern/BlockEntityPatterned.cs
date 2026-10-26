using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Pattern;

/// <summary>
/// Block entity of a <see cref="BlockPatterned"/> (class <c>actailor.Patterned</c>): the placed patterns, synced as tree
/// <c>patterns</c> (same layout as the stack). Client: the patterned mesh replaces the default block mesh; it is built
/// on the main thread (atlas insertion) and re-built whenever the patterns change.
/// </summary>
public class BlockEntityPatterned : BlockEntity
{
    /// <summary>Upper bound read from saves before the block (and its pattern count) is known.</summary>
    const int MaxPatterns = 9;

    public PatternData?[] Patterns { get; private set; } = Array.Empty<PatternData?>();
    /// <summary>
    /// Item code of the cloth item it was placed from (lane/linen-output: a tapestry hung from woven linen or woven cloth
    /// gives that item back); null for blocks placed from their own block stack and for older saves. Tree <c>sourceCode</c>.
    /// </summary>
    public string? SourceCode { get; private set; }
    public const string AttrSourceCode = "sourceCode";
    /// <summary>
    /// Extra turn of the placed mesh around Y (deg; lane/loom-feedback-6: floor / ceiling linen panels face the placer).
    /// 0 for every other block and older saves. Tree <c>turnDeg</c>.
    /// </summary>
    public int TurnDeg { get; private set; }
    public const string AttrTurn = "turnDeg";
    /// <summary>
    /// Attributes of an embroidered cloth item it was placed from (lane/loom-embroidery): breaking gives that cloth back with
    /// the woven pattern and the embroidery still apart (<see cref="Patterns"/> holds the composite). Tree <c>sourceCloth</c>;
    /// null otherwise.
    /// </summary>
    public ITreeAttribute? SourceAttrs { get; private set; }
    public const string AttrSourceCloth = "sourceCloth";
    volatile MeshData? mesh;

    int Count => (Block as BlockPatterned)?.PatternCount ?? 1;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        Fit();
        if (api is ICoreClientAPI)
        {
            RebuildMesh();
            MarkDirty(true);
        }
    }

    public override void OnBlockPlaced(ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(byItemStack);
        Patterns = PatternData.ReadList(byItemStack?.Attributes, Count);
        SourceCode = byItemStack?.Collectible is Item item ? item.Code?.ToString() : null;
        SourceAttrs = CaptureSource(SourceCode, byItemStack?.Attributes);
        if (Api is ICoreClientAPI) RebuildMesh();
        MarkDirty(true);
    }

    /// <summary>
    /// What the block remembers of the cloth item it was placed from: all its attributes (pattern, name, draft, embroidery, anything
    /// else), so breaking it returns an identical stack that still stacks with the original (lane/cloth-fixes; it used to keep
    /// only the pattern keys, or the whole tree only for embroidered cloth, so name and draft were lost). Null for blocks placed
    /// from their own stack. Pure (selftest).
    /// </summary>
    public static ITreeAttribute? CaptureSource(string? sourceCode, ITreeAttribute? stackAttrs)
        => sourceCode != null && stackAttrs != null ? stackAttrs.Clone() : null;

    /// <summary>Set the patterns from code (e.g. a tapestry hung with a hammer).</summary>
    public void SetPatterns(PatternData?[] patterns)
    {
        Patterns = patterns;
        Fit();
        MarkDirty(true);
    }

    /// <summary>Server: turn the placed mesh by <paramref name="deg"/> around Y (synced).</summary>
    public void SetTurn(int deg)
    {
        TurnDeg = ((deg % 360) + 360) % 360;
        MarkDirty(true);
    }

    void Fit()
    {
        int n = Count;
        if (Patterns.Length == n) return;
        var fitted = new PatternData?[n];
        Array.Copy(Patterns, fitted, Math.Min(n, Patterns.Length));
        Patterns = fitted;
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        PatternData.WriteList(tree, Patterns);
        if (SourceCode != null) tree.SetString(AttrSourceCode, SourceCode);
        if (TurnDeg != 0) tree.SetInt(AttrTurn, TurnDeg);
        if (SourceAttrs != null) tree[AttrSourceCloth] = SourceAttrs;
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        SourceCode = tree.GetString(AttrSourceCode);
        TurnDeg = tree.GetInt(AttrTurn, 0);
        SourceAttrs = tree.GetTreeAttribute(AttrSourceCloth);
        // A BE tree has no pattern keys of its own, so ReadList only finds the "patterns" list.
        Patterns = tree[PatternData.AttrList] is ITreeAttribute ? PatternData.ReadList(tree, Block != null ? Count : MaxPatterns) : new PatternData?[Count];
        // Old tall tapestry parts (lane/cloth-formats) stored "slice"/"slices": ignored, each part shows the whole cloth.
        if (Block != null) Fit();
        if (Api is ICoreClientAPI)
        {
            RebuildMesh();
            MarkDirty(true);
        }
    }

    /// <summary>Client: rebuild the patterned mesh on the main thread (queued from other threads).</summary>
    void RebuildMesh()
    {
        if (Api is not ICoreClientAPI capi || Block is not BlockPatterned block) return;
        if (Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId)
        {
            capi.Event.EnqueueMainThreadTask(() => { RebuildMesh(); MarkDirty(true); }, "actailor-patterned-mesh");
            return;
        }
        var m = Patterns.Any(p => p != null) ? block.GenPlacedMesh(capi, Patterns, Pos) : null;
        if (m != null && TurnDeg != 0) m.Rotate(new Vintagestory.API.MathTools.Vec3f(0.5f, 0.5f, 0.5f), 0, TurnDeg * Vintagestory.API.MathTools.GameMath.DEG2RAD, 0);
        mesh = m;
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        MeshData? m = mesh;
        if (m == null) return base.OnTesselation(mesher, tessThreadTesselator);
        mesher.AddMeshData(m);
        return true;
    }
}
