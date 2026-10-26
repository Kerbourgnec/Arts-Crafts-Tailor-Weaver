using System;
using ArtsAndCrafts.Tailor.Weaving;
using Cairo;
using Vintagestory.API.Client;
using EnumMouseButton = Vintagestory.API.Common.EnumMouseButton;

namespace ArtsAndCrafts.Tailor.Loom;

/// <summary>Part of the draft grid under the mouse (lane/draft-window).</summary>
public enum DraftRegion { None, Warp, Threading, TieUp, Drawdown, Treadling, Weft, Swatch }

/// <summary>A cell of the draft grid: region + column / row (meaning per region, see <see cref="GuiElementDraftGrid.HitAt"/>).</summary>
public readonly record struct DraftHit(DraftRegion Region, int A, int B);

/// <summary>
/// Copy square on the cursor (lane/loom-live-draft): <paramref name="Placing"/> false = picking the source (an outline of the
/// <paramref name="Size"/> square around the hovered cell), true = the source (<paramref name="SrcX"/>, <paramref name="SrcY"/>)
/// follows the cursor as a semi-transparent ghost.
/// </summary>
public readonly record struct SquareOverlay(bool Placing, int Size, int SrcX, int SrcY);

/// <summary>
/// The draft window's grid (lane/draft-window), laid out like the owner's designer: warp colour strip on top, threading
/// below it (shaft 4 top), tie-up to its right (4 treadles), drawdown in the centre (row 1 at the top), treadling and
/// weft colour to the right of the drawdown. One Cairo texture, redrawn on every change (<see cref="Redraw"/>).
/// <para>
/// Cells at zoom 1 (whole cloth): 12 px at 16, 8 px at 32, unscaled; wheel / buttons zoom, drag pans (lane/draft-zoom, <see cref="DraftView"/>). A click on a
/// warp or weft cell opens a small palette popup (7 x 2 swatches) inside the grid for the brush span around the cell
/// (lane/draft-polish: all / 8 / 4 / 2 / 1, previewed on hover); shift + click a second cell of the same
/// strip extends it to a range. Everything else goes to <c>onClick</c>.
/// </para>
/// </summary>
public class GuiElementDraftGrid : GuiElement
{
    public const double Gap = 6;
    readonly Func<DraftWindowModel> model;
    readonly Func<string, string> hexOf;
    readonly Action<DraftHit, bool> onClick;
    readonly Action<DraftHit> onHover;
    readonly Action<DraftRegion, int, int, int> onColour; // strip (Warp / Weft), from, to, colour
    int texId;
    DraftHit hover = new(DraftRegion.None, -1, -1);
    /// <summary>Open palette popup: strip, first clicked cell, anchor (last clicked cell), or <see cref="DraftRegion.None"/>.</summary>
    (DraftRegion strip, int a, int b) popup = (DraftRegion.None, -1, -1);
    /// <summary>Cells the popup's colour goes to (inclusive): the brush span of the click, or the shift + click range.</summary>
    (int lo, int hi) span = (-1, -1);
    /// <summary>Colour brush width (lane/draft-polish: 0 = all, 8, 4, 2, 1).</summary>
    readonly Func<int> brushWidth;

    public GuiElementDraftGrid(ICoreClientAPI capi, ElementBounds bounds, Func<DraftWindowModel> model, Func<string, string> hexOf,
        Action<DraftHit, bool> onClick, Action<DraftHit> onHover, Action<DraftRegion, int, int, int> onColour, Func<int> brushWidth) : base(capi, bounds)
    {
        this.brushWidth = brushWidth;
        this.model = model;
        this.hexOf = hexOf;
        this.onClick = onClick;
        this.onHover = onHover;
        this.onColour = onColour;
    }

    /// <summary>Unscaled cell size at zoom 1 (the whole cloth fits) for a cloth of <paramref name="n"/>.</summary>
    public static double CellFor(int n) => n <= 16 ? 12 : 8;

    /// <summary>Unscaled grid size for a cloth of <paramref name="n"/> (zoom does not change it).</summary>
    public static (double w, double h) SizeFor(int n) => DraftView.SizeFor(n, CellFor(n), Gap);

    /// <summary>Copy square on the cursor, or null (the window sets it; <see cref="Redraw"/> draws it at the hovered drawdown cell).</summary>
    public SquareOverlay? Square { get; set; }

