using System;

namespace ArtsAndCrafts.Tailor.Loom;

/// <summary>
/// Pure geometry of the draft grid (lane/draft-zoom), no GUI types. Lengths are screen px at <see cref="Scale"/> (the GUI
/// scale); zoom and pan are kept unscaled so they survive a rebuild at another GUI scale.
/// <para>
/// Layout: the drawdown is a square viewport of <see cref="ViewSize"/> (the whole cloth at zoom 1 = fit). Cells there are
/// <see cref="Cell"/> = base cell x zoom. Warp strip and threading sit above the viewport and scroll with the columns
/// (horizontal pan <see cref="Ox"/>); treadling and weft sit right of it and scroll with the rows (vertical pan
/// <see cref="Oy"/>). The tie-up never scrolls. Fixed-axis cells are <see cref="F"/> px (strip height, treadle column width).
/// </para>
/// </summary>
public sealed class DraftView
{
    public const int Shafts = 4, Treadles = 4;
    public const double ZoomFactor = 1.25, MaxCellPx = 32;
    public const double Gap = 6;

    /// <summary>Unscaled cell size at zoom 1 (the whole cloth fits) for a cloth of <paramref name="n"/>.</summary>
    public static double CellFor(int n) => n <= 16 ? 12 : n <= 32 ? 8 : 5;

    /// <summary>Unscaled grid size for a cloth of <paramref name="n"/> (zoom does not change it).</summary>
    public static (double w, double h) SizeFor(int n) => SizeFor(n, CellFor(n), Gap);

    public int N { get; }
    public double Scale { get; }
    public double BaseCell { get; }
    public double FixedCell { get; }
    public double GapU { get; }
    public double Zoom { get; private set; } = 1;
    public double PanXU { get; private set; }
    public double PanYU { get; private set; }

    public DraftView(int n, double baseCellU, double fixedCellU, double gapU, double scale)
    {
        N = n; BaseCell = baseCellU; FixedCell = fixedCellU; GapU = gapU; Scale = scale;
    }

    /// <summary>Fixed-axis cell size (unscaled) for a cloth of <paramref name="n"/>: the cell, at least 8 so the tie-up stays usable at 64.</summary>
    public static double FixedFor(double baseCell) => Math.Max(baseCell, 8);

    /// <summary>Unscaled size of the whole grid (viewport at zoom 1 + fixed strips).</summary>
    public static (double w, double h) SizeFor(int n, double baseCell, double gap)
    {
        double f = FixedFor(baseCell), v = n * baseCell;
        return (v + gap + Treadles * f + gap + f, f + gap + Shafts * f + gap + v);
    }

    public double MaxZoom => Math.Max(1, MaxCellPx / BaseCell);
    public double Cell => BaseCell * Zoom * Scale;
    public double F => FixedCell * Scale;
    public double G => GapU * Scale;
    public double ViewSize => N * BaseCell * Scale;
    public double Content => N * Cell;
    public double MaxPan => Math.Max(0, Content - ViewSize);
    public double Ox => PanXU * Scale;
    public double Oy => PanYU * Scale;
    public bool Zoomed => Zoom > 1.0000001;

    public double ThrY => F + G;
    public double DdY => ThrY + Shafts * F + G;
    public double TieX => ViewSize + G;
    public double WeftX => TieX + Treadles * F + G;
    public double TotalW => WeftX + F;
    public double TotalH => DdY + ViewSize;

    /// <summary>Left edge of column <paramref name="i"/> (warp, threading, drawdown) on screen.</summary>
    public double ColX(int i) => i * Cell - Ox;
    /// <summary>Top edge of drawdown / treadling / weft row <paramref name="j"/> on screen.</summary>
    public double RowY(int j) => DdY + j * Cell - Oy;

    /// <summary>Column under x (element-relative), or -1 outside the viewport's columns.</summary>
    public int ColAt(double x) => x < 0 || x >= ViewSize ? -1 : Math.Clamp((int)Math.Floor((x + Ox) / Cell), 0, N - 1);
    /// <summary>Row under y (element-relative), or -1 outside the viewport's rows.</summary>
    public int RowAt(double y) => y < DdY || y >= DdY + ViewSize ? -1 : Math.Clamp((int)Math.Floor((y - DdY + Oy) / Cell), 0, N - 1);

    public bool ColVisible(int i) => ColX(i) + Cell > 0 && ColX(i) < ViewSize;
    public bool RowVisible(int j) => RowY(j) + Cell > DdY && RowY(j) < DdY + ViewSize;

    /// <summary>Set zoom / pan (unscaled), clamped to the allowed range.</summary>
    public void Restore(double zoom, double panXU, double panYU)
    {
        Zoom = Math.Clamp(zoom, 1, MaxZoom);
        PanXU = panXU; PanYU = panYU;
        ClampPan();
    }

    public void ClampPan()
    {
        double max = MaxPan / Scale;
        PanXU = Math.Clamp(PanXU, 0, max);
        PanYU = Math.Clamp(PanYU, 0, max);
    }

    /// <summary>Pan by screen px (positive = content moves toward larger columns / rows, i.e. the view scrolls right / down).</summary>
    public void PanBy(double dx, double dy)
    {
        PanXU += dx / Scale; PanYU += dy / Scale;
        ClampPan();
    }

    /// <summary>Zoom by <paramref name="factor"/> keeping the cell under element-relative (px, py) fixed; a point outside the viewport uses its nearest edge.</summary>
    public void ZoomAt(double factor, double px, double py)
    {
        double ax = Math.Clamp(px, 0, ViewSize), ay = Math.Clamp(py - DdY, 0, ViewSize);
        double cx = (ax + Ox) / Cell, cy = (ay + Oy) / Cell; // content position in cells
        Zoom = Math.Clamp(Zoom * factor, 1, MaxZoom);
        PanXU = (cx * Cell - ax) / Scale;
        PanYU = (cy * Cell - ay) / Scale;
        ClampPan();
    }

    /// <summary>Zoom about the viewport centre (buttons).</summary>
    public void ZoomCentre(double factor) => ZoomAt(factor, ViewSize / 2, DdY + ViewSize / 2);

    /// <summary>Whole cloth in view (zoom 1, no pan).</summary>
    public void Fit() { Zoom = 1; PanXU = PanYU = 0; }

    /// <summary>Scrollbar thumb along one axis: (start, length) inside a track of <see cref="ViewSize"/>, or null when everything fits.</summary>
    public (double start, double len)? Thumb(bool horizontal)
    {
        if (!Zoomed || MaxPan <= 0) return null;
        double len = Math.Max(12 * Scale, ViewSize * ViewSize / Content);
        double pan = horizontal ? Ox : Oy;
        return (pan / MaxPan * (ViewSize - len), len);
    }

    /// <summary>Pan (screen px) that puts the thumb's start at <paramref name="start"/> along its track.</summary>
    public double PanForThumb(double start, double len) => ViewSize - len <= 0 ? 0 : Math.Clamp(start / (ViewSize - len), 0, 1) * MaxPan;

    public void SetPan(double oxPx, double oyPx) { PanXU = oxPx / Scale; PanYU = oyPx / Scale; ClampPan(); }
}
