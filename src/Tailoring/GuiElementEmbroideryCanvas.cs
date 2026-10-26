using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using EnumMouseButton = Vintagestory.API.Common.EnumMouseButton;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Embroider step canvas (lane/mannequin-embroidery): the garment texture unwrapped flat, one square per garment texel
/// (= one stitch). Texels that take no stitches (pelt, hard, fixed parts, outside every part) are dimmed. Left click /
/// drag stitches, right click / drag (or left with <see cref="PickOutHeld"/>, shift) picks out: <c>onPaint(x, y, pickOut)</c>
/// once per texel on the stroke (gaps of a fast drag filled with a line). <c>onStroke(true/false)</c> brackets a stroke
/// (undo snapshot). The brush footprint under the mouse is outlined. Zoom / pan (lane/sewing-editor-xl):
/// <see cref="GuiElementGarmentCanvas"/>; middle drag or space + left drag pans.
/// </summary>
public class GuiElementEmbroideryCanvas : GuiElementGarmentCanvas
{
    readonly Func<bool[]?> mask;
    readonly Func<int, int, IEnumerable<(int x, int y)>> brushCells;
    readonly Action<int, int, bool> onPaint;
    readonly Action<bool> onStroke;
    bool painting, erasing;
    (int x, int y) last = (-1, -1);
    bool[]? dimMask;

    /// <summary>Shift held: a left stroke picks out.</summary>
    public Func<bool>? PickOutHeld;
    /// <summary>The brush picks out (toggle or modifier): footprint drawn in white instead of red.</summary>
    public Func<bool>? PickOutActive;

    public GuiElementEmbroideryCanvas(ICoreClientAPI capi, ElementBounds bounds, Func<GarmentDef?> garment, Func<int[]?> pixels,
        Func<bool[]?> mask, Func<int, int, IEnumerable<(int x, int y)>> brushCells, Action<int, int, bool> onPaint,
        Action<bool> onStroke, CanvasView view) : base(capi, bounds, garment, pixels, view)
    {
        this.mask = mask;
        this.brushCells = brushCells;
        this.onPaint = onPaint;
        this.onStroke = onStroke;
    }

    public bool Painting => painting;

    protected override void OnContentChanged() { }

    protected override int TexelColor(int i, int argb)
    {
        dimMask = mask();
        bool takes = dimMask != null && i < dimMask.Length && dimMask[i];
        if (takes) return argb;
        // no stitches here: dimmed to 40 %
        int r = (int)(((argb >> 16) & 0xff) * 0.4), g = (int)(((argb >> 8) & 0xff) * 0.4), b = (int)((argb & 0xff) * 0.4);
        return (int)(argb & 0xff000000) | (r << 16) | (g << 8) | b;
    }

    static readonly Vec4f BrushStitch = new(1, 0.25f, 0.2f, 0.95f), BrushPick = new(1, 1, 1, 0.95f);

    protected override void RenderOverlay(double s, double ox, double oy)
    {
        if (!view.Inside(hover.x, hover.y)) return;
        int x1 = int.MaxValue, y1 = int.MaxValue, x2 = -1, y2 = -1;
        foreach (var (cx, cy) in brushCells(hover.x, hover.y))
        {
            x1 = Math.Min(x1, cx); y1 = Math.Min(y1, cy); x2 = Math.Max(x2, cx); y2 = Math.Max(y2, cy);
        }
        if (x2 < 0) return;
        double t = Math.Max(1, Math.Round(scaled(1.5)));
        bool pick = erasing && painting || PickOutActive?.Invoke() == true;
        Frame(ox + x1 * s - t, oy + y1 * s - t, (x2 - x1 + 1) * s + 2 * t, (y2 - y1 + 1) * s + 2 * t, t, pick ? BrushPick : BrushStitch);
    }

    protected override void OnCanvasMouseDown(MouseEvent args, (int x, int y) t)
    {
        if (args.Button != EnumMouseButton.Left && args.Button != EnumMouseButton.Right) return;
        if (!view.Inside(t.x, t.y)) return;
        args.Handled = true;
        painting = true;
        erasing = args.Button == EnumMouseButton.Right || PickOutHeld?.Invoke() == true;
        last = t;
        onStroke(true);
        onPaint(t.x, t.y, erasing);
    }

    protected override void OnCanvasMouseMove(MouseEvent args, (int x, int y) t)
    {
        if (!painting || t == last) return;
        var from = last;
        last = t;
        foreach (var p in CanvasView.Line(from, t))
        {
            if (p == from || !view.Inside(p.x, p.y)) continue;
            onPaint(p.x, p.y, erasing);
        }
    }

    protected override void OnCanvasMouseUp(MouseEvent args, (int x, int y) t)
    {
        if (!painting) return;
        painting = false;
        last = (-1, -1);
        onStroke(false);
    }
}
