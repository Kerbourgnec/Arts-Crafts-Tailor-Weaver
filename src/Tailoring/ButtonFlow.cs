using System;
using System.Collections.Generic;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Button rows sized to their text (lane/sewing-polish, owner: two-word labels such as "Lower body" overflowed the fixed
/// button widths). Pure: the client measures each label (<see cref="GuiFit"/>), this lays the buttons out left to right
/// and starts a new row when the next one would pass <c>maxW</c>. Offline-tested (selftest "button flow").
/// </summary>
public static class ButtonFlow
{
    /// <summary>One button: left edge, row index, width.</summary>
    public readonly record struct Cell(double X, int Row, double W);

    /// <summary>Button width for a label of <paramref name="textW"/> GUI units: text + padding, at least <paramref name="minW"/>, at most <paramref name="maxW"/>.</summary>
    public static double Width(double textW, double pad, double minW, double maxW)
        => Math.Min(maxW, Math.Max(minW, Math.Ceiling(textW + 2 * pad)));

    /// <summary>Flow <paramref name="widths"/> into rows of at most <paramref name="maxW"/>, <paramref name="gap"/> apart.</summary>
    public static Cell[] Layout(IReadOnlyList<double> widths, double maxW, double gap)
    {
        var cells = new Cell[widths.Count];
        double x = 0;
        int row = 0;
        for (int i = 0; i < widths.Count; i++)
        {
            double w = Math.Min(widths[i], maxW);
            if (x > 0 && x + w > maxW)
            {
                row++;
                x = 0;
            }
            cells[i] = new Cell(x, row, w);
            x += w + gap;
        }
        return cells;
    }

    /// <summary>Rows used by <paramref name="cells"/> (0 when empty).</summary>
    public static int Rows(Cell[] cells) => cells.Length == 0 ? 0 : cells[^1].Row + 1;

    /// <summary>
    /// Font size that fits <paramref name="textW"/> (measured at <paramref name="size"/>) into a button of width
    /// <paramref name="buttonW"/>: unchanged when it fits, else scaled down, not below <paramref name="minSize"/>.
    /// </summary>
    public static double FitFontSize(double size, double textW, double buttonW, double pad, double minSize)
    {
        double room = buttonW - 2 * pad;
        if (textW <= room || textW <= 0) return size;
        return Math.Max(minSize, Math.Floor(size * room / textW * 10) / 10);
    }
}
