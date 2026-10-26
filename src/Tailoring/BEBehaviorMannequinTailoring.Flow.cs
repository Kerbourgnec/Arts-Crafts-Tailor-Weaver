using System;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// lane/drape-cut-sew (docs/tailoring.md "In-world tailoring flow"): the physical flow on the mannequin, window optional.
/// Pick a garment (picker: F, or right click with nothing started; packet 1208), drape (packet 1207, mannequin-paint),
/// cut with shears / knife (1209: placement locked), sew with a sewing kit (1210), optional parts on / off (1211).
/// Rules: <see cref="DrapeFlow"/>. Server authoritative; the client checks first only to show refusals at once.
/// </summary>
public partial class BEBehaviorMannequinTailoring
{
    bool[] ghostParts = Array.Empty<bool>();

    /// <summary>The held item's role in the flow (shears and knives cut; the vanilla sewing kit sews).</summary>
    public static FlowTool ToolOf(ICoreAPI api, ItemSlot? slot)
    {
        var stack = slot?.Itemstack;
        if (stack == null) return FlowTool.Empty;
        if (Loom.LoomTwine.IsTwine(stack)) return FlowTool.Twine;
        if (TailorMaterials.Read(api, stack) != null) return FlowTool.Material;
        var tool = stack.Collectible.Tool;
        if (tool == EnumTool.Shears || tool == EnumTool.Knife) return FlowTool.Shears;
        if (stack.Collectible.Code?.ToString() == DrapeFlow.SewingKitCode) return FlowTool.SewingKit;
        return FlowTool.None;
    }

    /// <summary>Whether part <paramref name="p"/>'s elements show (filled, required, switched on, or ghost of the held item).</summary>
    public bool PartShown(int p)
    {
        var g = Garment;
        if (g == null || p < 0 || p >= g.Parts.Length) return false;
        return DrapeFlow.Shown(g.Parts[p], MaterialInSlot(p), Session.IsOn(p), p < ghostParts.Length && ghostParts[p]);
    }

    public PartState PartStateOf(int p) => Garment is { } g && p >= 0 && p < g.Parts.Length
        ? DrapeFlow.State(g, p, MaterialInSlot, Session.IsCut, Session.IsOn) : PartState.Off;

    public (string key, int part)? SewProblem() => DrapeFlow.SewProblem(Garment, MaterialInSlot, Session.IsCut, Session.IsOn);

    /// <summary>Segments whose cloth is cut (placement locked).</summary>
    public bool[] LockedSegments()
    {
        var g = Garment;
        return g == null ? new bool[BodySegments.Count]
            : DrapeFlow.LockedSegments(g, BodyWrapCache.Map(Api, g), MaterialInSlot, Session.IsCut, Session.Wrap.Continues(g));
    }

    /// <summary>Refusal (lang key) of moving part <paramref name="part"/> / segment <paramref name="seg"/>, null = allowed.</summary>
    public string? PlacementRefusal(int part, int seg)
        => Garment is { } g ? DrapeFlow.PlacementRefusal(g, part, seg, MaterialInSlot, Session.IsCut, LockedSegments()) : null;

    #region Client

    /// <summary>
    /// Client: empty optional parts shown as ghost while the held item could fill them (or while sneaking with an empty
    /// hand: every part that can be switched on). Rebuilds the preview when it changes.
    /// </summary>
    public void SetGhost(bool[]? parts)
    {
        parts ??= Array.Empty<bool>();
        if (parts.SequenceEqual(ghostParts)) return;
        ghostParts = parts;
        RegenPreviewClient();
    }

    public void SendStartGarment(string code) => Send(TailorPacket.StartGarment, Encoding.UTF8.GetBytes(code));

    public void SendCutPart(int part, bool cut)
    {
        var data = new byte[8];
        BitConverter.GetBytes(part).CopyTo(data, 0);
        BitConverter.GetBytes(cut ? 1 : 0).CopyTo(data, 4);
        Send(TailorPacket.CutPart, data);
    }

    public void SendSew() => Send(TailorPacket.Sew);

