using System;
using ArtsAndCrafts.Tailor.Weaving;
using Cairo;
using Vintagestory.API.Client;

namespace ArtsAndCrafts.Tailor.Loom;

/// <summary>
/// A draft preset as a small picture button (lane/draft-polish): the preset's grid drawn by <see cref="PresetIcons.Render"/>
/// (gold frame when the window's draft equals it, blue on hover, dimmed when disabled). The name is a hover tooltip, added
/// by the window next to it (<c>AddHoverText</c> over the same bounds).
/// </summary>
public class GuiElementPresetIcon : GuiElement
{
    readonly bool[,] mask;
    readonly Action onClick;
    readonly Func<bool> selected, enabled;
    LoadedTexture texture;
    PresetIconState drawn = (PresetIconState)(-1);
    int drawnSize;

    public GuiElementPresetIcon(ICoreClientAPI capi, ElementBounds bounds, bool[,] mask, Action onClick, Func<bool> selected, Func<bool> enabled) : base(capi, bounds)
    {
        this.mask = mask;
        this.onClick = onClick;
        this.selected = selected;
        this.enabled = enabled;
        texture = new LoadedTexture(capi);
        MouseOverCursor = "linkselect";
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();
    }

    PresetIconState State()
    {
        if (!enabled()) return PresetIconState.Disabled;
        if (selected()) return PresetIconState.Selected;
        return IsPositionInside(api.Input.MouseX, api.Input.MouseY) ? PresetIconState.Hover : PresetIconState.Normal;
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        int size = Math.Max(8, (int)Math.Min(Bounds.OuterWidth, Bounds.OuterHeight));
        var state = State();
        if (state != drawn || size != drawnSize || texture.TextureId == 0)
        {
            int[] px = PresetIcons.Render(mask, size, state);
            if (texture.Width != size || texture.Height != size)
            {
                texture.Dispose();
                texture = new LoadedTexture(api, 0, size, size);
            }
            api.Render.LoadOrUpdateTextureFromRgba(px, false, 0, ref texture);
            drawn = state;
            drawnSize = size;
        }
        api.Render.Render2DTexture(texture.TextureId, (float)Bounds.renderX, (float)Bounds.renderY, size, size);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI capi, MouseEvent args)
    {
        args.Handled = true;
        if (!enabled()) return;
        capi.Gui.PlaySound("menubutton_press");
        onClick();
    }

    public override void Dispose()
    {
        base.Dispose();
        texture.Dispose();
    }
}

public static class GuiElementPresetIconHelpers
{
    /// <summary>A preset icon button with a hover tooltip (<paramref name="tooltip"/>, wrapped at 260 px).</summary>
    public static GuiComposer AddPresetIcon(this GuiComposer composer, ElementBounds bounds, bool[,] mask, Action onClick, Func<bool> selected, Func<bool> enabled,
        string tooltip, string? key = null)
    {
        if (composer.Composed) return composer;
        composer.AddInteractiveElement(new GuiElementPresetIcon(composer.Api, bounds, mask, onClick, selected, enabled), key);
        composer.AddHoverText(tooltip, CairoFont.WhiteSmallText(), 260, ElementBounds.Fixed(bounds.fixedX, bounds.fixedY, bounds.fixedWidth, bounds.fixedHeight));
        return composer;
    }
}
