using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>Client → server sewing packets (BE packet ids; &lt;1000 inventory, 1000/1001 open/close).</summary>
public enum TailorPacket
{
    SelectGarment = 1200, // UTF-8 garment code ("" = none)
    SetPiece = 1201,      // int32 part, (unused, was cloth slot), dx, dy, rot
    Cut = 1202,           // no payload
    Cancel = 1203,        // no payload: materials back to the player, session reset
    EmbApply = 1204,      // lane/sewing-polish: int32 target (-1 = in progress, else worn slot); the pending stitches become applied (charged)
    SetSegment = 1205,    // lane/body-wrap: int32 segment (BodySegments), du, dv, rot
    SetSleeves = 1206,    // lane/body-wrap: UTF-8 sleeves mode ("" = recipe default, "continue", "separate")
    Drape = 1207,         // lane/mannequin-paint: int32 part, segment, du, dv, rot, put (1 = held item into the part's empty slot)
    StartGarment = 1208,  // lane/drape-cut-sew: UTF-8 garment code; in world (picker), makes the sender the session owner
    CutPart = 1209,       // lane/drape-cut-sew: int32 part, int32 cut (1 = cut with the held shears / knife, 0 = uncut)
    Sew = 1210,           // lane/drape-cut-sew: no payload; the held sewing kit finishes the garment
    TogglePart = 1211,    // lane/drape-cut-sew: int32 part; sneak + empty hand on an optional part
    EmbStroke = 1212,     // lane/sewing-polish: byte newStroke + GarmentEmbroidery.EncodeDiff onto the pending stitches (free)
    EmbEdit = 1213,       // lane/sewing-polish: int32 target, int32 EmbEditOp (undo / redo / discard the pending stitches)
    SetOption = 1214,     // lane/modular-parts: int32 part, int32 option (index into the part's options)
}

/// <summary>
/// Sewing session on a vanilla mannequin (block entity behaviour added to <c>game:mannequin-*</c> by
/// <c>patches/mannequin-tailoring.json</c>, after the vanilla <c>Mannequin</c> behaviour). Right click with a sewing
/// material opens the sewing dialog: pick a garment recipe, put one material per part into its slot (slot i = part i,
/// like the ingredient slots of a cooking pot), move the cloth patterns, sew. A held material also goes into the first
/// empty part slot that takes it.
/// One player at a time (<see cref="OpenedBy"/>, the session owner; <see cref="SewingLock"/>). Server authoritative: the
/// dialog sends packets, the server validates, changes state and syncs the block entity tree. "Cut and sew" dresses the
/// mannequin with the garment (or gives it to the player when that slot is taken). While a garment is picked the
/// mannequin shows it in progress (<c>BEBehaviorMannequinTailoring.Preview.cs</c>).
/// <para>
/// Session lifetime (lane/playtest-fixes-5, owner; docs/tailoring.md "Session lifetime"): closing the dialog keeps the
/// session (materials, garment, layout, preview) so the player can walk around the mannequin; a right click with any
/// other item or an empty hand reopens it for the owner. "Cancel" gives the materials back and ends it. The owner's lock
/// lapses while they are offline or more than <see cref="SewingLock.LockDistance"/> blocks away; then another player
/// with a sewing material takes the session over as it is.
/// </para>
/// </summary>
public partial class BEBehaviorMannequinTailoring : BlockEntityBehavior, IInteractableWithHelp
{
    public const string ClassName = "actailor.MannequinTailoring";
    public const string InventoryClassName = "actailormannequin";
    public const double MaxUseDistance = 8;
    /// <summary>Highest <see cref="TailorPacket"/> id this behaviour handles.</summary>
    public const int MaxTailorPacket = 1299;
    const string TreeKey = "actailorTailoring";

    readonly InventoryGeneric inventory;
    string[] allowed = Array.Empty<string>();
    ICoreClientAPI? capi;
    GuiDialogTailoring? dialog;
    WorldInteraction[]? help;

    public TailoringSession Session { get; } = new();
    /// <summary>
    /// UID of the session owner ("" = none): set when they open the dialog, kept when it closes (lane/playtest-fixes-5),
    /// cleared by Cancel or by closing with nothing left. Synced and saved (kept across a server restart).
    /// </summary>
    public string OpenedBy { get; private set; } = "";

