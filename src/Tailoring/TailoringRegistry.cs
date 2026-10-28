using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>Class registration and the server smoke summary for tailoring (called from TailorModSystem).</summary>
public static class TailoringRegistry
{
    public static void Register(ICoreAPI api)
    {
        api.RegisterBlockEntityBehaviorClass(BEBehaviorMannequinTailoring.ClassName, typeof(BEBehaviorMannequinTailoring));
        api.RegisterItemClass("actailor.ItemTailoredGarment", typeof(ItemTailoredGarment));
    }

    static void CollectNames(ShapeElement[]? elems, HashSet<string> into)
    {
        if (elems == null) return;
        foreach (var e in elems)
        {
            if (e.Name != null) into.Add(e.Name);
            CollectNames(e.Children, into);
        }
    }

    /// <summary>Smoke evidence (scripts/smoke-server.sh fails on "error" lines for our domain).</summary>
    public static void LogContentSummary(ICoreServerAPI sapi)
    {
        var cat = GarmentCatalog.Get(sapi);
        foreach (string problem in GarmentCatalog.Validate(cat, MaterialCatalog.Get(sapi))) sapi.Logger.Error("[actailor] tailoring garment error: {0}", problem);
        foreach (var g in cat.Garments)
        {
            void Err(string what) => sapi.Logger.Error("[actailor] tailoring garment error: {0}: {1}", g.Code, what);
            foreach (string code in new[] { g.Item }.Concat(g.LegacyItems))
            {
                var item = sapi.World.GetItem(new AssetLocation(code));
                if (item is not ItemTailoredGarment) { Err($"item {code} missing or wrong class (run scripts/garment-items.py)"); continue; }
                if (item.Shape?.Base?.ToString() != g.Shape) Err($"shape {g.Shape} != item {code} shape {item.Shape?.Base}");
                if (item.Attributes?["clothescategory"].AsString() != g.DressType) Err($"dressType {g.DressType} != item {code} clothescategory {item.Attributes?["clothescategory"].AsString()}");
                if (System.Math.Abs((item.Attributes?["warmth"].AsFloat(0) ?? 0) - g.Warmth) > 1e-4) Err($"warmth {g.Warmth} != item {code} warmth");
            }
            // Fixed (premade) parts: their texture asset exists.
            foreach (var p in g.Parts.Where(p => p.IsFixed))
            {
                var tex = new AssetLocation(p.FixedTexture).WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png");
                if (!sapi.Assets.Exists(tex)) Err($"fixed part {p.Code}: texture {tex} not found");
            }
            // Shading source (lane/garment-layers) exists.
            if (!string.IsNullOrEmpty(g.ShadingTexture))
            {
                var stex = new AssetLocation(g.ShadingTexture).WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png");
                if (!sapi.Assets.Exists(stex)) Err($"shadingTexture {stex} not found");
            }
            // Model: shape (+ extra shape) loads, every element a part or the recipe names exists.
            var shape = ItemTailoredGarment.LoadShape(sapi, g, System.Array.Empty<string>());
            if (shape == null) { Err($"shape {g.Shape} not found"); continue; }
            var names = new HashSet<string>(System.StringComparer.InvariantCultureIgnoreCase);
            CollectNames(shape.Elements, names);
            foreach (string e in g.HideElements.Concat(g.Parts.SelectMany(p => p.Elements.Concat(p.Options.SelectMany(o => o.Elements ?? System.Array.Empty<string>())))))
            {
                if (!names.Contains(e)) Err($"element {e} not in shape {g.Shape}{(g.ExtraShape == null ? "" : " + " + g.ExtraShape)}");
            }
        }

        // Materials (config/tailoring-materials.json of every domain): every entry matches a real item or block with a
        // texture; collectibles per family; every part token is a family or group.
        var mats = MaterialCatalog.Get(sapi);
        foreach (string problem in MaterialCatalog.Validate(mats)) sapi.Logger.Error("[actailor] tailoring materials error: {0}", problem);
        var collectibles = sapi.World.Collectibles.Where(c => c?.Code != null).ToList();
        var matched = new Dictionary<string, int>();
        var noTexture = new List<string>();
        foreach (var item in collectibles)
        {
            var m = mats.Find(item.Code.ToString());
            if (m == null) continue;
            matched[m.Family] = matched.GetValueOrDefault(m.Family) + 1;
            var loc = m.TextureLocation(item.Code.ToString());
            if (!sapi.Assets.Exists(loc)) noTexture.Add(item.Code + " (" + loc + ")");
        }
        foreach (var m in mats.Materials)
        {
            if (!collectibles.Any(i => m.Matches(i.Code.ToString()) && mats.Find(i.Code.ToString()) == m))
            {
                sapi.Logger.Error("[actailor] tailoring materials error: {0} matches no item or block (or only ones an earlier entry takes)", m.Code);
            }
        }
        foreach (var f in mats.Families.Where(f => matched.GetValueOrDefault(f.Code) == 0 && f.Code != MaterialKind.Cloth))
        {
            sapi.Logger.Error("[actailor] tailoring materials error: family {0} has no item", f.Code);
        }
        if (noTexture.Count > 0) sapi.Logger.Error("[actailor] tailoring materials error: no texture asset for {0}", string.Join(", ", noTexture));
        sapi.Logger.Notification("[actailor] tailoring materials: {0} entries; families {1}", mats.Materials.Length,
            string.Join(", ", mats.Families.Select(f => $"{f.Code}({f.Group}) {matched.GetValueOrDefault(f.Code)}")));
        sapi.Logger.Notification("[actailor] tailoring part families: {0}", string.Join(" ", cat.Garments.Select(g =>
            g.Code + ":" + string.Join(",", g.Parts.Select(p => p.Code + "=" + (p.IsFixed ? "fixed:" + p.FixedTexture : string.Join("|", mats.Expand(p.Materials))))))));

        // Mannequins (patches/mannequin-tailoring.json): our behaviour after the vanilla one, and what each part can wear.
        var mannequins = sapi.World.Blocks.Where(b => b?.Code?.Domain == "game" && b.Code.Path.StartsWith("mannequin-")).ToList();
        var parts = mannequins.Select(b =>
        {
            var names = b.BlockEntityBehaviors?.Select(t => t.Name).ToList() ?? new();
            int ours = names.IndexOf(BEBehaviorMannequinTailoring.ClassName), vanilla = names.IndexOf("Mannequin");
            if (ours < 0 || ours < vanilla) sapi.Logger.Error("[actailor] tailoring error: {0} behaviours [{1}] (ours must follow Mannequin)", b.Code, string.Join(", ", names));
            string[] allowed = b.Attributes?["allowedDresstypes"].AsArray<string>()! ?? System.Array.Empty<string>();
            var wear = MannequinFit.Garments(cat, allowed, BEBehaviorMannequinTailoring.CategoryOf);
            return b.Code.Path + ":" + (wear.Length == 0 ? "-" : string.Join("+", wear.Select(g => g.Code)));
        }).ToList();
        if (mannequins.Count == 0) sapi.Logger.Error("[actailor] tailoring error: no vanilla mannequin blocks");
        sapi.Logger.Notification("[actailor] tailoring: {0} garments ({1}); mannequins {2}", cat.Garments.Length,
            string.Join(", ", cat.Garments.Select(g => $"{g.Code}:{g.DressType}:{g.Parts.Length} parts ({g.Parts.Count(p => p.Optional)} optional{(g.Parts.Any(p => p.IsFixed) ? ", " + g.Parts.Count(p => p.IsFixed) + " fixed" : "")}{(g.Parts.Any(p => p.Layer > 0) ? ", " + g.Parts.Count(p => p.Layer > 0) + " overlay" : "")}{(g.HasOptions ? ", " + g.Parts.Count(p => p.Options.Length > 0) + " modular (" + string.Join("/", g.Parts.Where(p => p.Options.Length > 0).Select(p => p.Options.Length)) + " options)" : "")}{(g.Parts.Any(p => p.Shade) ? ", " + g.Parts.Count(p => p.Shade) + " shaded" : "")})")),
            string.Join(" ", parts));
    }
}
