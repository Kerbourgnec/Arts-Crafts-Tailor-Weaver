using System;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ArtsAndCrafts.Tailor.Loom;

/// <summary>
/// Chat command (lane/warp-shuttles-import): <c>/actailor draft &lt;code&gt;</c> imports a code exported by the owner's
/// draft designer onto a loom; <c>/actailor draft</c> shows the plan; <c>/actailor draft clear</c> drops it.
/// Privilege <c>chat</c> (every player). Target loom: the one the player sits at, else the nearest loom (controller
/// block) within reach (8 blocks, the loom's use distance) that the player may use (land claims).
/// </summary>
public static class DraftCommand
{
    public const string Name = "actailor", Sub = "draft";
    /// <summary>Reach to find a loom (blocks): the loom packets' use distance.</summary>
    public const int Reach = 8;

    public static void Register(ICoreServerAPI sapi)
    {
        var parsers = sapi.ChatCommands.Parsers;
        sapi.ChatCommands.GetOrCreate(Name)
            .WithDescription(Lang.Get(TailorModSystem.Domain + ":cmd-actailor-desc"))
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .BeginSubCommand(Sub)
                .WithDescription(Lang.Get(TailorModSystem.Domain + ":cmd-draft-desc"))
                .WithArgs(parsers.OptionalAll("code"))
                .HandleWith(args => Handle(sapi, args))
            .EndSubCommand();
    }

    static TextCommandResult Handle(ICoreServerAPI sapi, TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player) return TextCommandResult.Error("players only");
        string L(string key, params object[] a) => Lang.GetL(player.LanguageCode, TailorModSystem.Domain + ":" + key, a);
        string code = (args[0] as string ?? "").Trim();

        var loom = FindLoom(sapi, player);
        if (loom == null) return TextCommandResult.Error(L("draft-err-noloom", Reach));
        if (!sapi.World.Claims.TryAccess(player, loom.Pos, EnumBlockAccessFlags.Use)) return TextCommandResult.Error(L("draft-err-access"));

        if (code.Length == 0)
        {
            if (loom.Weaving.Draft.Plan is not { } p) return TextCommandResult.Success(L("draft-none"));
            return TextCommandResult.Success(L("draft-status", Math.Min(loom.Weaving.WovenRows + 1, p.Size), p.Size, loom.Weaving.WarpMatches, p.Code));
        }
        if (code.Equals("clear", StringComparison.OrdinalIgnoreCase))
            return loom.ClearPlan() ? TextCommandResult.Success(L("draft-cleared")) : TextCommandResult.Success(L("draft-none"));

        var plan = DraftPlan.Parse(code, ClothSizeSetting.NewClothSize(sapi.World), out var error);
        if (plan == null) return TextCommandResult.Error(L(error?.Key ?? "draft-err-range", error?.Args ?? Array.Empty<object>()));
        return loom.ImportPlan(plan) switch
        {
            BlockEntityLoom.ImportOutcome.Ok => TextCommandResult.Success(L("draft-ok", plan.Size, plan.SourceTreadles > DraftPresets.Treadles ? L("draft-ok-6") : "")),
            BlockEntityLoom.ImportOutcome.Rows => TextCommandResult.Error(L("draft-err-rows")),
            _ => TextCommandResult.Error(L("draft-err-loomsize", plan.Size, loom.Weaving.Width)),
        };
    }

    /// <summary>The loom the player sits at, else the nearest loom controller within <see cref="Reach"/> blocks of their feet.</summary>
    public static BlockEntityLoom? FindLoom(ICoreServerAPI sapi, IServerPlayer player)
    {
        if (player.Entity?.MountedOn is BlockEntityLoom seated) return seated;
        var feet = player.Entity?.Pos;
        if (feet == null) return null;
        var center = feet.AsBlockPos;
        BlockEntityLoom? best = null;
        double bestSq = BlockEntityLoom.MaxUseDistanceSq;
        var pos = new BlockPos(center.dimension);
        for (int dx = -Reach; dx <= Reach; dx++)
            for (int dy = -Reach; dy <= Reach; dy++)
                for (int dz = -Reach; dz <= Reach; dz++)
                {
                    pos.Set(center.X + dx, center.Y + dy, center.Z + dz);
                    if (sapi.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityLoom loom) continue;
                    double sq = feet.SquareDistanceTo(loom.Pos.X + 0.5, loom.Pos.Y + 0.5, loom.Pos.Z + 0.5);
                    if (sq <= bestSq) { bestSq = sq; best = loom; }
                }
        return best;
    }
}
