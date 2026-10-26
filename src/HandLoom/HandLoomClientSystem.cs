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

namespace ArtsAndCrafts.Tailor.HandLoom;

/// <summary>
/// Client helpers of the hand loom (lane/hand-loom): F with an empty hand at a hand loom opens its picker (threading
/// preset on a free warp, brush), by wrapping the vanilla tool mode hotkey handler at LevelFinalize like the
/// Artisan and tapestry looms (every other case is passed on); and the red outline of the warp ends a twine click loads.
/// </summary>
public class HandLoomClientSystem : ModSystem
{
    ICoreClientAPI? capi;
    GuiDialogHandLoom? dialog;
    HandLoomWarpOutline? outline;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        api.Event.LevelFinalize += WrapToolModeHotkey;
        outline = new HandLoomWarpOutline(api);
        api.Event.RegisterRenderer(outline, EnumRenderStage.AfterFinalComposition, TailorModSystem.Domain + "-handloomoutline");
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
        dialog = new GuiDialogHandLoom(capi, loom);
        dialog.TryOpen();
        return true;
    }

    /// <summary>The hand loom the player looks at (any of its blocks), else null.</summary>
    public static BlockEntityHandLoom? LookedAt(ICoreClientAPI capi, out BlockSelection? sel)
    {
        sel = capi.World.Player?.CurrentBlockSelection;
        if (sel?.Position == null) return null;
        BlockPos pos = sel.Position;
        if (capi.World.BlockAccessor.GetBlock(pos) is BlockMultiblock filler) pos = pos.AddCopy(filler.OffsetInv);
        return capi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityHandLoom;
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

/// <summary>
/// Hand loom picker (F, empty hand): cloth size line, product (cloth / small carpet / large carpet, empty loom only;
/// lane/loom-roles), threading presets of this loom's rods (only on a free warp; the
/// current one marked; fixed while rows are on the loom, no re-warp step since lane/loom-feedback-8), and the brush for loading the warp.
/// Recomposes on every sync; the brush closes it.
/// </summary>
public class GuiDialogHandLoom : GuiDialog
{
    readonly BlockEntityHandLoom loom;

    public override string ToggleKeyCombinationCode => null!;
    public override bool PrefersUngrabbedMouse => false;

    public GuiDialogHandLoom(ICoreClientAPI capi, BlockEntityHandLoom loom) : base(capi) => this.loom = loom;

    static string L(string key, params object[] args) => Lang.Get(TailorModSystem.Domain + ":" + key, args);

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        loom.ClientStateChanged += OnLoomChanged;
        Compose();
    }

    void OnLoomChanged()
    {
        if (IsOpened()) Compose();
    }

    void Compose()
    {
        ClearComposers();
        var w = loom.Work;
        var modes = CollectibleBehaviorLoomBrush.Modes(capi);
        int current = CollectibleBehaviorLoomBrush.ModeOf(capi.World.Player);
        double pitch = GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGridBase.unscaledSlotPadding;
        double width = Math.Max(modes.Length * pitch, 380);
        double y = 0;
        var compo = capi.Gui.CreateCompo(TailorModSystem.Domain + "-handloom", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(ElementStdBounds.DialogBackground().WithFixedPadding(GuiStyle.ElementToDialogPadding / 2), withTitleBar: false)
            .BeginChildElements();
        void Text(string text, double h, CairoFont font)
        {
            compo.AddStaticText(text, font, ElementBounds.Fixed(0, y, width, h));
            y += h + 2;
        }

        int worldSize = ClothSizeSetting.NewClothSize(capi.World);
        Text(L("handloom-gui-size", w.Size, w.RodCount) + (worldSize != w.Size ? " " + L("handloom-gui-size-world", worldSize) : ""), 20, CairoFont.WhiteSmallText());
        // Product (lane/loom-roles, as the other looms): cloth / small carpet / large carpet, chosen on an empty loom.
        Text(L("loom-gui-product", L(ClothProducts.LangKey(w.Cloth.Product)), w.Size, w.Cloth.Rows), 20, CairoFont.WhiteSmallText());
        if (w.WovenRows == 0)
        {
            double bx = 0;
            foreach (var p in ClothProducts.All)
            {
                var product = p;
                compo.AddSmallButton(L(ClothProducts.LangKey(p)) + (p == w.Cloth.Product ? " *" : ""), () => { loom.SendProduct(product); return true; },
                    ElementBounds.Fixed(bx, y, 120, 24), EnumButtonStyle.Small, "product" + (int)p);
                bx += 126;
            }
            y += 30;
        }
        else Text(L("loom-gui-product-fixed"), 20, CairoFont.WhiteSmallText().WithColor(GuiStyle.WarningTextColor));
        Text(L("handloom-gui-threading"), 20, CairoFont.WhiteSmallText());
        if (w.WarpFree)
        {
            double x = 0;
            foreach (int t in HandLoomPresets.For(w.RodCount))
            {
                int idx = t;
                string name = L("handloom-threading-" + HandLoomPresets.Threadings[t].Code);
                compo.AddSmallButton(t == w.Threading ? "> " + name + " <" : name, () => { loom.SendThreading(idx); return true; },
                    ElementBounds.Fixed(x, y, 105, 24), EnumButtonStyle.Small, "th" + t);
                x += 110;
            }
            y += 30;
        }
        else
        {
            Text(L("handloom-threading-" + HandLoomPresets.Threadings[w.Threading].Code) + " " + L("handloom-gui-locked"), 20, CairoFont.WhiteSmallText());
        }
        Text(L("handloom-gui-brush"), 20, CairoFont.WhiteSmallText());
        compo.AddSkillItemGrid(modes.ToList(), modes.Length, 1, OnSlotClick, ElementBounds.Fixed(0, y, modes.Length * pitch, pitch), "modes");
        y += pitch + 7;
        compo.AddDynamicText(modes[current].Name, CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, width, 30), "name");
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

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        loom.ClientStateChanged -= OnLoomChanged;
    }
}

