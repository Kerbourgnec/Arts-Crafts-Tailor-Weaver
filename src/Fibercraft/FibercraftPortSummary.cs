using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace ArtsAndCrafts.Tailor.Fibercraft;

/// <summary>
/// Not upstream: smoke evidence for the Immersive Fibercraft port (docs/fibercraft-port.md).
/// Logs ported content, recipe resolution, disabled vanilla twine recipes and the flax spinning ratios.
/// </summary>
public class FibercraftPortSummary : Vintagestory.API.Common.ModSystem
{
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        sapi.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => Log(sapi));
    }

    static bool IsPorted(AssetLocation? code) =>
        code?.Domain == TailorModSystem.Domain && (code.Path.StartsWith("hp") || code.Path.StartsWith("loom-"));

    static void Log(ICoreServerAPI sapi)
    {
        var wheels = sapi.World.Blocks.Count(b => b is Blocks.BlockSpinningWheel && b.Code?.Domain == TailorModSystem.Domain);
        var items = sapi.World.Items.Count(i => i?.Code != null && IsPorted(i.Code));
        var spindle = sapi.World.GetItem(new AssetLocation(TailorModSystem.Domain, "hpdropspindle"));
        sapi.Logger.Notification("[actailor] fibercraft: {0} spinning wheel blocks, {1} part/tool items, drop spindle {2} (class {3})",
            wheels, items, spindle != null ? "registered" : "MISSING", spindle?.GetType().Name ?? "-");

        var recipes = sapi.World.GridRecipes.Where(r => IsPorted(r.Output?.Code)).ToList();
        int resolved = recipes.Count(r => r.Output!.ResolvedItemStack != null);
        int unresolved = recipes.Sum(r => r.ResolvedIngredients?.Count(i =>
            i != null && i.MatchingType == EnumRecipeMatchType.Exact && i.ResolvedItemStack == null) ?? 0);
        int looms = recipes.Count(r => r.Output!.Code.Path.StartsWith("loom-"));
        sapi.Logger.Notification("[actailor] fibercraft: {0}/{1} grid recipes resolved ({2} loom), {3} unresolved ingredients",
            resolved, recipes.Count, looms, unresolved);

        int disabled = sapi.World.GridRecipes.Count(r => !r.Enabled && r.Output?.Code?.ToString() == "game:flaxtwine");
        int spinnable = sapi.World.Collectibles.Count(c => c?.Attributes?.KeyExists("spinningProps") == true);
        int weavable = sapi.World.Collectibles.Count(c => c?.Attributes?.KeyExists("weavingProps") == true);
        sapi.Logger.Notification("[actailor] fibercraft: {0} vanilla twine grid recipes disabled (DisableTwineGridRecipes={1}); {2} spinnable, {3} weavable collectibles",
            disabled, ModConfig.ModConfig.Loaded.DisableTwineGridRecipes, spinnable, weavable);

        var flax = sapi.World.GetItem(new AssetLocation("game:flaxfibers"))?.Attributes?["spinningProps"];
        int input = flax?["inputQuantity"].AsInt(0) ?? 0, output = flax?["outputQuantity"].AsInt(0) ?? 0;
        int spins = spindle?.Attributes?["spinsPerCompletion"].AsInt(0) ?? 0;
        sapi.Logger.Notification("[actailor] fibercraft: flax ratios: spinning wheel {0} fibres -> {1} twine, drop spindle {2} fibres -> {1} twine ({3} spins)",
            input, output, input * spins, spins);
    }
}
