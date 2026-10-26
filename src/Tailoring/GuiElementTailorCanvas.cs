using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using EnumMouseButton = Vintagestory.API.Common.EnumMouseButton;

namespace ArtsAndCrafts.Tailor.Tailoring;

public enum DragPhase { Begin, Move, End }

/// <summary>
/// Sew step canvas: the garment's texture grid (every piece drawn with the pattern it currently takes), the selected
/// piece outlined. Left click a piece to select it; left drag moves its cloth's pattern under it (offset snaps to whole
/// cells; preview while dragging, commit on release). Zoom / pan (lane/sewing-editor-xl): <see cref="GuiElementGarmentCanvas"/>;
/// right or middle drag pans here.
/// </summary>
public class GuiElementTailorCanvas : GuiElementGarmentCanvas
{
    readonly Func<int> selected;
    readonly Action<int> onSelect;
    readonly Action<int, int, int, DragPhase> onDrag; // piece, dCellsX, dCellsY, phase
    int dragPiece = -1;
    int dragX, dragY;
    int outlinedPiece = -2;
    GarmentDef? outlinedGarment;
    /// <summary>Outline of the selected piece in texel units: horizontal runs (x, y, length) and vertical runs (x, y, length).</summary>
    readonly List<(int x, int y, int n)> hEdges = new(), vEdges = new();

    public GuiElementTailorCanvas(ICoreClientAPI capi, ElementBounds bounds, Func<GarmentDef?> garment, Func<int[]?> pixels,
        Func<int> selected, Action<int> onSelect, Action<int, int, int, DragPhase> onDrag, CanvasView view)
        : base(capi, bounds, garment, pixels, view)
    {
        this.selected = selected;
        this.onSelect = onSelect;
        this.onDrag = onDrag;
    }

    public bool Dragging => dragPiece >= 0;

    protected override bool IsPanButton(EnumMouseButton b) => b == EnumMouseButton.Middle || b == EnumMouseButton.Right;

    protected override void OnContentChanged() => BuildOutline();

    void BuildOutline()
    {
        var g = garment();
        int piece = selected();
        if (g == outlinedGarment && piece == outlinedPiece) return;
        outlinedGarment = g;
        outlinedPiece = piece;
        hEdges.Clear();
        vEdges.Clear();
        if (g == null || piece < 0 || piece >= g.Coverage.Length) return;
        var cov = g.Coverage[piece]; // the whole part, also under overlays (lane/garment-layers)
        int W = g.TextureWidth, H = g.TextureHeight;
        bool Is(int x, int y) => x >= 0 && y >= 0 && x < W && y < H && cov[y * W + x];
        // Edge rows y = 0..H (top edge of texel row y); a run is a stretch of texels whose edge on that line is a border.
        for (int y = 0; y <= H; y++)
        {
            int start = -1;
            for (int x = 0; x <= W; x++)
            {
                bool edge = x < W && Is(x, y) != Is(x, y - 1);
                if (edge && start < 0) start = x;
                if (!edge && start >= 0) { hEdges.Add((start, y, x - start)); start = -1; }
            }
        }
        for (int x = 0; x <= W; x++)
        {
            int start = -1;
            for (int y = 0; y <= H; y++)
            {
                bool edge = y < H && Is(x, y) != Is(x - 1, y);
                if (edge && start < 0) start = y;
                if (!edge && start >= 0) { vEdges.Add((x, start, y - start)); start = -1; }
            }
        }
    }

    static readonly Vec4f PieceOutline = new(1, 1, 1, 0.95f);

    protected override void RenderOverlay(double s, double ox, double oy)
    {
        BuildOutline(); // the selection may change without a content change
        double t = Math.Max(1, Math.Round(scaled(1.5)));
        double h = t / 2;
        foreach (var (x, y, n) in hEdges) Fill(ox + x * s - h, oy + y * s - h, n * s + t, t, PieceOutline);
        foreach (var (x, y, n) in vEdges) Fill(ox + x * s - h, oy + y * s - h, t, n * s + t, PieceOutline);
    }

    int PieceAt((int x, int y) t)
    {
        var g = garment();
        return g != null && view.Inside(t.x, t.y) ? g.Owner[t.y * g.TextureWidth + t.x] : -1;
    }

    (int dx, int dy) CellDelta((int x, int y) t)
    {
        int c = GarmentLayout.CellTexels;
        return ((int)Math.Round((t.x - dragX) / (double)c), (int)Math.Round((t.y - dragY) / (double)c));
    }

    protected override void OnCanvasMouseDown(MouseEvent args, (int x, int y) t)
    {
        if (args.Button != EnumMouseButton.Left) return;
        int piece = PieceAt(t);
        if (piece < 0) return;
        args.Handled = true;
        if (piece != selected()) onSelect(piece);
        dragPiece = piece;
        dragX = t.x;
        dragY = t.y;
        onDrag(piece, 0, 0, DragPhase.Begin);
    }

    protected override void OnCanvasMouseMove(MouseEvent args, (int x, int y) t)
    {
        if (dragPiece < 0) return;
        var (dx, dy) = CellDelta(t);
        onDrag(dragPiece, dx, dy, DragPhase.Move);
    }

    protected override void OnCanvasMouseUp(MouseEvent args, (int x, int y) t)
    {
        if (dragPiece < 0) return;
        var (dx, dy) = CellDelta(t);
        int piece = dragPiece;
        dragPiece = -1;
        onDrag(piece, dx, dy, DragPhase.End);
    }
}
