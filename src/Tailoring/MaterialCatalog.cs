using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Built-in material family and group codes (lane/garment-families, docs/garments.md "Material families"). The families
/// themselves are data (<c>families</c> in config/tailoring-materials.json, extendable by other mods); these are the
/// codes the code relies on. A garment part lists the families it takes, or a group (= any family of that group),
/// like a cooking recipe ingredient that takes "vegetable" or "meat".
/// </summary>
public static class MaterialKind
{
    public const string Cloth = "cloth";     // patterned actailor:wovencloth / wovenlinen (pattern offset + rotation), or vanilla linen / cloth-{color} (texture)
    public const string Leather = "leather"; // vanilla leather: its texture tiled under the part (rotation only)
    public const string Pelt = "pelt";       // vanilla cured pelts: fur crop of the animal texture tiled (rotation only)
    public const string Wood = "wood";       // sticks, planks: item texture tiled
    public const string Metal = "metal";     // metal bits, nuggets: item texture tiled
    public const string Bone = "bone";       // bones: item texture tiled

    /// <summary>Group of the textile families (cloth, leather, pelt): a part listing it takes any of them.</summary>
    public const string Textile = "textile";
    /// <summary>Group of the hard families (wood, metal, bone): buttons, toggles, clasps. Also the family of garments sewn before families existed.</summary>
    public const string Hard = "hard";

    /// <summary>Kind of a fixed part's built-in material (<see cref="PartDef.FixedTexture"/>): never in a slot.</summary>
    public const string Fixed = "fixed";
}

/// <summary>One material family (cloth, leather, pelt, wood, metal, bone...) and the group it belongs to (textile | hard).</summary>
public class MaterialFamily
{
    [JsonProperty("code")] public string Code = "";
    [JsonProperty("group")] public string Group = MaterialKind.Textile;
}

/// <summary>
/// One vanilla item the sewing slots accept (leather, pelt, vanilla cloth, hard items). Data: <c>config/tailoring-materials.json</c>.
/// <see cref="Code"/> may hold wildcards (<c>game:hide-pelt-bear-brown-*</c>); the first matching entry wins.
/// </summary>
public class MaterialDef
{
    [JsonProperty("code")] public string Code = "";
    /// <summary>Material family code (<see cref="MaterialFamily"/>), e.g. cloth, leather, wood.</summary>
    [JsonProperty("family")] public string Family = MaterialKind.Leather;
    /// <summary>
    /// Texture asset (without <c>textures/</c> and <c>.png</c>), e.g. <c>game:block/leather/plain</c>: the item's own
    /// texture. <c>{*}</c> = what the code's wildcard matched (game:plank-* → game:block/wood/planks/{*}1).
    /// </summary>
    [JsonProperty("texture")] public string Texture = "";
    /// <summary>Texel rectangle [x1, y1, x2, y2] of the texture tiled under a piece; null = the whole texture.</summary>
    [JsonProperty("crop")] public int[]? Crop;
    /// <summary>
    /// Garment texels one item covers. Not used since lane/garment-system (a filled part consumes 1 item); kept for a
    /// later area-based cost.
    /// </summary>
    [JsonProperty("texels")] public int Texels = 512;
    /// <summary>Flat colour when the texture cannot be loaded (also the server's notion of the material).</summary>
    [JsonProperty("color")] public string Color = "#6b4a2e";

    public bool Matches(string itemCode)
        => WildcardUtil.Match(new AssetLocation(Code), new AssetLocation(itemCode));

    /// <summary>
    /// What the single <c>*</c> of <see cref="Code"/> stands for in <paramref name="itemCode"/> (e.g. game:plank-* on
    /// game:plank-oak → "oak"); "" when the code has no wildcard or does not match.
    /// </summary>
    public string WildcardPart(string itemCode)
    {
        var pattern = new AssetLocation(Code);
        var item = new AssetLocation(itemCode);
        int star = pattern.Path.IndexOf('*');
        if (star < 0 || pattern.Domain != item.Domain) return "";
        string pre = pattern.Path.Substring(0, star), post = pattern.Path.Substring(star + 1);
        string path = item.Path;
        if (!path.StartsWith(pre) || !path.EndsWith(post) || path.Length < pre.Length + post.Length) return "";
        return path.Substring(pre.Length, path.Length - pre.Length - post.Length);
    }

    /// <summary>
    /// Texture asset (textures/....png) of an item this entry matched: <see cref="Texture"/> with <c>{*}</c> replaced by
    /// <see cref="WildcardPart"/> (server items carry no textures, so the catalog names them; checked by the smoke).
    /// </summary>
    public AssetLocation TextureLocation(string itemCode)
        => new AssetLocation(Texture.Replace("{*}", WildcardPart(itemCode))).WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png");
}

/// <summary>
/// Material families + the vanilla items in them. Loaded from <c>config/tailoring-materials.json</c> of EVERY domain
/// (another mod adds its own file with more families / materials, or JSON-patches ours): other domains' files come
/// first (their material entries win over ours), families merge by code (first definition wins).
/// </summary>
public class MaterialCatalog
{
    public const string AssetPath = TailorModSystem.Domain + ":config/tailoring-materials.json";
    const string FileName = "tailoring-materials.json";
    const string CacheKey = TailorModSystem.Domain + "-materialcatalog";