    /// <summary>Garment picked or materials in the slots: something to reopen (<see cref="SewingLock.SessionActive"/>).</summary>
    public bool SessionActive => SewingLock.SessionActive(Session.GarmentCode, !inventory.Empty);
    public InventoryBase Inventory => inventory;
    public ItemSlot ClothSlot(int i) => inventory[i];

    public BEBehaviorMannequinTailoring(BlockEntity blockentity) : base(blockentity)
    {
        inventory = new InventoryGeneric(TailoringSession.MaxParts, null, null, null,
            (id, inv) => new ItemSlotTailorMaterial(inv, id, (part, m) => TailoringSession.SlotAccepts(Garment, part, m)));
    }

    /// <summary>
    /// The garment in progress with the session's options resolved (lane/modular-parts: its parts carry the chosen options).
    /// Client: a part left off (none) that shows as ghost (<see cref="SetGhost"/>) is resolved as its first present option, so
    /// it has texels and elements to see and to pick; <see cref="PartOn"/> still says it is off.
    /// </summary>
    public GarmentDef? Garment
    {
        get
        {
            var b = GarmentBase;
            if (b == null) return null;
            var c = Session.Choice;
            if (ghostParts.Length > 0 && c.Length == b.Parts.Length)
            {
                c = c.ToArray();
                for (int p = 0; p < c.Length && p < ghostParts.Length; p++)
                    if (ghostParts[p] && b.Parts[p].HasNone && c[p] == b.Parts[p].NoneIndex) c[p] = b.Parts[p].FirstPresent;
            }
            if (!ReferenceEquals(garmentFor, b) || garmentChoice.Length != c.Length || !garmentChoice.AsSpan().SequenceEqual(c)) // asked many times a tick: resolve on change only
            {
                garmentFor = b;
                garmentChoice = c.ToArray();
                garmentResolved = b.Resolve(c);
            }
            return garmentResolved;
        }
    }

    GarmentDef? garmentFor, garmentResolved;
    int[] garmentChoice = Array.Empty<int>();

    /// <summary>The catalog recipe of the garment in progress (first options): names, slots, option lists.</summary>
    public GarmentDef? GarmentBase => Api == null ? null : GarmentCatalog.Get(Api).Find(Session.GarmentCode);

    /// <summary>Clothes category (dress type) of a garment, e.g. upperbody (recipe <c>dressType</c> = item <c>clothescategory</c>).</summary>
    public static string? CategoryOf(GarmentDef g) => g.DressType;

    /// <summary>Garments this mannequin part can wear (vanilla <c>allowedDresstypes</c> of the block).</summary>
    public GarmentDef[] AllowedGarments => Api == null ? Array.Empty<GarmentDef>() : MannequinFit.Garments(GarmentCatalog.Get(Api), allowed, CategoryOf);

    /// <summary>Material in sewing slot = part <paramref name="slot"/>, null when empty or out of range.</summary>
    public SlotMaterial? MaterialInSlot(int slot)
    {
        if (Api == null || slot < 0 || slot >= inventory.Count) return null;
        return TailorMaterials.Read(Api, ClothSlot(slot).Itemstack);
    }

    public string? CutProblem() => TailoringSession.CutProblem(Garment, MaterialInSlot);

    BEBehaviorMannequin? Mannequin => Blockentity.GetBehavior<BEBehaviorMannequin>();

    /// <summary>True when the finished garment would be dressed on the mannequin (its slot is free).</summary>
    public bool WillDress(GarmentDef g)
    {
        var inv = Mannequin?.Inventory;
        return inv != null && MannequinFit.DressOnMannequin(allowed, CategoryOf(g), i => i < inv.Count && inv[i].Empty);
    }

    public override void Initialize(ICoreAPI api, JsonObject properties)
    {
        base.Initialize(api, properties);
        allowed = Block.Attributes?["allowedDresstypes"].AsArray<string>()! ?? Array.Empty<string>();
        inventory.LateInitialize(InventoryClassName + "-" + Pos, api);
        inventory.Pos = Pos;
        inventory.ResolveBlocksOrItems();
        capi = api as ICoreClientAPI;
        if (api.Side == EnumAppSide.Server) inventory.SlotModified += OnSlotModifiedServer; // lane/drape-cut-sew: an emptied slot is no longer cut
        if (capi != null)
        {
            inventory.SlotModified += _ => capi.Event.EnqueueMainThreadTask(OnStateChangedClient, "actailor-mannequin-slot");
            InitPreview(capi);
            ClientLoaded.Add(this); // lane/mannequin-paint: in-world raycast targets
        }
    }

