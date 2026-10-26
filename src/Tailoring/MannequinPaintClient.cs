using System;
using System.Linq;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using EnumMouseButton = Vintagestory.API.Common.EnumMouseButton;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// lane/mannequin-paint (docs/embroidery.md "In-world painting"): paint and drape directly on a mannequin's garments.
/// Every 50 ms the crosshair ray is cast against the garments of nearby mannequins (<see cref="GarmentSurface"/> in
/// mesh space, mannequin position + rotation + shapeOffset); the texel under it and the brush footprint are tinted on the
/// mannequin's preview texture. Right click (caught in <c>MouseDown</c> before the world interaction, so vanilla
/// sneak-undressing never sees it):
/// twine → stitch the footprint (F = brush size, the loom brush; sneak = pick out), sent as a packet-1204 diff (server
/// charges paid colours as in the dialog); holding a sewing material on a garment in progress → put it into the part
/// under the crosshair (when its slot is empty) and place the cloth so its cell (0, 0) lands there; slot already filled:
/// a click near a face edge nudges one cell towards it, in the middle re-anchors; sneak → quarter turn about that cell.
/// Holding right while moving keeps stitching (drag).
/// lane/drape-cut-sew (docs/tailoring.md "In-world tailoring flow"): shears / knife on a draped part cut it (sneak:
/// uncut); sneak + empty hand on an optional part switches it on / off (or takes its material back); off the garment:
/// empty hand / material with nothing started opens the garment picker for the body region under the crosshair, the
/// sewing kit sews, sneak + shears cancels. Empty optional parts the held item fits show as ghost (and can be hit).
/// </summary>
public class MannequinPaintClient : ModSystem
{
    public const double Range = 6;
    ICoreClientAPI? capi;
    Hover? hover;
    bool rightHeld;
    int lastStrokeTexel = -1;
    /// <summary>The current right-button hold already sent a stroke part (later parts join its undo step).</summary>
    bool strokeSent;
    BEBehaviorMannequinTailoring? ghostOn;
    GuiDialogGarmentPicker? picker;

