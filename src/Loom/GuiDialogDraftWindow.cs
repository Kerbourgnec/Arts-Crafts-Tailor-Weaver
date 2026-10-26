using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ArtsAndCrafts.Tailor.Loom;

/// <summary>
/// In-game draft window of the flying shuttle loom (lane/draft-window, docs/weaving.md "Draft window"). F with an empty
/// hand at the loom opens it directly (lane/draft-polish: the old F picker is gone; its product, cloth size and brush rows
/// live here). Shows and edits the whole draft (<see cref="DraftWindowModel"/>):
/// warp target colours, threading, tie-up, treadling + weft per row, live drawdown, checks (no shed, floats, cost),
/// presets as shortcuts, import / export of the designer's <c>ACD1:</c> code.
/// <para>
/// Live draft (lane/loom-live-draft): window and loom share one state. Every edit (also undo / redo, import, copy square) sends
/// the whole plan at once (<see cref="BlockEntityLoom.SendPlan"/>); the server validates it, applies it and broadcasts the
/// loom, so players watching see it live. A loom sync refreshes the woven rows, loaded warp and lock; a plan the window did not
/// send (another player's edit) replaces the window's (<see cref="DraftLiveSync"/>). No Apply, no Reset.
/// </para>
/// </summary>
public class GuiDialogDraftWindow : GuiDialog
{
    static GuiDialogDraftWindow? open;

    readonly BlockEntityLoom loom;
    DraftWindowModel model;
    readonly Dictionary<string, string> hexCache = new();
    string hoverText = "";
    string message = "";
    /// <summary>Undo / redo of plan edits (lane/draft-polish); survives Reset and loom syncs, cleared on a size change.</summary>
    readonly DraftUndo undo = new();
    /// <summary>States sent to the loom and not echoed yet (live draft).</summary>
    readonly DraftLiveSync live = new();

    /// <summary>Copy square (lane/loom-live-draft): nothing, picking the source square, or placing it (ghost on the cursor).</summary>
    enum SquareStage { Off, Pick, Place }
    SquareStage square = SquareStage.Off;
    int squareSize = 4, squareSrcX, squareSrcY;

    public override string ToggleKeyCombinationCode => null!;
    public override bool PrefersUngrabbedMouse => true;

    /// <summary>A draft window is open (the F hotkey wrapper leaves typing alone then).</summary>
    public static bool AnyOpen => open?.IsOpened() == true;

    public GuiDialogDraftWindow(ICoreClientAPI capi, BlockEntityLoom loom) : base(capi)
    {
        this.loom = loom;
        model = DraftWindowModel.FromLoom(loom.Weaving);
        colourBrush = InitialBrush();
    }

    /// <summary>F while the window is open: close it unless a text field has the focus (then the key is typing).</summary>
    public static bool CloseUnlessTyping()
    {
        if (open is not { } w || !w.IsOpened()) return false;
        if (w.Typing) return false;
        w.TryClose();
        return true;
    }

    /// <summary>Close any open draft window (mod unload).</summary>
    public static void CloseAny() => open?.TryClose();

    static readonly string[] InputKeys = { "from", "to", "seq", "code" };

    /// <summary>One of the window's text fields has the keyboard focus.</summary>
    bool Typing => SingleComposer != null && InputKeys.Any(k => SingleComposer.GetTextInput(k)?.HasFocus == true);

    /// <summary>Open the window for <paramref name="loom"/> (closes another draft window first).</summary>
    public static void Open(ICoreClientAPI capi, BlockEntityLoom loom)
    {
        open?.TryClose();
        open = new GuiDialogDraftWindow(capi, loom);
        open.TryOpen();
    }

