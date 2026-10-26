using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ArtsAndCrafts.Tailor.Loom;
using ArtsAndCrafts.Tailor.Tapestry;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ArtsAndCrafts.Tailor.HandLoom;

/// <summary>Client → server packets of the hand loom (block entity packet ids; row packets start with the row count the client saw).</summary>
public enum HandLoomPacket
{
    Thread = 1301,       // int row, int end, int ThreadAction
    Beat = 1302,         // int row
    RollBack = 1303,     // int row
    Copy = 1304,         // int row, int source row
    Cut = 1305,          // int row
    Rod = 1306,          // int row, int rod
    Shuttle = 1307,      // int shuttle, int HandLoomWork.ShuttleAction
    SetBrush = 1308,     // int brush mode index (F dialog)
    SetThreading = 1309, // int threading preset (F dialog)
    // 1310 was Rewarp (F dialog); retired with the re-warp step (lane/loom-feedback-8), ignored.
    SetProduct = 1311,   // int ClothProduct: what the empty loom weaves (F dialog, lane/loom-roles; LoomCore.TrySetProduct)
}

/// <summary>What a click on a warp end at the fell does (client picks it from held item, sneak and the warp state).</summary>
public enum ThreadAction { Pick = 0, LoadSpan = 1, LoadAll = 2, ClearWarp = 3, ClearPick = 4 }

/// <summary>
/// Hand loom block entity (one block since lane/loom-feedback-8, lane/hand-loom; docs/hand-loom.md). Holds a
/// <see cref="HandLoomWork"/>: warp colours, heddle rod threading, the shed of the current row, the stick shuttles and
/// the cloth. The client maps the clicked box to a packet; the server re-checks the held item and applies the rules
/// (server authority); the state syncs back through the tree attributes and persists with them. Cut via <see cref="LoomCore"/>.
/// </summary>
public class BlockEntityHandLoom : BlockEntity
{
    public readonly HandLoomWork Work = new();
    public WeavingState Cloth => Work.Cloth;

    /// <summary>Raised on the client after every sync (the F dialog recomposes).</summary>
    public event Action? ClientStateChanged;

    volatile MeshData? mesh;

    readonly object boxLock = new();
    int boxesForRows = -1, boxesForSize = -1, boxesForRods = -1, boxesForClothRows = -1;
    float boxesForRot = float.NaN;
    Cuboidf[] boxes = Array.Empty<Cuboidf>();
    HandLoomBoxPart[] parts = Array.Empty<HandLoomBoxPart>();
    readonly Dictionary<(int, int, int), Cuboidf[]> shifted = new();

    public float RotateYDeg => Block?.Shape?.rotateY ?? 0f;

    /// <summary>Heddle rods of a block type: crude 2, plank 3.</summary>
    public static int RodsOf(Block? block) => block?.Variant?["type"] == "crude" ? 2 : 3;