    sealed record Hover(BEBehaviorMannequinTailoring Be, BEBehaviorMannequinTailoring.PaintTarget T, SurfaceHit Hit, int Texel, FlowTool Tool,
        SlotMaterial? Held, string? Code, string? Hex)
    {
        public bool Twine => Tool == FlowTool.Twine;
    }

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        api.Event.RegisterGameTickListener(_ => OnTick(), 50);
        api.Event.MouseDown += OnMouseDown;
        api.Event.MouseUp += e => { if (e.Button == EnumMouseButton.Right) { rightHeld = false; lastStrokeTexel = -1; strokeSent = false; } };
        api.Event.LevelFinalize += WrapToolModeHotkey;
    }

    public override void Dispose()
    {
        BEBehaviorMannequinTailoring.ClientLoaded.Clear(); // static: never keep a left world's block entities
        picker?.TryClose();
        picker?.Dispose();
        base.Dispose();
    }

    bool Sneaking => capi!.World.Player.Entity.Controls.ShiftKey;

    void OnTick()
    {
        UpdateGhost();
        var old = hover;
        hover = capi!.Input.MouseGrabbed ? FindHover() : null;
        if (old != null && (hover == null || old.Be != hover.Be || old.T.Target != hover.T.Target)) old.Be.SetPaintHover(old.T.Target, null, 0);
        if (hover != null)
        {
            hover.Be.SetPaintHover(hover.T.Target, Footprint(hover), HoverArgb(hover));
            if (rightHeld && hover.Twine && hover.Texel != lastStrokeTexel) Stitch(hover);
        }
    }

    /// <summary>
    /// lane/drape-cut-sew: on the mannequin under the crosshair, empty optional parts the held material fits (or, sneaking
    /// with an empty hand, every part that is off) show as ghost and take raycasts; other mannequins drop their ghost.
    /// </summary>
    void UpdateGhost()
    {
        var be = capi!.Input.MouseGrabbed ? LookedAt(capi, out _) : null;
        bool[]? parts = null;
        if (be?.Garment is { } g)
        {
            var slot = capi.World.Player?.InventoryManager?.ActiveHotbarSlot;
            var tool = BEBehaviorMannequinTailoring.ToolOf(capi, slot);
            if (tool == FlowTool.Material) parts = DrapeFlow.GhostParts(g, be.MaterialInSlot, be.Session.IsOn, TailorMaterials.Read(capi, slot!.Itemstack));
            else if (tool == FlowTool.Empty && Sneaking) parts = DrapeFlow.ToggleGhostParts(g, be.MaterialInSlot, be.Session.IsOn);
        }
        if (ghostOn != null && ghostOn != be) ghostOn.SetGhost(null);
        be?.SetGhost(parts);
        ghostOn = be;
    }

    int[] Footprint(Hover h)
    {
        if (h.Tool is FlowTool.Shears or FlowTool.Empty)
        {
            // Shears / toggle: the whole part under the crosshair.
            var be = h.Be;
            var g = h.T.Def;
            int part = h.Tool == FlowTool.Empty ? DrapeFlow.TogglePart(g, h.Texel) : DrapeFlow.VisiblePart(g, h.Texel, be.MaterialInSlot, be.PartShown);
            return part < 0 ? new[] { h.Texel } : DrapeFlow.PartTexels(g, part, be.MaterialInSlot, be.PartShown);
        }
        int brush = h.Twine ? Loom.CollectibleBehaviorLoomBrush.BrushSize(capi!.World.Player) : 1;
        return MannequinPaint.Footprint(h.T.Surface, h.T.Map, h.Hit.TexelX, h.Hit.TexelY, brush);
    }

    int HoverArgb(Hover h)
    {
        if (h.Tool == FlowTool.Shears) return Sneaking ? unchecked((int)0xff60c060) : unchecked((int)0xffe0a040); // uncut green, cut amber
        if (!h.Twine || Sneaking) return unchecked((int)0xffffffff);
        return TwineColors.ToArgb(h.Hex ?? TwineColors.PlainHex);
    }

    Hover? FindHover()
    {
        var player = capi!.World.Player;
        var slot = player?.InventoryManager?.ActiveHotbarSlot;
        if (player?.Entity == null || slot == null) return null;
        var tool = BEBehaviorMannequinTailoring.ToolOf(capi, slot);
        bool sneak = player.Entity.Controls.ShiftKey;
        // Twine: stitch; material: drape; shears: cut / uncut; empty hand + sneak: toggle an optional part (lane/drape-cut-sew).
        if (tool is FlowTool.None or FlowTool.SewingKit || (tool == FlowTool.Empty && !sneak)) return null;
        var stack = slot.Itemstack;
        bool twine = tool == FlowTool.Twine;
        SlotMaterial? held = tool == FlowTool.Material ? TailorMaterials.Read(capi, stack) : null;
        var pos = player.Entity.Pos;
        var eye = pos.XYZ.Add(player.Entity.LocalEyePos);
        var view = pos.GetViewVector();
        var dir = new V3(view.X, view.Y, view.Z).Normalized;
        Hover? best = null;
        foreach (var be in BEBehaviorMannequinTailoring.ClientLoaded.ToList())
        {
            var bp = be.Pos;
            var rel = new V3(eye.X - bp.X, eye.Y - bp.Y, eye.Z - bp.Z);
            if (rel.Length > Range + 2) continue;
            var (o, d) = MannequinPaint.RayToMesh(rel, dir, be.MeshAngleRad, be.MeshOffset);
            foreach (var t in be.PaintTargets())
            {
                if (!twine && t.Target != GarmentEmbroidery.TargetSession) continue; // draping, cutting, toggles: the garment in progress only
                var hit = t.Surface.Raycast(o, d, Range, t.Shown);
                if (hit == null || (best != null && hit.Value.Distance >= best.Hit.Distance)) continue;
                best = new Hover(be, t, hit.Value, hit.Value.TexelY * t.Map.Width + hit.Value.TexelX, tool, held,
                    twine ? stack!.Collectible.Code.ToString() : null, twine ? TwineColors.HexOf(stack) : null);
            }
        }
        if (best == null) return null;
        if (tool == FlowTool.Empty && DrapeFlow.TogglePart(best.T.Def, best.Texel) < 0) return null; // no optional part there: vanilla undressing
        // A block between the eye and the garment wins (its selection is nearer).
        var sel = player.CurrentBlockSelection;
        if (sel != null && sel.Position.X == best.Be.Pos.X && sel.Position.Z == best.Be.Pos.Z && Math.Abs(sel.Position.Y - best.Be.Pos.Y) <= 1) return best;
        if (sel != null)
        {
            var hp = sel.Position.ToVec3d().Add(sel.HitPosition);
            if (hp.DistanceTo(eye) + 1e-3 < best.Hit.Distance) return null;
        }
        return best;
    }

    void OnMouseDown(MouseEvent e)
    {
        if (e.Button != EnumMouseButton.Right || capi == null || !capi.Input.MouseGrabbed) return;
        var h = hover ?? FindHover();
        if (h == null)
        {
            if (OffGarment()) e.Handled = true;
            return;
        }
        e.Handled = true; // no block interaction (no dialog, no vanilla undressing)
        switch (h.Tool)
        {
            case FlowTool.Twine:
                rightHeld = true;
                Stitch(h);
                break;
            case FlowTool.Shears:
                CutOrUncut(h);
                break;
            case FlowTool.Empty:
                Toggle(h);
                break;
            default:
                Drape(h);
                break;
        }
    }

    void Error(string key, params object[] args) => capi!.TriggerIngameError(this, "actailor-paint", Lang.Get(TailorModSystem.Domain + ":" + key, args));

    static string PartName(GarmentDef g, int p) => p >= 0 && p < g.Parts.Length ? Lang.Get(TailorModSystem.Domain + ":part-" + g.Parts[p].Code) : "";

    /// <summary>
    /// Right click on a mannequin, crosshair not on the garment in progress (lane/drape-cut-sew): picker, sew, cancel.
    /// True = handled (no vanilla interaction).
    /// </summary>
    bool OffGarment()
    {
        var be = LookedAt(capi!, out var sel);
        if (be == null || sel == null || be.AllowedGarments.Length == 0) return false;
        var tool = BEBehaviorMannequinTailoring.ToolOf(capi!, capi!.World.Player.InventoryManager.ActiveHotbarSlot);
        switch (DrapeFlow.OffGarmentAction(tool, Sneaking, be.Garment != null, be.SessionActive))
        {
            case "pick":
                double meshY = sel.Position.Y - be.Pos.Y + sel.HitPosition.Y - be.MeshOffset.Y;
                OpenPicker(be, meshY);
                return true;
            case "sew":
                if (be.SewProblem() is { } sp) { Error(sp.key, sp.part >= 0 ? PartName(be.Garment!, sp.part) : ""); return true; }
                be.SendSew();
                return true;
            case "cancel":
                be.SendCancel();
                return true;
        }
        return false;
    }

    void OpenPicker(BEBehaviorMannequinTailoring be, double? meshY)
    {
        picker?.TryClose();
        picker?.Dispose();
        picker = new GuiDialogGarmentPicker(capi!, be, meshY);
        picker.TryOpen();
    }

    /// <summary>The mannequin (our behaviour) under the crosshair, any of its blocks; null = none.</summary>
    public static BEBehaviorMannequinTailoring? LookedAt(ICoreClientAPI capi, out BlockSelection? sel)
    {
        sel = capi.World.Player?.CurrentBlockSelection;
        if (sel?.Position == null) return null;
        BlockPos pos = sel.Position;
        if (capi.World.BlockAccessor.GetBlock(pos) is Vintagestory.GameContent.BlockMultiblock filler) pos = pos.AddCopy(filler.OffsetInv);
        return capi.World.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorMannequinTailoring>();
    }

    // F (tool mode hotkey) with an empty hand or a sewing material at a mannequin: the garment picker (lane/drape-cut-sew).
    void WrapToolModeHotkey()
    {
        HotKey? hk = capi!.Input.GetHotKeyByCode(Loom.LoomClientSystem.ToolModeHotkey);
        if (hk == null) return;
        ActionConsumable<KeyCombination>? previous = hk.Handler;
        hk.Handler = comb => OnToolModeKey() || (previous?.Invoke(comb) ?? false);
    }

    bool OnToolModeKey()
    {
        if (capi == null) return false;
        if (picker?.IsOpened() == true) { picker.TryClose(); return true; }
        var tool = BEBehaviorMannequinTailoring.ToolOf(capi, capi.World.Player?.InventoryManager?.ActiveHotbarSlot);
        if (tool is not (FlowTool.Empty or FlowTool.Material)) return false;
        var be = LookedAt(capi, out _);
        if (be == null || be.AllowedGarments.Length == 0) return false;
        OpenPicker(be, null);
        return true;
    }

    void CutOrUncut(Hover h)
    {
        var be = h.Be;
        var g = h.T.Def;
        int part = DrapeFlow.VisiblePart(g, h.Texel, be.MaterialInSlot, be.PartShown);
        if (part < 0) return;
        bool uncut = Sneaking;
        var m = g.Parts[part].IsFixed ? null : be.MaterialInSlot(part);
        string? refusal = uncut ? DrapeFlow.UncutRefusal(g.Parts[part], m, be.Session.IsCut(part)) : DrapeFlow.CutRefusal(g.Parts[part], m, be.Session.IsCut(part));
        if (refusal != null) { Error(refusal, PartName(g, part)); return; }
        be.SendCutPart(part, !uncut);
    }

    void Toggle(Hover h)
    {
        int part = DrapeFlow.TogglePart(h.T.Def, h.Texel);
        if (part >= 0) h.Be.SendTogglePart(part);
    }

    void Stitch(Hover h)
    {
        lastStrokeTexel = h.Texel;
        bool pickOut = Sneaking;
        var def = h.T.Def;
        var mask = GarmentEmbroidery.Mask(def, h.T.MaterialOf);
        // lane/sewing-polish: in-world strokes go onto the shared pending stitches (same undo / apply as the window).
        var before = h.T.Pending ?? h.T.Applied;
        var working = MannequinPaint.Stroke(before, def.TextureWidth, def.TextureHeight, Footprint(h), mask, pickOut ? null : h.Code, h.Hex);
        if (GarmentEmbroidery.Same(before, working) || (before == null && working.IsEmpty))
        {
            if (!pickOut && !mask[h.Texel]) Error("tailoring-paint-nostitch");
            return;
        }
        h.Be.SendEmbStroke(h.T.Target, before, working, newStroke: !strokeSent);
        strokeSent = true;
    }

    void Drape(Hover h)
    {
        var be = h.Be;
        var g = h.T.Def;
        var held = h.Held!;
        int part = MannequinPaint.DrapePart(g, h.Texel, be.MaterialInSlot, held);
        if (part < 0)
        {
            Error("tailor-err-material");
            return;
        }
        var inSlot = be.MaterialInSlot(part);
        var mat = inSlot ?? held;
        int cw = mat.Cloth?.Width ?? GarmentLayout.OffsetPeriod, ch = mat.Cloth?.Height ?? GarmentLayout.OffsetPeriod;
        bool continues = be.Session.Wrap.Continues(g);
        int seg = MannequinPaint.PlacementSegment(h.T.Map, h.Texel, continues);
        if (seg < 0) return;
        // lane/drape-cut-sew: cut cloth stays put; an empty slot still takes the held item (placement unchanged).
        string? locked = be.PlacementRefusal(inSlot == null ? -1 : part, seg);
        if (locked != null)
        {
            if (inSlot != null) { Error(locked); return; }
            be.SendDrape(part, seg, be.Session.Wrap.Segments[seg], put: true);
            return;
        }
        var current = be.Session.Wrap.Segments[seg];
        var a = part < h.T.Pieces.Length ? h.T.Pieces[part] : new PieceAssignment(-1);
        SegmentPlacement? next;
        if (Sneaking) next = MannequinPaint.TurnAbout(h.T.Map, h.Texel, continues, a, current, cw, ch);
        else if (inSlot != null && MannequinPaint.EdgeNudge(h.T.Surface, h.T.Map, h.Hit.Face, h.Hit.TexelX, h.Hit.TexelY) is var (du, dv) && (du != 0 || dv != 0))
            next = SegmentPlacement.Of(current.Du + du, current.Dv + dv, current.Rot);
        else next = MannequinPaint.Anchor(h.T.Map, h.Texel, continues, a, current.Rot, cw, ch);
        if (next == null) return;
        be.SendDrape(part, seg, next.Value, inSlot == null);
        be.PredictSegment(seg, next.Value);
    }
}
