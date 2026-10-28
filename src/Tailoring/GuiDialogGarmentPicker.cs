using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Small garment picker of the in-world flow (lane/drape-cut-sew): F at a mannequin (empty hand or a sewing material),
/// or right click on the mannequin's body with nothing started (filtered to the body region under the crosshair,
/// <see cref="DrapeFlow.PickerSlots"/>). Same two steps as the sewing dialog (<see cref="MannequinFit"/>): body part,
/// then garment (paged). Picking sends packet 1208 and closes; the mannequin then shows the garment as a ghost.
/// "Sewing window" opens the full dialog (fallback).
/// </summary>
public class GuiDialogGarmentPicker : GuiDialog
{
    const int PerPage = 12;
    /// <summary>Width of the button rows; buttons are sized to their labels (<see cref="GuiFit.Flow"/>, lane/sewing-polish).</summary>
    const double Width = 552, RowPitch = 28;

    readonly BEBehaviorMannequinTailoring host;
    readonly string[] slots;
    string? chosen;
    int page;

    public override string ToggleKeyCombinationCode => null!;
    public override bool PrefersUngrabbedMouse => false;

    public GuiDialogGarmentPicker(ICoreClientAPI capi, BEBehaviorMannequinTailoring host, double? meshY) : base(capi)
    {
        this.host = host;
        slots = DrapeFlow.PickerSlots(MannequinFit.Slots(host.AllowedGarments), meshY);
        chosen = host.Garment?.DressType is { } d && slots.Contains(d) ? d : slots.Length == 1 ? slots[0] : null;
        if (chosen != null && host.Garment != null)
            page = MannequinFit.PageOf(Array.IndexOf(MannequinFit.ForSlot(host.AllowedGarments, chosen), host.Garment.Root), PerPage);
    }

    static string L(string key, params object[] args) => Lang.Get(TailorModSystem.Domain + ":" + key, args);

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        host.ClientStateChanged += OnStateChanged;
        Compose();
    }

    public override void OnGuiClosed()
    {
        host.ClientStateChanged -= OnStateChanged;
        base.OnGuiClosed();
    }

    void Compose()
    {
        ClearComposers();
        double width = Width;
        double y = 0;
        var c = capi.Gui.CreateCompo(TailorModSystem.Domain + "-garmentpicker", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(ElementStdBounds.DialogBackground().WithFixedPadding(GuiStyle.ElementToDialogPadding / 2), withTitleBar: false)
            .BeginChildElements();
        c.AddStaticText(L("tailoring-flow-picker-title"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, width, 24));
        y += 28;
        var (slotBounds, slotRows) = GuiFit.Flow(slots.Select(sl => Lang.Get("clothcategory-" + sl)), 0, y, width, 70, RowPitch);
        for (int i = 0; i < slots.Length; i++)
        {
            string slot = slots[i];
            c.AddToggleButton(Lang.Get("clothcategory-" + slot), CairoFont.SmallButtonText(), _ => { if (chosen != slot) page = 0; chosen = slot; Recompose(); },
                slotBounds[i], "slot-" + slot);
        }
        y += Math.Max(1, slotRows) * RowPitch + 4;
        c.AddStaticText(chosen == null ? L("tailoring-gui-pickslot") : L("tailoring-gui-slotgarments", Lang.Get("clothcategory-" + chosen)),
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y, width - 130, 18));
        var garments = MannequinFit.ForSlot(host.AllowedGarments, chosen);
        int pages = MannequinFit.Pages(garments.Length, PerPage);
        page = Math.Clamp(page, 0, pages - 1);
        if (pages > 1)
        {
            c.AddSmallButton("<", () => { page--; Recompose(); return true; }, ElementBounds.Fixed(width - 124, y - 3, 30, 22))
                .AddStaticText(L("tailoring-gui-page", page + 1, pages), CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center), ElementBounds.Fixed(width - 90, y, 52, 18))
                .AddSmallButton(">", () => { page++; Recompose(); return true; }, ElementBounds.Fixed(width - 34, y - 3, 30, 22));
        }
        y += 22;
        var onPage = garments.Skip(page * PerPage).Take(PerPage).ToArray();
        var (garmentBounds, garmentRows) = GuiFit.Flow(onPage.Select(gd => L("garment-" + gd.Code)), 0, y, width, 90, RowPitch);
        for (int i = 0; i < onPage.Length; i++)
        {
            string code = onPage[i].Code;
            c.AddToggleButton(L("garment-" + code), CairoFont.SmallButtonText(), _ => Pick(code), garmentBounds[i], "garment-" + code);
        }
        y += (chosen == null ? 0 : Math.Max(1, garmentRows)) * RowPitch + 8;
        // lane/modular-parts: the garment in progress with options: one row of buttons per part with options (packet 1214).
        optionRows.Clear();
        if (host.Garment is { } cur)
        {
            foreach (int part in cur.ChoosableParts)
            {
                var def = cur.Parts[part];
                string[] names = Enumerable.Range(0, def.Options.Length).Select(k => L(def.OptionName(k))).ToArray();
                double labelW = 150;
                c.AddStaticText(L("tailoring-gui-option-of", L("part-" + def.Code)), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y + 4, labelW, 18));
                var (bounds, nrows) = GuiFit.Flow(names, labelW + 6, y, width - labelW - 6, 60, RowPitch);
                for (int k = 0; k < names.Length; k++)
                {
                    int p = part, o = k;
                    c.AddToggleButton(names[k], CairoFont.SmallButtonText(), _ => { if (host.Garment?.Parts[p].Chosen != o) host.SendSetOption(p, o); else Recompose(); },
                        bounds[k], $"opt-{part}-{k}");
                    optionRows.Add((part, k));
                }
                y += Math.Max(1, nrows) * RowPitch;
            }
            if (optionRows.Count > 0) y += 8;
        }
        c.AddStaticText(L("tailoring-flow-picker-hint"), CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, width, 36));
        y += 40;
        c.AddFitButton(L("tailoring-flow-picker-window"), () => { TryClose(); host.OpenSewingWindow(); return true; }, ElementBounds.Fixed(0, y, 180, 24))
            .AddFitButton(L("tailoring-flow-picker-close"), () => TryClose(), ElementBounds.Fixed(width - 120, y, 120, 24));
        SingleComposer = c.EndChildElements().Compose();
        if (chosen != null) SingleComposer.GetToggleButton("slot-" + chosen)?.SetValue(true);
        if (host.Garment is { } g)
        {
            SingleComposer.GetToggleButton("garment-" + g.Code)?.SetValue(true);
            foreach (var (part, k) in optionRows) SingleComposer.GetToggleButton($"opt-{part}-{k}")?.SetValue(g.Parts[part].Chosen == k);
        }
    }

    readonly System.Collections.Generic.List<(int part, int option)> optionRows = new();

    /// <summary>The garment's choice changed (server sync): redraw the option buttons.</summary>
    public void OnStateChanged() { if (IsOpened()) Recompose(); }

    void Recompose() => capi.Event.EnqueueMainThreadTask(() => { if (IsOpened()) Compose(); }, "actailor-garmentpicker");

    void Pick(string code)
    {
        host.SendStartGarment(code);
        TryClose();
    }
}
