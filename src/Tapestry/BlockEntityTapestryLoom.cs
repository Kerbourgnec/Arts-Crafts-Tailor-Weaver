using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ArtsAndCrafts.Tailor.Loom;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ArtsAndCrafts.Tailor.Tapestry;

/// <summary>Client → server packets of the tapestry loom (block entity packet ids; row packets start with the row count the client saw).</summary>
public enum TapestryPacket
{
    Paint = 1201,    // int row, int cell
    Fill = 1202,     // int row
    Beat = 1203,     // int row
    RollBack = 1204, // int row
    Copy = 1205,     // int row, int source row
    Cut = 1206,      // int row
    Bobbin = 1207,   // int bobbin, int TapestryWork.BobbinAction
    SetBrush = 1208, // int brush mode index (empty-hand F dialog)
    SetProduct = 1209, // int ClothProduct (empty-hand F dialog on an empty frame, lane/linen-output)
    EmbMount = 1210,   // (held cloth) put it on the empty frame (lane/loom-embroidery)
    EmbPaint = 1211,   // int cell (y * size + x), int pick out (0/1)
    EmbTakeOff = 1212, // take the embroidered cloth off (cutter or empty hand)
}

/// <summary>
/// Tapestry loom block entity (controller of the 2x2x1 multiblock, lane/tapestry-loom; docs/tapestry-loom.md). Holds a
/// <see cref="TapestryWork"/>: the square cloth painted row by row (bottom → top) and 6 bobbins. The client maps the
/// clicked selection box to a part and sends a packet; the server re-checks the held item and applies the rules
/// (server authority); the state syncs back through the tree attributes and persists with them.
/// </summary>
public class BlockEntityTapestryLoom : BlockEntity
{
    public readonly TapestryWork Work = new();
    public WeavingState Cloth => Work.Cloth;

    volatile MeshData? clothMesh;

    readonly object boxLock = new();
    int boxesForRows = -1, boxesForSize = -1, boxesForClothRows = -1, boxesForHoop = -1;
    float boxesForRot = float.NaN;
    Cuboidf[] boxes = Array.Empty<Cuboidf>();
    TapestryBoxPart[] parts = Array.Empty<TapestryBoxPart>();
    readonly Dictionary<(int, int, int), Cuboidf[]> shifted = new();

    public float RotateYDeg => Block?.Shape?.rotateY ?? 0f;

    /// <summary>Size of the next cloth: the world setting (16 or 32; lane/square-cloth).</summary>
    public int NextClothSize => ClothSizeSetting.NewClothSize(Api?.World);

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        // An empty frame takes the world's cloth size (the setting may have changed); a started cloth keeps its own.
        if (api.Side == EnumAppSide.Server && Cloth.WovenRows == 0 && Cloth.Width != NextClothSize)
        {
            Work.StartNextCloth(NextClothSize);
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

    public TapestryBoxPart PartAt(int index)
    {
        lock (boxLock)
        {
            EnsureBoxes();
            return index >= 0 && index < parts.Length ? parts[index] : new TapestryBoxPart(TapestryPart.Frame, 0);
        }
    }

    /// <summary>Controller-relative boxes of the current row's cells [start, end).</summary>
    public List<Cuboidf> CellBoxes(int start, int end)
    {
        lock (boxLock)
        {
            EnsureBoxes();
            var list = new List<Cuboidf>();
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Part == TapestryPart.Cell && parts[i].Index >= start && parts[i].Index < end) list.Add(boxes[i]);
            return list;
        }
    }

    void EnsureBoxes()
    {
        float rot = RotateYDeg;
        int hoop = Work.Hoop?.Size ?? 0;
        if (boxesForRows == Cloth.WovenRows && boxesForSize == Cloth.Width && boxesForClothRows == Cloth.Rows && boxesForRot == rot && boxesForHoop == hoop) return;
        (boxes, parts) = hoop > 0 ? TapestryGeometry.BuildEmbroidery(hoop, rot) : TapestryGeometry.Build(Cloth.WovenRows, Cloth.Width, rot, Cloth.Rows);
        boxesForHoop = hoop;
        boxesForClothRows = Cloth.Rows;
        shifted.Clear();
        boxesForRows = Cloth.WovenRows;
        boxesForSize = Cloth.Width;
        boxesForRot = rot;
    }

