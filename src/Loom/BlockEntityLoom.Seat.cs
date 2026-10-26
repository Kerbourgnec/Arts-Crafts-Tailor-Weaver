using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace ArtsAndCrafts.Tailor.Loom;

/// <summary>
/// The weaver's bench: a mountable seat, ported from Immersive Fibercraft's fly-shuttle loom (MIT; seat position,
/// eye position, yaw and dismount spot as there). Differences: sneak does not stand up (sneak + right click is a
/// weaving action), jump or a right click on the bench does; the seated animation follows <see cref="Cycling"/>
/// (vanilla EntityAgent.OnGameTick switches to <see cref="SuggestedAnimation"/> on both sides).
/// Animations: actailor-sitloomidle / actailor-sitloomfull, patched into the seraph shape (patches/seraph-sitloom.json).
/// </summary>
public partial class BlockEntityLoom : IMountableSeat, IMountable
{
    /// <summary>Mountable class name (TreeAttribute "className"), registered in <see cref="TailorModSystem"/>.</summary>
    public const string MountableClass = TailorModSystem.Domain + ".loom";

    static readonly AnimationMetaData SitIdle = new AnimationMetaData
    {
        Code = TailorModSystem.Domain + "-sitloomidle", Animation = TailorModSystem.Domain + "-sitloomidle", EaseInSpeed = 1000f, EaseOutSpeed = 1000f
    }.Init();
    static readonly AnimationMetaData SitWeave = new AnimationMetaData
    {
        Code = TailorModSystem.Domain + "-sitloomfull", Animation = TailorModSystem.Domain + "-sitloomfull", EaseOutSpeed = 1000f
    }.Init();

    public EntityAgent? MountedBy;
    long mountedByEntityId;
    string? mountedByPlayerUid;
    bool blockBroken;
    readonly EntityControls seatControls = new();
    readonly EntityPos mountPos = new();

    BlockFacing Facing => BlockFacing.FromCode(Block?.LastCodePart()) ?? BlockFacing.NORTH;

    /// <summary>Quarter turns from north (north 0, east 1, south 2, west 3), the order the shape turns in (rotateY 0/270/180/90).</summary>
    static int Quarter(BlockFacing f) => f == BlockFacing.EAST ? 1 : f == BlockFacing.SOUTH ? 2 : f == BlockFacing.WEST ? 3 : 0;

    /// <summary>(dx, dz) from the controller block centre turned like the shape: north (dx, dz) → east (-dz, dx).</summary>
    static (double x, double z) TurnXZ(double dx, double dz, int quarters)
    {
        for (int i = 0; i < quarters; i++) (dx, dz) = (-dz, dx);
        return (dx, dz);
    }

    /// <summary>Seated feet position (block units, controller-relative) for a facing; north as the reference loom: (0.5, 0.6, 1.4).</summary>
    public static Vec3d SeatFeet(BlockFacing facing)
    {
        var n = LoomGeometry.SeatFeetNorth;
        var (x, z) = TurnXZ(n.X - 0.5, n.Z - 0.5, Quarter(facing));
        return new Vec3d(0.5 + x, n.Y, 0.5 + z);
    }

    /// <summary>Seated eye relative to the feet (the seat's LocalEyePos); north as the reference loom: (0, 1, 0.12).</summary>
    public static Vec3f SeatEyeOffset(BlockFacing facing)
    {
        var n = LoomGeometry.SeatEyeOffsetNorth;
        var (x, z) = TurnXZ(n.X, n.Z, Quarter(facing));
        return new Vec3f((float)x, n.Y, (float)z);
    }

    void InitializeSeat(ICoreAPI api)
    {
        seatControls.OnAction = OnSeatControls;
        // Remount a player who sat here when the world was saved (as vanilla beds do).
        if (MountedBy == null && (mountedByEntityId != 0 || mountedByPlayerUid != null))
        {
            var entity = mountedByPlayerUid != null ? api.World.PlayerByUid(mountedByPlayerUid)?.Entity : api.World.GetEntityById(mountedByEntityId) as EntityAgent;
            if (entity?.SidedProperties != null) entity.TryMount(this);
        }
    }

    void OnSeatControls(EnumEntityAction action, bool on, ref EnumHandling handled)
    {
        if (action == EnumEntityAction.Jump && on)
        {
            MountedBy?.TryUnmount();
            seatControls.StopAllMovement();
        }
        handled = EnumHandling.PassThrough;
    }

    /// <summary>Shift held by this player: own controls, or the seat's controls while they sit here (input goes to the mount).</summary>
    public bool IsSneaking(IPlayer player)
    {
        var c = player.Entity.Controls;
        if (c.ShiftKey || c.Sneak) return true;
        return MountedBy == player.Entity && (seatControls.ShiftKey || seatControls.Sneak);
    }

