using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace ArtsAndCrafts.Tailor.HandLoom;

/// <summary>
/// Hand looms (lane/hand-loom): registers the block and block entity classes and prints the smoke evidence line.
/// Kept apart from <see cref="TailorModSystem"/> so the lane touches no shared code (VS loads every ModSystem).
/// </summary>
public class HandLoomModSystem : ModSystem
{
    public const string BlockClass = "actailor.BlockHandLoom", EntityClass = "actailor.HandLoom";

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        api.RegisterBlockClass(BlockClass, typeof(BlockHandLoom));
        api.RegisterBlockEntityClass(EntityClass, typeof(BlockEntityHandLoom));
    }

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        base.StartServerSide(sapi);
        sapi.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () =>
        {
            var blocks = sapi.World.Blocks.Where(b => b is BlockHandLoom && b.Code?.Domain == TailorModSystem.Domain).ToList();
            int types = blocks.Select(b => b.Variant["type"]).Distinct().Count();
            int sides = blocks.Select(b => b.Variant["side"]).Distinct().Count();
            bool noRecipe = !sapi.World.GridRecipes.Any(r => r.Output?.Code?.Path?.StartsWith("handloom") == true && r.Output.Code.Domain == TailorModSystem.Domain);
            sapi.Logger.Notification("[actailor] hand looms: {0} blocks ({1} types x {2} sides), crude {3}, no recipe {4}",
                blocks.Count, types, sides, blocks.Any(b => b.Variant["type"] == "crude") ? "yes" : "MISSING", noRecipe);
        });
    }
}