    public void SendTogglePart(int part) => Send(TailorPacket.TogglePart, BitConverter.GetBytes(part));

    /// <summary>The garment picker's "Sewing window" button: the dialog as before (fallback).</summary>
    public void OpenSewingWindow()
    {
        if (capi?.World.Player is { } p) OpenDialogClient(p);
    }

    #endregion

    WorldInteraction[]? flowHelp;

    /// <summary>HUD help while a garment is in progress: cut / uncut, sew, optional part toggle, cancel.</summary>
    WorldInteraction[] FlowHelp(IWorldAccessor world)
    {
        if (flowHelp != null) return flowHelp;
        ItemStack[]? Stacks(params string[] codes)
        {
            var s = codes.Select(c => world.GetItem(new AssetLocation(c))).Where(i => i != null).Select(i => new ItemStack(i)).ToArray();
            return s.Length == 0 ? null : s;
        }
        var shears = Stacks("game:shears-copper", "game:knife-generic-copper", "game:knife-generic-flint");
        string d = TailorModSystem.Domain + ":";
        flowHelp = new[]
        {
            new WorldInteraction { ActionLangCode = d + "tailoring-help-cut", MouseButton = EnumMouseButton.Right, Itemstacks = shears },
            new WorldInteraction { ActionLangCode = d + "tailoring-help-sewkit", MouseButton = EnumMouseButton.Right, Itemstacks = Stacks(DrapeFlow.SewingKitCode) },
            new WorldInteraction { ActionLangCode = d + "tailoring-help-toggle", MouseButton = EnumMouseButton.Right, HotKeyCode = "shift", RequireFreeHand = true },
            new WorldInteraction { ActionLangCode = d + "tailoring-help-cancel", MouseButton = EnumMouseButton.Right, HotKeyCode = "shift", Itemstacks = shears },
        };
        return flowHelp;
    }

    /// <summary>Block info: parts ready and the next step of the flow.</summary>
    void FlowBlockInfo(GarmentDef? g, StringBuilder dsc)
    {
        if (g == null)
        {
            if (AllowedGarments.Length > 0 && !SessionActive) dsc.AppendLine(L("tailoring-flow-next-pick"));
            return;
        }
        var states = Enumerable.Range(0, g.Parts.Length).Select(PartStateOf).ToArray();
        int total = states.Count(s => s != PartState.Off && s != PartState.Fixed);
        int ready = states.Count(s => s is PartState.Cut or PartState.Attached);
        dsc.AppendLine(L("tailoring-flow-info-ready", ready, total));
        var (key, parts) = DrapeFlow.NextAction(g, MaterialInSlot, Session.IsCut, Session.IsOn);
        dsc.AppendLine(L(key, string.Join(", ", parts.Select(p => L("part-" + g.Parts[p].Code)))));
        var off = Enumerable.Range(0, states.Length).Where(p => states[p] == PartState.Off).ToArray();
        if (off.Length > 0) dsc.AppendLine(L("tailoring-flow-info-off", string.Join(", ", off.Select(p => L("part-" + g.Parts[p].Code)))));
    }

    #region Server

    void OnSlotModifiedServer(int slot)
    {
        if (slot >= 0 && slot < inventory.Count && inventory[slot].Empty && Session.SetCut(slot, false)) Blockentity.MarkDirty(true);
    }

    bool PlacementAllowed(IPlayer player, int part, int seg)
    {
        string? refusal = PlacementRefusal(part, seg);
        if (refusal == null) return true;
        Fail(player, refusal);
        return false;
    }

    string PartName(int part) => Garment is { } g && part >= 0 && part < g.Parts.Length ? L("part-" + g.Parts[part].Code) : "?";

    /// <summary>Picker: start (or switch to) a garment in world; the sender becomes the session owner.</summary>
    void OnStartGarment(IPlayer player, string code)
    {
        var g = AllowedGarments.FirstOrDefault(a => a.Code == code);
        if (g == null) return;
        OpenedBy = player.PlayerUID;
        if (code != Session.GarmentCode)
        {
            ReturnCloths(player);
            RefundEmbroidery(player);
            Session.SelectGarment(g);
        }
        Blockentity.MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/cloth"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.5f);
    }

