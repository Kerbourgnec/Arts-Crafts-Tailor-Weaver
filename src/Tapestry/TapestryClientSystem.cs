using System;
using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.Loom;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace ArtsAndCrafts.Tailor.Tapestry;

/// <summary>
/// Client helpers of the tapestry loom (lane/tapestry-loom): F with an empty hand at a tapestry loom opens the brush
/// picker (vanilla's tool mode hotkey only opens for items with tool modes; we wrap its handler at LevelFinalize, like
/// the Artisan loom's LoomClientSystem, and pass every other case on); and the red outline of the cells a click paints.
/// </summary>
public class TapestryClientSystem : ModSystem
{
    ICoreClientAPI? capi;
    GuiDialogTapestryBrush? dialog;
    TapestryBrushOutline? outline;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        api.Event.LevelFinalize += WrapToolModeHotkey;
        outline = new TapestryBrushOutline(api);
        api.Event.RegisterRenderer(outline, EnumRenderStage.AfterFinalComposition, TailorModSystem.Domain + "-tapestrybrushoutline");
    }

    void WrapToolModeHotkey()
    {
        HotKey? hk = capi!.Input.GetHotKeyByCode(LoomClientSystem.ToolModeHotkey);
        if (hk == null) return;
        ActionConsumable<KeyCombination>? previous = hk.Handler;
        hk.Handler = comb => OnToolModeKey() || (previous?.Invoke(comb) ?? false);
    }

    bool OnToolModeKey()
    {
        if (capi == null) return false;
        if (dialog?.IsOpened() == true) { dialog.TryClose(); return true; }
        if (capi.World.Player?.InventoryManager?.ActiveHotbarSlot?.Empty != true) return false;
        if (LookedAt(capi, out _) is not { } loom) return false;
        dialog = new GuiDialogTapestryBrush(capi, loom);
        dialog.TryOpen();
        return true;
    }

    /// <summary>The tapestry loom the player looks at (any of its blocks), else null.</summary>
    public static BlockEntityTapestryLoom? LookedAt(ICoreClientAPI capi, out BlockSelection? sel)
    {
        sel = capi.World.Player?.CurrentBlockSelection;
        if (sel?.Position == null) return null;
        BlockPos pos = sel.Position;
        if (capi.World.BlockAccessor.GetBlock(pos) is BlockMultiblock filler) pos = pos.AddCopy(filler.OffsetInv);
        return capi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityTapestryLoom;
    }

    public override void Dispose()
    {
        dialog?.TryClose();
        dialog?.Dispose();
        if (outline != null)
        {
            capi?.Event.UnregisterRenderer(outline, EnumRenderStage.AfterFinalComposition);
            outline.Dispose();
        }
        base.Dispose();
    }
}

/// <summary>Brush picker for the empty hand (F at a tapestry loom): the 1/2/4/8 icons of the twine tool modes.</summary>
public class GuiDialogTapestryBrush : GuiDialog
{
    readonly BlockEntityTapestryLoom loom;

    public override string ToggleKeyCombinationCode => null!;
    public override bool PrefersUngrabbedMouse => false;

    public GuiDialogTapestryBrush(ICoreClientAPI capi, BlockEntityTapestryLoom loom) : base(capi) => this.loom = loom;

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        // The icons are cached and disposed by CollectibleBehaviorLoomBrush; not ours to dispose.
        var modes = CollectibleBehaviorLoomBrush.Modes(capi);
        int current = CollectibleBehaviorLoomBrush.ModeOf(capi.World.Player);
        double pitch = GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGridBase.unscaledSlotPadding;
        double width = Math.Max(modes.Length * pitch, 260);
        var compo = capi.Gui.CreateCompo(TailorModSystem.Domain + "-tapestrybrush", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(ElementStdBounds.DialogBackground().WithFixedPadding(GuiStyle.ElementToDialogPadding / 2), withTitleBar: false)
            .BeginChildElements()
            .AddStaticText(Lang.Get(TailorModSystem.Domain + ":tapestryloom-gui-brush"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, 0, width, 20))
            .AddSkillItemGrid(modes.ToList(), modes.Length, 1, OnSlotClick, ElementBounds.Fixed(0, 22, modes.Length * pitch, pitch), "modes")
            .AddDynamicText(modes[current].Name, CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, 22 + pitch + 7, width, 30), "name");
        AddProductRow(compo, 22 + pitch + 40, width);
        SingleComposer = compo.EndChildElements().Compose();
        var grid = SingleComposer.GetSkillItemGrid("modes");
        grid.selectedIndex = current;
        grid.OnSlotOver = i => { if (i >= 0 && i < modes.Length) SingleComposer?.GetDynamicText("name")?.SetNewText(modes[i].Name); };
    }

    void OnSlotClick(int mode)
    {
        CollectibleBehaviorLoomBrush.SetMode(capi.World.Player, mode);
        loom.SendBrushMode(mode);
        TryClose();
    }

    /// <summary>Product (lane/linen-output): cloth / small carpet / large carpet, on an empty frame only.</summary>
    void AddProductRow(GuiComposer compo, double y, double width)
    {
        static string L(string key, params object[] args) => Lang.Get(TailorModSystem.Domain + ":" + key, args);
        var cloth = loom.Cloth;
        compo.AddStaticText(L("loom-gui-product", L(ClothProducts.LangKey(cloth.Product)), cloth.Width, cloth.Rows), CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(0, y, Math.Max(width, 380), 20));
        y += 22;
        if (cloth.WovenRows > 0)
        {
            compo.AddStaticText(L("loom-gui-product-fixed"), CairoFont.WhiteSmallText().WithColor(GuiStyle.WarningTextColor), ElementBounds.Fixed(0, y, Math.Max(width, 380), 20));
            return;
        }
        double bx = 0;
        foreach (var p in ClothProducts.All)
        {
            var product = p;
            compo.AddSmallButton(L(ClothProducts.LangKey(p)) + (p == cloth.Product ? " *" : ""),
                () => { loom.SendProduct(product); TryClose(); return true; }, ElementBounds.Fixed(bx, y, 120, 24), EnumButtonStyle.Small, "product" + (int)p);
            bx += 126;
        }
    }
}

