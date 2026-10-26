using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ArtsAndCrafts.Tailor.Weaving;

namespace ArtsAndCrafts.Tailor.Loom;

/// <summary>
/// Loom block entity (controller position of the multiblock). Holds the cloth being woven, the twine
/// shuttles and warp threads (weaving partial; twine charged at the cut, ClothCost) and the weaver's seat (seat partial). Client side it plays the
/// model's <c>loom_full_cycle</c> animation once per beat (static mesh otherwise) and adds the cloth/warp/shuttle mesh
/// to the chunk mesh. Mechanical power is not wired; <see cref="DriveSpeed"/> is the hook for an auto-loom.
/// </summary>
public partial class BlockEntityLoom : BlockEntity
{
    public const string ShapePath = TailorModSystem.Domain + ":shapes/block/loom.json";
    /// <summary>The reference loom's full weaving cycle: 257 frames at 30 fps, matched by its cycle sound (8.57 s).</summary>
    public const string CycleAnim = "loom_full_cycle";
    public const float CycleSeconds = 257f / 30f;
    public static readonly AssetLocation CycleSound = new(TailorModSystem.Domain, "sounds/block/loom-cycle");
    /// <summary>
    /// First half of the full cycle (2 of its 4 shuttle back-and-forths, 129 frames; scripts/loom-add-half-cycle.py), played
    /// per beat of a 32-wide cloth (lane/loom-feedback-6, owner): twice the rows, half the beat time, same total time.
    /// </summary>
    public const string HalfCycleAnim = "loom_half_cycle";
    public const float HalfCycleSeconds = 129f / 30f;

    /// <summary>A cloth this wide plays the half cycle (finer than 16, <see cref="LoomGeometry.ScaleFor"/>).</summary>
    public static bool HalfCycle(int width) => LoomGeometry.ScaleFor(width) > 1;
    public static string CycleAnimFor(int width) => HalfCycle(width) ? HalfCycleAnim : CycleAnim;
    public static float CycleSecondsFor(int width) => HalfCycle(width) ? HalfCycleSeconds : CycleSeconds;

    /// <summary>
    /// Picks (beater hits) in the cycle animation: the beater strikes at frame 16 + 32 i (verified keyframes, 30 fps), 8 in
    /// the full cycle, 4 in the half cycle. Immersive Fibercraft moves its cloth sheets 0.4 px toward the reed at each of
    /// them (clouth_output_bone*); our cloth is a chunk mesh, so the growing row is redrawn per pick instead.
    /// </summary>
    public const int PicksPerFullCycle = 8, PickFirstFrame = 16, PickFrameStep = 32;

    /// <summary>Picks struck <paramref name="seconds"/> into a cycle (full or half). Pure (selftest).</summary>
    public static int PicksDone(double seconds, bool half)
    {
        int n = half ? PicksPerFullCycle / 2 : PicksPerFullCycle;
        double frame = seconds * 30.0;
        return frame < PickFirstFrame ? 0 : System.Math.Min(n, 1 + (int)((frame - PickFirstFrame) / PickFrameStep));
    }

    /// <summary>Share of the row woven <paramref name="seconds"/> into a cycle: picks done / picks per cycle (0..1).</summary>
    public static float GrowFractionAt(double seconds, bool half)
        => PicksDone(seconds, half) / (float)(half ? PicksPerFullCycle / 2 : PicksPerFullCycle);

    /// <summary>Client: the cycle animation playing (full or half), and the half cycle's own cut-short sound.</summary>
    string playingCycleAnim = CycleAnim;
    ILoadedSound? halfCycleSound;

    long stopAnimListener;
    bool animatorReady;
    /// <summary>World time (ms) the current weaving cycle ends; both sides (seat animation, sound).</summary>
    long cycleEndMs;
    /// <summary>Client: cloth mesh for the current state; built on the main thread, read by the tesselation thread.</summary>
    volatile MeshData? clothMesh;

    /// <summary>Speed the loom is driven at by a machine. Always 0 for the hand loom (no MPConsumer).</summary>
    public virtual float DriveSpeed => 0f;

