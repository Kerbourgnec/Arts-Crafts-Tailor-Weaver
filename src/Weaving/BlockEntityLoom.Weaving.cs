using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ArtsAndCrafts.Tailor.Weaving;
using ArtsAndCrafts.Tailor.Tapestry;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ArtsAndCrafts.Tailor.Loom;

/// <summary>
/// Client → server weaving packets (block entity packet ids; &lt;1000 is inventory, 1000/1001 open/close).
/// Row packets start with the int32 row count the client saw, so stale clicks are dropped.
/// </summary>
public enum LoomPacket
{
    // 1101 PaintCell / 1102 FillRow: retired (lane/loom-roles: the Artisan loom weaves drafts only), ignored by the server.
    WeaveRow = 1103,   // int row
    CutCloth = 1104,   // int row (woven rows; cut when the square cloth is complete)
    UnweaveRow = 1105, // int row
    CopyRow = 1106,    // int row, int source row
    Shuttle = 1107,    // int shuttle, int ShuttleAction (was Spool before lane/warp-shuttles-import: same payload)
    ToggleUnroll = 1108,
    SetBrush = 1109,   // int mode index (empty-hand brush dialog, lane/loom-feedback-4)
    // 1110 was SetFormat (lane/cloth-formats); retired with the formats (lane/square-cloth), ignored.
    PressTreadle = 1111, // int row, int treadle 0..3 (draft mode, lane/draft-mode)
    SetDraft = 1112,   // int LoomMode, int threading, int tie-up, int treadling preset, int picked (bit 0 threading, bit 1 tie-up clicked; lane/warp-shuttles-import) (F picker; threading only on a free warp)
    // 1113 was Rewarp (lane/playtest-fixes-5); retired with the re-warp step (lane/loom-feedback-8), ignored.
    WarpThread = 1114, // int warp end, int all (sneak): load the held twine onto warp threads at the back, lane/warp-shuttles-import
    SetPlan = 1115,    // UTF-8 designer code (ACD1:...): the draft window's whole plan, lane/draft-window
    SetProduct = 1117, // int ClothProduct: what the empty loom weaves (F picker, lane/linen-output)
    LoadWarp = 1116,   // int from end, int to end, int designer palette index: the draft window loads warp threads (warp sync, lane/linen-output)
}

/// <summary>What a click on a shuttle does (decided by the client from held item + sneak, re-checked by the server).</summary>
public enum ShuttleAction { LoadOne = 0, LoadStack = 1, Select = 2, Unload = 3 }

/// <summary>
/// Weaving half of the loom, played in-world: the client maps the clicked selection box to a part and sends
/// a packet; the server checks the held item and applies the rules; state syncs back via tree attributes.
/// </summary>
public partial class BlockEntityLoom
{
    public const string WovenClothCode = TailorModSystem.Domain + ":wovencloth";
    internal const double MaxUseDistanceSq = 8 * 8;

    public readonly WeavingState Weaving = new();
    /// <summary>The 4 shuttles at the beater ends (lane/shuttle-fix; lane/warp-shuttles-import: breast beam ends; were 6 spools at the back).</summary>
    public readonly SpoolRack Shuttles = new(SpoolRack.ShuttleCount);
    /// <summary>Twine stocked on the warp beam (lane/loom-feedback-8 item 4): a twine click on a thread of its colour; the cut takes it after the shuttles.</summary>
    public readonly TwineStock WarpStock = new();
    /// <summary>Server: twine of an old save's spools 5 and 6 (no shuttle for them), dropped at the loom on load.</summary>
    readonly List<(string code, int amount)> legacyOverflow = new();
    /// <summary>Show the whole cloth hanging in front of the loom instead of rolled on the cloth beam.</summary>
    public bool Unrolled { get; private set; }
    int lastSeenRows = -1;

    // Selection boxes: rebuilt when the woven row count or the unrolled flag changes; one shifted copy per multiblock offset.
    readonly object boxLock = new();
    int boxesForRows = -1;
    bool boxesForUnrolled;
    bool boxesForDraft;
    float boxesForRotY = float.NaN;
    (int w, int h) boxesForSize;
    Cuboidf[] boxes = Array.Empty<Cuboidf>();
    LoomBoxPart[] boxParts = Array.Empty<LoomBoxPart>();
    readonly Dictionary<(int, int, int), Cuboidf[]> shiftedBoxes = new();

    #region Selection boxes

    /// <summary>Boxes relative to the block at controller - <paramref name="offset"/> (offset = controller - that block).</summary>
    public Cuboidf[] GetSelectionBoxes(Vec3i? offset)
    {
        lock (boxLock)
        {
            EnsureBoxes();
            if (offset == null || (offset.X == 0 && offset.Y == 0 && offset.Z == 0)) return boxes;
            var key = (offset.X, offset.Y, offset.Z);
            if (!shiftedBoxes.TryGetValue(key, out var shifted))
            {
                shifted = boxes.Select(b => b.OffsetCopy(offset.X, offset.Y, offset.Z)).ToArray();
                shiftedBoxes[key] = shifted;
            }
            return shifted;
        }
    }

    public LoomBoxPart PartAt(int selectionBoxIndex)
    {
        lock (boxLock)
        {
            EnsureBoxes();
            return selectionBoxIndex >= 0 && selectionBoxIndex < boxParts.Length ? boxParts[selectionBoxIndex] : new LoomBoxPart(LoomPart.Frame, 0);
        }
    }

    /// <summary>Selection boxes (controller-relative) of the current row's cells <paramref name="start"/>..<paramref name="end"/> (exclusive).</summary>
    public List<Cuboidf> CellBoxes(int start, int end)
    {
        lock (boxLock)
        {
            EnsureBoxes();
            var list = new List<Cuboidf>();
            for (int i = 0; i < boxParts.Length; i++)
                if (boxParts[i].Part == LoomPart.Cell && boxParts[i].Index >= start && boxParts[i].Index < end) list.Add(boxes[i]);
            return list;
        }
    }

    /// <summary>Selection boxes (controller-relative) of <paramref name="part"/> with an index in <paramref name="indices"/>.</summary>
    public List<Cuboidf> PartBoxes(LoomPart part, IReadOnlyCollection<int> indices)
    {
        lock (boxLock)
        {
            EnsureBoxes();
            var list = new List<Cuboidf>();
            for (int i = 0; i < boxParts.Length; i++)
                if (boxParts[i].Part == part && indices.Contains(boxParts[i].Index)) list.Add(boxes[i]);
            return list;
        }
    }

