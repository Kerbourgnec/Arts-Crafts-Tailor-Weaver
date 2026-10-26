using System;
using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// lane/mannequin-paint (docs/embroidery.md "In-world painting"): garment targets for the in-world raycast
/// (<see cref="MannequinPaintClient"/>), the brush highlight on the preview texture, and the server side of draping
/// (packet 1207). Stitches reuse packet 1204 (same paid-colour rules); placements reuse <see cref="TailoringSession.Wrap"/>.
/// </summary>
public partial class BEBehaviorMannequinTailoring
{
    /// <summary>Client: mannequins with this behaviour that are loaded (raycast candidates).</summary>
    public static readonly HashSet<BEBehaviorMannequinTailoring> ClientLoaded = new();

    /// <summary>One garment on this mannequin that can be painted: the session's (target -1) or a worn tailored one (slot).</summary>
    /// <remarks>Pending = the shared unapplied stitches (lane/sewing-polish); in-world strokes are painted over Pending ?? Applied.</remarks>
    public sealed record PaintTarget(int Target, GarmentDef Def, GarmentSurface Surface, BodyWrapMap Map, System.Func<int, bool> Shown,
        WrapLayout? Wrap, System.Func<int, SlotMaterial?> MaterialOf, PieceAssignment[] Pieces, Embroidery? Applied, Embroidery? Pending = null);

    /// <summary>Mannequin mesh rotation (same as OnTesselation).</summary>
    public double MeshAngleRad => (Mannequin?.MeshAngleRad ?? 0) + (Block.GetBehavior<BlockBehaviorRotateablePlaceable>()?.OffsetRad ?? 0);

    public V3 MeshOffset => shapeOffset == null ? default : new V3(shapeOffset.X, shapeOffset.Y, shapeOffset.Z);

    /// <summary>Garments shown on this mannequin with their surfaces (client; empty when a body map failed).</summary>
    public IEnumerable<PaintTarget> PaintTargets()
    {
        if (Api == null) yield break;
        if (Garment is { } g && BodyWrapCache.Surface(Api, g) is { } s && BodyWrapCache.Map(Api, g) is { } m)
            yield return new PaintTarget(GarmentEmbroidery.TargetSession, g, s, m, s.ShownFaces(g.HiddenElements(PartShown)), // lane/drape-cut-sew: on / ghost parts too
                Session.Wrap, MaterialInSlot, (localPieces ?? Session.Pieces).ToArray(), Session.Embroidery, PendingOf(GarmentEmbroidery.TargetSession));
        foreach (int slot in WornTailoredSlots().ToList())
        {
            if (WornGarment(slot) is not { } w || BodyWrapCache.Surface(Api, w.def) is not { } ws || BodyWrapCache.Map(Api, w.def) is not { } wm) continue;
            yield return new PaintTarget(slot, w.def, ws, wm, ws.ShownFaces(w.data.HiddenElements(w.def)), w.data.Wrap, w.data.PartMaterial,
                w.data.Pieces, w.data.Embroidery, PendingOf(slot));
        }
    }

    int hoverTarget = NoEmbroideryTarget;
    int[] hoverTexels = Array.Empty<int>();
    int hoverArgb;

    /// <summary>
    /// Client: highlight <paramref name="texels"/> of <paramref name="target"/> (null / empty = no highlight), blended
    /// half way to <paramref name="argb"/>. A worn target is drawn by the preview meanwhile (its own mesh hidden).
    /// </summary>
    public void SetPaintHover(int target, int[]? texels, int argb)
    {
        texels ??= Array.Empty<int>();
        int newTarget = texels.Length == 0 ? NoEmbroideryTarget : target;
        if (newTarget == hoverTarget && argb == hoverArgb && texels.SequenceEqual(hoverTexels)) return;
        // A hovered worn target is drawn by the preview (hidden worn mesh) with its shown stitches (WornPreviewSlots).
        hoverTarget = newTarget;
        hoverTexels = texels;
        hoverArgb = argb;
        RegenPreviewClient();
    }

    void TintHover(int target, int[] px)
    {
        if (target != hoverTarget || hoverTexels.Length == 0) return;
        foreach (int i in hoverTexels)
        {
            if (i < 0 || i >= px.Length) continue;
            int a = px[i], b = hoverArgb;
            int r = (((a >> 16) & 0xff) + ((b >> 16) & 0xff)) / 2, gg = (((a >> 8) & 0xff) + ((b >> 8) & 0xff)) / 2, bl = ((a & 0xff) + (b & 0xff)) / 2;
            px[i] = unchecked((int)0xff000000) | (r << 16) | (gg << 8) | bl;
        }
    }

    /// <summary>Client → server: drape (packet 1207).</summary>
    public void SendDrape(int part, int seg, SegmentPlacement p, bool put)
    {
        var data = new byte[24];
        BitConverter.GetBytes(part).CopyTo(data, 0);
        BitConverter.GetBytes(seg).CopyTo(data, 4);
        BitConverter.GetBytes(p.Du).CopyTo(data, 8);
        BitConverter.GetBytes(p.Dv).CopyTo(data, 12);
        BitConverter.GetBytes(p.Rot).CopyTo(data, 16);
        BitConverter.GetBytes(put ? 1 : 0).CopyTo(data, 20);
        Send(TailorPacket.Drape, data);
    }

    /// <summary>
    /// Client prediction of a drape / placement: the local session copy changes at once (the server sync confirms).
    /// </summary>
    public void PredictSegment(int seg, SegmentPlacement p)
    {
        if (Session.SetSegment(seg, p)) RegenPreviewClient();
    }

    /// <summary>
    /// Server: put the held material into part <paramref name="part"/>'s empty slot (when asked and accepted), then place
    /// the cloth on segment <c>seg</c>. Needs a garment in progress.
    /// </summary>
    void OnDrape(IPlayer player, byte[]? data)
    {
        var g = Garment;
        if (g == null || data == null || data.Length < 24) return;
        int part = BitConverter.ToInt32(data, 0), seg = BitConverter.ToInt32(data, 4);
        var p = new SegmentPlacement(BitConverter.ToInt32(data, 8), BitConverter.ToInt32(data, 12), BitConverter.ToInt32(data, 16));
        bool put = BitConverter.ToInt32(data, 20) == 1;
        if (part < 0 || part >= g.Parts.Length || g.Parts[part].IsFixed || seg < 0 || seg >= BodySegments.Count) return;
        bool changed = false;
        if (put && inventory[part].Empty)
        {
            var hslot = player.InventoryManager.ActiveHotbarSlot;
            if (hslot != null && !hslot.Empty && inventory[part].CanHold(hslot) && hslot.TryPutInto(Api.World, inventory[part], 1) > 0)
            {
                inventory[part].MarkDirty();
                hslot.MarkDirty();
                Session.SetCut(part, false); // lane/drape-cut-sew: a fresh drape is not cut yet
                changed = true;
            }
            else
            {
                Fail(player, "tailor-err-material");
            }
        }
        // lane/drape-cut-sew: cut cloth keeps its placement (sneak + shears uncuts); a put still goes in.
        string? locked = PlacementRefusal(part, seg);
        if (locked == null) changed |= Session.SetSegment(seg, p);
        else if (!changed) { Fail(player, locked); return; }
        if (!changed) return;
        Blockentity.MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/cloth"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.5f);
    }
}
