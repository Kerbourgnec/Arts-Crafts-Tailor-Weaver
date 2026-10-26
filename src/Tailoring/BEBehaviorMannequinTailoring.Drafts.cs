using System;
using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>Packet 1213 operations on the pending stitches of a target.</summary>
public enum EmbEditOp { Undo = 0, Redo = 1, Discard = 2 }

/// <summary>
/// lane/sewing-polish (docs/embroidery.md "Shared pending stitches"): ONE pending embroidery state per target, on the
/// server in the session (<see cref="TailoringSession.Drafts"/>, saved and synced), shared by the window and in-world
/// stitching. Strokes (packet 1212) only change the pending layer (no charge); undo / redo / discard (1213) act on it
/// whoever made the stroke; Apply (1204) charges the new colours and makes it the applied layer. Closing the window or
/// leaving the Embroider tab changes nothing.
/// </summary>
public partial class BEBehaviorMannequinTailoring
{
    /// <summary>What the draft of <paramref name="target"/> must have been painted on (null = no such target now).</summary>
    public string? DraftKey(int target)
    {
        if (target == GarmentEmbroidery.TargetSession) return Garment is { } g ? EmbroideryDrafts.SessionKey(g.Code) : null;
        return WornGarment(target) is { } w ? EmbroideryDrafts.WornKey(w.def, w.data) : null;
    }

    /// <summary>Pending stitches of <paramref name="target"/> (applied + unapplied changes), null = none.</summary>
    public Embroidery? PendingOf(int target) => DraftKey(target) is { } key ? Session.Drafts.Get(target, key) : null;

    /// <summary>Targets with unapplied changes (worn slots and / or the garment in progress).</summary>
    public IEnumerable<int> PendingTargets()
        => Session.Drafts.Targets.Where(t => DraftKey(t) is { } key && Session.Drafts.HasChanges(t, key, AppliedOf(t))).ToList();

    Embroidery? AppliedOf(int target)
        => target == GarmentEmbroidery.TargetSession ? Session.Embroidery : WornGarment(target)?.data.Embroidery;

    /// <summary>Unapplied texels of <paramref name="target"/> (0 = nothing pending).</summary>
    public int PendingChanges(int target) => PendingOf(target) is { } p ? GarmentEmbroidery.Changed(AppliedOf(target), p) : 0;

    #region Client

    /// <summary>
    /// A stroke (window or in world) made <paramref name="working"/> the pending stitches of <paramref name="target"/>;
    /// <paramref name="before"/> = the pending layer it was painted over (the diff base). <paramref name="newStroke"/> =
    /// one undo step starts here (an in-world drag sends its later parts with false).
    /// </summary>
    public void SendEmbStroke(int target, Embroidery? before, Embroidery working, bool newStroke)
    {
        byte[] diff = GarmentEmbroidery.EncodeDiff(target, before, working);
        var data = new byte[diff.Length + 1];
        data[0] = (byte)(newStroke ? 1 : 0);
        diff.CopyTo(data, 1);
        Send(TailorPacket.EmbStroke, data);
    }

    public void SendEmbEdit(int target, EmbEditOp op)
    {
        var data = new byte[8];
        BitConverter.GetBytes(target).CopyTo(data, 0);
        BitConverter.GetBytes((int)op).CopyTo(data, 4);
        Send(TailorPacket.EmbEdit, data);
    }

    public void SendEmbApply(int target) => Send(TailorPacket.EmbApply, BitConverter.GetBytes(target));

    #endregion

    #region Server

    /// <summary>Stroke: the diff goes onto the pending layer (only texels that take stitches; twine colours from the items). Free.</summary>
    void OnEmbStroke(IPlayer player, byte[]? data)
    {
        if (data == null || data.Length < 2) return;
        bool newStroke = data[0] == 1;
        var diff = GarmentEmbroidery.DecodeDiff(data.AsSpan(1).ToArray());
        if (diff == null) return;
        if (EmbroideryTargetOf(diff.Target) is not { } target || target.Key == null)
        {
            Fail(player, "tailor-err-emb-notarget");
            return;
        }
        var g = target.Garment;
        var mask = GarmentEmbroidery.Mask(g, target.MaterialOf);
        string? HexOf(string code)
        {
            var item = Api.World.GetItem(new AssetLocation(code));
            return Loom.LoomTwine.IsTwine(item) ? TwineColors.HexOf(new ItemStack(item)) : null;
        }
        var baseLayer = target.Pending ?? target.Applied;
        var result = GarmentEmbroidery.ApplyDiff(g, baseLayer, diff, mask, HexOf);
        if (result.Error != null) { Fail(player, result.Error); return; }
        var next = result.Layer ?? GarmentEmbroidery.New(g);
        Session.Drafts.Prune(DraftKey);
        if (!Session.Drafts.Stroke(diff.Target, target.Key, target.Applied, next, newStroke)) return;
        Blockentity.MarkDirty(true);
        if (newStroke) Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/cloth"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.4f);
    }

    /// <summary>Undo / redo / discard on the pending stitches of a target.</summary>
    void OnEmbEdit(IPlayer player, byte[]? data)
    {
        if (data == null || data.Length < 8) return;
        int target = BitConverter.ToInt32(data, 0);
        var op = (EmbEditOp)BitConverter.ToInt32(data, 4);
        if (DraftKey(target) is not { } key) { Fail(player, "tailor-err-emb-notarget"); return; }
        bool changed = op switch
        {
            EmbEditOp.Undo => Session.Drafts.Undo(target, key),
            EmbEditOp.Redo => Session.Drafts.Redo(target, key),
            EmbEditOp.Discard => Session.Drafts.Clear(target),
            _ => false,
        };
        if (changed) Blockentity.MarkDirty(true);
    }

    /// <summary>
    /// Apply: the pending layer becomes the applied one; 1 twine per twine code in use that is not paid yet (hotbar +
    /// backpack, all or nothing); the draft and its history go. Stored on the session or the worn garment stack.
    /// </summary>
    void OnEmbApply(IPlayer player, byte[]? data)
    {
        if (player is not IServerPlayer sp || data == null || data.Length < 4) return;
        int targetId = BitConverter.ToInt32(data, 0);
        if (EmbroideryTargetOf(targetId) is not { } target || target.Key == null)
        {
            Fail(player, "tailor-err-emb-notarget");
            return;
        }
        var pending = target.Pending;
        if (pending == null) return;
        if (GarmentEmbroidery.Same(target.Applied, pending) && pending.Paid.SetEquals(target.Applied?.Paid ?? new HashSet<string>()))
        {
            Session.Drafts.Clear(targetId);
            Blockentity.MarkDirty(true);
            return;
        }
        var layer = pending.Clone();
        layer.Compact();
        var cost = layer.Cost(); // codes not yet paid on this garment
        if (cost.Count > 0 && !Tapestry.LoomCore.TryCharge(sp, cost, null)) return;
        layer.MarkPaid();
        var stored = layer.IsBlank ? null : layer;
        if (targetId == GarmentEmbroidery.TargetSession)
        {
            Session.Embroidery = stored;
            Session.Charged.UnionWith(cost.Keys); // refunded on cancel / another garment
        }
        else if (WornGarment(targetId) is { } worn)
        {
            worn.data.Embroidery = stored;
            worn.data.WriteTo(worn.slot.Itemstack!, worn.def);
            worn.slot.MarkDirty();
        }
        Session.Drafts.Clear(targetId);
        Blockentity.MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/cloth"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 1f);
    }

    #endregion
}