    void EnsureBoxes()
    {
        float rotY = GeometryRotateYDeg;
        var size = (Weaving.Width, Weaving.Rows);
        bool draft = Weaving.DraftMode;
        if (boxesForRows == Weaving.WovenRows && boxesForUnrolled == Unrolled && boxesForRotY == rotY && boxesForSize == size && boxesForDraft == draft) return;
        (boxes, boxParts) = LoomGeometry.Build(Weaving.WovenRows, rotY, Unrolled, Weaving.Width, Weaving.Rows, draft);
        boxesForDraft = draft;
        boxesForSize = size;
        shiftedBoxes.Clear();
        boxesForRows = Weaving.WovenRows;
        boxesForUnrolled = Unrolled;
        boxesForRotY = rotY;
    }

    #endregion

    #region Interaction

    public enum HeldKind { Other, Twine, Cutter, EmptyHand }

    public static HeldKind Classify(ItemSlot? slot)
    {
        if (slot == null || slot.Empty) return HeldKind.EmptyHand;
        if (LoomTwine.IsTwine(slot.Itemstack)) return HeldKind.Twine;
        if (LoomTwine.IsCutter(slot)) return HeldKind.Cutter;
        return HeldKind.Other;
    }

    /// <summary>
    /// Right click on the loom (blockSel at the controller position, SelectionBoxIndex into our boxes).
    /// The client decides what was clicked and sends a packet; the server only claims the interaction.
    /// Returns false to let the held item do its own thing (frame, or an unrelated item on cloth/shuttles).
    /// </summary>
    public bool OnInteract(IPlayer byPlayer, BlockSelection blockSel)
    {
        HeldKind held = Classify(byPlayer.InventoryManager.ActiveHotbarSlot);
        LoomBoxPart part = PartAt(blockSel.SelectionBoxIndex);
        bool sneak = IsSneaking(byPlayer);
        if (!Handles(part.Part, held, sneak)) return false;
        if (part.Part == LoomPart.Seat) return OnSeatInteract(byPlayer);
        if (Api.Side == EnumAppSide.Server) return true;

        int row = Weaving.WovenRows;
        if (held == HeldKind.Cutter && part.Part is not (LoomPart.Shuttle or LoomPart.WarpThread or LoomPart.Treadle))
        {
            SendPacket(LoomPacket.CutCloth, row);
            return true;
        }
        switch (part.Part)
        {
            case LoomPart.Cell:
                // Draft weaving only (lane/loom-roles): cells are not painted; say where the warp and the rows come from.
                (Api as ICoreClientAPI)?.TriggerIngameError(this, "actailor-loom",
                    Lang.Get(TailorModSystem.Domain + ":" + (Weaving.WarpLocked ? "loom-err-draftpaint" : "loom-err-warpatback")));
                break;
            case LoomPart.Cloth:
                SendPacket(LoomPacket.CopyRow, row, part.Index);
                break;
            case LoomPart.Beater:
                SendPacket(sneak ? LoomPacket.UnweaveRow : LoomPacket.WeaveRow, row);
                break;
            case LoomPart.Shuttle:
                var action = held == HeldKind.Twine
                    ? (sneak ? ShuttleAction.LoadStack : ShuttleAction.LoadOne)
                    : (sneak ? ShuttleAction.Unload : ShuttleAction.Select);
                SendPacket(LoomPacket.Shuttle, part.Index, (int)action);
                break;
            case LoomPart.WarpThread:
                // Twine: load (sneak: all); empty hand + sneak: clear to flax (lane/shuttle-fix).
                SendPacket(LoomPacket.WarpThread, part.Index, held == HeldKind.EmptyHand ? 2 : sneak ? 1 : 0);
                break;
            case LoomPart.ClothBeam:
                SendPacket(LoomPacket.ToggleUnroll);
                break;
            case LoomPart.HangRow:
                if (sneak) SendPacket(LoomPacket.ToggleUnroll);
                else SendPacket(LoomPacket.CopyRow, row, part.Index);
                break;
            case LoomPart.Treadle:
                SendPacket(LoomPacket.PressTreadle, row, part.Index);
                break;
        }
        return true;
    }

    /// <summary>
    /// Which held items a part reacts to (same rule on both sides, so the server claims what the client sends).
    /// Unroll / roll up is sneak + right click on the cloth beam or the hanging cloth; a plain click on the cloth
    /// beam passes through (unless cutting), a plain click on a hanging row copies it.
    /// </summary>
    public static bool Handles(LoomPart part, HeldKind held, bool sneak = false) => part switch
    {
        LoomPart.Frame => false,
        LoomPart.Beater => true,
        LoomPart.Seat => true,
        LoomPart.Shuttle => held is HeldKind.Twine or HeldKind.EmptyHand,
        // Load the held twine onto the thread (sneak: all); sneak + empty hand: clear it to flax (lane/shuttle-fix).
        LoomPart.WarpThread => held is HeldKind.Twine || (sneak && held == HeldKind.EmptyHand),
        LoomPart.Treadle => held is HeldKind.Twine or HeldKind.EmptyHand, // weft: the active shuttle (twine in hand picks its shuttle)
        LoomPart.ClothBeam => held == HeldKind.Cutter || (sneak && held != HeldKind.Other),
        _ => held != HeldKind.Other,
    };

    /// <summary>Client: set this player's brush mode (empty-hand dialog); the server sets the watched attribute.</summary>
    public void SendBrushMode(int mode) => SendPacket(LoomPacket.SetBrush, mode);

    /// <summary>Client: choose the draft presets (the server refuses a threading change on a set warp). The packet keeps its
    /// mode int (always draft since lane/loom-roles; the server ignores it).</summary>
    /// <paramref name="picked"/>: bit 0 = the threading row was clicked, bit 1 = the tie-up row (drops an imported draft's own).
    public void SendDraft(int threading, int tieUp, int treadling, int picked = 0) => SendPacket(LoomPacket.SetDraft, (int)LoomMode.Draft, threading, tieUp, treadling, picked);

    /// <summary>Client (F picker, lane/linen-output): weave this product on the empty loom.</summary>
    public void SendProduct(ClothProduct product) => SendPacket(LoomPacket.SetProduct, (int)product);

