using System.Linq;
using ArtsAndCrafts.Tailor.Loom;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace ArtsAndCrafts.Tailor;

public class TailorModSystem : ModSystem
{
    public const string Domain = "actailor";

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        api.RegisterBlockClass("actailor.BlockLoom", typeof(BlockLoom));
        api.RegisterBlockEntityClass("actailor.Loom", typeof(BlockEntityLoom));
        api.RegisterMountable(BlockEntityLoom.MountableClass, BlockLoom.GetMountable);
        api.RegisterItemClass("actailor.ItemWovenCloth", typeof(Weaving.ItemWovenCloth));
        api.RegisterItemClass("actailor.ItemWovenLinen", typeof(Weaving.ItemWovenLinen));
        api.RegisterCollectibleBehaviorClass("actailor.LoomBrush", typeof(CollectibleBehaviorLoomBrush));
        Tailoring.TailoringRegistry.Register(api);
        // Cloth decor (lane/cloth-decor): patterned blocks, tapestry, hammer behaviour.
        api.RegisterBlockClass("actailor.BlockPatterned", typeof(Pattern.BlockPatterned));
        api.RegisterBlockClass("actailor.BlockClothTapestry", typeof(Decor.BlockClothTapestry));
        api.RegisterBlockEntityClass("actailor.Patterned", typeof(Pattern.BlockEntityPatterned));
        api.RegisterCollectibleBehaviorClass("actailor.HangTapestry", typeof(Decor.CollectibleBehaviorHangTapestry));
        api.Logger.Notification("[actailor] loaded");
    }

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        base.StartServerSide(sapi);
        // Draft import (lane/warp-shuttles-import): /actailor draft <code>.
        DraftCommand.Register(sapi);
        // Smoke evidence: content registered and grid recipes resolved (printed by scripts/smoke-server.sh).
        sapi.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => LogContentSummary(sapi));
    }

    static void LogContentSummary(ICoreServerAPI sapi)
    {
        // Only real looms: a world saved with older loom codes (e.g. actailor:loom-north) keeps placeholder blocks for them.
        var loomBlocks = sapi.World.Blocks.Where(b => b is BlockLoom && b.Code?.Domain == Domain).ToList();
        int looms = loomBlocks.Count;
        int stale = sapi.World.Blocks.Count(b => b != null && b is not BlockLoom && b.Code?.Domain == Domain && b.Code.Path.StartsWith("loom-"));
        sapi.Logger.Notification("[actailor] looms: {0} woods x {1} sides; {2} unknown old loom codes in this world",
            loomBlocks.Select(b => b.Variant["wood"]).Distinct().Count(), loomBlocks.Select(b => b.Variant["side"]).Distinct().Count(), stale);
        var ours = sapi.World.GridRecipes.Where(r => r.Output?.Code?.Domain == Domain).ToList();
        int resolved = ours.Count(r => r.Output!.ResolvedItemStack != null);
        sapi.Logger.Notification("[actailor] content: {0} loom blocks; {1}/{2} grid recipes resolved", looms, resolved, ours.Count);
        var cloth = sapi.World.GetItem(new AssetLocation(BlockEntityLoom.WovenClothCode));
        sapi.Logger.Notification("[actailor] weaving: wovencloth item {0} (class {1})", cloth != null ? "registered" : "MISSING", cloth?.GetType().Name ?? "-");
        LogLinenSummary(sapi);
        Tailoring.TailoringRegistry.LogContentSummary(sapi);
        LogDecorSummary(sapi);
        int brushes = sapi.World.Items.Count(i => i != null && LoomTwine.IsTwine(i) && i.CollectibleBehaviors.Any(bh => bh is CollectibleBehaviorLoomBrush));
        int twines = sapi.World.Items.Count(i => i != null && LoomTwine.IsTwine(i));
        sapi.Logger.Notification("[actailor] loom brush (F tool modes) on {0}/{1} twines; golden twine {2}", brushes, twines,
            sapi.World.GetItem(new AssetLocation(Domain, "twine-gold")) != null ? "registered" : "MISSING");
        // World setting (lane/square-cloth): declared in the mod's worldconfig.json; null attributes = the file was not loaded.
        var attrs = sapi.ModLoader.GetMod(Domain)?.WorldConfig?.WorldConfigAttributes;
        bool declared = attrs?.Any(a => a.Code == Weaving.ClothSizeSetting.Code) == true;
        if (!declared) sapi.Logger.Error("[actailor] world setting {0} not declared (worldconfig.json missing from the mod folder?)", Weaving.ClothSizeSetting.Code);
        sapi.Logger.Notification("[actailor] draft import: /{0} {1} <code> {2}", DraftCommand.Name, DraftCommand.Sub,
            sapi.ChatCommands.Get(DraftCommand.Name)?.AllSubcommands.ContainsKey(DraftCommand.Sub) == true ? "registered" : "MISSING");
        sapi.Logger.Notification("[actailor] new cloths {0}x{0}, square only (world setting {1} = {2}, declared: {3})",
            Weaving.ClothSizeSetting.NewClothSize(sapi.World), Weaving.ClothSizeSetting.Code,
            sapi.World.Config?.GetAsString(Weaving.ClothSizeSetting.Code) ?? "unset", declared);
    }

    /// <summary>Linen output (lane/linen-output): woven linen item and its stick recipes (vanilla cloth outputs count too).</summary>
    static void LogLinenSummary(ICoreServerAPI sapi)
    {
        var linen = sapi.World.GetItem(new AssetLocation(Weaving.ClothOutput.WovenLinenCode));
        var stick = sapi.World.GridRecipes.Where(r => r.RecipeIngredients.Any(i => i?.Code?.ToString() == Weaving.ClothOutput.WovenLinenCode)).ToList();
        int resolved = stick.Count(r => r.Output?.ResolvedItemStack != null);
        sapi.Logger.Notification("[actailor] linen: wovenlinen item {0} (class {1}); {2}/{3} stick recipes resolved; vanilla linen {4}",
            linen != null ? "registered" : "MISSING", linen?.GetType().Name ?? "-", resolved, stick.Count,
            sapi.World.GetBlock(new AssetLocation(Weaving.ClothOutput.VanillaLinenCode)) != null ? "found" : "MISSING");
        // Cut yield per loom, size and twine (lane/loom-feedback-8 item 7): real CutStack on cloths woven by each loom's rules.
        sapi.Logger.Notification("[actailor] cut yield: {0}", Weaving.LoomYield.Summary(sapi.World));
    }

    static void LogDecorSummary(ICoreServerAPI sapi)
    {
        int patterned = sapi.World.Blocks.Count(b => b?.Code?.Domain == Domain && b is Pattern.BlockPatterned);
        int hammers = sapi.World.Items.Count(i => i?.Tool == EnumTool.Hammer);
        int hanging = sapi.World.Items.Count(i => i?.Tool == EnumTool.Hammer && i.CollectibleBehaviors.Any(bh => bh is Decor.CollectibleBehaviorHangTapestry));
        sapi.Logger.Notification("[actailor] decor: {0} patterned blocks; tapestry behaviour on {1}/{2} hammers", patterned, hanging, hammers);
    }
}