    /// <summary>Shears / knife on a draped part: cut (placement locked; no durability, <see cref="DrapeFlow.ToolWear"/>) or, sneaking, uncut.</summary>
    void OnCutPart(IPlayer player, int part, bool cut)
    {
        var g = Garment;
        if (g == null) { Fail(player, "tailor-err-nogarment"); return; }
        if (part < 0 || part >= g.Parts.Length) return;
        var hslot = player.InventoryManager.ActiveHotbarSlot;
        if (ToolOf(Api, hslot) != FlowTool.Shears) { Fail(player, "tailoring-flow-cut-noshears"); return; }
        var m = g.Parts[part].IsFixed ? null : MaterialInSlot(part);
        string? refusal = cut ? DrapeFlow.CutRefusal(g.Parts[part], m, Session.IsCut(part)) : DrapeFlow.UncutRefusal(g.Parts[part], m, Session.IsCut(part));
        if (refusal != null) { Fail(player, refusal, PartName(part)); return; }
        if (!Session.SetCut(part, cut)) return;
        int wear = DrapeFlow.ToolWear(FlowTool.Shears);
        if (cut && wear > 0) hslot.Itemstack?.Collectible.DamageItem(Api.World, player.Entity, hslot, wear);
        Blockentity.MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation(cut ? "game:sounds/effect/clothrip" : "game:sounds/block/cloth"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.7f);
    }

    /// <summary>Sewing kit: finish the garment when every wanted part is filled and every textile cut.</summary>
    void OnSew(IPlayer player)
    {
        if (ToolOf(Api, player.InventoryManager.ActiveHotbarSlot) != FlowTool.SewingKit) { Fail(player, "tailoring-flow-sew-nokit"); return; }
        Finish(player, sewingKit: true);
    }

    /// <summary>
    /// The held sewing kit is the in-world sewing tool. It is not used up (owner 2026-10-25: garments cost raw materials
    /// only; <see cref="DrapeFlow.ToolWear"/> = 0), never in creative mode either.
    /// </summary>
    bool UseSewingKit(IPlayer player)
    {
        var hslot = player.InventoryManager.ActiveHotbarSlot;
        if (ToolOf(Api, hslot) != FlowTool.SewingKit) { Fail(player, "tailoring-flow-sew-nokit"); return false; }
        int wear = DrapeFlow.ToolWear(FlowTool.SewingKit);
        if (wear <= 0 || player.WorldData.CurrentGameMode == EnumGameMode.Creative) return true;
        hslot.TakeOut(wear);
        hslot.MarkDirty();
        return true;
    }

    /// <summary>Sneak + empty hand on an optional part: take its material back, or switch it on / off.</summary>
    void OnTogglePart(IPlayer player, int part)
    {
        var g = Garment;
        if (g == null || part < 0 || part >= g.Parts.Length) return;
        if (!player.InventoryManager.ActiveHotbarSlot.Empty) return;
        switch (DrapeFlow.Toggle(g.Parts[part], MaterialInSlot(part), Session.IsOn(part)))
        {
            case ToggleAction.Refuse:
                Fail(player, "tailoring-flow-toggle-required", PartName(part));
                return;
            case ToggleAction.TakeOut:
                var stack = inventory[part].TakeOutWhole();
                inventory[part].MarkDirty();
                if (stack != null && !player.InventoryManager.TryGiveItemstack(stack, true)) Api.World.SpawnItemEntity(stack, Pos.ToVec3d().Add(0.5, 1, 0.5));
                Session.SetCut(part, false);
                Session.SetOn(part, false);
                break;
            case ToggleAction.SwitchOn:
                Session.SetOn(part, true);
                break;
            case ToggleAction.SwitchOff:
                Session.SetOn(part, false);
                break;
        }
        Blockentity.MarkDirty(true);
    }

    #endregion
}