    /// <summary>Size of a new cloth on this loom: the world's cloth size (no re-warp step since lane/loom-feedback-8).</summary>
    public int NextClothSize => ClothSizeSetting.NewClothSize(Api?.World);

    void SendPacket(LoomPacket id, params int[] args)
    {
        var data = new byte[args.Length * 4];
        for (int i = 0; i < args.Length; i++) BitConverter.GetBytes(args[i]).CopyTo(data, i * 4);
        (Api as ICoreClientAPI)?.Network.SendBlockEntityPacket(Pos, (int)id, data);
    }

    #endregion

    #region Server

    public override void OnReceivedClientPacket(IPlayer player, int packetid, byte[] data)
    {
        if (packetid < FirstPacketId || packetid > (int)LoomPacket.SetProduct)
        {
            base.OnReceivedClientPacket(player, packetid, data);
            return;
        }
        if (player is not IServerPlayer sp) return;
        if (!Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.Use)) return;
        if (player.Entity.Pos.SquareDistanceTo(Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5) > MaxUseDistanceSq) return;

        var packet = (LoomPacket)packetid;
        if (packetid is 1101 or 1102 or 1110 or 1113) return; // retired PaintCell / FillRow (lane/loom-roles), SetFormat (lane/square-cloth), Rewarp (lane/loom-feedback-8)
        bool rowPacket = packet is not (LoomPacket.Shuttle or LoomPacket.ToggleUnroll or LoomPacket.SetBrush or LoomPacket.SetDraft
            or LoomPacket.WarpThread or LoomPacket.SetPlan or LoomPacket.LoadWarp or LoomPacket.SetProduct);
        if (rowPacket && ReadInt(data, 0) != Weaving.WovenRows) return; // stale click: the client saw another row