    static string L(string key, params object[] args) => Lang.Get(TailorModSystem.Domain + ":" + key, args);

    #region Interaction

    bool HoldsMaterial(IPlayer player) => TailorMaterials.Read(Api, player.InventoryManager?.ActiveHotbarSlot?.Itemstack) != null;

    bool HoldsTwine(IPlayer player) => Loom.LoomTwine.IsTwine(player.InventoryManager?.ActiveHotbarSlot?.Itemstack);

    /// <summary>Something to embroider here: a garment in progress or a tailored garment the mannequin wears.</summary>
    bool HasEmbroideryTarget => Garment != null || WornTailoredSlots().Any();

    public bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
    {
        // Shift = vanilla undressing; anything but a sewing material (patterned cloth, leather, pelt) = vanilla dressing,
        // except that the session owner's right click (any hand) reopens their sewing dialog (lane/playtest-fixes-5), and
        // twine in hand opens it on the Embroider step when there is a garment to embroider (lane/mannequin-embroidery).
        bool sneak = byPlayer.Entity.Controls.ShiftKey;
        if (sneak || AllowedGarments.Length == 0) return false;
        bool material = HoldsMaterial(byPlayer);
        bool twine = !material && HoldsTwine(byPlayer) && HasEmbroideryTarget;
        // lane/sewing-polish: pending stitches wait for Apply / Discard in the window; an empty hand opens it on Embroider.
        bool pending = !material && !twine && byPlayer.InventoryManager?.ActiveHotbarSlot?.Empty == true && PendingTargets().Any();
        twine |= pending && !SewingLock.Reopens(OpenedBy, byPlayer.PlayerUID, SessionActive, sneak);
        if (!material && !twine && !SewingLock.Reopens(OpenedBy, byPlayer.PlayerUID, SessionActive, sneak)) return false;
        handling = EnumHandling.PreventSubsequent;
        if (!world.Claims.TryAccess(byPlayer, Pos, EnumBlockAccessFlags.Use)) return false;

        if (world.Side == EnumAppSide.Server)
        {
            if (LockedByOther(byPlayer)) return true; // the Open packet that follows is refused with an error
            if (material) AddHeldMaterial(byPlayer);
        }
        else
        {
            OpenDialogClient(byPlayer, embroider: twine);
        }
        return true;
    }

    /// <summary>Server: one held item goes into the first empty part slot that takes it (needs a garment picked).</summary>
    void AddHeldMaterial(IPlayer player)
    {
        var hslot = player.InventoryManager.ActiveHotbarSlot;
        for (int i = 0; i < inventory.Count && !hslot.Empty; i++)
        {
            if (!inventory[i].Empty || !inventory[i].CanHold(hslot)) continue;
            if (hslot.TryPutInto(Api.World, inventory[i], 1) <= 0) continue;
            inventory[i].MarkDirty();
            hslot.MarkDirty();
            Blockentity.MarkDirty(true);
            return;
        }
    }

    WorldInteraction[]? reopenHelp;

