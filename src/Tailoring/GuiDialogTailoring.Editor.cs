using Vintagestory.API.Client;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Canvas controls of the sewing dialog (lane/sewing-editor-xl; the large editor mode was removed in lane/sewing-polish:
/// one dialog size, docs/tailoring.md "Sewing polish"). Both steps share the zoomable canvas
/// (<see cref="GuiElementGarmentCanvas"/>: wheel zoom, middle / space drag pan) and the keyboard shortcuts below.
/// The view (<see cref="CanvasView"/>) of each step lives here so zoom and pan survive dialog rebuilds.
/// </summary>
public partial class GuiDialogTailoring
{
    readonly CanvasView sewView = new(), embView = new();
    bool spaceHeld;

    static ElementBounds DialogBounds() => ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.RightMiddle)
        .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

    GuiElementGarmentCanvas? ActiveCanvas => embroidering ? EmbCanvas : Canvas;

    void HookCanvas(GuiElementGarmentCanvas canvas) => canvas.PanHeld = () => spaceHeld;

    bool ShiftHeld()
    {
        var keys = capi.Input.KeyboardKeyStateRaw;
        return keys[(int)GlKeys.ShiftLeft] || keys[(int)GlKeys.ShiftRight];
    }

    /// <summary>
    /// Shortcuts (both steps): + / - zoom, 0 fit, space + drag pans; Embroider: [ / ] brush, Ctrl+Z undo, Ctrl+Y or
    /// Ctrl+Shift+Z redo, X toggles pick out (shift held = pick out while held).
    /// </summary>
    bool HandleEditorKey(KeyEvent args)
    {
        var k = (GlKeys)args.KeyCode;
        switch (k)
        {
            case GlKeys.Space:
                spaceHeld = true;
                return true;
            case GlKeys.Plus or GlKeys.KeypadAdd or GlKeys.KeypadPlus:
                ActiveCanvas?.Zoom(1);
                return true;
            case GlKeys.Minus or GlKeys.KeypadSubtract or GlKeys.KeypadMinus:
                ActiveCanvas?.Zoom(-1);
                return true;
            case GlKeys.Number0 or GlKeys.Keypad0:
                ActiveCanvas?.Zoom(0);
                return true;
        }
        if (!embroidering) return false;
        switch (k)
        {
            case GlKeys.BracketLeft:
                StepEmbBrush(-1);
                return true;
            case GlKeys.BracketRight:
                StepEmbBrush(1);
                return true;
            case GlKeys.Z when args.CtrlPressed && args.ShiftPressed:
            case GlKeys.Y when args.CtrlPressed:
                EmbRedo();
                return true;
            case GlKeys.Z when args.CtrlPressed:
                EmbUndo();
                return true;
            case GlKeys.X when !args.CtrlPressed:
                embPickOut = !embPickOut;
                SetEmbToggles();
                return true;
        }
        return false;
    }

    public override void OnKeyDown(KeyEvent args)
    {
        if (IsOpened() && HandleEditorKey(args))
        {
            args.Handled = true;
            return;
        }
        base.OnKeyDown(args);
    }

    public override void OnKeyUp(KeyEvent args)
    {
        if (args.KeyCode == (int)GlKeys.Space && spaceHeld)
        {
            spaceHeld = false;
            args.Handled = true;
            return;
        }
        base.OnKeyUp(args);
    }

    public override void OnGuiClosed()
    {
        spaceHeld = false;
        base.OnGuiClosed();
    }
}
