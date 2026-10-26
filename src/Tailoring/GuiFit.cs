using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Client text measuring for the sewing windows (lane/sewing-polish): label widths in GUI units (unscaled, so the same
/// layout at every GUI scale), buttons sized to their labels (<see cref="ButtonFlow"/>), fonts shrunk to fit fixed
/// buttons. <c>CairoFont.GetTextExtents</c> measures at the scaled font size, hence the division by the GUI scale.
/// </summary>
public static class GuiFit
{
    /// <summary>Space left and right of a label inside its button.</summary>
    public const double Pad = 8;
    public const double MinFontSize = 9;

    public static double Scale => RuntimeEnv.GUIScale > 0 ? RuntimeEnv.GUIScale : 1;

    public static double TextWidth(CairoFont font, string text) => font.GetTextExtents(text).Width / Scale;

    /// <summary>Button bounds for <paramref name="labels"/> flowed from (<paramref name="x0"/>, <paramref name="y0"/>) within <paramref name="maxW"/>.</summary>
    public static (ElementBounds[] bounds, int rows) Flow(IEnumerable<string> labels, double x0, double y0, double maxW, double minW,
        double rowPitch, double height = 24, double gap = 4)
    {
        var font = CairoFont.SmallButtonText();
        var widths = labels.Select(l => ButtonFlow.Width(TextWidth(font, l), Pad, minW, maxW)).ToArray();
        var cells = ButtonFlow.Layout(widths, maxW, gap);
        return (cells.Select(c => ElementBounds.Fixed(x0 + c.X, y0 + c.Row * rowPitch, c.W, height)).ToArray(), ButtonFlow.Rows(cells));
    }

    /// <summary>Small button font for <paramref name="text"/> in a button <paramref name="width"/> wide (smaller when it would overflow).</summary>
    public static CairoFont Font(string text, double width)
    {
        var font = CairoFont.SmallButtonText();
        font.UnscaledFontsize = ButtonFlow.FitFontSize(font.UnscaledFontsize, TextWidth(font, text), width, Pad, MinFontSize);
        return font;
    }

    /// <summary>A plain button whose label always fits (font shrunk when needed); replaces <c>AddSmallButton</c> in our dialogs.</summary>
    public static GuiComposer AddFitButton(this GuiComposer c, string text, ActionConsumable onClick, ElementBounds bounds, string? key = null)
        => c.AddButton(text, onClick, bounds, Font(text, bounds.fixedWidth), EnumButtonStyle.Normal, key);

    /// <summary>A toggle button whose label always fits (font shrunk when needed).</summary>
    public static GuiComposer AddFitToggle(this GuiComposer c, string text, System.Action<bool> onToggle, ElementBounds bounds, string? key = null)
        => c.AddToggleButton(text, Font(text, bounds.fixedWidth), onToggle, bounds, key);
}
