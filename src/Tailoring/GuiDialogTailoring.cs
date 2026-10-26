using System;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Sewing dialog of a mannequin session, driven by the garment recipes (config/garments/*.json, lane/garment-system).
/// Step 1 (lane/garments-3): body part buttons = the dress slots this mannequin part can wear that have a garment
/// (slots without one are not shown); then the garments of the chosen slot (paged). Step 2: one row per part of the recipe: its
/// sewing slot (1 item: cloth, leather, pelt or hard item as the part allows) and a button that selects it; optional
/// parts may stay empty (their model elements are then left out). The canvas shows the garment's texture; drag inside a
/// patterned cloth part to move its pattern, nudge or rotate the selected part. The mannequin wears the layout live.
/// Step 3: "Cut and sew" once every required part is filled → the mannequin wears the garment (or the player gets it).
/// Closing keeps the session; "Cancel" gives the materials back and ends it (lane/playtest-fixes-5). Local copy of the
/// placements for drag previews; every change is a packet and the server state wins on the next sync.
/// Tabs Sew | Embroider (lane/mannequin-embroidery): the Embroider step lives in GuiDialogTailoring.Embroider.cs.
/// </summary>
public partial class GuiDialogTailoring : GuiDialogBlockEntity
{
    const double SlotPitch = 51;
    /// <summary>Step tabs (Sew | Embroider, lane/mannequin-embroidery) sit at <see cref="TabY"/>; the sew step starts below.</summary>
    const double TabY = 30;
    const double TopY = TabY + 28;
    /// <summary>One editor size for both steps (lane/sewing-polish removed the large editor): 64 x 88 garments at scale 5.</summary>
    const double CanvasW = 384, CanvasH = 440;
    const double PartColW = 214;
    const double ColW = 190;
    /// <summary>Width of the button rows above the canvas (body parts, garments, embroidery targets): the whole dialog.</summary>
    const double ListW = PartColW + CanvasW + 14 + ColW;
    const double RowPitch = 28;
    /// <summary>Garments per page; their buttons are sized to the names (<see cref="GuiFit.Flow"/>, lane/sewing-polish).</summary>
    const int GarmentsPerPage = 12;

    readonly BEBehaviorMannequinTailoring host;
    PieceAssignment[] local = Array.Empty<PieceAssignment>();
    PieceAssignment dragStart;
    (int x, int y) lastDrag;
    string shownGarment = "";
    string composedFor = "\0";
    int selectedPiece;
    int[]? pixels;
    /// <summary>Body part (dress slot) chosen in step 1, client side only; null = none chosen yet.</summary>
    string? chosenSlot;
    int page;

    protected override double FloatyDialogPosition => 1.6;

    /// <param name="embroider">Open on the Embroider step (twine in hand, lane/mannequin-embroidery).</param>
    public GuiDialogTailoring(string title, BEBehaviorMannequinTailoring host, ICoreClientAPI capi, bool embroider = false)
        : base(title, host.Inventory, host.Pos, capi)
    {
        this.host = host;
        if (IsDuplicate) return;
        embroidering = embroider || host.Garment == null && host.WornTailoredSlots().Any() && host.Inventory.Empty;
        SyncLocal();
        SetupDialog();
    }

    public bool Dragging => Canvas?.Dragging == true || EmbCanvas?.Painting == true;

    static string L(string key, params object[] args) => Lang.Get(TailorModSystem.Domain + ":" + key, args);

    void SetupDialog()
    {
        if (embroidering)
        {
            SetupEmbroider();
            return;
        }
        var garments = host.AllowedGarments;
        var g = host.Garment;
        composedFor = g?.Code ?? "";
        int parts = g?.Parts.Length ?? 0;

        double listW = ListW, partColW = PartColW;

        // Row 1: body parts (dress slots) that have a garment. A picked garment selects its slot.
        string[] slots = MannequinFit.Slots(garments);
        if (chosenSlot == null || Array.IndexOf(slots, chosenSlot) < 0)
        {
            chosenSlot = g?.DressType ?? (slots.Length == 1 ? slots[0] : null);
            page = g == null ? 0 : MannequinFit.PageOf(Array.IndexOf(MannequinFit.ForSlot(garments, chosenSlot), g), GarmentsPerPage);
        }
        // Buttons sized to their labels ("Upper body over"), flowed into rows (lane/sewing-polish).
        var (slotBounds, slotFlowRows) = GuiFit.Flow(slots.Select(sl => Lang.Get("clothcategory-" + sl)), 0, TopY, listW, 70, RowPitch);
        var slotButtons = slots.Select((sl, i) => (sl, slotBounds[i])).ToArray();
        int slotRows = Math.Max(1, slotFlowRows);
        // Row 2: the garments of that slot, a page at a time.
        var slotGarments = MannequinFit.ForSlot(garments, chosenSlot);
        int pages = MannequinFit.Pages(slotGarments.Length, GarmentsPerPage);
        page = Math.Clamp(page, 0, pages - 1);
        double garmentLabelY = TopY + slotRows * RowPitch + 4;
        ElementBounds garmentLabel = ElementBounds.Fixed(0, garmentLabelY, 400, 18);
        ElementBounds prevPage = ElementBounds.Fixed(listW - 124, garmentLabelY - 3, 30, 22);
        ElementBounds pageText = ElementBounds.Fixed(listW - 90, garmentLabelY, 52, 18);
        ElementBounds nextPage = ElementBounds.Fixed(listW - 34, garmentLabelY - 3, 30, 22);
        double garmentsY = garmentLabelY + 22;
        var onPage = slotGarments.Skip(page * GarmentsPerPage).Take(GarmentsPerPage).ToArray();
        var (garmentBounds, garmentFlowRows) = GuiFit.Flow(onPage.Select(gd => L("garment-" + gd.Code)), 0, garmentsY, listW, 90, RowPitch);
        var garmentButtons = onPage.Select((gd, i) => (gd, garmentBounds[i])).ToArray();
        int garmentRows = chosenSlot == null ? 0 : Math.Max(1, garmentFlowRows);
        double partsY = garmentsY + garmentRows * RowPitch + 22;
        ElementBounds partsLabel = ElementBounds.Fixed(0, partsY - 18, partColW, 18);
        // Left: one row per part (slot + select button)
        var partRows = Enumerable.Range(0, parts).Select(i =>
        {
            double y = partsY + i * SlotPitch;
            return (slot: ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, y, 1, 1), button: ElementBounds.Fixed(56, y + 12, partColW - 62, 24));
        }).ToArray();
        // Middle: canvas; right: controls
        ElementBounds canvasBounds = ElementBounds.Fixed(PartColW, partsY, CanvasW, CanvasH);
        double colX = PartColW + CanvasW + 14, y0 = partsY, bottom = partsY + Math.Max(CanvasH, parts * SlotPitch);
        double colW = ColW;
        ElementBounds pieceText = ElementBounds.Fixed(colX, y0, colW, 58); y0 += 62;
        ElementBounds left = ElementBounds.Fixed(colX, y0, 34, 24), right = ElementBounds.Fixed(colX + 40, y0, 34, 24);
        ElementBounds up = ElementBounds.Fixed(colX + 80, y0, 34, 24), down = ElementBounds.Fixed(colX + 120, y0, 34, 24); y0 += 32;
        ElementBounds rotate = ElementBounds.Fixed(colX, y0, colW, 24); y0 += 28;
        ElementBounds sleeves = ElementBounds.Fixed(colX, y0, colW, 24); y0 += 34; // lane/body-wrap; own row: "Sleeves: joined" did not fit
        ElementBounds status = ElementBounds.Fixed(colX, y0, colW, Math.Max(60, bottom - y0 - 64));
        ElementBounds cut = ElementBounds.Fixed(colX, bottom - 58, colW, 26);
        ElementBounds cancel = ElementBounds.Fixed(colX, bottom - 26, colW, 26);

        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        bgBounds.WithChildren(canvasBounds, status, cut, cancel);
        foreach (var row in partRows) bgBounds.WithChildren(row.slot, row.button);

        ClearComposers();
        var c = capi.Gui
            .CreateCompo("actailortailoring" + BlockEntityPosition, DialogBounds())
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(DialogTitle, () => TryClose())
            .BeginChildElements(bgBounds);
        AddTabs(c);
        foreach (var (sl, b) in slotButtons)
        {
            string slot = sl;
            c.AddToggleButton(Lang.Get("clothcategory-" + slot), CairoFont.SmallButtonText(), _ => OnChooseSlot(slot), b, "slot-" + slot);
        }
        c.AddStaticText(chosenSlot == null ? L("tailoring-gui-pickslot") : L("tailoring-gui-slotgarments", Lang.Get("clothcategory-" + chosenSlot)),
            CairoFont.WhiteSmallText(), garmentLabel);
        if (pages > 1)
        {
            c.AddSmallButton("<", () => OnPage(-1), prevPage)
                .AddStaticText(L("tailoring-gui-page", page + 1, pages), CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center), pageText)
                .AddSmallButton(">", () => OnPage(1), nextPage);
        }
        foreach (var (gd, b) in garmentButtons)
        {
            string code = gd.Code;
            c.AddToggleButton(L("garment-" + code), CairoFont.SmallButtonText(), _ => { host.SendSelectGarment(code); Rebuild(); }, b, "garment-" + code);
        }
        c.AddStaticText(L(g == null ? "tailoring-gui-parts-none" : "tailoring-gui-parts"), CairoFont.WhiteSmallText(), partsLabel);
        for (int i = 0; i < parts; i++)
        {
            int part = i;
            var def = g!.Parts[i];
            string label = L("part-" + def.Code) + (def.Optional ? " " + L("tailoring-gui-optional") : def.IsFixed ? " " + L("tailoring-gui-fixed") : "");
            // A fixed (premade) part has no sewing slot: it always shows the recipe's texture.
            if (!def.IsFixed) c.AddItemSlotGrid(Inventory, DoSendPacket, 1, new[] { i }, partRows[i].slot, "slot" + i);
            c.AddFitButton(label, () => { OnSelectPiece(part); return true; }, partRows[i].button);
        }
        var canvas = new GuiElementTailorCanvas(capi, canvasBounds, () => host.Garment, () => pixels,
            () => selectedPiece, OnSelectPiece, OnDrag, sewView);
        HookCanvas(canvas);
        c.AddInteractiveElement(canvas, "canvas")
            .AddDynamicText("", CairoFont.WhiteSmallText(), pieceText, "piece")
            .AddSmallButton("<", () => Change(a => new PieceAssignment(-1, a.Dx - 1, a.Dy, a.Rot)), left)
            .AddSmallButton(">", () => Change(a => new PieceAssignment(-1, a.Dx + 1, a.Dy, a.Rot)), right)
            .AddSmallButton("^", () => Change(a => new PieceAssignment(-1, a.Dx, a.Dy - 1, a.Rot)), up)
            .AddSmallButton("v", () => Change(a => new PieceAssignment(-1, a.Dx, a.Dy + 1, a.Rot)), down)
            .AddFitButton(L("tailoring-gui-rotate"), () => Change(a => new PieceAssignment(-1, a.Dx, a.Dy, a.Rot + 1)), rotate)
            .AddFitButton(SleevesLabel(g), OnToggleSleeves, sleeves, "sleeves")
            .AddDynamicText("", CairoFont.WhiteSmallText(), status, "status")
            .AddFitButton(L("tailoring-gui-cut"), () => { host.SendCut(); return true; }, cut)
            .AddFitButton(L("tailoring-gui-cancel"), () => { host.SendCancel(); TryClose(); return true; }, cancel)
            .EndChildElements();
        SingleComposer = c.Compose();
        SetTabs();
        if (chosenSlot != null) SingleComposer.GetToggleButton("slot-" + chosenSlot)?.SetValue(true);
        if (g != null) SingleComposer.GetToggleButton("garment-" + g.Code)?.SetValue(true);
        Refresh();
    }

    void OnChooseSlot(string slot)
    {
        if (chosenSlot != slot) page = 0;
        chosenSlot = slot;
        Rebuild();
    }

    bool OnPage(int delta)
    {
        page += delta;
        Rebuild();
        return true;
    }

    /// <summary>Rebuilds the dialog (toggle states, garment page); the server sync rebuilds again when the garment changed.</summary>
    void Rebuild() => capi.Event.EnqueueMainThreadTask(SetupDialog, "actailor-tailoring-gui");

    GuiElementTailorCanvas? Canvas => SingleComposer?.GetElement("canvas") as GuiElementTailorCanvas;

    void SyncLocal()
    {
        local = host.Session.Pieces.ToArray();
        if (host.Session.GarmentCode != shownGarment)
        {
            shownGarment = host.Session.GarmentCode;
            selectedPiece = 0;
        }
        if (selectedPiece >= local.Length) selectedPiece = local.Length > 0 ? 0 : -1;
    }

    /// <summary>Called by the behaviour after a tree sync or a slot change (main thread).</summary>
    public void OnStateChanged()
    {
        if (SingleComposer == null || !IsOpened()) return;
        if (Dragging) return; // keep the drag preview; the release commits and resyncs
        SyncLocal();
        if (embroidering)
        {
            OnStateChangedEmbroider();
            return;
        }
        if ((host.Garment?.Code ?? "") != composedFor) SetupDialog(); // other recipe: other part rows
        else Refresh();
    }

    void Refresh()
    {
        var g = host.Garment;
        pixels = g == null ? null : GarmentLayout.Compose(g, local, host.MaterialInSlot, MaterialTextureCache.For(capi), host.PendingOf(GarmentEmbroidery.TargetSession) ?? host.Session.Embroidery, host.SessionWrap(g)); // lane/sewing-polish: pending stitches show too
        Canvas?.Redraw();
        UpdateTexts(g);
    }

    void UpdateTexts(GarmentDef? g)
    {
        if (SingleComposer == null) return;
        string piece = "";
        if (g != null && selectedPiece >= 0 && selectedPiece < local.Length && selectedPiece < g.Parts.Length)
        {
            var a = local[selectedPiece];
            var def = g.Parts[selectedPiece];
            string name = L("tailoring-gui-piecetakes", L("part-" + def.Code), string.Join(", ", def.Materials.Select(KindName)));
            var m = host.MaterialInSlot(selectedPiece);
            piece = def.IsFixed
                ? L("tailoring-gui-piece-fixed", L("part-" + def.Code))
                : m == null
                ? L("tailoring-gui-piece-empty", name, L(def.Optional ? "tailoring-gui-optional" : "tailoring-gui-required"))
                : m.IsPatterned
                    ? L("tailoring-gui-piece", name, a.Dx, a.Dy, a.Rot * 90)
                    : L("tailoring-gui-piece-material", name, KindName(m.Kind), a.Rot * 90);
        }
        SingleComposer.GetDynamicText("piece")?.SetNewText(piece);

        var sb = new StringBuilder();
        if (g == null)
        {
            sb.AppendLine(L("tailoring-gui-pickgarment"));
        }
        else
        {
            sb.AppendLine(L("tailoring-gui-garment", L("garment-" + g.Code)));
            int filled = Enumerable.Range(0, g.Parts.Length).Count(p => !g.Parts[p].IsFixed && host.MaterialInSlot(p) != null);
            int required = g.Parts.Count(p => !p.Optional && !p.IsFixed);
            int requiredFilled = Enumerable.Range(0, g.Parts.Length).Count(p => !g.Parts[p].Optional && !g.Parts[p].IsFixed && host.MaterialInSlot(p) != null);
            sb.AppendLine(L("tailoring-gui-filled", requiredFilled, required, filled));
            string? problem = host.CutProblem();
            sb.AppendLine(problem == null ? L(host.WillDress(g) ? "tailoring-gui-ready" : "tailoring-gui-readytoplayer") : L(problem));
        }
        SingleComposer.GetDynamicText("status")?.SetNewText(sb.ToString());
    }

    void OnSelectPiece(int piece)
    {
        selectedPiece = piece;
        Refresh();
    }

    static string KindName(string? kind) => kind == null ? L("tailoring-gui-nocloth") : L("material-" + kind);

    /// <summary>Only patterned woven cloth has a pattern offset (item textures tile from the part's corner).</summary>
    bool Patterned(int piece) => piece >= 0 && piece < local.Length && host.MaterialInSlot(piece)?.IsPatterned == true;

    void OnDrag(int piece, int dCellsX, int dCellsY, DragPhase phase)
    {
        if (piece < 0 || piece >= local.Length || !Patterned(piece)) return;
        if (phase == DragPhase.Begin) dragStart = local[piece];
        else if (phase == DragPhase.Move && lastDrag == (dCellsX, dCellsY)) return; // same cell: nothing to redraw
        lastDrag = (dCellsX, dCellsY);
        var a = new PieceAssignment(-1, dragStart.Dx + dCellsX, dragStart.Dy + dCellsY, dragStart.Rot);
        local[piece] = a;
        Refresh();
        if (phase != DragPhase.Begin) host.SetLocalPreview(local);
        if (phase == DragPhase.End && (dCellsX != 0 || dCellsY != 0)) host.SendSetPiece(piece, a);
    }

    bool Change(Func<PieceAssignment, PieceAssignment> f)
    {
        if (selectedPiece < 0 || selectedPiece >= local.Length) return true;
        var g = host.Garment;
        if (g != null && selectedPiece < g.Parts.Length && g.Parts[selectedPiece].IsFixed) return true; // premade: not moved or turned
        local[selectedPiece] = f(local[selectedPiece]);
        host.SendSetPiece(selectedPiece, local[selectedPiece]);
        host.SetLocalPreview(local);
        Refresh();
        return true;
    }

    /// <summary>Handbook guide pages (assets/actailor/config/handbook/111-sewing.json, 112-embroidery.json).</summary>
    public const string SewingGuidePage = "actailor:sewing-guide", EmbroideryGuidePage = "actailor:embroidery-guide";

    /// <summary>
    /// Opens the survival handbook on <paramref name="pageCode"/> through the vanilla <c>handbook://</c> link protocol
    /// (shared helper <see cref="HandbookLink.Open"/>, also used by the draft window). Missing protocol: a hint instead.
    /// </summary>
    void OpenGuide(string pageCode)
    {
        if (!HandbookLink.Open(capi, pageCode)) capi.TriggerIngameError(this, "actailor-guide", L("tailoring-gui-guide-missing"));
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        OnStateChanged();
    }
}