    static string L(string key, params object[] args) => Lang.Get(TailorModSystem.Domain + ":" + key, args);

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        loom.ClientStateChanged += OnLoomChanged;
        Compose();
    }

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        loom.ClientStateChanged -= OnLoomChanged;
        if (open == this) open = null;
    }

    void OnLoomChanged()
    {
        if (!IsOpened()) return;
        int n = model.N;
        bool locked = model.ThreadingLocked;
        model.RefreshFromLoom(loom.Weaving);
        if (model.N != n)
        {
            undo.Clear(); // another cloth size: old steps do not fit
            live.Clear();
            square = SquareStage.Off;
        }
        else
        {
            // Live draft: a plan the window did not send is someone else's edit (or the server's); take it.
            var local = model.TakeSnapshot();
            var fresh = DraftWindowModel.FromLoom(loom.Weaving).TakeSnapshot();
            if (live.ShouldAdopt(fresh, local, capi.World.ElapsedMilliseconds)) model.AdoptLoom(loom.Weaving);
        }
        if (model.N != n || model.ThreadingLocked != locked) Compose(); // another size (new grid) or lock (labels)
        else Refresh();
    }

    /// <summary>Send the window's plan to the loom now (live draft) and remember it until the loom echoes it.</summary>
    void SendLive()
    {
        loom.SendPlan(model.ToCode());
        live.Sent(model.TakeSnapshot(), capi.World.ElapsedMilliseconds);
    }

    string HexOf(string code)
    {
        if (hexCache.TryGetValue(code, out var hex)) return hex;
        var item = capi.World.GetItem(new AssetLocation(code));
        hex = item != null ? TwineColors.HexOf(new ItemStack(item)) : TwineColors.PlainHex;
        hexCache[code] = hex;
        return hex;
    }

    string TwineName(string code)
    {
        var item = capi.World.GetItem(new AssetLocation(code));
        return item != null ? item.GetHeldItemName(new ItemStack(item)) : code;
    }

    #region Layout

    const double RightW = 330, Btn = 24, Icon = 36, IconPitch = 40, Gap = 6;

    /// <summary>Tooltip of a preset icon: its name, then its description (lang <c>key</c> and <c>key-desc</c>).</summary>
    static string PresetTip(string key) => L(key) + "\n" + L(key + "-desc");

    // Layout fit (lane/draft-polish item 7): every text is measured at the current GUI scale (GuiFit, unscaled units), labels
    // get the height their wrapped text needs, buttons shrink their font when the label is wider than the button, and the
    // dynamic texts log once when a text does not fit its box (client-main.log "[actailor] draft window: ... does not fit").

    /// <summary>Height (unscaled) of <paramref name="text"/> wrapped at <paramref name="width"/>.</summary>
    double TextH(CairoFont font, string text, double width)
        => capi.Gui.Text.GetMultilineTextHeight(font, text, GuiElement.scaled(width)) / Tailoring.GuiFit.Scale;

    /// <summary>Line height (unscaled) of <paramref name="font"/>.</summary>
    double LineH(CairoFont font) => capi.Gui.Text.GetLineHeight(font) / Tailoring.GuiFit.Scale;

    /// <summary>Box (unscaled) of each dynamic text, for the overflow log.</summary>
    readonly Dictionary<string, (CairoFont font, double w, double h)> boxes = new();
    readonly HashSet<string> overflowLogged = new();

    void Compose()
    {
        ClearComposers();
        boxes.Clear();
        var (gw, gh) = GuiElementDraftGrid.SizeFor(model.N);
        double lw = Math.Max(gw, RightW); // left column: grid, hover text, checks, cost
        double top = GuiStyle.TitleBarHeight + 4;
        double lx = lw + 16, y = top;

        var bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bg.BothSizing = ElementSizing.FitToChildren;
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        var compo = capi.Gui.CreateCompo(TailorModSystem.Domain + "-draftwindow", dialogBounds)
            .AddShadedDialogBG(bg)
            .AddDialogTitleBar(L("draftwin-title", model.N), () => TryClose())
            .BeginChildElements(bg);

        var small = CairoFont.WhiteSmallText();
        var detail = CairoFont.WhiteDetailText();

        void Dyn(string key, CairoFont font, double x, double yy, double w, double h)
        {
            boxes[key] = (font, w, h);
            compo.AddDynamicText("", font, ElementBounds.Fixed(x, yy, w, h), key);
        }
        // Equal-width buttons in a row; the font shrinks for a label wider than its button (GuiFit).
        void ButtonRow(double x, double w, params (string text, ActionConsumable click, string? key)[] buttons)
        {
            double bw = (w - (buttons.Length - 1) * 4) / buttons.Length;
            for (int i = 0; i < buttons.Length; i++)
            {
                var (text, click, key) = buttons[i];
                compo.AddButton(text, click, ElementBounds.Fixed(x + i * (bw + 4), y, bw, Btn), Tailoring.GuiFit.Font(text, bw), EnumButtonStyle.Small, key);
            }
        }
        void ToggleRow(string key, string[] texts, Action<int> onToggle, double x, double bw)
        {
            var font = Tailoring.GuiFit.Font(texts.OrderByDescending(t => Tailoring.GuiFit.TextWidth(CairoFont.SmallButtonText(), t)).First(), bw);
            compo.AddTextToggleButtons(texts, font, onToggle, texts.Select((_, i) => ElementBounds.Fixed(x + i * (bw + 4), y, bw, Btn)).ToArray(), key);
        }
        // A static label at (x, y), as tall as its wrapped text; returns its height.
        double LabelAt(string text, CairoFont font, double x, double yy, double w)
        {
            double h = Math.Ceiling(TextH(font, text, w)) + 1;
            compo.AddStaticText(text, font, ElementBounds.Fixed(x, yy, w, h));
            return h;
        }
        void Label(string text) => y += LabelAt(text, detail, lx, y, RightW) + 2;

        // Left column. The how-to lives in the handbook (item 6): one button opens the guide page.
        double guideW = Math.Max(120, Tailoring.GuiFit.TextWidth(CairoFont.SmallButtonText(), L("draftwin-guide")) + 2 * Tailoring.GuiFit.Pad);
        compo.AddButton(L("draftwin-guide"), OnGuide, ElementBounds.Fixed(0, top, guideW, Btn - 2), CairoFont.SmallButtonText(), EnumButtonStyle.Small, "guide");
        compo.AddHoverText(L("draftwin-guide-tip"), CairoFont.WhiteSmallText(), 260, ElementBounds.Fixed(0, top, guideW, Btn - 2));
        // Zoom buttons (lane/draft-zoom) at the right edge of the grid: - + Fit; the wheel over the grid zooms too.
        double fitW = Math.Max(40, Tailoring.GuiFit.TextWidth(CairoFont.SmallButtonText(), L("draftwin-zoom-fit")) + 2 * Tailoring.GuiFit.Pad);
        double zx = gw - fitW - 2 * (Btn + 4), zy = top;
        var zoomBtn = CairoFont.SmallButtonText();
        compo.AddButton("-", () => ZoomGrid(-1), ElementBounds.Fixed(zx, zy, Btn, Btn - 2), zoomBtn, EnumButtonStyle.Small, "zoomout");
        compo.AddHoverText(L("draftwin-zoom-out"), CairoFont.WhiteSmallText(), 260, ElementBounds.Fixed(zx, zy, Btn, Btn - 2));
        compo.AddButton("+", () => ZoomGrid(1), ElementBounds.Fixed(zx + Btn + 4, zy, Btn, Btn - 2), zoomBtn, EnumButtonStyle.Small, "zoomin");
        compo.AddHoverText(L("draftwin-zoom-in"), CairoFont.WhiteSmallText(), 260, ElementBounds.Fixed(zx + Btn + 4, zy, Btn, Btn - 2));
        compo.AddButton(L("draftwin-zoom-fit"), () => ZoomGrid(0), ElementBounds.Fixed(zx + 2 * (Btn + 4), zy, fitW, Btn - 2), Tailoring.GuiFit.Font(L("draftwin-zoom-fit"), fitW), EnumButtonStyle.Small, "zoomfit");
        compo.AddHoverText(L("draftwin-zoom-fit-tip"), CairoFont.WhiteSmallText(), 260, ElementBounds.Fixed(zx + 2 * (Btn + 4), zy, fitW, Btn - 2));
        double gy = top + Btn + 2;
        compo.AddInteractiveElement(new GuiElementDraftGrid(capi, ElementBounds.Fixed(0, gy, gw, gh), () => model, HexOf, OnGridClick, OnGridHover, OnColour, () => colourBrush), "grid");
        double ly = gy + gh + 6, dl = LineH(detail);
        Dyn("hover", detail, 0, ly, lw, Math.Ceiling(4 * dl));
        ly += Math.Ceiling(4 * dl) + Gap;
        Dyn("checks", detail, 0, ly, lw, Math.Ceiling(7 * dl));
        ly += Math.Ceiling(7 * dl) + Gap;
        Dyn("cost", detail, 0, ly, lw, Math.Ceiling(9 * dl));

        // Right column: status, undo/redo, product, brush, presets, treadling section, code, actions.
        double statusH = Math.Ceiling(Math.Max(3 * LineH(small), TextH(small, StatusText(), RightW)) + LineH(small)); // + 1 line slack
        Dyn("status", small, lx, y, RightW, statusH);
        y += statusH + 4;
        // Undo / redo of plan edits (Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z) and the loom's roll back of the last woven row (live draft).
        ButtonRow(lx, RightW, (L("draftwin-undo"), OnUndo, "undo"), (L("draftwin-redo"), OnRedo, "redo"), (L("draftwin-undorow"), OnUndoRow, "undorow"));
        compo.AddHoverText(L("draftwin-undorow-tip"), CairoFont.WhiteSmallText(), 260, ElementBounds.Fixed(lx + 2 * (RightW - 8) / 3 + 8, y, (RightW - 8) / 3, Btn));
        y += Btn + Gap;
        // Product (lane/linen-output; was the F picker's row): chosen while no row is woven.
        if (model.WovenRows == 0)
        {
            Label(L("draftwin-product"));
            var products = ClothProducts.All;
            ToggleRow("product", products.Select(p => L(ClothProducts.LangKey(p))).ToArray(), OnProduct, lx, (RightW - 4 * (products.Length - 1)) / products.Length);
            y += Btn + Gap;
        }
        // Brush (item 5): threads / rows per colour click in the strips (all / 8 / 4 / 2 / 1); 8 / 4 / 2 / 1 also set
        // the warp threads a twine click loads at the back (the old F picker's brush row; "all" leaves that one as it was).
        Label(L("draftwin-brush"));
        ToggleRow("brush", DraftWindowModel.ColourBrushes.Select(v => v == 0 ? L("draftwin-brush-all") : v.ToString()).ToArray(), OnBrush, lx, 40);
        y += Btn + Gap;
        // Copy square (live draft item 5): size, then the button starts picking the source square in the drawdown.
        Label(L("draftwin-square"));
        ToggleRow("square", DraftWindowModel.SquareSizes.Select(v => v.ToString()).ToArray(), OnSquareSize, lx, 40);
        {
            double sx0 = lx + DraftWindowModel.SquareSizes.Length * 44;
            compo.AddButton(L("draftwin-square-go"), OnSquareGo, ElementBounds.Fixed(sx0, y, lx + RightW - sx0, Btn),
                Tailoring.GuiFit.Font(L("draftwin-square-go"), lx + RightW - sx0), EnumButtonStyle.Small, "squarego");
        }
        y += Btn + Gap;

        // Threading and tie-up presets side by side as pictures of their grids (items 2, 3); the name is the tooltip.
        const double half = RightW / 2;
        double lh = Math.Max(LabelAt(L("draftwin-threading"), detail, lx, y, half - 4), LabelAt(L("draftwin-tieup"), detail, lx + half, y, half));
        y += lh + 2;
        for (int i = 0; i < DraftPresets.Threadings.Length; i++)
        {
            var t = DraftPresets.Threadings[i];
            compo.AddPresetIcon(ElementBounds.Fixed(lx + i * IconPitch, y, Icon, Icon), PresetIcons.Threading(t.Repeat), () => OnThreadingPreset(t),
                () => model.Threading.Select((s, x) => s == t.Repeat[x % t.Repeat.Length]).All(b => b), () => !model.ThreadingLocked,
                PresetTip("draft-threading-" + t.Code) + (model.ThreadingLocked ? "\n" + L("draftwin-locked-rows") : ""));
        }
        for (int i = 0; i < DraftPresets.TieUps.Length; i++)
        {
            var t = DraftPresets.TieUps[i];
            compo.AddPresetIcon(ElementBounds.Fixed(lx + half + i * IconPitch, y, Icon, Icon), PresetIcons.TieUp(t.Lifts), () => Edit(() => model.ApplyTieUp(t.Lifts)),
                () => model.TieUp.SequenceEqual(t.Lifts), () => true, PresetTip("draft-tieup-" + t.Code));
        }
        y += Icon + Gap + 2;

        // Treadling section: "Rows [from] to [to] [sequence] [Apply]", widths from the measured labels.
        Label(L("draftwin-section"));
        double rowsW = Math.Ceiling(Tailoring.GuiFit.TextWidth(small, L("draftwin-rows"))) + 4, toW = Math.Ceiling(Tailoring.GuiFit.TextWidth(small, L("draftwin-to"))) + 4;
        double applyW = Math.Max(56, Math.Ceiling(Tailoring.GuiFit.TextWidth(CairoFont.SmallButtonText(), L("draftwin-applyrows"))) + 2 * Tailoring.GuiFit.Pad);
        double sx = lx;
        compo.AddStaticText(L("draftwin-rows"), small, ElementBounds.Fixed(sx, y + 3, rowsW, Btn)); sx += rowsW + 2;
        compo.AddTextInput(ElementBounds.Fixed(sx, y, 36, Btn), _ => { }, CairoFont.TextInput(), "from"); sx += 40;
        compo.AddStaticText(L("draftwin-to"), small, ElementBounds.Fixed(sx, y + 3, toW, Btn)); sx += toW + 2;
        compo.AddTextInput(ElementBounds.Fixed(sx, y, 36, Btn), _ => { }, CairoFont.TextInput(), "to"); sx += 42;
        double seqW = Math.Max(60, lx + RightW - applyW - 4 - sx);
        compo.AddTextInput(ElementBounds.Fixed(sx, y, seqW, Btn), _ => { }, CairoFont.TextInput(), "seq"); sx += seqW + 4;
        compo.AddButton(L("draftwin-applyrows"), OnApplySection, ElementBounds.Fixed(sx, y, lx + RightW - sx, Btn), Tailoring.GuiFit.Font(L("draftwin-applyrows"), lx + RightW - sx), EnumButtonStyle.Small);
        y += Btn + 4;
        for (int i = 0; i < DraftPresets.WindowTreadlings.Length; i++)
        {
            var t = DraftPresets.WindowTreadlings[i];
            compo.AddPresetIcon(ElementBounds.Fixed(lx + i * IconPitch, y, Icon, Icon), PresetIcons.Treadling(t.Sequence), () => OnTreadlingPreset(t),
                () => DraftWindowModel.ParseSequence(SingleComposer?.GetTextInput("seq")?.GetText()) is { } seq && seq.SequenceEqual(t.Sequence), () => true,
                PresetTip("draftwin-treadling-" + t.Code));
        }
        y += Icon + Gap + 2;

        // Draft code (item 7 bug: the label wrapped over the field): a measured label, then a one-line field the full width
        // (a VS text input is single-line and scrolls horizontally with the caret; the code never wraps).
        Label(L("draftwin-code"));
        compo.AddTextInput(ElementBounds.Fixed(lx, y, RightW, Btn), _ => { }, CairoFont.SmallTextInput(), "code");
        y += Btn + 4;
        ButtonRow(lx, RightW, (L("draftwin-copy"), OnCopy, null), (L("draftwin-paste"), OnPaste, null), (L("draftwin-import"), OnImport, null));
        y += Btn + 4;
        double msgH = Math.Ceiling(2 * LineH(small));
        Dyn("message", small.Clone().WithColor(GuiStyle.WarningTextColor), lx, y, RightW, msgH);
        y += msgH + 4;
        ButtonRow(lx, RightW, (L("draftwin-close"), () => TryClose(), null));

        SingleComposer = compo.EndChildElements().Compose();
        var from = SingleComposer.GetTextInput("from");
        var to = SingleComposer.GetTextInput("to");
        from.SetValue((Math.Min(model.WovenRows, model.N - 1) + 1).ToString());
        to.SetValue(model.N.ToString());
        SingleComposer.GetTextInput("seq").SetValue("1 2 3 4");
        SingleComposer.GetTextInput("code").SetMaxLength(512);
        if (model.WovenRows == 0) SingleComposer.ToggleButtonsSetValue("product", Array.IndexOf(ClothProducts.All, loom.Weaving.Product));
        SingleComposer.ToggleButtonsSetValue("brush", Math.Max(0, Array.IndexOf(DraftWindowModel.ColourBrushes, colourBrush)));
        SingleComposer.ToggleButtonsSetValue("square", Math.Max(0, Array.IndexOf(DraftWindowModel.SquareSizes, squareSize)));
        Refresh();
    }

    /// <summary>Set a dynamic text; log once when it is taller than its box (a layout bug to report, not a crash).</summary>
    void SetText(string key, string text)
    {
        if (SingleComposer?.GetDynamicText(key) is not { } el) return;
        el.SetNewText(text);
        if (!boxes.TryGetValue(key, out var box) || overflowLogged.Contains(key)) return;
        double h = TextH(box.font, text, box.w);
        if (h > box.h + 1)
        {
            overflowLogged.Add(key);
            capi.Logger.Warning("[actailor] draft window: text '{0}' does not fit its box ({1:0} > {2:0} px): {3}", key, h, box.h, text.Replace("\n", " | "));
        }
    }


    bool ZoomGrid(int dir)
    {
        Grid?.ZoomStep(dir);
        return true;
    }

    GuiElementDraftGrid? Grid =>SingleComposer?.GetElement("grid") as GuiElementDraftGrid;

    #endregion

    #region Texts

    /// <summary>Redraw the grid and every text from the model.</summary>
    void Refresh()
    {
        if (SingleComposer == null) return;
        if (Grid is { } grid)
        {
            grid.Square = square == SquareStage.Off ? null : new SquareOverlay(square == SquareStage.Place, Math.Min(squareSize, model.N), squareSrcX, squareSrcY);
            grid.Redraw();
        }
        SetText("status", StatusText());
        SetText("checks", ChecksText());
        SetText("cost", CostText());
        SetText("hover", hoverText);
        SetText("message", message);
        SingleComposer.GetTextInput("code")?.SetValue(model.ToCode());
        if (SingleComposer.GetButton("undo") is { } u) u.Enabled = undo.CanUndo;
        if (SingleComposer.GetButton("redo") is { } r) r.Enabled = undo.CanRedo;
        if (SingleComposer.GetButton("undorow") is { } ur) ur.Enabled = model.WovenRows > 0;
    }

    string StatusText()
    {
        var sb = new StringBuilder();
        // Cloth size (was the F picker's first line, lane/square-cloth): the world setting applies to the next cloth.
        int worldSize = ClothSizeSetting.NewClothSize(capi.World);
        sb.AppendLine(L("draftwin-size", model.N, L(ClothProducts.LangKey(loom.Weaving.Product)))
            + (worldSize != model.N ? " " + L("draftwin-size-world", worldSize) : ""));
        sb.AppendLine(L("draftwin-status", model.WovenRows, model.N));
        if (model.ThreadingLocked) sb.AppendLine(L("draftwin-locked-rows")); // only rows lock it (lane/loom-feedback-8)
        else sb.AppendLine(L("draftwin-free"));
        return sb.ToString();
    }

    static string Ranges(IEnumerable<int> zeroBased)
    {
        var parts = new List<string>();
        int a = -2, p = -2;
        foreach (int v in zeroBased.Select(i => i + 1))
        {
            if (v == p + 1) { p = v; continue; }
            if (a > 0) parts.Add(a == p ? a.ToString() : a + "-" + p);
            a = p = v;
        }
        if (a > 0) parts.Add(a == p ? a.ToString() : a + "-" + p);
        return string.Join(", ", parts);
    }

    string ChecksText()
    {
        var sb = new StringBuilder();
        var bad = model.NoShedRows();
        for (int t = 0; t < DraftWindowModel.Treadles; t++)
        {
            if (!model.NoShed(t)) continue;
            var rows = bad.Where(y => model.Treadling[y] == t).ToList();
            sb.AppendLine(rows.Count > 0 ? L("draftwin-check-noshed", t + 1, Ranges(rows)) : L("draftwin-check-noshed-unused", t + 1));
        }
        var (_, weft, warp) = model.Floats();
        if (weft.Length > DraftWindowModel.FloatLimit) sb.AppendLine(L("draftwin-check-weftfloat", weft.Length, weft.Index + 1, DraftWindowModel.FloatLimit));
        if (warp.Length > DraftWindowModel.FloatLimit) sb.AppendLine(L("draftwin-check-warpfloat", warp.Length, warp.Index + 1, DraftWindowModel.FloatLimit));
        var mism = model.WarpMismatches();
        if (mism.Count > 0) sb.AppendLine(L("draftwin-check-warp", mism.Count, model.N));
        if (sb.Length == 0) sb.AppendLine(L("draftwin-check-ok", DraftWindowModel.FloatLimit));
        return sb.ToString();
    }

    string CostText()
    {
        var cost = model.Cost(loom.Weaving.Product);
        var sb = new StringBuilder(L("draftwin-cost", cost.Values.Sum()));
        foreach (var (code, n) in cost.OrderByDescending(kv => kv.Value)) sb.Append("\n" + L("draftwin-costline", TwineName(code), n));
        return sb.ToString();
    }

    static string Shafts(int mask) => mask < 0 ? "-" : string.Join(" + ", Enumerable.Range(0, DraftWindowModel.Shafts).Where(s => ((mask >> s) & 1) != 0).Select(s => (s + 1).ToString()));

    void OnGridHover(DraftHit hit)
    {
        int n = model.N;
        int row = hit.Region switch { DraftRegion.Drawdown => hit.B, DraftRegion.Treadling or DraftRegion.Weft => hit.A, _ => -1 };
        string text = "";
        if (row >= 0 && row < n)
        {
            int lift = model.LiftOf(row);
            string weft = TwineName(DraftWindowModel.CodeOf(model.Weft[row]));
            text = lift < 0 ? L("draftwin-hover-tapestry", row + 1)
                : L(model.Woven(row) ? "draftwin-hover-woven" : "draftwin-hover-row", row + 1, model.Treadling[row] + 1, Shafts(lift), weft);
        }
        else if (hit.Region == DraftRegion.Warp && hit.A >= 0 && hit.A < n)
        {
            string target = TwineName(DraftWindowModel.CodeOf(model.Warp[hit.A]));
            string loaded = hit.A < model.LoadedWarp.Length ? TwineName(model.LoadedWarp[hit.A]) : target;
            text = L("draftwin-hover-warp", hit.A + 1, target, loaded);
        }
        else if (hit.Region == DraftRegion.Threading) text = L(model.ThreadingLocked ? "draftwin-hover-threading-locked" : "draftwin-hover-threading", hit.A + 1, hit.B + 1);
        else if (hit.Region == DraftRegion.TieUp) text = L("draftwin-hover-tieup", hit.A + 1, hit.B + 1);
        if (text == hoverText) return;
        hoverText = text;
        SetText("hover", hoverText);
    }

    #endregion

    #region Edits

    /// <summary>Run a plan edit as one undo step (lane/draft-polish): refused → message; done → tick + redraw.</summary>
    bool Edit(Func<bool> edit, string? refusedKey = null)
    {
        var before = model.TakeSnapshot();
        bool ok = undo.Do(model, edit);
        if (ok) { message = ""; capi.Gui.PlaySound("tick"); }
        if (ok && !model.TakeSnapshot().SameAs(before)) SendLive();
        else if (refusedKey != null) Refuse(refusedKey);
        Refresh();
        return true;
    }

    void Refuse(string key, params object[] args)
    {
        message = L(key, args);
        capi.TriggerIngameError(this, "actailor-draftwin", message);
    }

    string LockKey => "draftwin-err-locked-rows"; // only rows lock the warp (lane/loom-feedback-8: no set warp)

    void OnGridClick(DraftHit hit, bool shift)
    {
        if (square != SquareStage.Off)
        {
            if (hit.Region == DraftRegion.Drawdown) OnSquareClick(hit); // other cells do nothing while a square is on the cursor
            return;
        }
        switch (hit.Region)
        {
            case DraftRegion.Threading: Edit(() => model.SetThreading(hit.A, hit.B), LockKey); break;
            case DraftRegion.TieUp: Edit(() => model.ToggleTieUp(hit.A, hit.B)); break;
            case DraftRegion.Treadling: Edit(() => model.SetTreadle(hit.A, hit.B), "draftwin-err-woven"); break;
            case DraftRegion.Drawdown:
                // A drawdown click picks that row for the treadling section (rows N to the end).
                SingleComposer?.GetTextInput("from")?.SetValue((hit.B + 1).ToString());
                break;
        }
    }

    void OnColour(DraftRegion strip, int from, int to, int colour)
    {
        // Warp sync (lane/linen-output): a warp colour is the physical warp; the server loads it (twine in the inventory) and
        // the loom sync redraws the strip. Refused here while the warp is set.
        if (strip == DraftRegion.Warp)
        {
            if (model.ThreadingLocked) { Refuse("draftwin-err-warplocked-load"); Refresh(); return; }
            model.RequireWarp(from, to, colour); // shown at once; the loom's sync confirms the load
            loom.SendLoadWarp(from, to, colour);
            message = L("draftwin-warp-sent");
            capi.Gui.PlaySound("tick");
            Refresh();
        }
        else if (strip == DraftRegion.Weft) Edit(() => model.SetWeft(from, to, colour) > 0, "draftwin-err-woven");
    }

    void OnProduct(int i)
    {
        if (i < 0 || i >= ClothProducts.All.Length) return;
        loom.SendProduct(ClothProducts.All[i]); // the loom sync redraws (another row count)
        capi.Gui.PlaySound("tick");
    }

    /// <summary>Colour brush width (0 = all); "all" is kept for the session, else the window starts on the loom brush.</summary>
    int colourBrush;
    static bool sessionAll;

    /// <summary>The window's brush to start with: "all" if picked last time, else the player's loom brush (1 / 2 / 4 / 8).</summary>
    int InitialBrush() => sessionAll ? 0 : CollectibleBehaviorLoomBrush.BrushSize(capi.World.Player);

    void OnBrush(int index)
    {
        if (index < 0 || index >= DraftWindowModel.ColourBrushes.Length) return;
        colourBrush = DraftWindowModel.ColourBrushes[index];
        sessionAll = colourBrush == 0;
        int mode = Array.IndexOf(CollectibleBehaviorLoomBrush.Sizes, colourBrush);
        if (mode >= 0)
        {
            // 8 / 4 / 2 / 1 also set the back of the loom (twine click loads that many threads).
            CollectibleBehaviorLoomBrush.SetMode(capi.World.Player, mode); // local at once (outline, HUD); the server via the packet
            loom.SendBrushMode(mode);
        }
        capi.Gui.PlaySound("tick");
        Grid?.Redraw();
    }

    bool OnThreadingPreset(ThreadingPreset t) => Edit(() => model.ApplyThreading(t.Repeat), LockKey);

    int ReadRow(string key, int fallback)
        => int.TryParse(SingleComposer?.GetTextInput(key)?.GetText()?.Trim(), out int v) ? Math.Clamp(v, 1, model.N) - 1 : fallback;

    bool OnApplySection()
    {
        var seq = DraftWindowModel.ParseSequence(SingleComposer?.GetTextInput("seq")?.GetText());
        if (seq == null) { Refuse("draftwin-err-seq"); Refresh(); return true; }
        int from = ReadRow("from", model.WovenRows), to = ReadRow("to", model.N - 1);
        return Edit(() => model.ApplyTreadling(from, to, seq) > 0, "draftwin-err-woven");
    }

    /// <summary>A treadling preset icon: its sequence into the field (1-based text), then applied to rows from..to.</summary>
    void OnTreadlingPreset(TreadlingPreset t)
    {
        SingleComposer?.GetTextInput("seq")?.SetValue(string.Join(" ", t.Sequence.Select(v => v + 1)));
        OnApplySection();
    }

    #endregion

    #region Code and apply

    bool OnCopy()
    {
        string code = model.ToCode();
        try
        {
            capi.Forms.SetClipboardText(code);
            message = L("draftwin-copied");
        }
        catch (Exception e)
        {
            // No clipboard on this platform: the field holds the code; select it and press Ctrl+C.
            capi.Logger.Warning("[actailor] clipboard: {0}", e.Message);
            message = L("draftwin-copyfailed");
        }
        Refresh();
        return true;
    }

    bool OnPaste()
    {
        string? text = null;
        try { text = capi.Forms.GetClipboardText(); }
        catch (Exception e) { capi.Logger.Warning("[actailor] clipboard: {0}", e.Message); }
        if (string.IsNullOrWhiteSpace(text)) { Refuse("draftwin-err-clipboard"); Refresh(); return true; }
        return Import(text);
    }

    bool OnImport() => Import(SingleComposer?.GetTextInput("code")?.GetText());

    bool Import(string? code)
    {
        DraftCode.Error? error = null;
        if (!undo.Do(model, () => model.Import(code, out error)))
        {
            Refuse(error?.Key ?? "draft-err-range", error?.Args ?? Array.Empty<object>());
            Refresh();
            // Keep the rejected text in the field so it can be fixed.
            SingleComposer?.GetTextInput("code")?.SetValue(code ?? "");
            return true;
        }
        SendLive();
        message = L("draftwin-imported");
        capi.Gui.PlaySound("tick");
        Refresh();
        return true;
    }

    #endregion

    /// <summary>Handbook guide page of draft weaving (assets/actailor/config/handbook/103-weavingdrafts.json).</summary>
    public const string GuidePage = "actailor:weaving-draft-guide";

    bool OnGuide()
    {
        if (!HandbookLink.Open(capi, GuidePage)) Refuse("draftwin-guide-missing");
        Refresh();
        return true;
    }

    #region Undo last row (lane/loom-live-draft)

    /// <summary>The loom's roll back (as sneak + reed): the last woven row is the current row again and editable; the loom's sync updates the window.</summary>
    bool OnUndoRow()
    {
        if (model.WovenRows <= 0) { Refuse("loom-err-nothingwoven"); Refresh(); return true; }
        loom.SendUnweaveRow();
        capi.Gui.PlaySound("tick");
        return true;
    }

    #endregion

    #region Copy square (lane/loom-live-draft)

    void OnSquareSize(int i)
    {
        if (i < 0 || i >= DraftWindowModel.SquareSizes.Length) return;
        squareSize = DraftWindowModel.SquareSizes[i];
        if (square != SquareStage.Off) { square = SquareStage.Pick; message = L("draftwin-square-pick", Math.Min(squareSize, model.N)); }
        capi.Gui.PlaySound("tick");
        Refresh();
    }

    /// <summary>The Copy square button: start picking the source square; pressed again, cancel.</summary>
    bool OnSquareGo()
    {
        if (square != SquareStage.Off) { CancelSquare(); return true; }
        square = SquareStage.Pick;
        message = L("draftwin-square-pick", Math.Min(squareSize, model.N));
        capi.Gui.PlaySound("tick");
        Refresh();
        return true;
    }

    void CancelSquare()
    {
        if (square == SquareStage.Off) return;
        square = SquareStage.Off;
        message = "";
        Refresh();
    }

    /// <summary>A drawdown click while a square is on the cursor: the first picks the source, the second places it (one undo step).</summary>
    void OnSquareClick(DraftHit hit)
    {
        int n = model.N, size = Math.Min(squareSize, n);
        int x0 = DraftWindowModel.SquareStart(size, hit.A, n), y0 = DraftWindowModel.SquareStart(size, hit.B, n);
        if (square == SquareStage.Pick)
        {
            squareSrcX = x0;
            squareSrcY = y0;
            square = SquareStage.Place;
            message = L("draftwin-square-place");
            capi.Gui.PlaySound("tick");
            Refresh();
            return;
        }
        var runs = model.CopySquareWarpRuns(size, squareSrcX, x0);
        bool changed = false;
        Edit(() => changed = model.CopySquare(size, squareSrcX, squareSrcY, x0, y0));
        foreach (var (from, to, colour) in runs)
        {
            model.RequireWarp(from, to, colour);
            loom.SendLoadWarp(from, to, colour); // the warp colours are twine loads (needs the twine in the inventory)
        }
        square = SquareStage.Off;
        message = changed || runs.Count > 0 ? L("draftwin-square-done") : L("draftwin-square-none");
        Refresh();
    }

    /// <summary>Escape: cancels a square on the cursor first (the window stays open), else closes as usual.</summary>
    public override bool OnEscapePressed()
    {
        if (square == SquareStage.Off) return base.OnEscapePressed();
        CancelSquare();
        return true;
    }

    #endregion

    #region Undo / redo (lane/draft-polish)

    bool OnUndo() => UndoStep(undo.Undo(model));

    bool OnRedo() => UndoStep(undo.Redo(model));

    bool UndoStep(bool done)
    {
        if (done) { message = ""; capi.Gui.PlaySound("tick"); SendLive(); } // an undone step is a live edit too
        Refresh();
        return true;
    }

    /// <summary>Ctrl+Z undo, Ctrl+Y / Ctrl+Shift+Z redo; only when no text field took the key (a focused field handles every key).</summary>
    public override void OnKeyDown(KeyEvent args)
    {
        base.OnKeyDown(args);
        if (args.Handled || !(args.CtrlPressed || args.CommandPressed) || Typing) return;
        if (args.KeyCode == (int)GlKeys.Z) { if (args.ShiftPressed) OnRedo(); else OnUndo(); args.Handled = true; }
        else if (args.KeyCode == (int)GlKeys.Y) { OnRedo(); args.Handled = true; }
    }

    #endregion

    public override void OnMouseDown(MouseEvent args)
    {
        // Right click cancels a square on the cursor, anywhere in the window.
        if (square != SquareStage.Off && args.Button == EnumMouseButton.Right)
        {
            CancelSquare();
            args.Handled = true;
            return;
        }
        // A click outside the grid closes its palette popup.
        if (Grid is { PopupOpen: true } g && !g.IsPositionInside(args.X, args.Y)) g.ClosePopup();
        base.OnMouseDown(args);
    }
}
