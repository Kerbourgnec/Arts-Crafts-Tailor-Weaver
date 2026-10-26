using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace ArtsAndCrafts.Tailor.Tapestry;

/// <summary>
/// Tapestry looms (lane/tapestry-loom): registers the block and block entity classes and prints the smoke evidence line.
/// Kept apart from <see cref="TailorModSystem"/> so the lane touches no shared code (VS loads every ModSystem).
/// </summary>
public class TapestryModSystem : ModSystem
{
    public const string BlockClass = "actailor.BlockTapestryLoom", EntityClass = "actailor.TapestryLoom";

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        api.RegisterBlockClass(BlockClass, typeof(BlockTapestryLoom));
        api.RegisterBlockEntityClass(EntityClass, typeof(BlockEntityTapestryLoom));
    }

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        base.StartServerSide(sapi);
        sapi.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () =>
        {
            var blocks = sapi.World.Blocks.Where(b => b is BlockTapestryLoom && b.Code?.Domain == TailorModSystem.Domain).ToList();
            int types = blocks.Select(b => b.Variant["type"]).Distinct().Count();
            int sides = blocks.Select(b => b.Variant["side"]).Distinct().Count();
            bool noRecipe = !sapi.World.GridRecipes.Any(r => r.Output?.Code?.Path?.StartsWith("tapestryloom") == true && r.Output.Code.Domain == TailorModSystem.Domain);
            sapi.Logger.Notification("[actailor] tapestry looms: {0} blocks ({1} types x {2} sides), crude {3}, no recipe {4}",
                blocks.Count, types, sides, blocks.Any(b => b.Variant["type"] == "crude") ? "yes" : "MISSING", noRecipe);
        });
    }
}