    [JsonProperty("families")] public MaterialFamily[] Families = System.Array.Empty<MaterialFamily>();
    [JsonProperty("materials")] public MaterialDef[] Materials = System.Array.Empty<MaterialDef>();

    /// <summary>First entry matching the item code, null when the item is no tailoring material.</summary>
    public MaterialDef? Find(string? itemCode) => itemCode == null ? null : Materials.FirstOrDefault(m => m.Matches(itemCode));

    public MaterialFamily? FindFamily(string? code) => code == null ? null : Families.FirstOrDefault(f => f.Code == code);

    /// <summary>Group (textile | hard) of a family, "" when the family is unknown.</summary>
    public string GroupOf(string? family) => FindFamily(family)?.Group ?? "";

    /// <summary>Group codes the families use (textile, hard, and any a mod adds).</summary>
    public IEnumerable<string> Groups => Families.Select(f => f.Group).Distinct();

    /// <summary>What a garment part may list in <c>materials</c>: a family code or a group code.</summary>
    public bool KnownToken(string token) => FindFamily(token) != null || Groups.Contains(token);

    /// <summary>Families a part's <c>materials</c> list stands for (groups expanded), in catalog order.</summary>
    public string[] Expand(IEnumerable<string> tokens)
    {
        var set = new HashSet<string>(tokens);
        return Families.Where(f => set.Contains(f.Code) || set.Contains(f.Group)).Select(f => f.Code).ToArray();
    }

    /// <summary>Sewing material of a catalog item (family + group resolved), null when the item is not in the catalog.</summary>
    public SlotMaterial? ItemMaterial(string itemCode)
    {
        var def = Find(itemCode);
        return def == null ? null : SlotMaterial.OfItem(def.Family, itemCode, GroupOf(def.Family));
    }

    public static MaterialCatalog Parse(string json)
    {
        var cat = JsonConvert.DeserializeObject<MaterialCatalog>(json) ?? new MaterialCatalog();
        cat.Families ??= System.Array.Empty<MaterialFamily>();
        cat.Materials ??= System.Array.Empty<MaterialDef>();
        foreach (var m in cat.Materials) if (m.Texels < 1) m.Texels = 512;
        return cat;
    }

    /// <summary>Several files merged: materials in the given order, families by code (first definition wins).</summary>
    public static MaterialCatalog Merge(IEnumerable<MaterialCatalog> parts)
    {
        var list = parts.ToList();
        var families = new List<MaterialFamily>();
        foreach (var f in list.SelectMany(c => c.Families)) if (families.All(x => x.Code != f.Code)) families.Add(f);
        return new MaterialCatalog { Families = families.ToArray(), Materials = list.SelectMany(c => c.Materials).ToArray() };
    }

    public static MaterialCatalog Get(ICoreAPI api) => ObjectCacheUtil.GetOrCreate(api, CacheKey, () =>
    {
        var assets = api.Assets.GetManyInCategory("config", FileName, null, true)
            .Where(a => a.Location.Path == "config/" + FileName)
            .OrderBy(a => a.Location.Domain == TailorModSystem.Domain ? 1 : 0).ThenBy(a => a.Location.Domain, System.StringComparer.Ordinal)
            .ToList();
        if (assets.All(a => a.Location.Domain != TailorModSystem.Domain))
        {
            api.Logger.Error("[actailor] tailoring materials asset {0} not found", AssetPath);
        }
        return Merge(assets.Select(a => Parse(a.ToText())));
    });

    /// <summary>Problems in the data alone (texture sizes are checked by the selftest against the PNGs).</summary>
    public static IEnumerable<string> Validate(MaterialCatalog cat)
    {
        foreach (var f in cat.Families)
        {
            if (f.Code.Length == 0) yield return "family without code";
            if (f.Group.Length == 0) yield return $"family {f.Code}: no group";
            if (cat.Families.Any(x => x.Code == f.Group)) yield return $"family {f.Code}: group '{f.Group}' is also a family code";
        }
        foreach (string code in new[] { MaterialKind.Cloth })
        {
            if (cat.FindFamily(code) == null) yield return $"built-in family '{code}' missing (woven cloth is always {code})";
        }
        foreach (var m in cat.Materials)
        {
            if (m.Code.Length == 0) yield return "material without code";
            if (cat.FindFamily(m.Family) == null) yield return $"{m.Code}: family '{m.Family}' ({string.Join(", ", cat.Families.Select(f => f.Code))})";
            if (m.Texture.Length == 0) yield return $"{m.Code}: no texture";
            if (m.Texture.Contains("{*}") && m.Code.Count(c => c == '*') != 1) yield return $"{m.Code}: texture uses {{*}} but the code has not exactly one *";
            var r = m.Crop;
            if (r != null && (r.Length != 4 || r[0] < 0 || r[1] < 0 || r[0] >= r[2] || r[1] >= r[3])) yield return $"{m.Code}: bad crop";
        }
    }
}