        switch (packet)
        {
            case LoomPacket.WeaveRow: OnWeave(sp); break;
            case LoomPacket.CutCloth: OnCut(sp); break;
            case LoomPacket.UnweaveRow: OnUnweave(sp); break;
            case LoomPacket.CopyRow: OnCopyRow(sp, ReadInt(data, 1)); break;
            case LoomPacket.Shuttle: OnShuttle(sp, ReadInt(data, 0), (ShuttleAction)ReadInt(data, 1)); break;
            case LoomPacket.WarpThread:
                if (ReadInt(data, 1) == 2) OnClearWarpThread(sp, ReadInt(data, 0));
                else OnWarpThread(sp, ReadInt(data, 0), ReadInt(data, 1) == 1);
                break;
            case LoomPacket.ToggleUnroll: OnToggleUnroll(); break;
            case LoomPacket.SetBrush: CollectibleBehaviorLoomBrush.SetMode(sp, ReadInt(data, 0)); break;
            case LoomPacket.PressTreadle: OnPressTreadle(sp, ReadInt(data, 1)); break;
            case LoomPacket.SetDraft: OnSetDraft(sp, ReadInt(data, 1), ReadInt(data, 2), ReadInt(data, 3), ReadInt(data, 4)); break;
            case LoomPacket.SetPlan: OnSetPlan(sp, data); break;
            case LoomPacket.SetProduct:
                if (LoomCore.TrySetProduct(sp, Weaving, ReadInt(data, 0)) == LoomCore.ProductResult.Ok) { Unrolled = false; MarkDirty(true); }
                break;
            case LoomPacket.LoadWarp: OnLoadWarp(sp, ReadInt(data, 0), ReadInt(data, 1), ReadInt(data, 2)); break;
        }
        OpenDefaultShed();
    }

    /// <summary>
    /// Server (lane/loom-feedback-8 item 6, owner): a closed shed opens on its own on the plan's treadle for the row, else the
    /// treadling preset's, else treadle 1 (<see cref="WeavingState.DefaultTreadle"/>), with the resolved weft (plan's shuttle
    /// first, <see cref="WeavingState.ResolveWeft"/>), so the weaver can beat at once. Nothing without a filled shuttle.
    /// Called after every packet, on load and after an import.
    /// </summary>
    void OpenDefaultShed()
    {
        if (Api?.Side != EnumAppSide.Server || !Weaving.DraftMode || Weaving.Complete || Weaving.Draft.CurrentTreadle != DraftState.NoTreadle) return;
        if (Weaving.ResolveWeft(Shuttles) is not { } code) return;
        int idx = Weaving.PaletteIndexFor(code, TwineHex(code));
        if (idx >= 0 && Weaving.OpenDefaultShed((byte)idx)) MarkDirty(true);
    }

    /// <summary>Lowest loom packet id (1101, the retired PaintCell): ids from here to SetProduct are ours.</summary>
    const int FirstPacketId = 1101;

    static int ReadInt(byte[]? data, int index)
        => data != null && data.Length >= (index + 1) * 4 ? BitConverter.ToInt32(data, index * 4) : -1;

    /// <summary>
    /// Weft source (lane/warp-shuttles-import): a shuttle. Twine in hand picks (activates) the shuttle holding it;
    /// an empty hand uses the active shuttle. Null (with an error) when no shuttle fits.
    /// </summary>
    (string code, string hex)? PaintSource(IServerPlayer player)
    {
        ItemSlot? slot = player.InventoryManager.ActiveHotbarSlot;
        ItemStack? held = slot?.Itemstack;
        if (held != null && LoomTwine.IsTwine(held))
        {
            string heldCode = held.Collectible.Code.ToString();
            int i = Shuttles.IndexOf(heldCode);
            if (i < 0)
            {
                // Forgiving (lane/shuttle-fix, owner playtest): twine on no shuttle goes onto the first empty one (one
                // twine, as a plain click on a shuttle), which becomes active; only all 4 holding other twine fails.
                var (free, added) = Shuttles.LoadIntoFree(heldCode, 1);
                if (free < 0) { Fail(player, "loom-err-shuttlesfull", TwineDisplayName(heldCode), SpoolRack.ShuttleCount); return null; }
                slot!.TakeOut(added);
                slot.MarkDirty();
                player.SendMessage(GlobalConstants.GeneralChatGroup,
                    Lang.GetL(player.LanguageCode, TailorModSystem.Domain + ":" + "loom-info-autoshuttle", TwineDisplayName(heldCode), free + 1), EnumChatType.Notification);
                Api.World.PlaySoundAt(new AssetLocation("game:sounds/player/build"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.5f);
                MarkDirty(true); // the shuttle changed even if the press / paint then fails
                return (heldCode, TwineHex(heldCode));
            }
            Shuttles.Select(i);
            return (heldCode, TwineHex(heldCode));
        }
        if (held != null) return null;
        // Empty hand (lane/loom-feedback-8 item 5): the plan's weft shuttle for this row first, else the active shuttle.
        string? code = Weaving.ResolveWeft(Shuttles);
        if (code == null) { Fail(player, "loom-err-noshuttle"); return null; }
        return (code, TwineHex(code));
    }

    void OnCopyRow(IServerPlayer player, int source)
    {
        if (Weaving.Complete) { Fail(player, "loom-err-complete"); return; }
        if (!Weaving.CopyRow(source)) return;
        MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/cloth"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.6f);
    }

    void OnWeave(IServerPlayer player)
    {
        if (Weaving.Complete) { Fail(player, "loom-err-complete"); return; }
        if (!Weaving.CurrentRowFilled)
        {
            // Draft with twine in hand (lane/shuttle-fix): name both steps, the weaver may be after a shuttle. The shed opens by
            // itself once a shuttle holds twine (lane/loom-feedback-8), so an empty hand without shuttles is told to fill one.
            bool twine = player.InventoryManager.ActiveHotbarSlot?.Itemstack is { } held && LoomTwine.IsTwine(held);
            Fail(player, twine ? "loom-err-notreadle-twine" : Shuttles.Contents().Any() ? "loom-err-notreadle" : "loom-err-noshuttle");
            return;
        }

        // Free: the whole cloth is charged when it is cut off (ClothCost).
        Weaving.CommitCurrentRow();
        Weaving.AutoSelectShuttle(Shuttles); // guided weaving: the next row's planned weft
        SyncWeftToShuttle(); // an auto-pressed next treadle weaves with the active shuttle
        MarkDirty(true);

        // One weaving cycle per beat (reference loom's cycle sound); a beat during a running cycle only clacks. A 32-wide cloth
        // plays the half cycle (lane/loom-feedback-6): its sound is cut short on each client instead (PlayCycleClient).
        if (StartCycleServer()) { if (!HalfCycle(Weaving.Width)) Api.World.PlaySoundAt(CycleSound, Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, false, 16, 1f); }
        else Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/stickhit"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.6f);
    }

    /// <summary>Roll back the last woven row: it becomes the current row again (free: twine is charged at the cut).</summary>
    void OnUnweave(IServerPlayer player)
    {
        if (!Weaving.Unweave(out _)) { Fail(player, "loom-err-nothingwoven"); return; }
        Weaving.AutoSelectShuttle(Shuttles); // the plan steps back with the row
        MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/loosestick"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.7f);
    }

    /// <summary>
    /// Draft, open shed (lane/shuttle-fix, owner): the unbeaten row follows the active shuttle at once (no new treadle
    /// press needed). Not after a roll back: the rolled-back row keeps its recorded weft.
    /// </summary>
    void SyncWeftToShuttle()
    {
        if (Shuttles.SelectedCode is not { } code) return;
        int idx = Weaving.PaletteIndexFor(code, TwineHex(code));
        if (idx >= 0) Weaving.ChangeWeft((byte)idx);
    }

    /// <summary>Shuttle: twine + click winds one twine on (sneak: the stack); empty hand selects it (sneak: takes the twine back).</summary>
    void OnShuttle(IServerPlayer player, int spool, ShuttleAction action)
    {
        if (spool < 0 || spool >= Shuttles.Count) return;
        ItemSlot? slot = player.InventoryManager.ActiveHotbarSlot;
        ItemStack? held = slot?.Itemstack;
        switch (action)
        {
            case ShuttleAction.LoadOne:
            case ShuttleAction.LoadStack:
            {
                if (slot == null || held == null || !LoomTwine.IsTwine(held)) return;
                string code = held.Collectible.Code.ToString();
                if (Shuttles.CodeAt(spool) is { } other && other != code) { Fail(player, "loom-err-spoolother", TwineDisplayName(other)); return; }
                int want = action == ShuttleAction.LoadStack ? held.StackSize : 1;
                int added = Shuttles.Load(spool, code, want);
                if (added <= 0) { Fail(player, "loom-err-spoolfull", SpoolRack.Capacity); return; }
                slot.TakeOut(added);
                Weaving.ShuttleLoaded(Shuttles, spool); // the plan's weft shuttle wins, else the first filled one is active (item 5)
                SyncWeftToShuttle();
                slot.MarkDirty();
                break;
            }
            case ShuttleAction.Select:
                if (held != null) return;
                if (!Shuttles.Select(spool)) { Fail(player, "loom-err-spoolempty"); return; }
                SyncWeftToShuttle(); // an open shed takes the new weft at once (lane/shuttle-fix)
                break;
            case ShuttleAction.Unload:
            {
                if (held != null) return;
                var (code, amount) = Shuttles.Unload(spool);
                if (code == null) return;
                GiveTwine(player, code, amount);
                break;
            }
            default:
                return;
        }
        MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/player/build"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.5f);
    }

    /// <summary>
    /// Warp thread at the back (lane/warp-shuttles-import): the held twine goes onto the clicked thread (brush span) or, with
    /// sneak, onto every thread (<see cref="WeavingState.WarpTargets"/>). Draft mode, free warp only. Nothing is consumed:
    /// the cut charges the cloth's twine by colour share (warp cells included), as before.
    /// </summary>
    void OnWarpThread(IServerPlayer player, int end, bool all)
    {
        ItemSlot? slot = player.InventoryManager.ActiveHotbarSlot;
        ItemStack? held = slot?.Itemstack;
        if (slot == null || held == null || !LoomTwine.IsTwine(held)) return;
        string code = held.Collectible.Code.ToString();
        // The thread's own twine (lane/loom-feedback-8 item 4): stock on the warp beam (one; sneak: the stack), any time.
        if (Weaving.WarpClickStocks(end, CollectibleBehaviorLoomBrush.BrushSize(player), code, all))
        {
            int added = WarpStock.Add(code, all ? held.StackSize : 1);
            if (added <= 0) { Fail(player, "loom-err-warpstockfull", TwineDisplayName(code), TwineStock.CapacityPerCode); return; }
            slot.TakeOut(added);
            slot.MarkDirty();
            MarkDirty(true);
            Api.World.PlaySoundAt(new AssetLocation("game:sounds/player/build"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.4f);
            return;
        }
        if (Weaving.WarpLocked) { Fail(player, "loom-err-draftpaint"); return; }
        if (Weaving.Complete) { Fail(player, "loom-err-complete"); return; }
        int idx = Weaving.PaletteIndexFor(code, TwineColors.HexOf(held));
        if (idx < 0) { Fail(player, "loom-err-palettefull"); return; }
        int n = Weaving.LoadWarpThreads(end, CollectibleBehaviorLoomBrush.BrushSize(player), (byte)idx, all);
        if (n < 0) return;
        if (n == 0) { Fail(player, "loom-err-warpnone", TwineDisplayName(code)); return; }
        MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/player/build"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.4f);
    }

    /// <summary>
    /// Warp thread, sneak + empty hand (lane/shuttle-fix): back to plain flax (brush span). Free warp only (no row woven).
    /// Loading took no twine, so nothing is given back. A thread whose colour has stock on the warp beam (lane/loom-feedback-8)
    /// gives that stock back first (any time); the next click clears it.
    /// </summary>
    void OnClearWarpThread(IServerPlayer player, int end)
    {
        if (player.InventoryManager.ActiveHotbarSlot?.Itemstack != null) return;
        if (end >= 0 && end < Weaving.Width && Weaving.WarpCode(end) is var stocked && WarpStock.Count(stocked) > 0)
        {
            GiveTwine(player, stocked, WarpStock.TakeAll(stocked));
            MarkDirty(true);
            Api.World.PlaySoundAt(new AssetLocation("game:sounds/player/build"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.4f);
            return;
        }
        if (Weaving.WarpLocked) { Fail(player, "loom-err-draftpaint"); return; }
        if (Weaving.ClearWarpThreads(end, CollectibleBehaviorLoomBrush.BrushSize(player), all: false) <= 0) return;
        MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/loosestick"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.4f);
    }

    /// <summary>Draft: press a treadle; the weft is the active shuttle (twine in hand activates its shuttle first).</summary>
    void OnPressTreadle(IServerPlayer player, int treadle)
    {
        if (!Weaving.DraftMode) return;
        if (Weaving.Complete) { Fail(player, "loom-err-complete"); return; }
        if (PaintSource(player) is not { } source) return;
        int idx = Weaving.PaletteIndexFor(source.code, source.hex);
        if (idx < 0) { Fail(player, "loom-err-palettefull"); return; }
        switch (Weaving.PressTreadle(treadle, (byte)idx))
        {
            case WeavingState.TreadleResult.Ok:
                MarkDirty(true);
                Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/loosestick"), Pos.X + 0.5, Pos.Y + 0.3, Pos.Z + 0.5, null, true, 16, 0.4f);
                break;
            case WeavingState.TreadleResult.NoShed:
                Fail(player, "loom-err-noshed", treadle + 1);
                break;
        }
    }

    /// <summary>
    /// Tie-up and treadling: anytime between rows (draft weaving only since lane/loom-roles: the packet's mode int is ignored); threading only with no row woven (lane/loom-feedback-8).
    /// <paramref name="picked"/>: bit 0 = a threading was clicked, bit 1 = a tie-up (replaces an imported draft's own, -1 = none).
    /// </summary>
    void OnSetDraft(IServerPlayer player, int threading, int tieUp, int treadling, int picked = 0)
    {
        if (treadling < 0) treadling = Weaving.Draft.Treadling; // an old client sends 3 ints
        if (picked < 0) picked = 0;
        switch (Weaving.SetDraft(LoomMode.Draft, threading, tieUp, treadling, (picked & 1) != 0, (picked & 2) != 0))
        {
            case WeavingState.DraftResult.ThreadingLocked:
                Fail(player, "loom-err-warpset");
                return;
            case WeavingState.DraftResult.Invalid:
                return;
        }
        MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/cloth"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.6f);
    }

    /// <summary>Server: start the next cloth (after a cut, or on load of an empty loom) at <see cref="NextClothSize"/>; the warp colours stay (resampled).</summary>
    void StartNextSquareCloth() => Weaving.StartNextCloth(NextClothSize);

    public enum ImportOutcome { Ok, Rows, Size }

    /// <summary>
    /// Server (chat command <c>/actailor draft &lt;code&gt;</c>, lane/warp-shuttles-import): put an imported draft on this
    /// loom. A free warp first takes the world's cloth size (the plan's size was checked against it). The shuttle of the
    /// first row's weft becomes active when one holds it.
    /// </summary>
    public ImportOutcome ImportPlan(DraftPlan plan)
    {
        if (Weaving.WovenRows == 0 && Weaving.Width != plan.Size && WeavingState.IsAllowedSize(plan.Size))
            Weaving.StartNextCloth(plan.Size);
        var result = Weaving.ApplyPlan(plan);
        if (result != WeavingState.ImportResult.Ok) return (ImportOutcome)(int)result;
        Weaving.AutoSelectShuttle(Shuttles);
        OpenDefaultShed(); // row 1 of the plan: its treadle pressed (item 6)
        MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/cloth"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.8f);
        return ImportOutcome.Ok;
    }

    /// <summary>Client (draft window, lane/draft-window): send the whole plan as one designer code.</summary>
    public void SendPlan(string code)
        => (Api as ICoreClientAPI)?.Network.SendBlockEntityPacket(Pos, (int)LoomPacket.SetPlan, Encoding.UTF8.GetBytes(code));

    /// <summary>Longest code accepted (a 32 x 32 code with 6 treadles is 101 characters).</summary>
    const int MaxPlanCodeLength = 512;

    /// <summary>Client (draft window, lane/loom-live-draft): roll back the last woven row (the beater's sneak click).</summary>
    public void SendUnweaveRow() => SendPacket(LoomPacket.UnweaveRow, Weaving.WovenRows);

    /// <summary>
    /// Server: a live edit of the draft window (lane/loom-live-draft; was Apply): the whole plan as one code, validated like
    /// <c>/actailor draft</c> but without the no-shed check (<see cref="DraftWindowRules.Parse"/> live), applied between any rows
    /// (<see cref="DraftWindowRules.Apply"/>) and broadcast with the next sync. A weft colour the edit brings in that no shuttle
    /// holds goes onto a free shuttle (<see cref="AutoLoadShuttles"/>).
    /// </summary>
    void OnSetPlan(IServerPlayer player, byte[]? data)
    {
        if (data == null || data.Length == 0 || data.Length > MaxPlanCodeLength) return;
        string code = Encoding.UTF8.GetString(data);
        var plan = DraftWindowRules.Parse(Weaving, code, out var error, live: true);
        if (plan == null) { Fail(player, error?.Key ?? "draft-err-range", error?.Args ?? Array.Empty<object>()); return; }
        var weftBefore = DraftWindowRules.WeftBefore(Weaving);
        if (DraftWindowRules.Apply(Weaving, plan) is var result && result != DraftWindowRules.Result.Ok)
        {
            Fail(player, result == DraftWindowRules.Result.ThreadingLocked ? "draftwin-err-threadinglocked" : "draft-err-loomsize", plan.Size, Weaving.Width);
            return;
        }
        AutoLoadShuttles(player, DraftWindowRules.NewWeftColours(weftBefore, plan.Weft, Weaving.WovenRows));
        Weaving.AutoSelectShuttle(Shuttles);
        SyncWeftToShuttle();
        MarkDirty(true);
    }

    /// <summary>
    /// Server (live draft item 4): each of <paramref name="colours"/> that no shuttle holds is wound (one twine, taken from the
    /// player's hotbar or backpack) onto a free shuttle. Nothing is taken without that twine, and when all shuttles hold other
    /// twine the player is told once; the plan keeps the colour either way (the weaver can free a shuttle later).
    /// </summary>
    void AutoLoadShuttles(IServerPlayer player, List<string> colours)
    {
        var loaded = new List<string>();
        foreach (string code in colours)
        {
            if (Shuttles.IndexOf(code) >= 0) continue;
            if (CountInInventory(player, code) < 1) { Fail(player, "draftwin-err-shuttle-notwine", TwineDisplayName(code)); continue; }
            var (free, added) = Shuttles.LoadIntoFree(code, 1);
            if (free < 0) { Fail(player, "loom-err-shuttlesfull", TwineDisplayName(code), SpoolRack.ShuttleCount); break; }
            TakeFromInventory(player, code, added);
            loaded.Add(TwineDisplayName(code) + " (" + (free + 1) + ")");
        }
        if (loaded.Count == 0) return;
        player.SendMessage(GlobalConstants.GeneralChatGroup,
            Lang.GetL(player.LanguageCode, TailorModSystem.Domain + ":draftwin-info-shuttles", string.Join(", ", loaded)), EnumChatType.Notification);
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/player/build"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.5f);
    }

    /// <summary>Client (draft window, warp sync): load the twine of designer palette index <paramref name="paletteIdx"/> onto ends from..to.</summary>
    public void SendLoadWarp(int from, int to, int paletteIdx)
    {
        var data = new byte[12];
        BitConverter.GetBytes(from).CopyTo(data, 0);
        BitConverter.GetBytes(to).CopyTo(data, 4);
        BitConverter.GetBytes(paletteIdx).CopyTo(data, 8);
        (Api as ICoreClientAPI)?.Network.SendBlockEntityPacket(Pos, (int)LoomPacket.LoadWarp, data);
    }

    /// <summary>
    /// Server: a draft-window warp edit (warp sync, lane/linen-output; rules <see cref="DraftWindowRules.LoadWarp"/>): the twine
    /// must be in the player's hotbar or backpack (flax always works); nothing is taken (charged at the cut).
    /// </summary>
    void OnLoadWarp(IServerPlayer player, int from, int to, int paletteIdx)
    {
        string HexOf(string code) => Api.World.GetItem(new AssetLocation(code)) is { } item ? TwineColors.HexOf(new ItemStack(item)) : TwineColors.PlainHex;
        var result = DraftWindowRules.LoadWarp(Weaving, from, to, paletteIdx, code => CountInInventory(player, code), HexOf, out int loaded);
        int lo = Math.Min(from, to) + 1, hi = Math.Max(from, to) + 1;
        string ends = lo == hi ? lo.ToString() : lo + "-" + hi;
        switch (result)
        {
            case DraftWindowRules.LoadResult.Ok: break;
            case DraftWindowRules.LoadResult.WarpLocked: Fail(player, "draftwin-err-warplocked-load"); return;
            case DraftWindowRules.LoadResult.Complete: Fail(player, "loom-err-complete"); return;
            case DraftWindowRules.LoadResult.MissingTwine:
                Fail(player, "draftwin-err-warpnotwine", TwineDisplayName(DraftCode.PaletteCodes[paletteIdx]), ends);
                return;
            case DraftWindowRules.LoadResult.PaletteFull: Fail(player, "loom-err-palettefull"); return;
            default: return;
        }
        Weaving.AutoSelectShuttle(Shuttles);
        MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/player/build"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 0.4f);
    }

    /// <summary>Server: drop the imported plan (<c>/actailor draft clear</c>); false when there was none.</summary>
    public bool ClearPlan()
    {
        if (!Weaving.ClearPlan()) return false;
        MarkDirty(true);
        return true;
    }

    void OnToggleUnroll()
    {
        Unrolled = !Unrolled;
        MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/cloth"), Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16, 0.8f);
    }

    /// <summary>Twine back to the player; whatever does not fit drops at their feet.</summary>
    void GiveTwine(IServerPlayer player, string code, int amount)
    {
        var item = Api.World.GetItem(new AssetLocation(code));
        if (item == null || amount <= 0) return;
        var stack = new ItemStack(item, amount);
        player.InventoryManager.TryGiveItemstack(stack, true);
        if (stack.StackSize > 0) Api.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
    }

    void OnCut(IServerPlayer player)
    {
        if (!Weaving.CanCut) { Fail(player, "loom-err-notcomplete", Weaving.WovenRows, Weaving.Rows); return; }
        ItemSlot slot = player.InventoryManager.ActiveHotbarSlot;
        if (!LoomTwine.IsCutter(slot)) return;
        // Output (lane/linen-output): woven linen with the pattern, or vanilla linen for flax only (ClothOutput, as LoomCore).
        var stack = ClothOutput.CutStack(Api.World, Weaving, flyingShuttle: true);
        if (stack == null) return;

        // Charge the cloth (ClothCost, warp and weft cells by colour share): loom stock first (shuttles, then the warp beam's
        // stock, lane/loom-feedback-8), then hotbar + backpack; nothing is taken if any twine is short. Loading warp threads took nothing.
        var cost = CutCost();
        var missing = TwineStock.Shortfall(cost, Shuttles, WarpStock, code => CountInInventory(player, code));
        if (missing.Count > 0)
        {
            string list = string.Join(", ", missing.Select(m => Lang.GetL(player.LanguageCode, TailorModSystem.Domain + ":loom-err-cutnotwine-line", TwineDisplayName(m.code), m.need, m.have)));
            Fail(player, "loom-err-cutnotwine", list);
            return;
        }
        TwineStock.Pay(cost, Shuttles, WarpStock, (code, n) => TakeFromInventory(player, code, n));

        // Whatever does not fit drops at the player's feet (lane/loom-feedback-8: a partial give left the rest of a 4-item stack unaccounted).
        player.InventoryManager.TryGiveItemstack(stack, true);
        if (stack.StackSize > 0) Api.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
        slot.Itemstack?.Collectible.DamageItem(Api.World, player.Entity, slot);
        slot.MarkDirty();

        // The next cloth (same warp width) starts with this cloth's last row, so the colours stay (lane/loom-feedback-4).
        StartNextSquareCloth();
        Weaving.AutoSelectShuttle(Shuttles); // an imported draft starts again at row 1
        Unrolled = false;
        MarkDirty(true);
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/cloth"), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, 1f);
    }

    /// <summary>Hotbar + backpack slots of the player (the inventories vanilla crafting draws from).</summary>
    static IEnumerable<ItemSlot> PlayerSlots(IPlayer player)
    {
        foreach (string inv in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
        {
            var inventory = player.InventoryManager.GetOwnInventory(inv);
            if (inventory == null) continue;
            foreach (var slot in inventory) yield return slot;
        }
    }

    static bool Matches(ItemSlot slot, string code) => slot.Itemstack?.Collectible?.Code?.ToString() == code;

    public static int CountInInventory(IPlayer player, string code)
        => PlayerSlots(player).Where(s => Matches(s, code)).Sum(s => s.StackSize);

    static void TakeFromInventory(IPlayer player, string code, int count)
    {
        foreach (var slot in PlayerSlots(player))
        {
            if (count <= 0) break;
            if (!Matches(slot, code)) continue;
            int take = Math.Min(count, slot.StackSize);
            slot.TakeOut(take);
            slot.MarkDirty();
            count -= take;
        }
    }

    /// <summary>Colour of a twine code as its item shows it (warp threads, shuttles).</summary>
    public string TwineHex(string code)
    {
        var item = Api.World.GetItem(new AssetLocation(code));
        return item != null ? TwineColors.HexOf(new ItemStack(item)) : TwineColors.PlainHex;
    }

    string TwineDisplayName(string code)
    {
        var item = Api.World.GetItem(new AssetLocation(code));
        return item != null ? item.GetHeldItemName(new ItemStack(item)) : code;
    }

    void Fail(IServerPlayer player, string langKey, params object[] args)
        => player.SendIngameError("actailor-loom", Lang.GetL(player.LanguageCode, TailorModSystem.Domain + ":" + langKey, args));

    /// <summary>Twine per code the cloth costs if cut now (ClothCost, flat per product).</summary>
    public Dictionary<string, int> CutCost() => ClothCost.TwineByCode(Weaving.CellsByCode(Weaving.WovenCells), Weaving.Product,
        flyingShuttle: true); // 12 twine per cut here (lane/cloth-cost-flat)

    /// <summary>Breaking the loom returns the shuttles' twine; the cloth in progress is lost (it was not charged yet).</summary>
    public override void OnBlockBroken(IPlayer? byPlayer = null)
    {
        SeatOnRemoved();
        base.OnBlockBroken(byPlayer);
        if (Api.Side != EnumAppSide.Server) return;
        var drops = Shuttles.Contents().Concat(WarpStock.Contents()).ToList();
        Shuttles.Clear();
        WarpStock.Clear();
        DropTwine(drops);
    }

    /// <summary>Server: twine items dropped at the loom.</summary>
    void DropTwine(IEnumerable<(string code, int amount)> drops)
    {
        foreach (var (code, count) in drops)
        {
            var item = Api.World.GetItem(new AssetLocation(code));
            if (item == null || count <= 0) continue;
            Api.World.SpawnItemEntity(new ItemStack(item, count), Pos.ToVec3d().Add(0.5, 1, 0.5));
        }
    }

    /// <summary>
    /// Server, on load: an old save's spools 5 and 6 have no shuttle (lane/warp-shuttles-import: 6 spools → 4 shuttles);
    /// their twine drops at the loom instead of vanishing.
    /// </summary>
    void DropLegacyOverflow()
    {
        if (Api?.Side != EnumAppSide.Server || legacyOverflow.Count == 0) return;
        DropTwine(legacyOverflow.ToList());
        legacyOverflow.Clear();
        MarkDirty(true);
    }

    #endregion

    #region Sync

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        var wtree = new TreeAttribute();
        Weaving.ToTree(wtree);
        tree["weaving"] = wtree;
        var stree = new TreeAttribute();
        Shuttles.ToTree(stree);
        tree["shuttles"] = stree;
        var wstree = new TreeAttribute();
        WarpStock.ToTree(wstree);
        tree["warpStock"] = wstree;
        tree.SetBool("unrolled", Unrolled);
        SeatToTree(tree);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        Weaving.FromTree(tree.GetTreeAttribute("weaving"));
        // Draft weaving only (lane/loom-roles): an old save in tapestry mode loads as draft; its woven painted rows stay
        // (a mixed cloth, cut as usual), the painted current row goes (the next row waits for a treadle).
        Weaving.ForceDraftMode();
        // Old saves (before lane/warp-shuttles-import): tree "spools" (6); spools 1-4 become the shuttles, 5-6 drop.
        if (tree.GetTreeAttribute("shuttles") is { } shuttles) Shuttles.FromTree(shuttles);
        else
        {
            legacyOverflow.Clear();
            Shuttles.FromTree(tree.GetTreeAttribute("spools"), legacyOverflow);
            if (Api != null) DropLegacyOverflow();
        }
        WarpStock.FromTree(tree.GetTreeAttribute("warpStock"));
        Unrolled = tree.GetBool("unrolled");
        SeatFromTree(tree);

        if (Api is ICoreClientAPI)
        {
            if (lastSeenRows >= 0 && Weaving.WovenRows > lastSeenRows) OnBeatSeenClient();
            UpdateShedClient();
            RebuildClothMesh();
            MarkDirty(true);
            ClientStateChanged?.Invoke();
        }
        lastSeenRows = Weaving.WovenRows;
    }

    /// <summary>Client: the synced state changed (the F picker refreshes its locked rows, lane/playtest-fixes-5).</summary>
    public event System.Action? ClientStateChanged;

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        string d = TailorModSystem.Domain + ":";
        dsc.AppendLine(Weaving.Complete
            ? Lang.Get(d + "loom-info-complete", Weaving.Width, Weaving.Rows)
            : Lang.Get(d + "loom-info-rows", Weaving.WovenRows, Weaving.Rows, Weaving.Width));
        dsc.AppendLine(Lang.Get(d + "loom-info-product", Lang.Get(d + ClothProducts.LangKey(Weaving.Product)), Weaving.Width, Weaving.Rows));
        AppendDraftInfo(dsc, d);
        if (Weaving.WovenRows == 0) dsc.AppendLine(Lang.Get(d + "loom-info-pickerhint"));
        // The world's cloth size changed under a cloth on the loom (lane/square-cloth): the next cloth takes it.
        int worldSize = ClothSizeSetting.NewClothSize(Api.World);
        if (worldSize != Weaving.Width) dsc.AppendLine(Lang.Get(d + "loom-info-worldsize", worldSize, Weaving.Width));
        if (!Weaving.Complete && !Weaving.WarpLocked) dsc.AppendLine(Lang.Get(d + "loom-info-brush", CollectibleBehaviorLoomBrush.BrushSize(forPlayer)));
        for (int i = 0; i < Shuttles.Count; i++)
        {
            if (Shuttles.CodeAt(i) is not { } code) continue;
            string line = Lang.Get(d + "loom-info-shuttleline", i + 1, TwineDisplayName(code), Shuttles.AmountAt(i));
            dsc.AppendLine(i == Shuttles.Selected ? Lang.Get(d + "loom-info-shuttleactive", line) : line);
        }
        if (Shuttles.Contents().FirstOrDefault().code == null) dsc.AppendLine(Lang.Get(d + "loom-info-noshuttle"));
        // Twine stock on the warp beam (lane/loom-feedback-8 item 4): the cut takes it after the shuttles.
        foreach (var (code, n) in WarpStock.Contents()) dsc.AppendLine(Lang.Get(d + "loom-info-warpstock", TwineDisplayName(code), n));
        var cost = CutCost();
        if (cost.Count == 0) return;
        dsc.AppendLine(Lang.Get(d + "loom-info-cost", Weaving.Width, Weaving.WovenRows));
        foreach (var (code, n) in cost.OrderBy(kv => kv.Key)) dsc.AppendLine(Lang.Get(d + "loom-info-costline", TwineDisplayName(code), n));
    }

    /// <summary>
    /// HUD, imported draft (lane/warp-shuttles-import): plan progress (row n / N, next treadle and weft), how many warp
    /// threads are loaded as the draft needs (while the warp is free or wrong), a missing weft shuttle.
    /// </summary>
    void AppendPlanInfo(StringBuilder dsc, string d)
    {
        if (Weaving.ActivePlan is not { } plan) return;
        int n = plan.Size;
        if (Weaving.Complete || Weaving.PlannedTreadle < 0) dsc.AppendLine(Lang.Get(d + "loom-info-plandone", n));
        else
        {
            string weft = Weaving.PlannedWeft is { } w ? TwineDisplayName(w) : "?";
            dsc.AppendLine(Lang.Get(d + "loom-info-plan", Weaving.WovenRows + 1, n, Weaving.PlannedTreadle + 1, weft));
            if (Shuttles.IndexOf(Weaving.PlannedWeft) < 0) dsc.AppendLine(Lang.Get(d + "loom-info-planshuttle", weft));
        }
        int ok = Weaving.WarpMatches;
        if (ok < n) dsc.AppendLine(Lang.Get(d + (Weaving.WarpLocked ? "loom-info-planwarpset" : "loom-info-planwarp"), ok, n)); // set = rows woven
    }

    /// <summary>HUD: draft presets, the open shed or what to do next (lane/draft-mode).</summary>
    void AppendDraftInfo(StringBuilder dsc, string d)
    {
        var draft = Weaving.Draft;
        string threading = draft.CustomThreading != null ? WeavingState.CustomCode : DraftPresets.Threading(draft.Threading).Code;
        string tieUp = draft.CustomTieUp != null ? WeavingState.CustomCode : DraftPresets.TieUp(draft.TieUp).Code;
        dsc.AppendLine(Lang.Get(d + "loom-info-mode-draft", Lang.Get(d + "draft-threading-" + threading), Lang.Get(d + "draft-tieup-" + tieUp)));
        if (draft.Sequence.Length > 0) dsc.AppendLine(Lang.Get(d + "loom-info-draft-treadling", Lang.Get(d + "draft-treadling-" + DraftPresets.Treadling(draft.Treadling).Code)));
        AppendPlanInfo(dsc, d);
        if (Weaving.Complete) return;
        if (draft.CurrentTreadle == DraftState.NoTreadle)
            dsc.AppendLine(Lang.Get(d + (!Weaving.WarpLocked ? "loom-info-draft-warp" : "loom-info-draft-press")));
        else
        {
            int mask = draft.LiftMask(draft.CurrentTreadle);
            string shafts = string.Join(" + ", Enumerable.Range(0, DraftPresets.Shafts).Where(s => ((mask >> s) & 1) != 0).Select(s => (s + 1).ToString()));
            string weft = draft.CurrentWeft < Weaving.PaletteCodes.Count ? TwineDisplayName(Weaving.PaletteCodes[draft.CurrentWeft]) : "?";
            dsc.AppendLine(Lang.Get(d + "loom-info-draft-shed", draft.CurrentTreadle + 1, shafts, weft));
        }
    }

    #endregion
}