    #endregion

    #region Interaction (client decides, server applies)

    public static bool IsSneaking(IPlayer player) => player.Entity.Controls.ShiftKey || player.Entity.Controls.Sneak;

    /// <summary>Which held items a part reacts to (same rule on both sides, so the server claims what the client sends).</summary>
    public static bool Handles(TapestryPart part, BlockEntityLoom.HeldKind held) => part switch
    {
        TapestryPart.Frame => false,
        TapestryPart.Bobbin => held is BlockEntityLoom.HeldKind.Twine or BlockEntityLoom.HeldKind.EmptyHand,
        _ => held != BlockEntityLoom.HeldKind.Other,
    };

    /// <summary>Controller-relative boxes of the embroidery cells with these indices (brush outline).</summary>
    public List<Cuboidf> EmbCellBoxes(ISet<int> cells)
    {
        lock (boxLock)
        {
            EnsureBoxes();
            var list = new List<Cuboidf>();
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Part == TapestryPart.EmbCell && cells.Contains(parts[i].Index)) list.Add(boxes[i]);
            return list;
        }
    }

    /// <summary>A stack that can go on the frame for embroidery: woven cloth / linen, vanilla linen or cloth (lane/loom-embroidery).</summary>
    public static bool IsMountable(ItemStack? stack)
        => stack?.Collectible is ItemWovenCloth || (stack?.Collectible?.Code is { } c && EmbroideryHoop.VanillaBase(c.ToString()) != null);

    /// <summary>What a click does in embroidery terms (both sides decide the same).</summary>
    public enum EmbAction { Mount, Paint, PickOut, TakeOff, PassOn }

    /// <summary>
    /// Embroidery rules (lane/loom-embroidery, docs/embroidery.md): a cloth in hand on an empty frame (cell, row or beam) goes
    /// on it; with a cloth on: cell + twine / empty hand = stitch (sneak: pick out), cutter on a cell or beam or empty hand
    /// on a beam = take off; bobbins as usual (<see cref="EmbAction.PassOn"/>). Null = not an embroidery click.
    /// </summary>
    public static EmbAction? EmbRule(bool hoop, bool canMount, TapestryPart part, BlockEntityLoom.HeldKind held, bool mountable, bool sneak)
    {
        if (!hoop)
            return canMount && mountable && part is TapestryPart.Cell or TapestryPart.Row or TapestryPart.Beam ? EmbAction.Mount : null;
        return part switch
        {
            TapestryPart.EmbCell when held == BlockEntityLoom.HeldKind.Cutter => EmbAction.TakeOff,
            TapestryPart.EmbCell when held is BlockEntityLoom.HeldKind.Twine or BlockEntityLoom.HeldKind.EmptyHand => sneak ? EmbAction.PickOut : EmbAction.Paint,
            TapestryPart.Beam when held is BlockEntityLoom.HeldKind.Cutter or BlockEntityLoom.HeldKind.EmptyHand => EmbAction.TakeOff,
            TapestryPart.Bobbin => EmbAction.PassOn,
            _ => null,
        };
    }

    /// <summary>Right click (blockSel at the controller). False lets the held item do its own thing.</summary>
    public bool OnInteract(IPlayer byPlayer, BlockSelection blockSel)
    {
        var slot = byPlayer.InventoryManager.ActiveHotbarSlot;
        var held = BlockEntityLoom.Classify(slot);
        var part = PartAt(blockSel.SelectionBoxIndex);
        var emb = EmbRule(Work.Hoop != null, Work.CanMount, part.Part, held, IsMountable(slot?.Itemstack), IsSneaking(byPlayer));
        if (emb is { } ea && ea != EmbAction.PassOn)
        {
            if (Api.Side == EnumAppSide.Server) return true;
            switch (ea)
            {
                case EmbAction.Mount: Send(TapestryPacket.EmbMount); break;
                case EmbAction.Paint: Send(TapestryPacket.EmbPaint, part.Index, 0); break;
                case EmbAction.PickOut: Send(TapestryPacket.EmbPaint, part.Index, 1); break;
                case EmbAction.TakeOff: Send(TapestryPacket.EmbTakeOff); break;
            }
            return true;
        }
        if (Work.Hoop != null && emb == null) return false; // the weaving waits while a cloth is on the frame
        if (!Handles(part.Part, held)) return false;
        if (Api.Side == EnumAppSide.Server) return true;
        bool sneak = IsSneaking(byPlayer);
        int row = Cloth.WovenRows;
        if (held == BlockEntityLoom.HeldKind.Cutter)
        {
            Send(TapestryPacket.Cut, row);
            return true;
        }
        switch (part.Part)
        {
            case TapestryPart.Cell:
                if (sneak) Send(TapestryPacket.Fill, row);
                else Send(TapestryPacket.Paint, row, part.Index);
                break;
            case TapestryPart.Row:
                if (sneak) Send(TapestryPacket.Fill, row);
                else Send(TapestryPacket.Copy, row, part.Index);
                break;
            case TapestryPart.Beam:
                Send(sneak ? TapestryPacket.RollBack : TapestryPacket.Beat, row);
                break;
            case TapestryPart.Bobbin:
                var action = held == BlockEntityLoom.HeldKind.Twine
                    ? (sneak ? TapestryWork.BobbinAction.WindStack : TapestryWork.BobbinAction.WindOne)
                    : (sneak ? TapestryWork.BobbinAction.Unwind : TapestryWork.BobbinAction.Select);
                Send(TapestryPacket.Bobbin, part.Index, (int)action);
                break;
        }
        return true;
    }

    /// <summary>Client: this player's brush size (the server sets the watched player attribute).</summary>
    public void SendBrushMode(int mode) => Send(TapestryPacket.SetBrush, mode);

    /// <summary>Client (F picker on an empty frame, lane/linen-output): weave this product.</summary>
    public void SendProduct(ClothProduct product) => Send(TapestryPacket.SetProduct, (int)product);

    void Send(TapestryPacket id, params int[] args)
    {
        var data = new byte[args.Length * 4];
        for (int i = 0; i < args.Length; i++) BitConverter.GetBytes(args[i]).CopyTo(data, i * 4);
        (Api as ICoreClientAPI)?.Network.SendBlockEntityPacket(Pos, (int)id, data);
    }

    static int ReadInt(byte[]? data, int index) => data != null && data.Length >= (index + 1) * 4 ? BitConverter.ToInt32(data, index * 4) : -1;

    public override void OnReceivedClientPacket(IPlayer player, int packetid, byte[] data)
    {
        if (packetid < (int)TapestryPacket.Paint || packetid > (int)TapestryPacket.EmbTakeOff)
        {
            base.OnReceivedClientPacket(player, packetid, data);
            return;
        }
        if (player is not IServerPlayer sp) return;
        if (!Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.Use)) return;
        if (player.Entity.Pos.SquareDistanceTo(Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5) > LoomCore.MaxUseDistanceSq) return;
        var packet = (TapestryPacket)packetid;
        bool embPacket = packet is TapestryPacket.EmbMount or TapestryPacket.EmbPaint or TapestryPacket.EmbTakeOff;
        bool rowPacket = !embPacket && packet is not (TapestryPacket.Bobbin or TapestryPacket.SetBrush or TapestryPacket.SetProduct);
        // While a cloth is on the frame the weaving waits (lane/loom-embroidery): row packets are stale.
        if (rowPacket && (Work.Hoop != null || ReadInt(data, 0) != Cloth.WovenRows)) return; // stale click: the client saw another row

        switch (packet)
        {
            case TapestryPacket.Paint: OnPaint(sp, ReadInt(data, 1), fill: false); break;
            case TapestryPacket.Fill: OnPaint(sp, 0, fill: true); break;
            case TapestryPacket.Beat: OnBeat(sp); break;
            case TapestryPacket.RollBack: OnRollBack(sp); break;
            case TapestryPacket.Copy: OnCopy(sp, ReadInt(data, 1)); break;
            case TapestryPacket.Cut: OnCut(sp); break;
            case TapestryPacket.Bobbin: OnBobbin(sp, ReadInt(data, 0), (TapestryWork.BobbinAction)ReadInt(data, 1)); break;
            case TapestryPacket.SetBrush: CollectibleBehaviorLoomBrush.SetMode(sp, ReadInt(data, 0)); break;
            case TapestryPacket.SetProduct:
                if (LoomCore.TrySetProduct(sp, Cloth, ReadInt(data, 0)) == LoomCore.ProductResult.Ok) MarkDirty(true);
                break;
            case TapestryPacket.EmbMount: OnMount(sp); break;
            case TapestryPacket.EmbPaint: OnEmbPaint(sp, ReadInt(data, 0), ReadInt(data, 1) == 1); break;
            case TapestryPacket.EmbTakeOff: OnTakeOff(sp); break;
        }
    }

    #region Embroidery (lane/loom-embroidery)

    void OnMount(IServerPlayer player)
    {
        ItemSlot? slot = player.InventoryManager.ActiveHotbarSlot;
        ItemStack? stack = slot?.Itemstack;
        if (slot == null || stack == null || !IsMountable(stack)) return;
        if (!Work.CanMount) { LoomCore.Fail(player, Work.Hoop != null ? "tapestryloom-emb-err-occupied" : "tapestryloom-emb-err-notempty"); return; }
        var result = EmbroideryHoop.TryMount(stack.Collectible.Code.ToString(), stack.Attributes, stack.Collectible is ItemWovenCloth, NextClothSize,
            code => LoomCore.TwineHex(Api.World, code), out var hoop);
        if (result == EmbroideryHoop.MountResult.Size) { LoomCore.Fail(player, "tapestryloom-emb-err-size"); return; }
        if (hoop == null) return;
        Work.Hoop = hoop;
        slot.TakeOut(1);
        slot.MarkDirty();
        MarkDirty(true);
        Sound("game:sounds/block/cloth", 0.8f);
    }

    void OnEmbPaint(IServerPlayer player, int cell, bool pickOut)
    {
        if (Work.Hoop is not { } hoop || !hoop.ValidCell(cell)) return;
        var held = BlockEntityLoom.Classify(player.InventoryManager.ActiveHotbarSlot);
        if (held is not (BlockEntityLoom.HeldKind.Twine or BlockEntityLoom.HeldKind.EmptyHand)) return;
        var (x, y) = EmbroideryHoop.CellXY(cell, hoop.Size);
        int brush = CollectibleBehaviorLoomBrush.BrushSize(player);
        Embroidery.Result r;
        if (pickOut) r = hoop.Layer.PickOut(x, y, brush);
        else
        {
            if (WeftOf(player) is not { } weft) return;
            r = hoop.Layer.Paint(x, y, brush, weft.code, weft.hex);
        }
        if (r == Embroidery.Result.PaletteFull) { LoomCore.Fail(player, "tapestryloom-err-palettefull"); return; }
        if (r != Embroidery.Result.Ok) return;
        MarkDirty(true);
        if (pickOut) Sound("game:sounds/block/loosestick", 0.4f);
    }

    void OnTakeOff(IServerPlayer player)
    {
        if (Work.Hoop is not { } hoop) return;
        ItemSlot slot = player.InventoryManager.ActiveHotbarSlot;
        var held = BlockEntityLoom.Classify(slot);
        if (held is not (BlockEntityLoom.HeldKind.Cutter or BlockEntityLoom.HeldKind.EmptyHand)) return;
        var stack = TakeOffStack(Api.World, hoop);
        if (stack == null) return;
        if (!LoomCore.TryCharge(player, hoop.Cost(), Work.Bobbins)) return;
        Work.Hoop = null;
        LoomCore.GiveOrDrop(player, stack);
        if (held == BlockEntityLoom.HeldKind.Cutter)
        {
            slot.Itemstack?.Collectible.DamageItem(Api.World, player.Entity, slot);
            slot.MarkDirty();
        }
        MarkDirty(true);
        Sound("game:sounds/block/cloth", 1f);
    }

    static CollectibleObject? Collectible(IWorldAccessor world, string code)
    {
        var loc = new AssetLocation(code);
        return (CollectibleObject?)world.GetItem(loc) ?? world.GetBlock(loc);
    }

    /// <summary>The cloth a take-off gives (embroidery stored apart from the weave), or null when the item is missing.</summary>
    public static ItemStack? TakeOffStack(IWorldAccessor world, EmbroideryHoop hoop)
        => Collectible(world, hoop.TakeOffCode) is { } obj ? new ItemStack(obj) { Attributes = hoop.TakeOffAttributes() } : null;

    /// <summary>The cloth as it was put on (the loom broke: the stitches made since were never charged).</summary>
    static ItemStack? SourceStack(IWorldAccessor world, EmbroideryHoop hoop)
        => Collectible(world, hoop.SourceCode) is { } obj ? new ItemStack(obj) { Attributes = hoop.SourceAttrs.Clone() } : null;

    #endregion

    void Sound(string path, float volume) => Api.World.PlaySoundAt(new AssetLocation(path), Pos.X + 0.5, Pos.Y + 1, Pos.Z + 0.5, null, true, 16, volume);

    /// <summary>Weft colour: the twine in hand (on a bobbin or not), else the active bobbin; null (with an error) for neither.</summary>
    (string code, string hex)? WeftOf(IServerPlayer player)
    {
        ItemStack? held = player.InventoryManager.ActiveHotbarSlot?.Itemstack;
        if (held != null)
        {
            if (!LoomTwine.IsTwine(held)) return null;
            string code = held.Collectible.Code.ToString();
            return (code, TwineColors.HexOf(held));
        }
        if (Work.Bobbins.SelectedCode is not { } active) { LoomCore.Fail(player, "tapestryloom-err-nobobbin"); return null; }
        return (active, LoomCore.TwineHex(Api.World, active));
    }

    void OnPaint(IServerPlayer player, int cell, bool fill)
    {
        if (Cloth.Complete) { LoomCore.Fail(player, "tapestryloom-err-complete"); return; }
        if (WeftOf(player) is not { } weft) return;
        switch (Work.Paint(cell, weft.code, weft.hex, CollectibleBehaviorLoomBrush.BrushSize(player), fill))
        {
            case TapestryWork.PaintResult.Ok: MarkDirty(true); break;
            case TapestryWork.PaintResult.PaletteFull: LoomCore.Fail(player, "tapestryloom-err-palettefull"); break;
        }
    }

    void OnBeat(IServerPlayer player)
    {
        switch (Work.Beat())
        {
            case TapestryWork.BeatResult.Complete: LoomCore.Fail(player, "tapestryloom-err-complete"); return;
            case TapestryWork.BeatResult.RowIncomplete: LoomCore.Fail(player, "tapestryloom-err-rowincomplete"); return;
        }
        MarkDirty(true);
        Sound("game:sounds/block/stickhit", 0.7f);
    }

    void OnRollBack(IServerPlayer player)
    {
        if (!Work.RollBack()) { LoomCore.Fail(player, "tapestryloom-err-nothingwoven"); return; }
        MarkDirty(true);
        Sound("game:sounds/block/loosestick", 0.7f);
    }

    void OnCopy(IServerPlayer player, int source)
    {
        if (Cloth.Complete) { LoomCore.Fail(player, "tapestryloom-err-complete"); return; }
        if (!Work.CopyRow(source)) return;
        MarkDirty(true);
        Sound("game:sounds/block/cloth", 0.6f);
    }

    void OnCut(IServerPlayer player)
    {
        if (LoomCore.TryCut(player, Cloth, Work.Bobbins) != LoomCore.CutResult.Ok) return;
        // The next cloth (world size) starts with this cloth's last row, so the colours stay.
        Work.StartNextCloth(NextClothSize);
        MarkDirty(true);
        Sound("game:sounds/block/cloth", 1f);
    }

    void OnBobbin(IServerPlayer player, int i, TapestryWork.BobbinAction action)
    {
        if (i < 0 || i >= Work.Bobbins.Count) return;
        ItemSlot? slot = player.InventoryManager.ActiveHotbarSlot;
        ItemStack? held = slot?.Itemstack;
        switch (action)
        {
            case TapestryWork.BobbinAction.WindOne:
            case TapestryWork.BobbinAction.WindStack:
            {
                if (slot == null || held == null || !LoomTwine.IsTwine(held)) return;
                string code = held.Collectible.Code.ToString();
                int want = action == TapestryWork.BobbinAction.WindStack ? held.StackSize : 1;
                switch (Work.Wind(i, code, want, out int added))
                {
                    case TapestryWork.BobbinResult.OtherTwine:
                        LoomCore.Fail(player, "tapestryloom-err-bobbinother", LoomCore.TwineName(Api.World, Work.Bobbins.CodeAt(i)!)); return;
                    case TapestryWork.BobbinResult.Full:
                        LoomCore.Fail(player, "tapestryloom-err-bobbinfull", SpoolRack.Capacity); return;
                    case TapestryWork.BobbinResult.Invalid: return;
                }
                slot.TakeOut(added);
                slot.MarkDirty();
                break;
            }
            case TapestryWork.BobbinAction.Select:
                if (held != null) return;
                if (Work.Select(i) != TapestryWork.BobbinResult.Ok) { LoomCore.Fail(player, "tapestryloom-err-bobbinempty"); return; }
                break;
            case TapestryWork.BobbinAction.Unwind:
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
        MarkDirty(true);
        Sound("game:sounds/player/build", 0.5f);
    }

    /// <summary>Breaking the loom gives the bobbins' twine back; the cloth in progress is lost (it was not charged yet).</summary>
    public override void OnBlockBroken(IPlayer? byPlayer = null)
    {
        base.OnBlockBroken(byPlayer);
        if (Api.Side != EnumAppSide.Server) return;
        var drops = Work.Bobbins.Contents().ToList();
        Work.Bobbins.Clear();
        LoomCore.DropTwine(Api.World, Pos.ToVec3d().Add(0.5, 0.5, 0.5), drops);
        // A cloth on the frame (lane/loom-embroidery) drops as it was put on.
        if (Work.Hoop is { } hoop && SourceStack(Api.World, hoop) is { } cloth) Api.World.SpawnItemEntity(cloth, Pos.ToVec3d().Add(0.5, 0.5, 0.5));
        Work.Hoop = null;
    }

    #endregion

    #region Sync, mesh, info

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        var t = new TreeAttribute();
        Work.ToTree(t);
        tree["tapestry"] = t;
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        Work.FromTree(tree.GetTreeAttribute("tapestry"));
        if (Api is ICoreClientAPI)
        {
            RebuildMesh();
            MarkDirty(true);
        }
    }

    void RebuildMesh()
    {
        if (Api is not ICoreClientAPI capi || Block == null) return;
        clothMesh = TapestryMesh.Build(capi, Block, Work, code => LoomCore.TwineHex(capi.World, code));
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        if (clothMesh is { } mesh) mesher.AddMeshData(mesh);
        return false; // the frame: the block's own shape
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        string d = TailorModSystem.Domain + ":";
        if (Work.Hoop is { } hoop)
        {
            EmbroideryInfo(forPlayer, dsc, hoop, d);
            return;
        }
        if (Work.CanMount) dsc.AppendLine(Lang.Get(d + "tapestryloom-emb-info-canmount"));
        dsc.AppendLine(Cloth.Complete
            ? Lang.Get(d + "tapestryloom-info-complete", Cloth.Width, Cloth.Rows)
            : Lang.Get(d + "tapestryloom-info-rows", Cloth.WovenRows, Cloth.Rows, Cloth.Width));
        dsc.AppendLine(Lang.Get(d + "loom-info-product", Lang.Get(d + ClothProducts.LangKey(Cloth.Product)), Cloth.Width, Cloth.Rows));
        if (Cloth.WovenRows == 0) dsc.AppendLine(Lang.Get(d + "tapestryloom-info-producthint"));
        if (!Cloth.Complete) dsc.AppendLine(Lang.Get(d + "tapestryloom-info-brush", CollectibleBehaviorLoomBrush.BrushSize(forPlayer)));
        int worldSize = ClothSizeSetting.NewClothSize(Api.World);
        if (worldSize != Cloth.Width) dsc.AppendLine(Lang.Get(d + "tapestryloom-info-worldsize", worldSize, Cloth.Width));
        bool any = false;
        for (int i = 0; i < Work.Bobbins.Count; i++)
        {
            if (Work.Bobbins.CodeAt(i) is not { } code) continue;
            any = true;
            string line = Lang.Get(d + "tapestryloom-info-bobbin", i + 1, LoomCore.TwineName(Api.World, code), Work.Bobbins.AmountAt(i));
            dsc.AppendLine(i == Work.Bobbins.Selected ? Lang.Get(d + "tapestryloom-info-bobbinactive", line) : line);
        }
        if (!any) dsc.AppendLine(Lang.Get(d + "tapestryloom-info-nobobbin"));
        var cost = Work.CutCost();
        if (cost.Count == 0) return;
        dsc.AppendLine(Lang.Get(d + "tapestryloom-info-cost", Cloth.Width, Cloth.WovenRows));
        foreach (var (code, n) in cost.OrderBy(kv => kv.Key)) dsc.AppendLine(Lang.Get(d + "tapestryloom-info-costline", LoomCore.TwineName(Api.World, code), n));
    }

    void EmbroideryInfo(IPlayer forPlayer, StringBuilder dsc, EmbroideryHoop hoop, string d)
    {
        var obj = Collectible(Api.World, hoop.SourceCode);
        string name = obj != null ? obj.GetHeldItemName(new ItemStack(obj)) : hoop.SourceCode;
        dsc.AppendLine(Lang.Get(d + "tapestryloom-emb-info-cloth", name, hoop.Size, hoop.Size));
        dsc.AppendLine(Lang.Get(d + "tapestryloom-emb-info-stitches", hoop.Layer.StitchCount, hoop.StitchCodes().Length));
        dsc.AppendLine(Lang.Get(d + "tapestryloom-info-brush", CollectibleBehaviorLoomBrush.BrushSize(forPlayer)));
        var cost = hoop.Cost();
        if (cost.Count == 0) dsc.AppendLine(Lang.Get(d + "tapestryloom-emb-info-free"));
        else
        {
            dsc.AppendLine(Lang.Get(d + "tapestryloom-emb-info-cost"));
            foreach (var (code, n) in cost.OrderBy(kv => kv.Key)) dsc.AppendLine(Lang.Get(d + "tapestryloom-info-costline", LoomCore.TwineName(Api.World, code), n));
        }
        for (int i = 0; i < Work.Bobbins.Count; i++)
        {
            if (Work.Bobbins.CodeAt(i) is not { } code) continue;
            string line = Lang.Get(d + "tapestryloom-info-bobbin", i + 1, LoomCore.TwineName(Api.World, code), Work.Bobbins.AmountAt(i));
            dsc.AppendLine(i == Work.Bobbins.Selected ? Lang.Get(d + "tapestryloom-info-bobbinactive", line) : line);
        }
    }

    #endregion
}
