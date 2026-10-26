using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ArtsAndCrafts.Tailor.Loom;

/// <summary>
/// Client-only loom helpers (lane/loom-feedback-4).
/// F with an empty hand at a loom: vanilla's tool mode hotkey (<c>toolmodeselect</c>) only opens for an item that
/// has tool modes (GuiDialogToolMode.OnKeyCombinationToggle returns false for an empty slot). Its handler is set by
/// the dialog in OnBlockTexturesLoaded; at LevelFinalize (later) we wrap it: empty hand + looking at a loom opens
/// the draft window <see cref="GuiDialogDraftWindow"/> at once (lane/draft-polish, owner 2026-10-25: no picker in between;
/// product, cloth size and brush live in the window). F again closes it unless a text field has the focus.
/// Everything else goes to the vanilla handler unchanged.
/// Brush outline: <see cref="LoomBrushOutline"/> (renderer registered here).
/// </summary>
public class LoomClientSystem : ModSystem
{
    public const string ToolModeHotkey = "toolmodeselect";

    ICoreClientAPI? capi;
    LoomBrushOutline? outline;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        api.Event.LevelFinalize += WrapToolModeHotkey;
        outline = new LoomBrushOutline(api);
        api.Event.RegisterRenderer(outline, EnumRenderStage.AfterFinalComposition, TailorModSystem.Domain + "-loombrushoutline");
    }

    void WrapToolModeHotkey()
    {
        HotKey? hk = capi!.Input.GetHotKeyByCode(ToolModeHotkey);
        if (hk == null)
        {
            capi.Logger.Warning("[actailor] hotkey {0} not found: no empty-hand loom brush dialog", ToolModeHotkey);
            return;
        }
        ActionConsumable<KeyCombination>? vanilla = hk.Handler;
        hk.Handler = comb => OnToolModeKey() || (vanilla?.Invoke(comb) ?? false);
    }

    bool OnToolModeKey()
    {
        if (capi == null) return false;
        // Open window: F closes it, unless the player types in one of its fields (lane/draft-window gotcha).
        if (GuiDialogDraftWindow.AnyOpen) return GuiDialogDraftWindow.CloseUnlessTyping();
        if (capi.World.Player?.InventoryManager?.ActiveHotbarSlot?.Empty != true) return false;
        if (LookedAtLoom(capi, out _) is not { } loom) return false;
        // Straight into the draft window (lane/draft-polish): every loom state, locked parts greyed with the reason.
        GuiDialogDraftWindow.Open(capi, loom);
        return true;
    }

    /// <summary>The loom the player looks at (any of its blocks; filler blocks map to the controller), else null.</summary>
    public static BlockEntityLoom? LookedAtLoom(ICoreClientAPI capi, out BlockSelection? sel)
    {
        sel = capi.World.Player?.CurrentBlockSelection;
        if (sel?.Position == null) return null;
        BlockPos pos = sel.Position;
        if (capi.World.BlockAccessor.GetBlock(pos) is BlockMultiblock filler) pos = pos.AddCopy(filler.OffsetInv);
        return capi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityLoom;
    }

    public override void Dispose()
    {
        GuiDialogDraftWindow.CloseAny();
        if (outline != null)
        {
            capi?.Event.UnregisterRenderer(outline, EnumRenderStage.AfterFinalComposition);
            outline.Dispose();
        }
        base.Dispose();
    }
}