    BEBehaviorAnimatable? Animatable => GetBehavior<BEBehaviorAnimatable>();

    /// <summary>The block shape's rotateY (unturned model).</summary>
    public float RotateYDeg => Block?.Shape?.rotateY ?? 0f;
    /// <summary>Rotation of <see cref="LoomGeometry"/> (selection boxes, cloth mesh) for this facing.</summary>
    public float GeometryRotateYDeg => LoomGeometry.GeometryRotateY(RotateYDeg);

    /// <summary>A weaving cycle (animation + sound) is running.</summary>
    public bool Cycling => Api != null && Api.World.ElapsedMilliseconds < cycleEndMs;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        InitializeSeat(api);
        DropLegacyOverflow(); // old save: twine of spools 5-6 (no shuttle for them) drops at the loom
        // Server: a new loom starts in draft mode (lane/playtest-fixes-5): its warp needs a twine per end (plain flax).
        if (api.Side == EnumAppSide.Server && Weaving.DraftMode && Weaving.Draft.Warp.Any(c => c == WeavingState.Empty))
        {
            Weaving.EnsureWarp();
            Weaving.RecomputeCurrent();
            MarkDirty(true);
        }
        // Server: a loom with no woven row starts its cloth at the world's cloth size (lane/square-cloth; the setting may
        // have changed; no set warp since lane/loom-feedback-8); a started cloth keeps its own. Warp colours resampled.
        if (api.Side == EnumAppSide.Server && Weaving.WovenRows == 0 && Weaving.Width != NextClothSize)
        {
            StartNextSquareCloth();
            MarkDirty(true);
        }
        OpenDefaultShed(); // server: a loaded loom with a closed shed opens its default treadle (lane/loom-feedback-8)
        if (api is ICoreClientAPI)
        {
            UpdateShedClient(); // a loom loaded with a treadle pressed (draft mode) shows its shed
            RebuildClothMesh();
            MarkDirty(true);
        }
    }

    #region Animation (client)

    /// <summary>
    /// Client: play one weaving cycle, then go back to the static mesh. A beat during a running cycle does not
    /// restart it (the cycle and its sound run to their end). When the cycle ends the hidden next row is revealed
    /// (<see cref="HideCurrentRow"/>), also when the animator is missing.
    /// </summary>
    public void PlayCycleClient()
    {
        if (Api is not ICoreClientAPI capi || Cycling) return;
        float seconds = CycleSecondsFor(Weaving.Width);
        string anim = CycleAnimFor(Weaving.Width);
        cycleStartMs = Api.World.ElapsedMilliseconds;
        cycleEndMs = cycleStartMs + (long)(seconds * 1000);
        if (HalfCycle(Weaving.Width)) StartHalfCycleSound(capi);
        // The woven row grows at the fell pick by pick (lane/loom-feedback-6): redraw when a pick is struck.
        growPicks = -1;
        if (growListener != 0) UnregisterGameTickListener(growListener);
        growListener = RegisterGameTickListener(_ =>
        {
            int picks = PicksDone((Api.World.ElapsedMilliseconds - cycleStartMs) / 1000.0, HalfCycle(Weaving.Width));
            if (picks == growPicks) return;
            growPicks = picks;
            RebuildClothMesh();
            MarkDirty(true);
        }, 50);

        if (stopAnimListener != 0) UnregisterDelayedCallback(stopAnimListener);
        stopAnimListener = RegisterDelayedCallback(_ =>
        {
            stopAnimListener = 0;
            if (growListener != 0) UnregisterGameTickListener(growListener);
            growListener = 0;
            Animatable?.animUtil?.StopAnimation(playingCycleAnim);
            StopHalfCycleSound();
            cycleEndMs = System.Math.Min(cycleEndMs, Api.World.ElapsedMilliseconds); // the cycle is over (callback may run a tick early)
            UpdateShedClient(); // a treadle pressed during the cycle opens its shed now
            hiddenRow = null;
            RebuildClothMesh(); // the shuttles (hidden while the beater swings, lane/shuttle-fix) and the hidden row show again
            MarkDirty(true);
        }, (int)(seconds * 1000));

        var animUtil = EnsureAnimator();
        if (animUtil == null) return;
        // The beat closes the shed (lane/draft-mode): the cycle animation takes over from the held shed pose.
        StopShedAnims(animUtil);
        shedAnimPlaying = null;

        if (!animUtil.activeAnimationsByAnimCode.ContainsKey(anim))
        {
            playingCycleAnim = anim;
            animUtil.StartAnimation(new AnimationMetaData
            {
                Animation = anim,
                Code = anim,
                AnimationSpeed = 1f,
                EaseInSpeed = 10,
                EaseOutSpeed = 10,
            });
        }
    }

    /// <summary>
    /// Client: the cycle sound for a half cycle, stopped with a short fade when the half cycle ends (the server plays the
    /// whole sound for full cycles only). Its first half matches the animation's first two back-and-forths.
    /// </summary>
    void StartHalfCycleSound(ICoreClientAPI capi)
    {
        StopHalfCycleSound();
        halfCycleSound = capi.World.LoadSound(new SoundParams
        {
            Location = CycleSound.Clone().WithPathAppendixOnce(".ogg"),
            ShouldLoop = false,
            Position = new Vec3f(Pos.X + 0.5f, Pos.Y + 1f, Pos.Z + 0.5f),
            DisposeOnFinish = false,
            Range = 16,
            Volume = 1f,
        });
        halfCycleSound?.Start();
    }

    void StopHalfCycleSound()
    {
        var s = halfCycleSound;
        halfCycleSound = null;
        if (s == null) return;
        s.FadeOutAndStop(0.25f);
        RegisterDelayedCallback(_ => s.Dispose(), 400);
    }

    public override void OnBlockUnloaded()
    {
        halfCycleSound?.Stop();
        halfCycleSound?.Dispose();
        halfCycleSound = null;
        base.OnBlockUnloaded();
    }

    /// <summary>Client: the block entity's animator, initialised on first use (null without the behaviour).</summary>
    BlockEntityAnimationUtil? EnsureAnimator()
    {
        var animUtil = Animatable?.animUtil;
        if (animUtil == null) return null;
        if (!animatorReady)
        {
            if (Api is ICoreClientAPI capi) InitAnimatorMesh(capi, animUtil); // weft in the active shuttle's colour (Weft partial)
            animatorReady = true;
        }
        return animUtil;
    }

    /// <summary>
    /// Shed poses of the draft mode (scripts/loom-add-shed-anims.py): the pedal poses (the model has 2 treadles) and, per mask of
    /// lifted shafts, the 4 heddle frames (lane/cloth-fixes: the model has 4 now, scripts/loom-add-shafts.py).
    /// </summary>
    public const string ShedAnimA = "draft_shed_a", ShedAnimB = "draft_shed_b", ShedShaftsPrefix = "draft_shafts_";

    /// <summary>Pedal pose for draft treadle <paramref name="treadle"/> (0..3): even treadles = left treadle down, odd = the right one; null = none.</summary>
    public static string? ShedAnimFor(int treadle) => treadle is < 0 or >= DraftPresets.Treadles ? null : treadle % 2 == 0 ? ShedAnimA : ShedAnimB;

    /// <summary>Heddle frame pose for the shafts in <paramref name="liftMask"/> (bit k = shaft k+1): those rise, the others sink.</summary>
    public static string ShedShaftsFor(int liftMask) => ShedShaftsPrefix + (liftMask & ((1 << DraftPresets.Shafts) - 1));

    string? shedAnimPlaying;

    static void StopShedAnims(BlockEntityAnimationUtil animUtil)
    {
        animUtil.StopAnimation(ShedAnimA);
        animUtil.StopAnimation(ShedAnimB);
        for (int m = 0; m < 1 << DraftPresets.Shafts; m++) animUtil.StopAnimation(ShedShaftsFor(m));
    }

    /// <summary>
    /// Client: hold the shed pose of the pressed treadle (draft mode), none while no treadle is pressed or a cycle runs.
    /// Start/stop switch between the animated renderer and the chunk mesh themselves (BlockEntityAnimationUtil).
    /// </summary>
    void UpdateShedClient()
    {
        if (Api is not ICoreClientAPI) return;
        string? pedal = Weaving.DraftMode && !Weaving.Complete ? ShedAnimFor(Weaving.Draft.CurrentTreadle) : null;
        if (Cycling) pedal = null;
        string? shafts = pedal == null ? null : ShedShaftsFor(Weaving.Draft.LiftMask(Weaving.Draft.CurrentTreadle));
        string want = pedal + "|" + shafts;
        if (want == shedAnimPlaying) return;
        var animUtil = pedal == null ? Animatable?.animUtil : EnsureAnimator();
        shedAnimPlaying = want;
        if (animUtil == null || !animatorReady) return;
        StopShedAnims(animUtil);
        foreach (string? anim in new[] { pedal, shafts })
        {
            if (anim == null) continue;
            animUtil.StartAnimation(new AnimationMetaData { Animation = anim, Code = anim, AnimationSpeed = 1f, EaseInSpeed = 10, EaseOutSpeed = 10 });
        }
    }

    /// <summary>
    /// Client: the current row as it was right after the last beat (a copy of the woven row). While the cycle runs and
    /// the row is unchanged, the mesh shows it empty, so the weaver sees the row being woven (lane/loom-feedback-4, user);
    /// it is revealed when the cycle ends, or as soon as someone paints it.
    /// </summary>
    byte[]? hiddenRow;
    int hiddenRowAt = -1;

    /// <summary>Client: show the current row empty (see <see cref="hiddenRow"/>).</summary>
    bool HideCurrentRow => hiddenRow != null && Cycling && !Weaving.Complete && Weaving.WovenRows == hiddenRowAt
        && Weaving.Current.AsSpan().SequenceEqual(hiddenRow);

    /// <summary>
    /// Client (lane/loom-feedback-6): during the cycle after a beat, the row just woven is drawn back at the fell and grows
    /// pick by pick (<see cref="GrowFractionAt"/>); the cloth advances one slot when the cycle ends. Also for the last row.
    /// </summary>
    bool Growing => hiddenRow != null && Cycling && Weaving.WovenRows == hiddenRowAt && Weaving.WovenRows > 0
        && (Weaving.Complete || Weaving.Current.AsSpan().SequenceEqual(hiddenRow));

    long cycleStartMs, growListener;
    int growPicks = -1;

    /// <summary>Client: a beat was seen (rows went up): hide the new current row until the cycle ends.</summary>
    void OnBeatSeenClient()
    {
        hiddenRow = (byte[])Weaving.Current.Clone();
        hiddenRowAt = Weaving.WovenRows;
        PlayCycleClient();
    }

    /// <summary>Server: start a cycle (seat animation follows <see cref="Cycling"/>); false when one is already running.</summary>
    bool StartCycleServer()
    {
        if (Cycling) return false;
        cycleEndMs = Api.World.ElapsedMilliseconds + (long)(CycleSecondsFor(Weaving.Width) * 1000);
        return true;
    }

    #endregion

    #region Mesh (client)

    /// <summary>Client, main thread: rebuild the cloth mesh from the weaving state (inserts cell textures on demand).</summary>
    void RebuildClothMesh()
    {
        if (Api is not ICoreClientAPI capi || Block == null) return;
        float grow = Growing ? GrowFractionAt((Api.World.ElapsedMilliseconds - cycleStartMs) / 1000.0, HalfCycle(Weaving.Width)) : -1f;
        clothMesh = LoomClothMesh.Build(capi, Block, new LoomLook(Weaving, Shuttles, Unrolled, TwineHex, HideCurrentRow, HideShuttles: Cycling,
            GrowFraction: grow));
        RebuildLoomMesh();
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        MeshData? cloth = clothMesh;
        if (cloth != null) mesher.AddMeshData(cloth);
        if (base.OnTesselation(mesher, tessThreadTesselator)) return true; // animated: the animator draws the model
        // Static: our model mesh with the weft colour (lane/shuttle-fix) instead of the default block mesh.
        MeshData? model = loomMesh;
        if (model == null) return false;
        mesher.AddMeshData(model);
        return true;
    }

    #endregion
}
