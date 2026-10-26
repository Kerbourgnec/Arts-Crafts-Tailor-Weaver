using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using EnumMouseButton = Vintagestory.API.Common.EnumMouseButton;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Shared garment canvas (lane/sewing-editor-xl) of the Sew and Embroider steps: the composed garment bitmap
/// (<c>pixels()</c>, one int ARGB per texel) drawn at the zoom and pan of a <see cref="CanvasView"/> the dialog owns
/// (so the view survives dialog rebuilds). Drawing is a pure function of that bitmap: the bitmap becomes a W x H texture
/// (nearest filtering) on <see cref="Redraw"/>; zoom, pan, grid, crosshair and overlays are drawn per frame with no
/// texture upload. Wheel zooms at the mouse; middle drag, or left drag while <see cref="PanHeld"/> (space), pans;
/// subclasses take the remaining buttons (<see cref="IsPanButton"/>).
/// </summary>
public abstract class GuiElementGarmentCanvas : GuiElement
{
    protected readonly Func<GarmentDef?> garment;
    readonly Func<int[]?> pixels;
    protected readonly CanvasView view;
    int bitmapTex, pixelTex;
    bool hasBitmap;
    bool panning;
    int panX, panY;
    protected (int x, int y) hover = (-1, -1);

    /// <summary>Hovered texel changed ((-1, -1) = none): the dialog's cell readout.</summary>
    public Action<int, int>? HoverChanged;
    /// <summary>Space held: left drag pans.</summary>
    public Func<bool>? PanHeld;
    /// <summary>Zoom or pan changed (readout shows the zoom).</summary>
    public Action? ViewChanged;

    protected GuiElementGarmentCanvas(ICoreClientAPI capi, ElementBounds bounds, Func<GarmentDef?> garment, Func<int[]?> pixels,
        CanvasView view) : base(capi, bounds)
    {
        this.garment = garment;
        this.pixels = pixels;
        this.view = view;
    }

