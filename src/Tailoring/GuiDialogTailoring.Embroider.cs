using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Embroider step of the sewing dialog (lane/mannequin-embroidery, docs/embroidery.md "Mannequin"): pick a target (the
/// garment in progress, or a tailored garment the mannequin wears), a twine you carry and a brush (1/2/4 texels), then
/// paint stitches on the garment texture shown flat (<see cref="GuiElementEmbroideryCanvas"/>); right click picks out.
/// lane/sewing-polish (docs/embroidery.md "Shared pending stitches"): the window paints the SAME pending stitches as a
/// right click on the mannequin (<see cref="EmbroideryDrafts"/>, on the server, synced). Each finished stroke is sent
/// (packet 1212, free); undo / redo / discard (1213) and Apply (1204, charged) act on that shared state. Leaving the tab,
/// switching target or closing the window keeps everything pending. The mannequin shows the stroke being painted at
/// once (<see cref="BEBehaviorMannequinTailoring.SetLocalEmbroidery"/>), then the synced pending layer.
/// </summary>
public partial class GuiDialogTailoring
{
    const double EmbLeftW = 200, EmbCanvasW = 384, EmbCanvasH = 440, EmbRowH = 26;
    const int EmbMaxPalette = 10;
    const int NoTarget = int.MinValue;

    bool embroidering;
    int embTarget = NoTarget;
    /// <summary>
    /// Applied stitches of the target as last synced (cost / changes shown), the pending layer last synced or sent (diff
    /// base of the next stroke), and the working copy being painted.
    /// </summary>
    Embroidery? embApplied, embBase, embWorking;
    string? embCode;
    int embBrush = 1;
    bool embPickOut;
    bool[]? embMask;
    int[]? embPixels;
    GarmentDef? embGarment;
    string embShapeKey = "";

    GuiElementEmbroideryCanvas? EmbCanvas => SingleComposer?.GetElement("embcanvas") as GuiElementEmbroideryCanvas;

    /// <summary>Sew | Embroider tabs (both steps).</summary>
    void AddTabs(GuiComposer c)
    {
        c.AddFitToggle(L("tailoring-tab-sew"), on => OnTab(false), ElementBounds.Fixed(0, TabY, 120, 24), "tab-sew")
            .AddFitToggle(L("tailoring-tab-embroider"), on => OnTab(true), ElementBounds.Fixed(124, TabY, 120, 24), "tab-emb")
            // lane/sewing-polish: the how-to lives in the handbook; this opens the page of the current tab.
            .AddFitButton(L("tailoring-gui-guide"), () => { OpenGuide(embroidering ? EmbroideryGuidePage : SewingGuidePage); return true; },
                ElementBounds.Fixed(256, TabY, 100, 24));
    }

    void SetTabs()
    {
        SingleComposer?.GetToggleButton("tab-sew")?.SetValue(!embroidering);
        SingleComposer?.GetToggleButton("tab-emb")?.SetValue(embroidering);
    }

    void OnTab(bool embroider)
    {
        if (embroider != embroidering)
        {
            embroidering = embroider;
            embTarget = NoTarget;
            host.SetLocalEmbroidery(GarmentEmbroidery.TargetSession, null); // the pending stitches stay (shared, on the server)
        }
        Rebuild();
    }

    /// <summary>Targets: the garment in progress, then each tailored garment the mannequin wears.</summary>
    List<(int target, string label)> EmbTargets()
    {
        var list = new List<(int, string)>();
        if (host.Garment is { } g) list.Add((GarmentEmbroidery.TargetSession, L("tailoring-emb-target-session", L("garment-" + g.Code))));
        foreach (int s in host.WornTailoredSlots())
        {
            if (host.WornGarment(s) is { } w) list.Add((s, L("tailoring-emb-target-worn", L("garment-" + w.def.Code))));
        }
        return list;
    }

    /// <summary>Twine in the player's hotbar + backpack: code, name, colour, count (first seen first).</summary>
    List<(string code, string name, string hex, int count)> EmbPalette()
    {
        var list = new List<(string code, string name, string hex, int count)>();
        var player = capi.World.Player;
        if (player == null) return list;
        foreach (var slot in Tapestry.LoomCore.PlayerSlots(player))
        {
            var stack = slot.Itemstack;
            if (!Loom.LoomTwine.IsTwine(stack)) continue;
            string code = stack!.Collectible.Code.ToString();
            int i = list.FindIndex(e => e.code == code);
            if (i >= 0) list[i] = (list[i].code, list[i].name, list[i].hex, list[i].count + stack.StackSize);
            else list.Add((code, stack.GetName(), TwineColors.HexOf(stack), stack.StackSize));
        }
        return list;
    }

    string EmbShapeKey(List<(int target, string label)> targets, List<(string code, string name, string hex, int count)> palette)
        => string.Join(",", targets.Select(t => t.target + ":" + t.label)) + "|" + string.Join(",", palette.Take(EmbMaxPalette).Select(p => p.code));

    /// <summary>Working copy = the target's shared pending stitches, else its applied ones (target change, any sync that changed them).</summary>
    void EmbRebase()
    {
        var t = host.EmbroideryTargetOf(embTarget);
        embApplied = t?.Applied?.Clone();
        embBase = t?.Shown?.Clone();
        embWorking = t == null ? null : embBase?.Clone() ?? GarmentEmbroidery.New(t.Garment);
        host.SetLocalEmbroidery(embTarget, null); // the synced layer is what shows now
    }

    /// <summary>Same stitches and same paid set (an Apply changes only the paid set of an unchanged layer).</summary>
    static bool SameLayer(Embroidery? a, Embroidery? b)
        => GarmentEmbroidery.Same(a, b) && (a?.Paid ?? new HashSet<string>()).SetEquals(b?.Paid ?? new HashSet<string>());

    /// <summary>A stroke ends (mouse up): it goes to the shared pending stitches as one undo step.</summary>
    void OnEmbStroke(bool begin)
    {
        if (begin) return;
        SendPendingStroke();
    }

    void SendPendingStroke()
    {
        if (embWorking == null || GarmentEmbroidery.Same(embBase, embWorking)) return;
        host.SendEmbStroke(embTarget, embBase, embWorking, newStroke: true);
        embBase = embWorking.Clone(); // optimistic; the sync confirms
    }

    void SetEmbBrush(int brush)
    {
        embBrush = brush;
        SetEmbToggles();
    }

    /// <summary>[ / ] keys: next smaller / larger brush of <see cref="GarmentEmbroidery.Brushes"/>.</summary>
    void StepEmbBrush(int dir)
    {
        var b = GarmentEmbroidery.Brushes;
        int i = Math.Clamp(Array.IndexOf(b, embBrush) + dir, 0, b.Length - 1);
        SetEmbBrush(b[i]);
    }

    /// <summary>Undo / redo / discard: on the shared pending stitches (strokes from the window and from the mannequin alike).</summary>
    void EmbEdit(EmbEditOp op)
    {
        if (embWorking == null || EmbCanvas?.Painting == true) return;
        SendPendingStroke();
        host.SendEmbEdit(embTarget, op);
    }

    void EmbUndo() => EmbEdit(EmbEditOp.Undo);
    void EmbRedo() => EmbEdit(EmbEditOp.Redo);

    void SetupEmbroider()
    {
        var targets = EmbTargets();
        var palette = EmbPalette();
        embShapeKey = EmbShapeKey(targets, palette);
        if (!targets.Any(t => t.target == embTarget))
        {
            // A target with pending stitches first (they wait for Apply / Discard), else the first one.
            int pending = host.PendingTargets().Where(p => targets.Any(t => t.target == p)).DefaultIfEmpty(NoTarget).First();
            embTarget = pending != NoTarget ? pending : targets.Count > 0 ? targets[0].target : NoTarget;
            EmbRebase();
        }
        if (embCode == null || !palette.Any(p => p.code == embCode)) embCode = palette.FirstOrDefault().code;

        double leftW = EmbLeftW;
        var (targetBounds, targetFlowRows) = GuiFit.Flow(targets.Select(t => t.label), 0, TopY, EmbLeftW + 14 + EmbCanvasW, 120, RowPitch);
        int targetRows = Math.Max(1, targetFlowRows);
        double contentY = TopY + targetRows * RowPitch + 8;
        double y = contentY;
        ElementBounds paletteLabel = ElementBounds.Fixed(0, y, leftW, 18); y += 20;
        var shown = palette.Take(EmbMaxPalette).ToList();
        var paletteButtons = shown.Select((p, i) => (p.code, p.name, ElementBounds.Fixed(0, y + i * EmbRowH, leftW, 22))).ToList();
        y += Math.Max(1, shown.Count) * EmbRowH + 6;
        ElementBounds brushLabel = ElementBounds.Fixed(0, y + 3, 60, 18);
        var brushButtons = GarmentEmbroidery.Brushes.Select((b, i) => (b, ElementBounds.Fixed(62 + i * 46, y, 42, 22))).ToList();
        y += EmbRowH;
        ElementBounds pickOut = ElementBounds.Fixed(0, y, leftW, 22); y += EmbRowH + 6;

        double colX = 0, colW = EmbLeftW, bottom = contentY + EmbCanvasH;
        ElementBounds canvasBounds = ElementBounds.Fixed(EmbLeftW + 14, contentY, EmbCanvasW, EmbCanvasH);
        ElementBounds apply = ElementBounds.Fixed(colX, y, colW, 26); y += 30;
        ElementBounds discard = ElementBounds.Fixed(colX, y, colW, 26); y += 30;
        double halfW = Math.Floor((colW - 4) / 2);
        ElementBounds undo = ElementBounds.Fixed(colX, y, halfW, 24), redo = ElementBounds.Fixed(colX + halfW + 4, y, halfW, 24); y += 32;
        ElementBounds status = ElementBounds.Fixed(colX, y, colW, Math.Max(120, bottom - y));

        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        bgBounds.WithChildren(canvasBounds, status, apply, discard);

        ClearComposers();
        var c = capi.Gui
            .CreateCompo("actailortailoring" + BlockEntityPosition, DialogBounds())
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(DialogTitle, () => TryClose())
            .BeginChildElements(bgBounds);
        AddTabs(c);
        if (targets.Count == 0)
        {
            c.AddStaticText(L("tailoring-emb-notarget"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, TopY + 3, 600, 18));
        }
        for (int i = 0; i < targets.Count; i++)
        {
            int t = targets[i].target;
            c.AddToggleButton(targets[i].label, CairoFont.SmallButtonText(), _ => OnEmbTarget(t), targetBounds[i], "embtarget-" + i);
        }
        c.AddStaticText(L(shown.Count == 0 ? "tailoring-emb-palette-none" : "tailoring-emb-palette"), CairoFont.WhiteSmallText(), paletteLabel);
        foreach (var (code, name, b) in paletteButtons)
        {
            string cd = code;
            c.AddFitToggle(name, _ => { embCode = cd; embPickOut = false; SetEmbToggles(); }, b, "embtwine-" + cd);
        }
        c.AddStaticText(L("tailoring-emb-brush"), CairoFont.WhiteSmallText(), brushLabel);
        foreach (var (b, bb) in brushButtons)
        {
            int br = b;
            c.AddToggleButton(br.ToString(), CairoFont.SmallButtonText(), _ => SetEmbBrush(br), bb, "embbrush-" + br);
        }
        var canvas = new GuiElementEmbroideryCanvas(capi, canvasBounds, () => embGarment, () => embPixels, () => embMask,
            (x, yy) => embWorking?.BrushCells(x, yy, embBrush) ?? Enumerable.Empty<(int, int)>(), OnEmbPaint, OnEmbStroke, embView)
        {
            PickOutHeld = ShiftHeld,
            PickOutActive = () => embPickOut || ShiftHeld(),
        };
        HookCanvas(canvas);
        c.AddFitToggle(L("tailoring-emb-pickout"), on => { embPickOut = on; SetEmbToggles(); }, pickOut, "embpickout")
            .AddFitButton(L("tailoring-emb-apply"), OnEmbApply, apply)
            .AddFitButton(L("tailoring-emb-discard"), () => { EmbEdit(EmbEditOp.Discard); return true; }, discard)
            .AddFitButton(L("tailoring-xl-undo"), () => { EmbUndo(); return true; }, undo)
            .AddFitButton(L("tailoring-xl-redo"), () => { EmbRedo(); return true; }, redo)
            .AddDynamicText("", CairoFont.WhiteSmallText(), status, "embstatus")
            .AddInteractiveElement(canvas, "embcanvas")
            .EndChildElements();
        SingleComposer = c.Compose();
        SetTabs();
        int ti = targets.FindIndex(t => t.target == embTarget);
        if (ti >= 0) SingleComposer.GetToggleButton("embtarget-" + ti)?.SetValue(true);
        SetEmbToggles();
        RefreshEmbroider();
    }

    void SetEmbToggles()
    {
        if (SingleComposer == null) return;
        foreach (var (code, _, _, _) in EmbPalette().Take(EmbMaxPalette))
            SingleComposer.GetToggleButton("embtwine-" + code)?.SetValue(!embPickOut && code == embCode);
        foreach (int b in GarmentEmbroidery.Brushes) SingleComposer.GetToggleButton("embbrush-" + b)?.SetValue(b == embBrush);
        SingleComposer.GetToggleButton("embpickout")?.SetValue(embPickOut);
    }

    void OnEmbTarget(int target)
    {
        if (target != embTarget)
        {
            embTarget = target;
            EmbRebase();
        }
        Rebuild();
    }

    /// <summary>After a sync or slot change: rebuild when targets or twine changed; rebase when the applied stitches changed.</summary>
    void OnStateChangedEmbroider()
    {
        if (EmbShapeKey(EmbTargets(), EmbPalette()) != embShapeKey || host.EmbroideryTargetOf(embTarget) == null)
        {
            SetupEmbroider();
            return;
        }
        var t = host.EmbroideryTargetOf(embTarget)!;
        if (!SameLayer(t.Applied, embApplied) || !SameLayer(t.Shown, embBase) || embWorking == null) EmbRebase();
        RefreshEmbroider();
    }

    void RefreshEmbroider()
    {
        var t = host.EmbroideryTargetOf(embTarget);
        embGarment = t?.Garment;
        if (t == null || embWorking == null)
        {
            embMask = null;
            embPixels = null;
        }
        else
        {
            var pieces = embTarget == GarmentEmbroidery.TargetSession ? local : t.Pieces;
            embMask = GarmentEmbroidery.Mask(t.Garment, t.MaterialOf);
            embPixels = GarmentLayout.Compose(t.Garment, pieces, t.MaterialOf, MaterialTextureCache.For(capi), embWorking, t.Wrap);
        }
        EmbCanvas?.Redraw();
        UpdateEmbStatus(t);
    }

    void UpdateEmbStatus(BEBehaviorMannequinTailoring.EmbroideryTargetInfo? t)
    {
        if (SingleComposer == null) return;
        var sb = new StringBuilder();
        if (t == null || embWorking == null)
        {
            sb.AppendLine(L("tailoring-emb-notarget"));
        }
        else
        {
            sb.AppendLine(L("tailoring-emb-stitches", embWorking.StitchCount, embWorking.UsedCodes().Count));
            int changed = GarmentEmbroidery.Changed(embApplied, embWorking);
            sb.AppendLine(changed == 0 ? L("tailoring-emb-nochanges") : L("tailoring-emb-changes", changed));
            var cost = embWorking.Cost();
            if (changed > 0)
            {
                var palette = EmbPalette();
                sb.AppendLine(cost.Count == 0 ? L("tailoring-emb-cost-free") : L("tailoring-emb-cost", string.Join(", ", cost.Keys.Select(code =>
                {
                    var p = palette.FirstOrDefault(e => e.code == code);
                    var item = capi.World.GetItem(new AssetLocation(code));
                    string name = item == null ? code : item.GetHeldItemName(new ItemStack(item));
                    return L("tailoring-emb-cost-line", name, cost[code], p.count);
                }))));
            }
        }
        SingleComposer.GetDynamicText("embstatus")?.SetNewText(sb.ToString());
    }

    void OnEmbPaint(int x, int y, bool erase)
    {
        if (embWorking == null || embMask == null) return;
        Embroidery.Result r;
        if (erase || embPickOut)
        {
            r = GarmentEmbroidery.PickOut(embWorking, x, y, embBrush);
        }
        else if (embCode == null)
        {
            capi.TriggerIngameError(this, "actailor-emb", L("tailoring-emb-err-notwine"));
            return;
        }
        else
        {
            var item = capi.World.GetItem(new AssetLocation(embCode));
            string hex = item == null ? TwineColors.PlainHex : TwineColors.HexOf(new ItemStack(item));
            r = GarmentEmbroidery.Paint(embWorking, embMask, x, y, embBrush, embCode, hex);
        }
        if (r == Embroidery.Result.PaletteFull) capi.TriggerIngameError(this, "actailor-emb", L("tailor-err-emb-palette"));
        if (r != Embroidery.Result.Ok) return;
        host.SetLocalEmbroidery(embTarget, embWorking); // the stroke shows on the mannequin before the server has it
        RefreshEmbroider();
    }

    bool OnEmbApply()
    {
        if (embWorking == null) return true;
        SendPendingStroke();
        if (host.PendingOf(embTarget) == null && GarmentEmbroidery.Same(embApplied, embWorking)) return true;
        host.SendEmbApply(embTarget);
        return true;
    }
}