    /// <summary>Right click on the bench: sit down, or stand up when already seated. Runs on both sides (as vanilla beds).</summary>
    bool OnSeatInteract(IPlayer byPlayer)
    {
        if (MountedBy == byPlayer.Entity) return byPlayer.Entity.TryUnmount();
        if (MountedBy != null)
        {
            (Api as ICoreClientAPI)?.TriggerIngameError(this, "occupied", Lang.Get(TailorModSystem.Domain + ":loom-err-occupied"));
            return true;
        }
        return byPlayer.Entity.TryMount(this);
    }

    #region IMountable / IMountableSeat

    public EntityPos SeatPosition => Position;
    public double StepPitch => 0;

    public EntityPos Position
    {
        get
        {
            var facing = Facing;
            mountPos.SetPos(Pos);
            mountPos.Yaw = facing.HorizontalAngleIndex * GameMath.PIHALF + GameMath.PIHALF;
            var feet = SeatFeet(facing);
            return mountPos.Add(feet.X, feet.Y, feet.Z);
        }
    }

    public Vec3f LocalEyePos => SeatEyeOffset(Facing);
    public AnimationMetaData SuggestedAnimation => Cycling ? SitWeave : SitIdle;
    public EntityControls Controls => seatControls;
    public IMountable MountSupplier => this;
    public EnumMountAngleMode AngleMode => EnumMountAngleMode.FixateYaw;
    Entity IMountableSeat.Passenger => MountedBy!;
    public bool CanControl => false;
    public Entity Entity => null!;
    public Matrixf RenderTransform => null!;
    public IMountableSeat[] Seats => new IMountableSeat[] { this };
    public bool SkipIdleAnimation => false;
    public float FpHandPitchFollow => 1;
    public string SeatId { get => "weaver-0"; set { } }
    public SeatConfig Config { get => null!; set { } }
    public long PassengerEntityIdForInit { get => mountedByEntityId; set => mountedByEntityId = value; }
    public bool DoTeleportOnUnmount { get; set; } = true;
    public Entity Controller => MountedBy!;
    public Entity OnEntity => null!;
    public EntityControls ControllingControls => null!;

    public bool AnyMounted() => MountedBy != null;
    public bool CanUnmount(EntityAgent entityAgent) => true;
    public bool CanMount(EntityAgent entityAgent) => MountedBy == null;

    public void MountableToTreeAttributes(TreeAttribute tree)
    {
        tree.SetString("className", MountableClass);
        tree.SetInt("posx", Pos.X);
        tree.SetInt("posy", Pos.InternalY);
        tree.SetInt("posz", Pos.Z);
    }

    public void DidMount(EntityAgent entityAgent)
    {
        if (MountedBy != null && MountedBy != entityAgent)
        {
            entityAgent.TryUnmount();
            return;
        }
        if (MountedBy == entityAgent) return;
        MountedBy = entityAgent;
        mountedByPlayerUid = (entityAgent as EntityPlayer)?.PlayerUID;
        mountedByEntityId = entityAgent.EntityId;
        MarkDirty(false);
    }

    public void DidUnmount(EntityAgent entityAgent)
    {
        entityAgent.AnimManager?.StopAnimation(SitIdle.Code);
        entityAgent.AnimManager?.StopAnimation(SitWeave.Code);
        MountedBy = null;
        if (!blockBroken && DoTeleportOnUnmount && Api != null)
        {
            // In front of the bench first, then the sides, then behind (reference loom's order).
            var facing = Facing;
            var front = Pos.ToVec3d().Add(SeatFeet(facing).X, 0, SeatFeet(facing).Z);
            front.Add(facing.Opposite.Normalf.X * 0.1, 0, facing.Opposite.Normalf.Z * 0.1);
            foreach (var f in new[] { facing.Opposite, facing.GetCW(), facing.GetCCW(), facing })
            {
                var place = front.AddCopy(f.Normalf.X, 0.001, f.Normalf.Z);
                if (!Api.World.CollisionTester.IsColliding(Api.World.BlockAccessor, entityAgent.SelectionBox, place, false))
                {
                    entityAgent.TeleportTo(place);
                    break;
                }
            }
        }
        mountedByEntityId = 0;
        mountedByPlayerUid = null;
        MarkDirty(false);
    }

    #endregion

    void SeatToTree(ITreeAttribute tree)
    {
        tree.SetLong("mountedByEntityId", mountedByEntityId);
        if (mountedByPlayerUid != null) tree.SetString("mountedByPlayerUid", mountedByPlayerUid);
    }

    void SeatFromTree(ITreeAttribute tree)
    {
        mountedByEntityId = tree.GetLong("mountedByEntityId");
        mountedByPlayerUid = tree.GetString("mountedByPlayerUid");
    }

    void SeatOnRemoved()
    {
        blockBroken = true;
        MountedBy?.TryUnmount();
    }

    public override void OnBlockRemoved()
    {
        SeatOnRemoved();
        halfCycleSound?.Stop(); // a half cycle's client sound (lane/loom-feedback-6)
        base.OnBlockRemoved();
    }
}