    public bool PopupOpen => popup.strip != DraftRegion.None;
    public void ClosePopup() { popup = (DraftRegion.None, -1, -1); Redraw(); }

    // Zoom and pan (lane/draft-zoom) are kept for the session: a new window or a recompose at the same size starts where it was.
    static int sessionN;
    static double sessionZoom = 1, sessionPanX, sessionPanY;

    DraftView? viewCache;
    /// <summary>Grid geometry at the current GUI scale (rebuilt, keeping zoom and pan, when the scale changes).</summary>
    DraftView V
    {
        get
        {
            int n = model().N;
            double sc = scaled(1);
            if (viewCache == null || viewCache.N != n || Math.Abs(viewCache.Scale - sc) > 1e-9)
            {
                var old = viewCache;
                viewCache = new DraftView(n, CellFor(n), DraftView.FixedFor(CellFor(n)), Gap, sc);
                if (old != null && old.N == n) viewCache.Restore(old.Zoom, old.PanXU, old.PanYU);
                else if (sessionN == n) viewCache.Restore(sessionZoom, sessionPanX, sessionPanY);
            }
            return viewCache;
        }
    }

    void SaveSession()
    {
        var v = V;
        sessionN = v.N; sessionZoom = v.Zoom; sessionPanX = v.PanXU; sessionPanY = v.PanYU;
    }

    public double Zoom => V.Zoom;

    /// <summary>+ / - buttons: one zoom step about the viewport centre (dir 0 = fit the whole cloth).</summary>
    public void ZoomStep(int dir)
    {
        if (dir == 0) V.Fit(); else V.ZoomCentre(dir > 0 ? DraftView.ZoomFactor : 1 / DraftView.ZoomFactor);
        ViewChanged();
    }

    void ViewChanged()
    {
        SaveSession();
        UpdateHover(api.Input.MouseX, api.Input.MouseY, force: true);
        Redraw();
    }

    double Sw => scaled(14);

    (double x, double y) PopupOrigin()
    {
        var v = V;
        double w = 7 * Sw, h = 2 * Sw;
        double x, y;
        if (popup.strip == DraftRegion.Warp) { x = v.ColX(popup.b); y = v.F + scaled(2); }
        else { x = v.WeftX - w - scaled(2); y = v.RowY(popup.b); }
        x = Math.Clamp(x, 0, Math.Max(0, Bounds.InnerWidth - w));
        y = Math.Clamp(y, 0, Math.Max(0, Bounds.InnerHeight - h));
        return (x, y);
    }

    /// <summary>
    /// The cell at element-relative (x, y) px, at any zoom and pan: Warp (end, -), Threading (end, shaft), TieUp (treadle, shaft),
    /// Drawdown (end, row), Treadling (row, treadle), Weft (row, -), Swatch (colour, -).
    /// </summary>
    public DraftHit HitAt(double x, double y)
    {
        var v = V;
        if (PopupOpen)
        {
            var (px, py) = PopupOrigin();
            if (x >= px && y >= py && x < px + 7 * Sw && y < py + 2 * Sw)
            {
                int col = (int)((x - px) / Sw), row = (int)((y - py) / Sw);
                int idx = row * 7 + col;
                return idx < DraftWindowModel.PaletteSize ? new DraftHit(DraftRegion.Swatch, idx, 0) : default;
            }
        }
        int c = v.ColAt(x), r = v.RowAt(y);
        double f = v.F;
        bool inTie = x >= v.TieX && x < v.TieX + DraftView.Treadles * f;
        int treadle = (int)Math.Floor((x - v.TieX) / f);
        if (y >= 0 && y < f && c >= 0) return new DraftHit(DraftRegion.Warp, c, 0);
        if (y >= v.ThrY && y < v.ThrY + DraftView.Shafts * f)
        {
            int shaft = DraftView.Shafts - 1 - (int)Math.Floor((y - v.ThrY) / f);
            if (c >= 0) return new DraftHit(DraftRegion.Threading, c, shaft);
            if (inTie) return new DraftHit(DraftRegion.TieUp, treadle, shaft);
        }
        if (r >= 0)
        {
            if (c >= 0) return new DraftHit(DraftRegion.Drawdown, c, r);
            if (inTie) return new DraftHit(DraftRegion.Treadling, r, treadle);
            if (x >= v.WeftX && x < v.WeftX + f) return new DraftHit(DraftRegion.Weft, r, 0);
        }
        return new DraftHit(DraftRegion.None, -1, -1);
    }

