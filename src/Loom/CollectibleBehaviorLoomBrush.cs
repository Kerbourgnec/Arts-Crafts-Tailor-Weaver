using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;

namespace ArtsAndCrafts.Tailor.Loom;

/// <summary>
/// Brush size for painting loom cells (F key, tool modes like clayforming): 1, 2, 4 or 8 cells of the current row
/// per click. On our twines (itemtypes/resource/twine.json) and on game:flaxtwine (patches/flaxtwine-loombrush.json).
/// One size per player (lane/loom-feedback-4): the player's watched attribute <see cref="PlayerAttr"/>, shared by every
/// twine and the empty hand. Twine: vanilla tool mode dialog (its packet runs <see cref="SetToolMode"/> on the server).
/// Empty hand at a loom: <see cref="LoomClientSystem"/> opens the draft window (<see cref="GuiDialogDraftWindow"/>, its brush row), which sends a loom packet.
/// </summary>
public class CollectibleBehaviorLoomBrush : CollectibleBehavior
{
    public static readonly int[] Sizes = { 1, 2, 4, 8 };
    public const string PlayerAttr = TailorModSystem.Domain + "LoomBrush";
    const string ModesCacheKey = TailorModSystem.Domain + "-loombrush-modes";

    public CollectibleBehaviorLoomBrush(CollectibleObject collObj) : base(collObj) { }

    public override SkillItem[] GetToolModes(ItemSlot slot, IClientPlayer forPlayer, BlockSelection blockSel)
        => forPlayer?.Entity?.Api is ICoreClientAPI capi ? Modes(capi) : null!;

    /// <summary>The 1/2/4/8 tool mode icons (cached per client; also used by the empty-hand dialog).</summary>
    public static SkillItem[] Modes(ICoreClientAPI capi)
        => ObjectCacheUtil.GetOrCreate(capi, ModesCacheKey, () =>
        {
            var modes = new SkillItem[Sizes.Length];
            for (int i = 0; i < Sizes.Length; i++)
            {
                modes[i] = new SkillItem
                {
                    Code = new AssetLocation(TailorModSystem.Domain, "loombrush-" + Sizes[i]),
                    Name = Lang.Get(TailorModSystem.Domain + ":loom-brush-" + Sizes[i]),
                }.WithLetterIcon(capi, Sizes[i].ToString());
            }
            return modes;
        });

    public override int GetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSelection) => ModeOf(byPlayer);

    public override void SetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSelection, int toolMode) => SetMode(byPlayer, toolMode);

    /// <summary>The player's brush mode index (0..3).</summary>
    public static int ModeOf(IPlayer? player) => Math.Clamp(player?.Entity?.WatchedAttributes.GetInt(PlayerAttr) ?? 0, 0, Sizes.Length - 1);

    /// <summary>Set the player's brush mode (on the server it syncs to the client through the watched attribute).</summary>
    public static void SetMode(IPlayer? player, int mode)
        => player?.Entity?.WatchedAttributes.SetInt(PlayerAttr, Math.Clamp(mode, 0, Sizes.Length - 1));

    public override void OnUnloaded(ICoreAPI api)
    {
        if (api is ICoreClientAPI capi && capi.ObjectCache.TryGetValue(ModesCacheKey, out object? cached) && cached is SkillItem[] modes)
        {
            foreach (var m in modes) m?.Dispose();
            capi.ObjectCache.Remove(ModesCacheKey);
        }
        base.OnUnloaded(api);
    }

    /// <summary>Cells per click for this player (twine or empty hand: one size per player).</summary>
    public static int BrushSize(IPlayer? player) => Sizes[ModeOf(player)];
}