    public WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer, ref EnumHandling handling)
    {
        var baseHelp = BaseHelp(world, forPlayer);
        return Garment == null ? baseHelp : baseHelp.Concat(FlowHelp(world)).ToArray(); // lane/drape-cut-sew
    }

    WorldInteraction[] BaseHelp(IWorldAccessor world, IPlayer forPlayer)
    {
        if (AllowedGarments.Length == 0) return Array.Empty<WorldInteraction>();
        if (SewingLock.Reopens(OpenedBy, forPlayer.PlayerUID, SessionActive, false))
        {
            reopenHelp ??= new[]
            {
                new WorldInteraction { ActionLangCode = TailorModSystem.Domain + ":tailoring-help-reopen", MouseButton = EnumMouseButton.Right },
            };
            return reopenHelp;
        }
        if (help == null)
        {
            var stacks = new[] { Loom.BlockEntityLoom.WovenClothCode, "game:leather-normal-plain", "game:hide-pelt-small", "game:bone" }
                .Select(c => world.GetItem(new AssetLocation(c))).Where(i => i != null).Select(i => new ItemStack(i)).ToArray();
            help = new[]
            {
                new WorldInteraction
                {
                    ActionLangCode = TailorModSystem.Domain + ":tailoring-help-sew",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = stacks.Length == 0 ? null : stacks,
                }
            };
        }
        if (!HasEmbroideryTarget) return help;
        if (embroiderHelp == null)
        {
            var twine = world.GetItem(new AssetLocation(Loom.LoomTwine.PlainTwineCode));
            embroiderHelp = help.Append(new WorldInteraction
            {
                ActionLangCode = TailorModSystem.Domain + ":tailoring-help-embroider",
                MouseButton = EnumMouseButton.Right,
                Itemstacks = twine == null ? null : new[] { new ItemStack(twine) },
            }).ToArray();
        }
        return embroiderHelp;
    }

    WorldInteraction[]? embroiderHelp;

    #endregion

    #region Client

    void OpenDialogClient(IPlayer byPlayer, bool embroider = false)
    {
        if (capi == null) return;
        if (dialog != null)
        {
            dialog.TryClose();
            return;
        }
        dialog = new GuiDialogTailoring(L("tailoring-title"), this, capi, embroider);
        if (dialog.IsDuplicate)
        {
            dialog = null;
            return;
        }
        dialog.OnClosed += () =>
        {
            dialog = null;
            localPieces = null;
            SetLocalEmbroidery(GarmentEmbroidery.TargetSession, null, regen: false); // the shared pending stitches stay (lane/sewing-polish)
            RegenPreviewClient();
        };
        dialog.TryOpen(); // OnGuiOpened opens the inventory client side; OnGuiClosed sends the Close packet
        capi.Network.SendPacketClient(inventory.Open(byPlayer));
        capi.Network.SendBlockEntityPacket(Pos, (int)EnumBlockEntityPacketId.Open, null);
    }

    void Send(TailorPacket id, byte[]? data = null) => capi?.Network.SendBlockEntityPacket(Pos, (int)id, data);

    public void SendSelectGarment(string code) => Send(TailorPacket.SelectGarment, Encoding.UTF8.GetBytes(code));

    public void SendSetPiece(int part, PieceAssignment a)
    {
        var data = new byte[20];
        BitConverter.GetBytes(part).CopyTo(data, 0);
        BitConverter.GetBytes(-1).CopyTo(data, 4);
        BitConverter.GetBytes(a.Dx).CopyTo(data, 8);
        BitConverter.GetBytes(a.Dy).CopyTo(data, 12);
        BitConverter.GetBytes(a.Rot).CopyTo(data, 16);
        Send(TailorPacket.SetPiece, data);
    }

    /// <summary>Body-space placement of the cloth on segment <paramref name="seg"/> (lane/body-wrap).</summary>
    public void SendSetSegment(int seg, SegmentPlacement p)
    {
        var data = new byte[16];
        BitConverter.GetBytes(seg).CopyTo(data, 0);
        BitConverter.GetBytes(p.Du).CopyTo(data, 4);
        BitConverter.GetBytes(p.Dv).CopyTo(data, 8);
        BitConverter.GetBytes(p.Rot).CopyTo(data, 12);
        Send(TailorPacket.SetSegment, data);
    }

    public void SendSetSleeves(string mode) => Send(TailorPacket.SetSleeves, Encoding.UTF8.GetBytes(mode));

    /// <summary>Body-space composition input of the garment in progress (lane/body-wrap); null without a body map.</summary>
    public BodyWrap? SessionWrap(GarmentDef g) => BodyWrapCache.For(Api, g, Session.Wrap);

    /// <summary>Chooses option <paramref name="option"/> of part <paramref name="part"/> (lane/modular-parts).</summary>
    public void SendSetOption(int part, int option)
    {
        var data = new byte[8];
        BitConverter.GetBytes(part).CopyTo(data, 0);
        BitConverter.GetBytes(option).CopyTo(data, 4);
        Send(TailorPacket.SetOption, data);
    }

    public void SendCut() => Send(TailorPacket.Cut);
    public void SendCancel() => Send(TailorPacket.Cancel);


    /// <summary>Main thread: dialog refresh + mannequin preview after a sync or a slot change.</summary>
    void OnStateChangedClient()
    {
        dialog?.OnStateChanged();
        RegenPreviewClient();
        ClientStateChanged?.Invoke();
    }

    /// <summary>Client: raised after every sync / slot change (the garment picker redraws its option buttons, lane/modular-parts).</summary>
    public event Action? ClientStateChanged;

    public override void OnReceivedServerPacket(int packetid, byte[] data)
    {
        base.OnReceivedServerPacket(packetid, data);
        if (packetid == (int)EnumBlockEntityPacketId.Close && dialog != null)
        {
            dialog.TryClose(); // refused (someone else is sewing here)
        }
    }

    #endregion

    #region Server

    /// <summary>The owner still holds the lock: online, in the world, within <see cref="SewingLock.LockDistance"/> blocks.</summary>
    bool PlayerActive(string uid)
    {
        if (Api.World.PlayerByUid(uid) is not IServerPlayer sp || sp.ConnectionState != EnumClientState.Playing || sp.Entity == null) return false;
        return sp.Entity.Pos.DistanceTo(Pos.ToVec3d().Add(0.5, 0.5, 0.5)) <= SewingLock.LockDistance;
    }

    bool LockedByOther(IPlayer player) => SewingLock.LockedFor(OpenedBy, player.PlayerUID, OpenedBy.Length > 0 && PlayerActive(OpenedBy));

    public override void OnReceivedClientPacket(IPlayer player, int packetid, byte[] data)
    {
        base.OnReceivedClientPacket(player, packetid, data);
        if (packetid == (int)EnumBlockEntityPacketId.Close)
        {
            // The dialog closed: the session stays with its owner (lane/playtest-fixes-5) unless nothing is left in it.
            player.InventoryManager?.CloseInventory(inventory);
            if (OpenedBy == player.PlayerUID && SewingLock.EndsOnClose(SessionActive))
            {
                OpenedBy = "";
                Blockentity.MarkDirty(true);
            }
            return;
        }
        bool ours = packetid < 1000 || packetid == (int)EnumBlockEntityPacketId.Open
            || (packetid >= (int)TailorPacket.SelectGarment && packetid <= MaxTailorPacket);
        if (!ours) return;
        if (!Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.Use)) return;
        if (player.Entity == null || player.Entity.Pos.DistanceTo(Pos.ToVec3d().Add(0.5, 0.5, 0.5)) > MaxUseDistance) return;

        if (packetid == (int)EnumBlockEntityPacketId.Open)
        {
            if (LockedByOther(player))
            {
                Fail(player, "tailor-err-inuse", Api.World.PlayerByUid(OpenedBy)?.PlayerName ?? "?");
                if (player is IServerPlayer sp) ((ICoreServerAPI)Api).Network.SendBlockEntityPacket(sp, Pos, (int)EnumBlockEntityPacketId.Close);
                return;
            }
            OpenedBy = player.PlayerUID;
            player.InventoryManager?.OpenInventory(inventory);
            Blockentity.MarkDirty(true);
            return;
        }
        // In-world painting and draping (lane/mannequin-paint) need no open dialog: allowed while nobody else holds the lock.
        bool inWorld = packetid == (int)TailorPacket.EmbApply || packetid == (int)TailorPacket.SetSegment || packetid == (int)TailorPacket.Drape
            || packetid == (int)TailorPacket.Cancel || (packetid >= (int)TailorPacket.StartGarment && packetid <= (int)TailorPacket.SetOption); // lane/drape-cut-sew, lane/sewing-polish
        if (OpenedBy != player.PlayerUID && !(inWorld && !LockedByOther(player)))
        {
            if (inWorld) Fail(player, "tailor-err-inuse", Api.World.PlayerByUid(OpenedBy)?.PlayerName ?? "?");
            return;
        }

        if (packetid < 1000)
        {
            inventory.InvNetworkUtil.HandleClientPacket(player, packetid, data);
            Api.World.BlockAccessor.GetChunkAtBlockPos(Pos)?.MarkModified();
            return;
        }

        switch ((TailorPacket)packetid)
        {
            case TailorPacket.SelectGarment:
                OnSelectGarment(player, data == null ? "" : Encoding.UTF8.GetString(data));
                break;
            case TailorPacket.SetPiece:
                if (data != null && data.Length >= 20 && PlacementAllowed(player, BitConverter.ToInt32(data, 0), -1)
                    && Session.SetPiece(BitConverter.ToInt32(data, 0), new PieceAssignment(BitConverter.ToInt32(data, 4),
                        BitConverter.ToInt32(data, 8), BitConverter.ToInt32(data, 12), BitConverter.ToInt32(data, 16))))
                {
                    Blockentity.MarkDirty(true);
                }
                break;
            case TailorPacket.Cut:
                OnCut(player);
                break;
            case TailorPacket.Cancel:
                OnCancel(player);
                break;
            case TailorPacket.EmbApply:
                OnEmbApply(player, data);
                break;
            case TailorPacket.EmbStroke:
                OnEmbStroke(player, data);
                break;
            case TailorPacket.EmbEdit:
                OnEmbEdit(player, data);
                break;
            case TailorPacket.SetSegment:
                if (data != null && data.Length >= 16 && PlacementAllowed(player, -1, BitConverter.ToInt32(data, 0))
                    && Session.SetSegment(BitConverter.ToInt32(data, 0),
                        new SegmentPlacement(BitConverter.ToInt32(data, 4), BitConverter.ToInt32(data, 8), BitConverter.ToInt32(data, 12))))
                {
                    Blockentity.MarkDirty(true);
                }
                break;
            case TailorPacket.Drape:
                OnDrape(player, data);
                break;
            case TailorPacket.SetSleeves:
                if (Session.AnyCut) { Fail(player, "tailoring-flow-locked-sleeves"); break; } // would move cut cloth
                if (Session.SetSleeves(data == null ? "" : Encoding.UTF8.GetString(data))) Blockentity.MarkDirty(true);
                break;
            case TailorPacket.StartGarment:
                OnStartGarment(player, data == null ? "" : Encoding.UTF8.GetString(data));
                break;
            case TailorPacket.CutPart:
                if (data != null && data.Length >= 8) OnCutPart(player, BitConverter.ToInt32(data, 0), BitConverter.ToInt32(data, 4) == 1);
                break;
            case TailorPacket.Sew:
                OnSew(player);
                break;
            case TailorPacket.SetOption:
                if (data != null && data.Length >= 8) OnSetOption(player, BitConverter.ToInt32(data, 0), BitConverter.ToInt32(data, 4));
                break;
            case TailorPacket.TogglePart:
                if (data != null && data.Length >= 4) OnTogglePart(player, BitConverter.ToInt32(data, 0));
                break;
        }
    }

    #region Embroidery (lane/mannequin-embroidery)

    /// <summary>Mannequin slots (indices into the vanilla worn inventory) holding a tailored garment with data.</summary>
    public IEnumerable<int> WornTailoredSlots()
    {
        var inv = Mannequin?.Inventory;
        if (inv == null) yield break;
        for (int i = 0; i < inv.Count; i++) if (WornGarment(i) != null) yield return i;
    }

    /// <summary>The tailored garment worn in mannequin slot <paramref name="slot"/>: recipe, data and the slot; null when none.</summary>
    public (GarmentDef def, GarmentData data, ItemSlot slot)? WornGarment(int slot)
    {
        var inv = Mannequin?.Inventory;
        if (inv == null || slot < 0 || slot >= inv.Count) return null;
        var stack = inv[slot].Itemstack;
        if (stack?.Collectible is not ItemTailoredGarment item || item.Def is not { } def) return null;
        var data = GarmentData.FromStack(stack, def);
        return data == null ? null : (data.Resolve(def), data, inv[slot]); // lane/modular-parts: the def of the stack's options
    }

    /// <summary>
    /// What an embroidery target is: the recipe, its applied stitches and the part materials (stitch mask).
    /// <see cref="GarmentEmbroidery.TargetSession"/> = the garment in progress (slot materials), else a worn slot.
    /// </summary>
    public EmbroideryTargetInfo? EmbroideryTargetOf(int target)
    {
        if (target == GarmentEmbroidery.TargetSession)
            return Garment is { } g ? new EmbroideryTargetInfo(g, Session.Embroidery, MaterialInSlot, Session.Pieces, SessionWrap(g),
                EmbroideryDrafts.SessionKey(g.Code), Session.Drafts.Get(target, EmbroideryDrafts.SessionKey(g.Code))) : null;
        if (WornGarment(target) is not { } worn) return null;
        string key = EmbroideryDrafts.WornKey(worn.def, worn.data);
        return new EmbroideryTargetInfo(worn.def, worn.data.Embroidery, worn.data.PartMaterial, worn.data.Pieces, BodyWrapCache.For(Api, worn.def, worn.data.Wrap),
            key, Session.Drafts.Get(target, key));
    }

    /// <summary>
    /// An embroidery target: recipe, applied stitches, part materials (stitch mask), placements and body-space wrap
    /// (composition; null = UV space).
    /// </summary>
    public sealed record EmbroideryTargetInfo(GarmentDef Garment, Embroidery? Applied, System.Func<int, SlotMaterial?> MaterialOf, PieceAssignment[] Pieces,
        BodyWrap? Wrap = null, string? Key = null, Embroidery? Pending = null)
    {
        /// <summary>What the target shows: the pending stitches (shared by window and world), else the applied ones.</summary>
        public Embroidery? Shown => Pending ?? Applied;
    }

    #endregion

    /// <summary>Another garment: the part slots belong to the old recipe, so their materials go back to the player first.</summary>
    void OnSelectGarment(IPlayer player, string code)
    {
        GarmentDef? g = code.Length == 0 ? null : AllowedGarments.FirstOrDefault(a => a.Code == code);
        if (code.Length > 0 && g == null) return; // unknown, or not wearable by this mannequin part
        if (code == Session.GarmentCode) return;
        ReturnCloths(player);
        RefundEmbroidery(player); // before the session forgets what it charged
        if (Session.SelectGarment(g)) Blockentity.MarkDirty(true);
    }

    void OnCut(IPlayer player) => Finish(player, sewingKit: false);

    /// <summary>
    /// Cut and sew: materials consumed, the mannequin wears the garment (or the player gets it). The dialog's Cut checks
    /// <see cref="CutProblem"/> only; the in-world sewing kit (<paramref name="sewingKit"/>, lane/drape-cut-sew) also needs
    /// every wanted part filled and every textile cut; the kit is not used up (lane/sewing-polish). Only the part
    /// materials are consumed.
    /// </summary>
    void Finish(IPlayer player, bool sewingKit)
    {
        var g = Garment;
        string? problem = CutProblem();
        if (problem == null && sewingKit && SewProblem() is { } sp)
        {
            Fail(player, sp.key, sp.part >= 0 && g != null ? L("part-" + g.Parts[sp.part].Code) : "");
            return;
        }
        if (problem != null || g == null)
        {
            Fail(player, problem ?? "tailor-err-nogarment");
            return;
        }
        // lane/sewing-polish: pending stitches are never dropped implicitly; apply or discard them first.
        if (PendingChanges(GarmentEmbroidery.TargetSession) > 0)
        {
            Fail(player, "tailor-err-emb-pending");
            return;
        }
        Item? item = Api.World.GetItem(new AssetLocation(g.Item));
        if (item == null) { Fail(player, "tailor-err-noitem", g.Item); return; }
        if (sewingKit && !UseSewingKit(player)) return;

        var stack = new ItemStack(item);
        TailoringSession.BuildGarment(g, Session.Pieces, MaterialInSlot, Session.Embroidery, Session.Wrap).WriteNew(stack, g); // applied stitches + body wrap go along; condition 1
        foreach (int slot in TailoringSession.ConsumedSlots(g, MaterialInSlot))
        {
            ClothSlot(slot).TakeOut(1);
            ClothSlot(slot).MarkDirty();
        }
        Session.Reset(); // the finished garment replaces the preview

        var mannequinInv = Mannequin?.Inventory;
        int wornSlot = MannequinFit.WornSlot(allowed, CategoryOf(g));
        if (mannequinInv != null && WillDress(g))
        {
            mannequinInv[wornSlot].Itemstack = stack;
            mannequinInv[wornSlot].MarkDirty();
        }
        else if (!player.InventoryManager.TryGiveItemstack(stack, true))
        {
            Api.World.SpawnItemEntity(stack, Pos.ToVec3d().Add(0.5, 1, 0.5));
        }
        if (sewingKit && OpenedBy == player.PlayerUID && !SessionActive && player.InventoryManager.OpenedInventories?.Contains(inventory) != true) OpenedBy = ""; // in world: nothing left, session over
        Blockentity.MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/cloth"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 1f);
    }

    /// <summary>Cancel: cloths back to the player (ground if full), session reset and ended (no owner).</summary>
    void OnCancel(IPlayer player)
    {
        ReturnCloths(player);
        RefundEmbroidery(player);
        Session.Reset();
        OpenedBy = "";
        Blockentity.MarkDirty(true);
    }

    /// <summary>
    /// Gives back the twine charged for the garment in progress (1 per code, lane/embroidery-paid): into the player's
    /// inventory, on the ground at the mannequin when full. Twine paid on a finished garment is never refunded.
    /// </summary>
    void RefundEmbroidery(IPlayer player)
    {
        foreach (string code in Session.TakeCharged())
        {
            var item = Api.World.GetItem(new AssetLocation(code));
            if (item == null) continue;
            var stack = new ItemStack(item);
            if (!player.InventoryManager.TryGiveItemstack(stack, true)) Api.World.SpawnItemEntity(stack, Pos.ToVec3d().Add(0.5, 1, 0.5));
        }
    }

    void ReturnCloths(IPlayer player)
    {
        foreach (var slot in inventory)
        {
            if (slot.Empty) continue;
            var stack = slot.TakeOutWhole();
            if (!player.InventoryManager.TryGiveItemstack(stack, true)) Api.World.SpawnItemEntity(stack, Pos.ToVec3d().Add(0.5, 1, 0.5));
            slot.MarkDirty();
        }
    }

    void Fail(IPlayer player, string langKey, params object[] args)
    {
        if (player is IServerPlayer sp)
        {
            sp.SendIngameError("actailor-tailor", Lang.GetL(sp.LanguageCode, TailorModSystem.Domain + ":" + langKey, args));
        }
    }

    public override void OnBlockBroken(IPlayer? byPlayer = null)
    {
        base.OnBlockBroken(byPlayer);
        if (Api.Side == EnumAppSide.Server) inventory.DropAll(Pos.ToVec3d().Add(0.5, 0.5, 0.5));
    }

    #endregion

    #region Sync, lifecycle

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        var t = new TreeAttribute();
        Session.ToTree(t);
        t.SetString("openedBy", OpenedBy);
        var inv = new TreeAttribute();
        inventory.ToTreeAttributes(inv);
        t["cloths"] = inv;
        tree[TreeKey] = t;
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        var t = tree.GetTreeAttribute(TreeKey);
        if (t != null)
        {
            Session.FromTree(t, Api == null ? null : code => GarmentCatalog.Get(Api).Find(code));
            // The session owner is kept across a restart (lane/playtest-fixes-5); the lock only holds while they are around.
            OpenedBy = t.GetString("openedBy", "") ?? "";
            var inv = t.GetTreeAttribute("cloths");
            if (inv != null) inventory.FromTreeAttributes(inv);
        }
        if (worldAccessForResolve.Side == EnumAppSide.Client)
        {
            if (dialog?.Dragging != true) localPieces = null;
            OnSyncedClient(tree);
            capi?.Event.EnqueueMainThreadTask(OnStateChangedClient, "actailor-mannequin-sync");
        }
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        var g = Garment;
        if (OpenedBy.Length > 0 && SessionActive)
            dsc.AppendLine(L("tailoring-info-owner", Api.World.PlayerByUid(OpenedBy)?.PlayerName ?? "?"));
        if (g != null) dsc.AppendLine(L("tailoring-info-garment", L("garment-" + g.Code)));
        FlowBlockInfo(g, dsc); // lane/drape-cut-sew: parts missing, next step
        foreach (int t in PendingTargets()) // lane/sewing-polish: shared pending stitches wait for Apply / Discard
        {
            var name = t == GarmentEmbroidery.TargetSession ? g?.Code : WornGarment(t)?.def.Code;
            if (name != null) dsc.AppendLine(L("tailoring-info-emb-pending", L("garment-" + name), PendingChanges(t)));
        }
        int cloths = inventory.Sum(s => s.StackSize);
        if (cloths > 0) dsc.AppendLine(L("tailoring-info-cloths", cloths));
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        Dispose();
    }

    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        Dispose();
    }

    void Dispose()
    {
        if (dialog?.IsOpened() == true) dialog.TryClose();
        dialog?.Dispose();
        dialog = null;
        DisposePreview();
        ClientLoaded.Remove(this);
    }

    #endregion
}