/// <summary>
/// Light red outline around the current-row cells a right click would paint (brush span at the hovered cell, the whole
/// row while sneaking): while holding twine, or with an empty hand and an active bobbin. Same drawing as the Artisan
/// loom's LoomBrushOutline (vanilla WireframeCube at AfterFinalComposition).
/// </summary>
public sealed class TapestryBrushOutline : IRenderer
{
    const float Grow = 0.002f;
    readonly ICoreClientAPI capi;
    WireframeCube? cube;

    public double RenderOrder => 0.91;
    public int RenderRange => 24;

    public TapestryBrushOutline(ICoreClientAPI capi) => this.capi = capi;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        IPlayer? player = capi.World.Player;
        if (player?.Entity == null) return;
        if (TapestryClientSystem.LookedAt(capi, out BlockSelection? sel) is not { } loom || sel == null) return;
        var part = loom.PartAt(sel.SelectionBoxIndex);
        var held = BlockEntityLoom.Classify(player.InventoryManager.ActiveHotbarSlot);
        List<Cuboidf> boxes;
        if (part.Part == TapestryPart.EmbCell && loom.Work.Hoop is { } hoop)
        {
            // Embroidery (lane/loom-embroidery): the square brush; sneaking (pick out) needs no twine.
            bool sneak = BlockEntityTapestryLoom.IsSneaking(player);
            if (!(sneak && held is BlockEntityLoom.HeldKind.Twine or BlockEntityLoom.HeldKind.EmptyHand) && !LoomBrushOutline.Paints(held, loom.Work.Bobbins.SelectedCode != null)) return;
            var (cx, cy) = EmbroideryHoop.CellXY(part.Index, hoop.Size);
            var cells = hoop.Layer.BrushCells(cx, cy, CollectibleBehaviorLoomBrush.BrushSize(player)).Select(c => c.y * hoop.Size + c.x).ToHashSet();
            boxes = loom.EmbCellBoxes(cells);
        }
        else
        {
            if (loom.Cloth.Complete || loom.Work.Hoop != null) return;
            if (part.Part != TapestryPart.Cell) return;
            if (!LoomBrushOutline.Paints(held, loom.Work.Bobbins.SelectedCode != null)) return;
            var (start, end) = LoomBrushOutline.Footprint(part.Index, CollectibleBehaviorLoomBrush.BrushSize(player), loom.Cloth.Width, BlockEntityTapestryLoom.IsSneaking(player));
            boxes = loom.CellBoxes(start, end);
        }
        Vec3d offset = player.Entity.CameraPosOffset;
        double x = loom.Pos.X + offset.X, y = loom.Pos.InternalY + offset.Y, z = loom.Pos.Z + offset.Z;
        cube ??= WireframeCube.CreateUnitCube(capi, -1);
        float lineWidth = 2f * Math.Max(0.5f, capi.Settings.Float["wireframethickness"]);
        foreach (Cuboidf box in boxes)
            cube.Render(capi, x + box.X1 - Grow, y + box.Y1 - Grow, z + box.Z1 - Grow, box.XSize + 2 * Grow, box.YSize + 2 * Grow, box.ZSize + 2 * Grow, lineWidth, LoomBrushOutline.Color);
    }

    public void Dispose() => cube?.Dispose();
}