    /// <summary>The row the hover points at (drawdown, treadling or weft), else -1.</summary>
    int HoverRow => hover.Region switch
    {
        DraftRegion.Drawdown => hover.B,
        DraftRegion.Treadling or DraftRegion.Weft => hover.A,
        _ => -1,
    };

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();
        Redraw();
    }

    static void Rgb(Context ctx, string hex, double alpha = 1)
    {
        TwineColors.TryParse(hex, out int rgb);
        ctx.SetSourceRGBA(((rgb >> 16) & 0xff) / 255.0, ((rgb >> 8) & 0xff) / 255.0, (rgb & 0xff) / 255.0, alpha);
    }

    void Box(Context ctx, double x, double y, double w, double h, string hex, double alpha = 1)
    {
        Rgb(ctx, hex, alpha);
        ctx.Rectangle(x, y, w, h);
        ctx.Fill();
    }

    void Outline(Context ctx, double x, double y, double w, double h, string hex, double width = 1.5)
    {
        Rgb(ctx, hex);
        ctx.LineWidth = scaled(width);
        double i = ctx.LineWidth / 2;
        ctx.Rectangle(x + i, y + i, w - 2 * i, h - 2 * i);
        ctx.Stroke();
    }

    const string OnHex = "#e7e6e1", OffHex = "#3a3f45", WarnHex = "#f0b44a", BadHex = "#e0785f", LiftHex = "#8fa8d8", WovenLineHex = "#c9a227";

    /// <summary>Clip to a rectangle until <see cref="Context.Restore"/>.</summary>
    static void ClipTo(Context ctx, double x, double y, double w, double h)
    {
        ctx.Save();
        ctx.Rectangle(x, y, w, h);
        ctx.Clip();
    }

    public void Redraw()
    {
        if (Bounds.OuterWidthInt <= 0 || Bounds.OuterHeightInt <= 0) return;
        var m = model();
        var v = V;
        int n = m.N;
        double c = v.Cell, f = v.F, inset = Math.Max(1, scaled(1));
        double vs = v.ViewSize, ddY = v.DdY, tieX = v.TieX, weftX = v.WeftX, thrY = v.ThrY;
        using var surface = new ImageSurface(Format.Argb32, Bounds.OuterWidthInt, Bounds.OuterHeightInt);
        using var ctx = new Context(surface);
        ctx.SetSourceRGBA(0.08, 0.07, 0.06, 0.9);
        ctx.Rectangle(0, 0, Bounds.OuterWidth, Bounds.OuterHeight);
        ctx.Fill();

        var floats = m.Floats().mask;
        int hoverRow = HoverRow;
        int hoverLift = hoverRow >= 0 && hoverRow < n ? m.LiftOf(hoverRow) : -1;
        double lockAlpha = m.ThreadingLocked ? 0.45 : 1;
        // Visible columns / rows (the strips scroll with their axis; the tie-up never does).
        int c0 = Math.Max(0, (int)Math.Floor(v.Ox / c)), c1 = Math.Min(n - 1, (int)Math.Floor((v.Ox + vs) / c));
        int r0 = Math.Max(0, (int)Math.Floor(v.Oy / c)), r1 = Math.Min(n - 1, (int)Math.Floor((v.Oy + vs) / c));

        // Warp strip and threading: columns scroll horizontally.
        ClipTo(ctx, 0, 0, vs, thrY + DraftView.Shafts * f);
        for (int x = c0; x <= c1; x++)
        {
            double cx = v.ColX(x);
            string target = hexOf(DraftWindowModel.CodeOf(m.Warp[x]));
            Box(ctx, cx + inset / 2, 0, c - inset, f, target);
            if (x < m.LoadedWarp.Length && m.LoadedWarp[x] != DraftWindowModel.CodeOf(m.Warp[x]))
            {
                Box(ctx, cx + inset / 2, f * 0.62, c - inset, f * 0.38, hexOf(m.LoadedWarp[x]));
                Outline(ctx, cx, 0, c, f, WarnHex, 1);
            }
            for (int s = 0; s < DraftView.Shafts; s++)
            {
                double y = thrY + (DraftView.Shafts - 1 - s) * f;
                bool on = m.Threading[x] == s;
                Box(ctx, cx + inset / 2, y + inset / 2, c - inset, f - inset, on ? OnHex : OffHex, lockAlpha);
                if (on && hoverLift >= 0 && ((hoverLift >> s) & 1) != 0) Outline(ctx, cx, y, c, f, LiftHex);
            }
        }
        ctx.Restore();
        // Tie-up (fixed).
        for (int s = 0; s < DraftView.Shafts; s++)
        {
            double y = thrY + (DraftView.Shafts - 1 - s) * f;
            for (int t = 0; t < DraftView.Treadles; t++)
            {
                bool on = ((m.TieUp[t] >> s) & 1) != 0;
                double x = tieX + t * f;
                Box(ctx, x + inset / 2, y + inset / 2, f - inset, f - inset, on ? OnHex : OffHex);
                if (m.NoShed(t)) Outline(ctx, x, y, f, f, BadHex, 1);
                if (hoverRow >= 0 && hoverRow < n && !m.Woven(hoverRow) && m.Treadling[hoverRow] == t) Outline(ctx, x, y, f, f, LiftHex);
            }
        }
        // Drawdown, treadling and weft per row, clipped to the viewport (woven rows: the loom's cells; treadling greyed).
        ClipTo(ctx, 0, ddY, weftX + f, vs);
        for (int y = r0; y <= r1; y++)
        {
            double py = v.RowY(y);
            bool woven = m.Woven(y);
            for (int x = c0; x <= c1; x++)
            {
                double px = v.ColX(x);
                Box(ctx, px, py, c, c, hexOf(m.CellCode(x, y)));
                if (floats[y * n + x]) Outline(ctx, px, py, c, c, WarnHex, 1);
            }
            bool tapestryRow = woven && m.WovenLifts[y] < 0;
            for (int t = 0; t < DraftView.Treadles; t++)
            {
                bool on = !tapestryRow && m.Treadling[y] == t;
                Box(ctx, tieX + t * f + inset / 2, py + inset / 2, f - inset, c - inset, on ? (!woven && m.NoShed(t) ? BadHex : OnHex) : OffHex, woven ? 0.4 : 1);
            }
            Box(ctx, weftX, py + inset / 2, f, c - inset, hexOf(woven && tapestryRow ? DraftState.PlainCode : DraftWindowModel.CodeOf(m.Weft[y])), woven ? 0.5 : 1);
            if (y == hoverRow) Outline(ctx, tieX, py, DraftView.Treadles * f, c, LiftHex, 1);
        }
        // The fell: a line under the woven rows (rows above it are read-only).
        if (m.WovenRows > 0 && m.WovenRows <= n)
        {
            Rgb(ctx, WovenLineHex);
            ctx.LineWidth = scaled(2);
            double ly = v.RowY(m.WovenRows);
            ctx.MoveTo(0, ly);
            ctx.LineTo(weftX + f, ly);
            ctx.Stroke();
        }
        ctx.Restore();
        // Brush preview: the cells a click on the hovered warp / weft cell would colour.
        if (!PopupOpen && (hover.Region is DraftRegion.Warp or DraftRegion.Weft) && hover.A >= 0 && hover.A < n)
        {
            var (lo, hi) = DraftWindowModel.BrushRange(brushWidth(), hover.A, n);
            if (hover.Region == DraftRegion.Warp) ClipTo(ctx, 0, 0, vs, f); else ClipTo(ctx, weftX, ddY, f, vs);
            for (int i = lo; i <= hi; i++)
            {
                if (hover.Region == DraftRegion.Warp) Outline(ctx, v.ColX(i), 0, c, f, LiftHex, 1);
                else if (!m.Woven(i)) Outline(ctx, weftX, v.RowY(i), f, c, LiftHex, 1);
            }
            ctx.Restore();
        }
        if (Square is { } sq && !PopupOpen)
        {
            ClipTo(ctx, 0, ddY, vs, vs);
            DrawSquare(ctx, m, sq);
            ctx.Restore();
        }
        if (PopupOpen) DrawPopup(ctx, m);
        DrawScrollbars(ctx, v);
        generateTexture(surface, ref texId);
    }

    const double BarPx = 5;

    /// <summary>Scrollbar thumb rectangles (screen px, element-relative): horizontal along the bottom / vertical along the right edge of the drawdown, only when zoomed in.</summary>
    (double x, double y, double w, double h)? ThumbRect(DraftView v, bool horizontal)
    {
        if (v.Thumb(horizontal) is not { } t) return null;
        double bar = scaled(BarPx);
        return horizontal ? (t.start, v.DdY + v.ViewSize - bar, t.len, bar) : (v.ViewSize - bar, v.DdY + t.start, bar, t.len);
    }

    void DrawScrollbars(Context ctx, DraftView v)
    {
        foreach (bool horizontal in new[] { true, false })
        {
            if (ThumbRect(v, horizontal) is not { } r) continue;
            double bar = scaled(BarPx);
            if (horizontal) Box(ctx, 0, v.DdY + v.ViewSize - bar, v.ViewSize, bar, "#000000", 0.35);
            else Box(ctx, v.ViewSize - bar, v.DdY, bar, v.ViewSize, "#000000", 0.35);
            bool active = dragBar == horizontal && dragging;
            Box(ctx, r.x, r.y, r.w, r.h, active ? "#ffffff" : "#c8c8c8", active ? 0.85 : 0.6);
        }
    }

    /// <summary>
    /// Copy square: the square around the hovered drawdown cell (clamped inside the cloth). While placing, the source cells are drawn
    /// there semi-transparent (the copied threading, treadling and colours give exactly those cells), woven rows skipped; the
    /// source square keeps a dim outline.
    /// </summary>
    void DrawSquare(Context ctx, DraftWindowModel m, SquareOverlay sq)
    {
        var v = V;
        double c = v.Cell;
        int n = m.N, size = Math.Min(sq.Size, n);
        if (sq.Placing) Outline(ctx, v.ColX(sq.SrcX), v.RowY(sq.SrcY), size * c, size * c, "#ffffff", 1);
        if (hover.Region != DraftRegion.Drawdown) return;
        int x0 = DraftWindowModel.SquareStart(size, hover.A, n), y0 = DraftWindowModel.SquareStart(size, hover.B, n);
        if (sq.Placing)
        {
            for (int j = 0; j < size; j++)
            {
                if (m.Woven(y0 + j)) continue;
                for (int i = 0; i < size; i++) Box(ctx, v.ColX(x0 + i), v.RowY(y0 + j), c, c, hexOf(m.CellCode(sq.SrcX + i, sq.SrcY + j)), 0.6);
            }
        }
        else Box(ctx, v.ColX(x0), v.RowY(y0), size * c, size * c, "#ffffff", 0.25);
        Outline(ctx, v.ColX(x0), v.RowY(y0), size * c, size * c, sq.Placing ? LiftHex : "#ffffff", 2);
    }

    void DrawPopup(Context ctx, DraftWindowModel m)
    {
        // Selected range outlined, then the swatches (current colour of the clicked cell outlined).
        var (lo, hi) = span;
        var v = V;
        double c = v.Cell;
        if (popup.strip == DraftRegion.Warp) ClipTo(ctx, 0, 0, v.ViewSize, v.F); else ClipTo(ctx, v.WeftX, v.DdY, v.F, v.ViewSize);
        for (int i = lo; i <= hi; i++)
        {
            if (popup.strip == DraftRegion.Warp) Outline(ctx, v.ColX(i), 0, c, v.F, "#ffffff");
            else Outline(ctx, v.WeftX, v.RowY(i), v.F, c, "#ffffff");
        }
        ctx.Restore();
        var (px, py) = PopupOrigin();
        Box(ctx, px - scaled(2), py - scaled(2), 7 * Sw + scaled(4), 2 * Sw + scaled(4), "#14171a", 0.95);
        int current = popup.strip == DraftRegion.Warp ? m.Warp[Math.Clamp(popup.b, 0, m.N - 1)] : m.Weft[Math.Clamp(popup.b, 0, m.N - 1)];
        for (int k = 0; k < DraftWindowModel.PaletteSize; k++)
        {
            double x = px + (k % 7) * Sw, y = py + (k / 7) * Sw;
            Box(ctx, x + 1, y + 1, Sw - 2, Sw - 2, hexOf(DraftWindowModel.CodeOf(k)));
            if (k == current) Outline(ctx, x, y, Sw, Sw, "#ffffff", 2);
        }
    }

    // Pan: middle or right button drag on the grid (a right click with a square on the cursor still cancels it: the window takes it first),
    // or drag a scrollbar thumb; the wheel zooms. State of a drag:
    bool panning, dragging, dragBar; // dragBar: true = horizontal thumb
    double lastX, lastY, grab;

    public override void OnMouseDownOnElement(ICoreClientAPI capi, MouseEvent args)
    {
        double ex = args.X - Bounds.absX, ey = args.Y - Bounds.absY;
        args.Handled = true;
        if (args.Button is EnumMouseButton.Middle or EnumMouseButton.Right)
        {
            if (V.Zoomed) { panning = true; lastX = args.X; lastY = args.Y; }
            return;
        }
        if (args.Button != EnumMouseButton.Left) return;
        var v = V;
        foreach (bool horizontal in new[] { true, false })
        {
            if (ThumbRect(v, horizontal) is not { } r) continue;
            if (ex >= r.x && ey >= r.y && ex < r.x + r.w && ey < r.y + r.h)
            {
                dragging = true; dragBar = horizontal;
                grab = horizontal ? ex - r.x : ey - r.y;
                Redraw();
                return;
            }
        }
        var hit = HitAt(ex, ey);
        if (Square != null)
        {
            if (hit.Region == DraftRegion.Drawdown) onClick(hit, false); // a square on the cursor: only the drawdown takes clicks (the window picks / places it)
            return;
        }
        bool shift = capi.Input.KeyboardKeyStateRaw[(int)GlKeys.ShiftLeft] || capi.Input.KeyboardKeyStateRaw[(int)GlKeys.ShiftRight];
        if (hit.Region == DraftRegion.Swatch)
        {
            var strip = popup.strip;
            var (lo, hi) = span;
            popup = (DraftRegion.None, -1, -1);
            onColour(strip, lo, hi, hit.A);
            return;
        }
        if (hit.Region is DraftRegion.Warp or DraftRegion.Weft)
        {
            int i = hit.A;
            // Shift + click a second cell of the same strip: that range; else the brush span around the cell.
            if (shift && popup.strip == hit.Region) { popup = (hit.Region, popup.a, i); span = (Math.Min(popup.a, i), Math.Max(popup.a, i)); }
            else { popup = (hit.Region, i, i); span = DraftWindowModel.BrushRange(brushWidth(), i, model().N); }
            capi.Gui.PlaySound("tick");
            Redraw();
            return;
        }
        if (PopupOpen) { ClosePopup(); return; }
        if (hit.Region != DraftRegion.None) onClick(hit, shift);
    }

    public override void OnMouseUp(ICoreClientAPI capi, MouseEvent args)
    {
        bool was = dragging;
        panning = dragging = false;
        if (was) Redraw();
        base.OnMouseUp(capi, args);
    }

    public override void OnMouseMove(ICoreClientAPI capi, MouseEvent args)
    {
        var v = V;
        if (dragging)
        {
            double along = (dragBar ? args.X - Bounds.absX : args.Y - Bounds.absY) - grab;
            if (v.Thumb(dragBar) is { } th)
            {
                double pan = v.PanForThumb(along, th.len);
                if (dragBar) v.SetPan(pan, v.Oy); else v.SetPan(v.Ox, pan);
                ViewChanged();
            }
            return;
        }
        if (panning)
        {
            v.PanBy(lastX - args.X, lastY - args.Y); // the content follows the cursor
            lastX = args.X; lastY = args.Y;
            ViewChanged();
            return;
        }
        UpdateHover(args.X, args.Y);
    }

    void UpdateHover(int mx, int my, bool force = false)
    {
        var hit = IsPositionInside(mx, my) ? HitAt(mx - Bounds.absX, my - Bounds.absY) : new DraftHit(DraftRegion.None, -1, -1);
        if (hit == hover && !force) return;
        int before = HoverRow;
        bool strip = hover.Region == DraftRegion.Warp || hit.Region == DraftRegion.Warp; // the brush preview follows the column
        hover = hit;
        onHover(hit);
        if (HoverRow != before || strip || Square != null) Redraw();
    }

    public override void OnMouseWheel(ICoreClientAPI capi, MouseWheelEventArgs args)
    {
        int mx = capi.Input.MouseX, my = capi.Input.MouseY;
        if (!IsPositionInside(mx, my) || args.delta == 0) return;
        args.SetHandled(true);
        V.ZoomAt(args.delta > 0 ? DraftView.ZoomFactor : 1 / DraftView.ZoomFactor, mx - Bounds.absX, my - Bounds.absY);
        ViewChanged();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (texId > 0) api.Render.Render2DTexture(texId, Bounds);
    }

    public override void Dispose()
    {
        base.Dispose();
        if (texId > 0) api.Render.GLDeleteTexture(texId);
        texId = 0;
    }
}
