using System;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace ArtsAndCrafts.Tailor.Loom;

/// <summary>
/// Client (lane/loom-feedback-4, item 2): a light red outline around the warp threads at the back a twine click loads
/// (brush span; sneak: every end the click reaches). No cell outline since lane/loom-roles (the Artisan loom weaves
/// drafts only); <see cref="Footprint"/> and <see cref="Paints"/> stay for the tapestry looms' outline. Drawn like
/// vanilla's selected-block outline (SystemSelectedBlockOutline: <see cref="WireframeCube"/> at AfterFinalComposition).
/// </summary>
public sealed class LoomBrushOutline : IRenderer
{
    public static readonly Vec4f Color = new(1f, 0.5f, 0.5f, 0.9f);
    /// <summary>Grow each cell box this much (blocks) so the lines sit just off the cloth surface.</summary>
    const float Grow = 0.002f;

    readonly ICoreClientAPI capi;
    /// <summary>Uploaded on the first frame that draws (render thread), not at mod start.</summary>
    WireframeCube? cube;

    public double RenderOrder => 0.91; // right after vanilla's outline (0.9)
    public int RenderRange => 24;

    public LoomBrushOutline(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    /// <summary>Cells [start, end) a click on <paramref name="cell"/> paints: the brush span, or the whole row when sneaking (fill).</summary>
    public static (int start, int end) Footprint(int cell, int brushSize, int width, bool sneak)
        => sneak ? (0, width) : WeavingState.BrushSpan(cell, brushSize, width);

    /// <summary>Whether the held item paints at all: twine, or an empty hand with an active shuttle.</summary>
    public static bool Paints(BlockEntityLoom.HeldKind held, bool shuttleActive)
        => held == BlockEntityLoom.HeldKind.Twine || (held == BlockEntityLoom.HeldKind.EmptyHand && shuttleActive);

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        IPlayer? player = capi.World.Player;
        if (player?.Entity == null) return;
        if (LoomClientSystem.LookedAtLoom(capi, out BlockSelection? sel) is not { } loom || sel == null) return;
        if (loom.Weaving.Complete) return;
        LoomBoxPart part = loom.PartAt(sel.SelectionBoxIndex);
        var held = BlockEntityLoom.Classify(player.InventoryManager.ActiveHotbarSlot);
        int brush = CollectibleBehaviorLoomBrush.BrushSize(player);
        bool sneak = loom.IsSneaking(player);
        // Warp threads at the back: the ends a click loads (brush span; sneak: every end the click reaches).
        if (part.Part != LoomPart.WarpThread || loom.Weaving.WarpLocked || held != BlockEntityLoom.HeldKind.Twine) return;
        string? code = player.InventoryManager.ActiveHotbarSlot?.Itemstack?.Collectible?.Code?.ToString();
        if (loom.Weaving.WarpTargets(part.Index, brush, code, sneak) is not { } ends) return;
        var boxes = loom.PartBoxes(LoomPart.WarpThread, ends);

        Vec3d offset = capi.World.Player.Entity.CameraPosOffset;
        double x = loom.Pos.X + offset.X, y = loom.Pos.InternalY + offset.Y, z = loom.Pos.Z + offset.Z;
        cube ??= WireframeCube.CreateUnitCube(capi, -1);
        float lineWidth = 2f * Math.Max(0.5f, capi.Settings.Float["wireframethickness"]);
        foreach (Cuboidf box in boxes)
        {
            cube.Render(capi, x + box.X1 - Grow, y + box.Y1 - Grow, z + box.Z1 - Grow,
                box.XSize + 2 * Grow, box.YSize + 2 * Grow, box.ZSize + 2 * Grow, lineWidth, Color);
        }
    }

    public void Dispose() => cube?.Dispose();
}