    public CanvasView View => view;
    public (int x, int y) Hover => hover;
    public bool Panning => panning;

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();
        using (var s = new ImageSurface(Format.Argb32, 1, 1))
        using (var c = new Context(s))
        {
            c.SetSourceRGBA(1, 1, 1, 1);
            c.Paint();
            generateTexture(s, ref pixelTex, false);
        }
        Redraw();
    }

    void SyncView()
    {
        var g = garment();
        if (g != null && view.SetSize(g.TextureWidth, g.TextureHeight, Bounds.OuterWidth, Bounds.OuterHeight)) ViewChanged?.Invoke();
    }

    /// <summary>Per-texel colour drawn (subclasses dim texels); <paramref name="i"/> = y * W + x.</summary>
    protected virtual int TexelColor(int i, int argb) => argb;

    /// <summary>The bitmap (or what the subclass derives from it) changed: rebuild the W x H texture.</summary>
    public void Redraw()
    {
        if (Bounds.OuterWidthInt <= 0 || Bounds.OuterHeightInt <= 0) return;
        SyncView();
        var g = garment();
        var px = pixels();
        int W = g?.TextureWidth ?? 0, H = g?.TextureHeight ?? 0;
        hasBitmap = g != null && px != null && W > 0 && H > 0 && px.Length == W * H;
        if (hasBitmap)
        {
            using var surface = new ImageSurface(Format.Argb32, W, H);
            using var ctx = new Context(surface);
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int argb = TexelColor(y * W + x, px![y * W + x]);
                    int a = (argb >> 24) & 0xff;
                    if (a == 0) continue;
                    ctx.SetSourceRGBA(((argb >> 16) & 0xff) / 255.0, ((argb >> 8) & 0xff) / 255.0, (argb & 0xff) / 255.0, a / 255.0);
                    ctx.Rectangle(x, y, 1, 1);
                    ctx.Fill();
                }
            }
            generateTexture(surface, ref bitmapTex, false); // nearest: one crisp square per texel at any zoom
        }
        OnContentChanged();
    }

    protected virtual void OnContentChanged() { }

    // --- Drawing (per frame) ----------------------------------------------------------------------------------

    protected void Fill(double x, double y, double w, double h, Vec4f color)
    {
        if (pixelTex > 0 && w > 0 && h > 0) api.Render.Render2DTexture(pixelTex, (float)x, (float)y, (float)w, (float)h, 50, color);
    }

    protected void Frame(double x, double y, double w, double h, double t, Vec4f color)
    {
        Fill(x, y, w, t, color);
        Fill(x, y + h - t, w, t, color);
        Fill(x, y + t, t, h - 2 * t, color);
        Fill(x + w - t, y + t, t, h - 2 * t, color);
    }

    /// <summary>Screen position (render coordinates) of a texel corner.</summary>
    protected (double x, double y) At(double tx, double ty)
    {
        var (x, y) = view.TexelToScreen(tx, ty);
        return (Bounds.renderX + x, Bounds.renderY + y);
    }

    static readonly Vec4f Back = new(0.08f, 0.07f, 0.06f, 0.85f);
    static readonly Vec4f TexBack = new(1, 1, 1, 0.04f);
    static readonly Vec4f GridLine = new(0, 0, 0, 0.35f);
    static readonly Vec4f CrossBand = new(1, 1, 1, 0.07f);
    static readonly Vec4f HoverLine = new(1, 1, 1, 0.9f);

    public override void RenderInteractiveElements(float deltaTime)
    {
        api.Render.PushScissor(Bounds);
        Fill(Bounds.renderX, Bounds.renderY, Bounds.OuterWidth, Bounds.OuterHeight, Back);
        var g = garment();
        if (hasBitmap && g != null && bitmapTex > 0 && view.TexW == g.TextureWidth && view.TexH == g.TextureHeight)
        {
            double s = view.Scale;
            var (ox, oy) = At(0, 0);
            Fill(ox, oy, view.TexW * s, view.TexH * s, TexBack);
            api.Render.Render2DTexture(bitmapTex, (float)ox, (float)oy, (float)(view.TexW * s), (float)(view.TexH * s));
            var (x0, y0, x1, y1) = view.VisibleTexels();
            if (view.GridVisible)
            {
                for (int x = x0; x <= x1; x++) Fill(ox + x * s, oy + y0 * s, 1, (y1 - y0) * s, GridLine);
                for (int y = y0; y <= y1; y++) Fill(ox + x0 * s, oy + y * s, (x1 - x0) * s, 1, GridLine);
            }
            RenderOverlay(s, ox, oy);
            if (view.Inside(hover.x, hover.y))
            {
                // Crosshair: the hovered texel's row and column, faint, plus its outline.
                Fill(ox + x0 * s, oy + hover.y * s, (x1 - x0) * s, s, CrossBand);
                Fill(ox + hover.x * s, oy + y0 * s, s, (y1 - y0) * s, CrossBand);
                Frame(ox + hover.x * s, oy + hover.y * s, s, s, Math.Max(1, Math.Floor(s / 8)), HoverLine);
            }
        }
        api.Render.PopScissor();
    }

    /// <summary>Drawn above the bitmap and grid, below the crosshair; s = pixels per texel, (ox, oy) = texel (0, 0).</summary>
    protected virtual void RenderOverlay(double s, double ox, double oy) { }

    // --- Input ------------------------------------------------------------------------------------------------

    protected (int x, int y) TexelAt(int mouseX, int mouseY) => view.TexelAt(mouseX - Bounds.absX, mouseY - Bounds.absY);

    protected virtual bool IsPanButton(EnumMouseButton b) => b == EnumMouseButton.Middle;

    public override void OnMouseDownOnElement(ICoreClientAPI capi, MouseEvent args)
    {
        if (IsPanButton(args.Button) || args.Button == EnumMouseButton.Left && PanHeld?.Invoke() == true)
        {
            args.Handled = true;
            panning = true;
            panX = args.X;
            panY = args.Y;
            return;
        }
        OnCanvasMouseDown(args, TexelAt(args.X, args.Y));
    }

    protected abstract void OnCanvasMouseDown(MouseEvent args, (int x, int y) texel);
    protected virtual void OnCanvasMouseMove(MouseEvent args, (int x, int y) texel) { }
    protected virtual void OnCanvasMouseUp(MouseEvent args, (int x, int y) texel) { }

    public override void OnMouseMove(ICoreClientAPI capi, MouseEvent args)
    {
        if (panning)
        {
            view.PanBy(args.X - panX, args.Y - panY);
            panX = args.X;
            panY = args.Y;
            ViewChanged?.Invoke();
        }
        UpdateHover(args.X, args.Y);
        if (!panning) OnCanvasMouseMove(args, TexelAt(args.X, args.Y));
    }

    void UpdateHover(int mouseX, int mouseY)
    {
        var t = TexelAt(mouseX, mouseY);
        if (!IsPositionInside(mouseX, mouseY) || !view.Inside(t.x, t.y)) t = (-1, -1);
        if (t == hover) return;
        hover = t;
        HoverChanged?.Invoke(t.x, t.y);
    }

    public override void OnMouseUp(ICoreClientAPI capi, MouseEvent args)
    {
        if (panning) panning = false;
        else OnCanvasMouseUp(args, TexelAt(args.X, args.Y));
        base.OnMouseUp(capi, args);
    }

    public override void OnMouseWheel(ICoreClientAPI capi, MouseWheelEventArgs args)
    {
        int mx = capi.Input.MouseX, my = capi.Input.MouseY;
        if (!IsPositionInside(mx, my) || args.delta == 0) return;
        args.SetHandled(true);
        view.ZoomStep(Math.Sign(args.delta), mx - Bounds.absX, my - Bounds.absY);
        UpdateHover(mx, my);
        ViewChanged?.Invoke();
    }

    /// <summary>Zoom about the centre (buttons, keys) or fit.</summary>
    public void Zoom(int dir)
    {
        if (dir == 0) view.Fit();
        else view.ZoomStep(dir);
        UpdateHover(api.Input.MouseX, api.Input.MouseY);
        ViewChanged?.Invoke();
    }

    public override void Dispose()
    {
        base.Dispose();
        if (bitmapTex > 0) api.Render.GLDeleteTexture(bitmapTex);
        if (pixelTex > 0) api.Render.GLDeleteTexture(pixelTex);
        bitmapTex = pixelTex = 0;
    }
}