/// <summary>
/// Light red outline around the warp ends a twine click at the fell would load (brush span, every end while sneaking),
/// only while the warp is free. Same drawing as the Artisan loom's LoomBrushOutline.
/// </summary>
public sealed class HandLoomWarpOutline : IRenderer
{
    const float Grow = 0.002f;
    readonly ICoreClientAPI capi;
    WireframeCube? cube;

    public double RenderOrder => 0.91;
    public int RenderRange => 24;

    public HandLoomWarpOutline(ICoreClientAPI capi) => this.capi = capi;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        IPlayer? player = capi.World.Player;
        if (player?.Entity == null) return;
        if (HandLoomClientSystem.LookedAt(capi, out BlockSelection? sel) is not { } loom || sel == null) return;
        if (loom.Cloth.Complete || !loom.Work.WarpFree) return;
        var part = loom.PartAt(sel.SelectionBoxIndex);
        if (part.Part != HandLoomPart.Thread) return;
        if (BlockEntityLoom.Classify(player.InventoryManager.ActiveHotbarSlot) != BlockEntityLoom.HeldKind.Twine) return;
        bool all = BlockEntityHandLoom.IsSneaking(player);
        var ends = loom.Work.WarpTargets(part.Index, CollectibleBehaviorLoomBrush.BrushSize(player), all);
        if (ends == null || ends.Count == 0) return;
        List<Cuboidf> boxes = loom.ThreadBoxes(ends.Min(), ends.Max() + 1);
        Vec3d offset = player.Entity.CameraPosOffset;
        double x = loom.Pos.X + offset.X, y = loom.Pos.InternalY + offset.Y, z = loom.Pos.Z + offset.Z;
        cube ??= WireframeCube.CreateUnitCube(capi, -1);
        float lineWidth = 2f * Math.Max(0.5f, capi.Settings.Float["wireframethickness"]);
        foreach (Cuboidf box in boxes)
            cube.Render(capi, x + box.X1 - Grow, y + box.Y1 - Grow, z + box.Z1 - Grow, box.XSize + 2 * Grow, box.YSize + 2 * Grow, box.ZSize + 2 * Grow, lineWidth, LoomBrushOutline.Color);
    }

    public void Dispose() => cube?.Dispose();
}
