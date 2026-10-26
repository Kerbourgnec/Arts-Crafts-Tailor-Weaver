using System;
using System.Collections.Generic;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Zoom and pan of a garment canvas (lane/sewing-editor-xl), pure maths, no game types (selftest: EditorViewChecks).
/// Screen coordinates are pixels relative to the canvas' top-left corner; texel (0, 0)'s top-left corner sits at
/// (<see cref="OffX"/>, <see cref="OffY"/>) and one texel is <see cref="Scale"/> pixels wide. Offsets stay whole pixels
/// so texel edges stay crisp. Zoom keeps the texel under the zoom point fixed; panning keeps a part of the texture on screen.
/// </summary>
public sealed class CanvasView
{
    /// <summary>Zoom levels in screen pixels per texel (mouse wheel, + / - buttons step through them).</summary>
    public static readonly double[] Zooms = { 1, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24, 32, 40, 48, 64 };
    /// <summary>Texel grid lines are drawn from this many pixels per texel.</summary>
    public const double GridMinScale = 8;
    /// <summary>At most this many pixels of the texture may leave the view on any side minus this margin (pan clamp).</summary>
    public const double KeepVisible = 32;

    public int TexW { get; private set; }
    public int TexH { get; private set; }
    public double ViewW { get; private set; }
    public double ViewH { get; private set; }
    public double Scale { get; private set; } = 1;
    public double OffX { get; private set; }
    public double OffY { get; private set; }

    public bool GridVisible => Scale >= GridMinScale;

    /// <summary>Texture or view size changed (other garment, other dialog layout): fit again. Same sizes: view kept.</summary>
    public bool SetSize(int texW, int texH, double viewW, double viewH)
    {
        if (texW == TexW && texH == TexH && viewW == ViewW && viewH == ViewH) return false;
        TexW = texW; TexH = texH; ViewW = viewW; ViewH = viewH;
        Fit();
        return true;
    }

    /// <summary>Largest whole scale that shows the whole texture (at least 1).</summary>
    public double FitScale => TexW <= 0 || TexH <= 0 ? 1 : Math.Max(1, Math.Floor(Math.Min(ViewW / TexW, ViewH / TexH)));

    /// <summary>Whole texture, centred, at <see cref="FitScale"/> (the compact dialog's old fixed layout).</summary>
    public void Fit()
    {
        Scale = FitScale;
        OffX = Math.Floor((ViewW - TexW * Scale) / 2);
        OffY = Math.Floor((ViewH - TexH * Scale) / 2);
    }

    public double MinScale => Math.Min(Zooms[0], FitScale);
    public double MaxScale => Zooms[^1];

    public (double x, double y) ScreenToTexelF(double sx, double sy) => ((sx - OffX) / Scale, (sy - OffY) / Scale);

    /// <summary>Texel under a screen point (may be outside the texture; see <see cref="Inside"/>).</summary>
    public (int x, int y) TexelAt(double sx, double sy)
    {
        var (x, y) = ScreenToTexelF(sx, sy);
        return ((int)Math.Floor(x), (int)Math.Floor(y));
    }

    public bool Inside(int x, int y) => x >= 0 && y >= 0 && x < TexW && y < TexH;

    /// <summary>Top-left screen corner of a texel.</summary>
    public (double x, double y) TexelToScreen(double tx, double ty) => (OffX + tx * Scale, OffY + ty * Scale);

    /// <summary>Zoom to <paramref name="scale"/> (clamped) keeping the texture point under (sx, sy) fixed.</summary>
    public void ZoomTo(double scale, double sx, double sy)
    {
        scale = Math.Clamp(scale, MinScale, MaxScale);
        var (tx, ty) = ScreenToTexelF(sx, sy);
        Scale = scale;
        OffX = Math.Round(sx - tx * scale);
        OffY = Math.Round(sy - ty * scale);
        Clamp();
    }

    /// <summary>Next zoom level up (dir &gt; 0) or down (dir &lt; 0) from the current scale.</summary>
    public double NextZoom(int dir)
    {
        if (dir > 0)
        {
            foreach (double z in Zooms) if (z > Scale + 1e-9) return z;
            return MaxScale;
        }
        for (int i = Zooms.Length - 1; i >= 0; i--) if (Zooms[i] < Scale - 1e-9) return Zooms[i];
        return MinScale;
    }

    public void ZoomStep(int dir, double sx, double sy) => ZoomTo(NextZoom(dir), sx, sy);

    /// <summary>Zoom about the view centre (+ / - buttons and keys).</summary>
    public void ZoomStep(int dir) => ZoomStep(dir, Math.Floor(ViewW / 2), Math.Floor(ViewH / 2));

    public void PanBy(double dx, double dy)
    {
        OffX = Math.Round(OffX + dx);
        OffY = Math.Round(OffY + dy);
        Clamp();
    }

    /// <summary>Keep at least <see cref="KeepVisible"/> pixels (or the whole texture when smaller) inside the view.</summary>
    void Clamp()
    {
        double w = TexW * Scale, h = TexH * Scale;
        double kx = Math.Min(KeepVisible, w), ky = Math.Min(KeepVisible, h);
        OffX = Math.Clamp(OffX, kx - w, Math.Max(kx - w, ViewW - kx));
        OffY = Math.Clamp(OffY, ky - h, Math.Max(ky - h, ViewH - ky));
    }

    /// <summary>Texels at least partly on screen: x0 ≤ x &lt; x1, y0 ≤ y &lt; y1 (clipped to the texture).</summary>
    public (int x0, int y0, int x1, int y1) VisibleTexels()
    {
        var (ax, ay) = TexelAt(0, 0);
        var (bx, by) = ScreenToTexelF(ViewW, ViewH);
        return (Math.Max(0, ax), Math.Max(0, ay), Math.Min(TexW, (int)Math.Ceiling(bx)), Math.Min(TexH, (int)Math.Ceiling(by)));
    }

    /// <summary>Texels on the straight line a → b, both ends included (Bresenham): a fast drag paints without gaps.</summary>
    public static IEnumerable<(int x, int y)> Line((int x, int y) a, (int x, int y) b)
    {
        int x = a.x, y = a.y;
        int dx = Math.Abs(b.x - x), dy = -Math.Abs(b.y - y);
        int sx = x < b.x ? 1 : -1, sy = y < b.y ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            yield return (x, y);
            if (x == b.x && y == b.y) yield break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }
}