    public int NextClothSize => ClothSizeSetting.NewClothSize(Api?.World); // every new cloth (lane/loom-feedback-8: no set warp)

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        Work.SetRodCount(RodsOf(Block));
        // A free warp takes the world's cloth size (the setting may have changed); a set warp keeps its own.
        if (api.Side == EnumAppSide.Server && Cloth.WovenRows == 0 && Cloth.Width != NextClothSize)
        {
            Work.StartNextCloth(NextClothSize);
            SyncWeft();
            MarkDirty(true);
        }
        if (api is ICoreClientAPI) RebuildMesh();
    }

    #region Selection boxes

    public Cuboidf[] GetSelectionBoxes(Vec3i? offset)
    {
        lock (boxLock)
        {
            EnsureBoxes();
            if (offset == null || (offset.X == 0 && offset.Y == 0 && offset.Z == 0)) return boxes;
            var key = (offset.X, offset.Y, offset.Z);
            if (!shifted.TryGetValue(key, out var s))
            {
                s = boxes.Select(b => b.OffsetCopy(offset.X, offset.Y, offset.Z)).ToArray();
                shifted[key] = s;
            }
            return s;
        }
    }

    public HandLoomBoxPart PartAt(int index)
    {
        lock (boxLock)
        {
            EnsureBoxes();
            return index >= 0 && index < parts.Length ? parts[index] : new HandLoomBoxPart(HandLoomPart.Frame, 0);
        }
    }

    /// <summary>Controller-relative boxes of the current row's warp ends [start, end) (brush outline).</summary>
    public List<Cuboidf> ThreadBoxes(int start, int end)
    {
        lock (boxLock)
        {
            EnsureBoxes();
            var list = new List<Cuboidf>();
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Part == HandLoomPart.Thread && parts[i].Index >= start && parts[i].Index < end) list.Add(boxes[i]);
            return list;
        }
    }

    void EnsureBoxes()
    {
        float rot = RotateYDeg;
        if (boxesForRows == Cloth.WovenRows && boxesForSize == Cloth.Width && boxesForRot == rot && boxesForRods == Work.RodCount && boxesForClothRows == Cloth.Rows) return;
        (boxes, parts) = HandLoomGeometry.Build(Cloth.WovenRows, Cloth.Width, Work.RodCount, rot, Cloth.Rows);
        boxesForClothRows = Cloth.Rows;
        shifted.Clear();
        boxesForRows = Cloth.WovenRows;
        boxesForSize = Cloth.Width;
        boxesForRods = Work.RodCount;
        boxesForRot = rot;
    }

    #endregion

    #region Interaction (client decides, server applies)

    public static bool IsSneaking(IPlayer player) => player.Entity.Controls.ShiftKey || player.Entity.Controls.Sneak;

    /// <summary>Which held items a part reacts to (same rule on both sides).</summary>
    public static bool Handles(HandLoomPart part, BlockEntityLoom.HeldKind held) => part switch
    {
        HandLoomPart.Frame => false,
        HandLoomPart.Shuttle => held is BlockEntityLoom.HeldKind.Twine or BlockEntityLoom.HeldKind.EmptyHand,
        HandLoomPart.Rod => held is BlockEntityLoom.HeldKind.Twine or BlockEntityLoom.HeldKind.EmptyHand,
        _ => held != BlockEntityLoom.HeldKind.Other,
    };

    /// <summary>The action of a click on a warp end at the fell (pure; shared by the client, the help and the selftest).</summary>
    public static ThreadAction ThreadActionFor(BlockEntityLoom.HeldKind held, bool sneak, bool warpFree) => held switch
    {
        BlockEntityLoom.HeldKind.Twine when warpFree => sneak ? ThreadAction.LoadAll : ThreadAction.LoadSpan,
        BlockEntityLoom.HeldKind.EmptyHand when warpFree && sneak => ThreadAction.ClearWarp,
        _ => sneak ? ThreadAction.ClearPick : ThreadAction.Pick,
    };

    /// <summary>Right click (blockSel at the controller). False lets the held item do its own thing.</summary>
    public bool OnInteract(IPlayer byPlayer, BlockSelection blockSel)
    {
        var held = BlockEntityLoom.Classify(byPlayer.InventoryManager.ActiveHotbarSlot);
        var part = PartAt(blockSel.SelectionBoxIndex);
        if (!Handles(part.Part, held)) return false;
        if (Api.Side == EnumAppSide.Server) return true;
        bool sneak = IsSneaking(byPlayer);
        int row = Cloth.WovenRows;
        if (held == BlockEntityLoom.HeldKind.Cutter)
        {
            Send(HandLoomPacket.Cut, row);
            return true;
        }
        switch (part.Part)
        {
            case HandLoomPart.Thread:
                Send(HandLoomPacket.Thread, row, part.Index, (int)ThreadActionFor(held, sneak, Work.WarpFree));
                break;
            case HandLoomPart.Row:
                Send(HandLoomPacket.Copy, row, part.Index);
                break;
            case HandLoomPart.Beam:
                Send(sneak ? HandLoomPacket.RollBack : HandLoomPacket.Beat, row);
                break;
            case HandLoomPart.Rod:
                Send(HandLoomPacket.Rod, row, part.Index);
                break;
            case HandLoomPart.Shuttle:
                var action = held == BlockEntityLoom.HeldKind.Twine
                    ? (sneak ? HandLoomWork.ShuttleAction.WindStack : HandLoomWork.ShuttleAction.WindOne)
                    : (sneak ? HandLoomWork.ShuttleAction.Unwind : HandLoomWork.ShuttleAction.Select);
                Send(HandLoomPacket.Shuttle, part.Index, (int)action);
                break;
        }
        return true;
    }

    public void SendBrushMode(int mode) => Send(HandLoomPacket.SetBrush, mode);
    public void SendThreading(int threading) => Send(HandLoomPacket.SetThreading, threading);
    /// <summary>Client (F dialog, lane/loom-roles): weave this product on the empty loom.</summary>
    public void SendProduct(ClothProduct product) => Send(HandLoomPacket.SetProduct, (int)product);

    void Send(HandLoomPacket id, params int[] args)
    {
        var data = new byte[args.Length * 4];
        for (int i = 0; i < args.Length; i++) BitConverter.GetBytes(args[i]).CopyTo(data, i * 4);
        (Api as ICoreClientAPI)?.Network.SendBlockEntityPacket(Pos, (int)id, data);
    }

    static int ReadInt(byte[]? data, int index) => data != null && data.Length >= (index + 1) * 4 ? BitConverter.ToInt32(data, index * 4) : -1;

    public override void OnReceivedClientPacket(IPlayer player, int packetid, byte[] data)
    {
        if (packetid < (int)HandLoomPacket.Thread || packetid > (int)HandLoomPacket.SetProduct)
        {
            base.OnReceivedClientPacket(player, packetid, data);
            return;
        }
        if (player is not IServerPlayer sp) return;
        if (!Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.Use)) return;
        if (player.Entity.Pos.SquareDistanceTo(Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5) > LoomCore.MaxUseDistanceSq) return;
        if (packetid == 1310) return; // retired Rewarp (lane/loom-feedback-8)
        var packet = (HandLoomPacket)packetid;
        bool rowPacket = packet is not (HandLoomPacket.Shuttle or HandLoomPacket.SetBrush or HandLoomPacket.SetThreading
            or HandLoomPacket.SetProduct);
        if (rowPacket && ReadInt(data, 0) != Cloth.WovenRows) return; // stale click: the client saw another row

        switch (packet)
        {
            case HandLoomPacket.Thread: OnThread(sp, ReadInt(data, 1), (ThreadAction)ReadInt(data, 2)); break;
            case HandLoomPacket.Beat: OnBeat(sp); break;
            case HandLoomPacket.RollBack: OnRollBack(sp); break;
            case HandLoomPacket.Copy: OnCopy(sp, ReadInt(data, 1)); break;
            case HandLoomPacket.Cut: OnCut(sp); break;
            case HandLoomPacket.Rod: OnRod(sp, ReadInt(data, 1)); break;
            case HandLoomPacket.Shuttle: OnShuttle(sp, ReadInt(data, 0), (HandLoomWork.ShuttleAction)ReadInt(data, 1)); break;
            case HandLoomPacket.SetBrush: CollectibleBehaviorLoomBrush.SetMode(sp, ReadInt(data, 0)); break;
            case HandLoomPacket.SetThreading: OnSetThreading(sp, ReadInt(data, 0)); break;
            case HandLoomPacket.SetProduct:
                // Cloth / small carpet (N x N) / large carpet (N x 2N), empty loom only; kept for the next cloths.
                if (LoomCore.TrySetProduct(sp, Cloth, ReadInt(data, 0)) == LoomCore.ProductResult.Ok) { MarkDirty(true); Sound("game:sounds/block/cloth", 0.6f); }
                break;
        }
    }

    void Sound(string path, float volume) => Api.World.PlaySoundAt(new AssetLocation(path), Pos.X + 0.5, Pos.Y + 0.3, Pos.Z + 0.5, null, true, 16, volume);

    /// <summary>The current row's weft = the active stick shuttle's twine (none: no weft).</summary>
    void SyncWeft()
    {
        string? code = Work.Shuttles.SelectedCode;
        Work.SetWeft(code, code == null ? TwineColors.PlainHex : LoomCore.TwineHex(Api.World, code));
    }

    /// <summary>Server: report a refused action (true when it went through).</summary>
    bool Report(IServerPlayer player, HandLoomWork.Result result)
    {
        switch (result)
        {
            case HandLoomWork.Result.Ok: return true;
            case HandLoomWork.Result.Complete: LoomCore.Fail(player, "handloom-err-complete"); break;
            case HandLoomWork.Result.WarpSet: LoomCore.Fail(player, "handloom-err-warpset"); break;
            case HandLoomWork.Result.NoShed: LoomCore.Fail(player, "handloom-err-noshed"); break;
            case HandLoomWork.Result.NoWeft: LoomCore.Fail(player, "handloom-err-noweft"); break;
            case HandLoomWork.Result.NothingWoven: LoomCore.Fail(player, "handloom-err-nothingwoven"); break;
            case HandLoomWork.Result.PaletteFull: LoomCore.Fail(player, "handloom-err-palettefull"); break;
        }
        return false;
    }

    void OnThread(IServerPlayer player, int end, ThreadAction action)
    {
        ItemStack? held = player.InventoryManager.ActiveHotbarSlot?.Itemstack;
        int brush = CollectibleBehaviorLoomBrush.BrushSize(player);
        HandLoomWork.Result result;
        switch (action)
        {
            case ThreadAction.LoadSpan:
            case ThreadAction.LoadAll:
                if (held == null || !LoomTwine.IsTwine(held)) return;
                string code = held.Collectible.Code.ToString();
                result = Work.LoadWarp(end, brush, action == ThreadAction.LoadAll, code, TwineColors.HexOf(held));
                break;
            case ThreadAction.ClearWarp:
                if (held != null) return;
                result = Work.ClearWarp(end, brush, all: false);
                break;
            case ThreadAction.ClearPick:
                result = Work.ClearPick();
                break;
            case ThreadAction.Pick:
                result = Work.TogglePick(end);
                break;
            default:
                return;
        }
        if (!Report(player, result)) return;
        MarkDirty(true);
        Sound(action == ThreadAction.Pick ? "game:sounds/block/loosestick" : "game:sounds/player/build", 0.4f);
    }

    void OnRod(IServerPlayer player, int rod)
    {
        if (!Report(player, Work.ToggleRod(rod))) return;
        MarkDirty(true);
        Sound("game:sounds/block/loosestick", 0.6f);
    }

    void OnBeat(IServerPlayer player)
    {
        if (!Report(player, Work.Beat())) return;
        MarkDirty(true);
        Sound("game:sounds/block/stickhit", 0.7f);
    }

    void OnRollBack(IServerPlayer player)
    {
        if (!Report(player, Work.RollBack())) return;
        Work.SelectShuttleForWeft();
        MarkDirty(true);
        Sound("game:sounds/block/loosestick", 0.7f);
    }

    void OnCopy(IServerPlayer player, int source)
    {
        if (!Report(player, Work.CopyRow(source))) return;
        Work.SelectShuttleForWeft();
        MarkDirty(true);
        Sound("game:sounds/block/cloth", 0.6f);
    }

    void OnCut(IServerPlayer player)
    {
        if (LoomCore.TryCut(player, Cloth, Work.Shuttles) != LoomCore.CutResult.Ok) return;
        // The next cloth keeps the warp colours (same size while the warp is set).
        Work.StartNextCloth(NextClothSize);
        SyncWeft();
        MarkDirty(true);
        Sound("game:sounds/block/cloth", 1f);
    }

    void OnSetThreading(IServerPlayer player, int threading)
    {
        if (!Report(player, Work.SetThreading(threading))) return;
        MarkDirty(true);
    }

    void OnShuttle(IServerPlayer player, int i, HandLoomWork.ShuttleAction action)
    {
        if (i < 0 || i >= Work.Shuttles.Count) return;
        ItemSlot? slot = player.InventoryManager.ActiveHotbarSlot;
        ItemStack? held = slot?.Itemstack;
        switch (action)
        {
            case HandLoomWork.ShuttleAction.WindOne:
            case HandLoomWork.ShuttleAction.WindStack:
            {
                if (slot == null || held == null || !LoomTwine.IsTwine(held)) return;
                string code = held.Collectible.Code.ToString();
                int want = action == HandLoomWork.ShuttleAction.WindStack ? held.StackSize : 1;
                switch (Work.Wind(i, code, want, out int added))
                {
                    case HandLoomWork.ShuttleResult.OtherTwine:
                        LoomCore.Fail(player, "handloom-err-shuttleother", LoomCore.TwineName(Api.World, Work.Shuttles.CodeAt(i)!)); return;
                    case HandLoomWork.ShuttleResult.Full:
                        LoomCore.Fail(player, "handloom-err-shuttlefull", SpoolRack.Capacity); return;
                    case HandLoomWork.ShuttleResult.Invalid: return;
                }
                slot.TakeOut(added);
                slot.MarkDirty();
                break;
            }
            case HandLoomWork.ShuttleAction.Select:
                if (held != null) return;
                if (Work.Select(i) != HandLoomWork.ShuttleResult.Ok) { LoomCore.Fail(player, "handloom-err-shuttleempty"); return; }
                break;
            case HandLoomWork.ShuttleAction.Unwind:
            {
                if (held != null) return;
                var (code, amount) = Work.Unwind(i);
                if (code == null) return;
                LoomCore.GiveTwine(player, code, amount);
                break;
            }
            default:
                return;
        }
        SyncWeft();
        MarkDirty(true);
        Sound("game:sounds/player/build", 0.5f);
    }

    /// <summary>Breaking the loom gives the shuttles' twine back; the cloth in progress is lost (it was not charged yet).</summary>
    public override void OnBlockBroken(IPlayer? byPlayer = null)
    {
        base.OnBlockBroken(byPlayer);
        if (Api.Side != EnumAppSide.Server) return;
        var drops = Work.Shuttles.Contents().ToList();
        Work.Shuttles.Clear();
        LoomCore.DropTwine(Api.World, Pos.ToVec3d().Add(0.5, 0.3, 0.5), drops);
    }

    #endregion

    #region Sync, mesh, info

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        var t = new TreeAttribute();
        Work.ToTree(t);
        tree["handloom"] = t;
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        if (Block != null) Work.SetRodCount(RodsOf(Block));
        Work.FromTree(tree.GetTreeAttribute("handloom"));
        if (Api is ICoreClientAPI)
        {
            RebuildMesh();
            MarkDirty(true);
            ClientStateChanged?.Invoke();
        }
    }

    void RebuildMesh()
    {
        if (Api is not ICoreClientAPI capi || Block == null) return;
        mesh = HandLoomMesh.Build(capi, Block, Work, code => LoomCore.TwineHex(capi.World, code));
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        if (mesh is { } m) mesher.AddMeshData(m);
        return false; // the frame: the block's own shape
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        string d = TailorModSystem.Domain + ":";
        dsc.AppendLine(Cloth.Complete
            ? Lang.Get(d + "handloom-info-complete", Cloth.Width, Cloth.Rows)
            : Lang.Get(d + "handloom-info-rows", Cloth.WovenRows, Cloth.Rows, Cloth.Width));
        dsc.AppendLine(Lang.Get(d + "loom-info-product", Lang.Get(d + ClothProducts.LangKey(Cloth.Product)), Cloth.Width, Cloth.Rows));
        if (Cloth.WovenRows == 0) dsc.AppendLine(Lang.Get(d + "handloom-info-producthint"));
        int wound = HandLoomGeometry.ViewStart(Cloth.WovenRows, Cloth.Rows, Cloth.Width);
        if (wound > 0) dsc.AppendLine(Lang.Get(d + "handloom-info-wound", wound));
        dsc.AppendLine(Lang.Get(d + "handloom-info-threading", Lang.Get(d + "handloom-threading-" + HandLoomPresets.Threadings[Work.Threading].Code), Work.RodCount));
        dsc.AppendLine(Lang.Get(d + (Work.WarpFree ? "handloom-info-warpfree" : "handloom-info-warpset")));
        int worldSize = ClothSizeSetting.NewClothSize(Api.World);
        if (worldSize != Cloth.Width) dsc.AppendLine(Lang.Get(d + "handloom-info-worldsize", worldSize, Cloth.Width));
        if (!Cloth.Complete)
        {
            var lifted = Enumerable.Range(0, Work.RodCount).Where(Work.RodLifted).Select(r => (r + 1).ToString()).ToList();
            dsc.AppendLine(lifted.Count == 0 ? Lang.Get(d + "handloom-info-norods") : Lang.Get(d + "handloom-info-rods", string.Join(" + ", lifted)));
            if (Work.PickUsed) dsc.AppendLine(Lang.Get(d + "handloom-info-pick", BitCount(Work.PickUp), BitCount(Work.PickDown)));
            dsc.AppendLine(Lang.Get(d + "handloom-info-shed", Work.UpCount, Cloth.Width));
        }
        for (int i = 0; i < Work.Shuttles.Count; i++)
        {
            string line = Work.Shuttles.CodeAt(i) is { } code
                ? Lang.Get(d + "handloom-info-shuttle", i + 1, LoomCore.TwineName(Api.World, code), Work.Shuttles.AmountAt(i))
                : Lang.Get(d + "handloom-info-shuttleempty", i + 1);
            dsc.AppendLine(i == Work.Shuttles.Selected && Work.Shuttles.CodeAt(i) != null ? Lang.Get(d + "handloom-info-shuttleactive", line) : line);
        }
        if (Work.WeftCode is { } weft && !Cloth.Complete) dsc.AppendLine(Lang.Get(d + "handloom-info-weft", LoomCore.TwineName(Api.World, weft)));
        var cost = Work.CutCost();
        if (cost.Count == 0) return;
        dsc.AppendLine(Lang.Get(d + "handloom-info-cost", Cloth.Width, Cloth.WovenRows));
        foreach (var (code, n) in cost.OrderBy(kv => kv.Key)) dsc.AppendLine(Lang.Get(d + "handloom-info-costline", LoomCore.TwineName(Api.World, code), n));
    }

    static int BitCount(uint v) => System.Numerics.BitOperations.PopCount(v);

    #endregion
}
