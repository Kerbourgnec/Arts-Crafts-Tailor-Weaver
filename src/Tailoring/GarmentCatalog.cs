using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// One part of a garment recipe (like an ingredient of a vanilla cooking recipe): texel regions of the garment
/// texture (half-open [x1,y1,x2,y2]), the material kinds it takes, whether it may stay empty, and the shape elements
/// that only exist when it is filled. A filled part consumes 1 item of its material (lane/garment-system).
/// </summary>
public class PartOption
{
    /// <summary>Option code (unique within the part), stored in stacks (<c>pieceOption</c>).</summary>
    [JsonProperty("code")] public string Code = "";
    /// <summary>Lang key (without domain) of the option's name; default <c>part-{part}-{option}</c>.</summary>
    [JsonProperty("name")] public string? Name;
    /// <summary>Texel regions of this variant on the garment texture (half-open); required.</summary>
    [JsonProperty("regions")] public int[][] Regions = System.Array.Empty<int[]>();
    /// <summary>Mask / layer / shading: omitted = the part's own value (filled in at load).</summary>
    [JsonProperty("mask")] public int[][]? Mask;
    [JsonProperty("layer")] public int? Layer;
    [JsonProperty("shade")] public bool? Shade;
    [JsonProperty("highlight")] public bool? Highlight;
    /// <summary>Shape elements of this variant (children go with them): shown only while it is chosen (an optional part: and filled).</summary>
    [JsonProperty("elements")] public string[]? Elements;
}

/// <summary>
/// One part of a garment recipe (like an ingredient of a vanilla cooking recipe): texel regions of the garment
/// texture (half-open [x1,y1,x2,y2]), the material kinds it takes, whether it may stay empty, and the shape elements
/// that only exist when it is filled. A filled part consumes 1 item of its material (lane/garment-system).
/// lane/modular-parts: a part may list <see cref="Options"/> (variants with their own geometry and texture regions); it
/// still has one sewing slot and one material. The catalog def carries the first option; <see cref="GarmentDef.Resolve"/>
/// gives the def of any choice (docs/garments.md "Modular parts").
/// </summary>
public class PartDef
{
    /// <summary>Variants (lane/modular-parts); empty = a plain part. Kept on every resolved copy.</summary>
    [JsonProperty("options")] public PartOption[] Options = System.Array.Empty<PartOption>();
    /// <summary>Resolved copy only: index of the option these fields come from (-1 = plain part).</summary>
    [JsonIgnore] public int Chosen = -1;
    public PartDef Copy() => (PartDef)MemberwiseClone();
    /// <summary>Lang key (without domain) of option <paramref name="k"/>.</summary>
    public string OptionName(int k) => Options[k].Name ?? $"part-{Code}-{Options[k].Code}";
    /// <summary>This part with option <paramref name="k"/>'s regions, mask, layer, shading and elements (options are complete after load).</summary>
    public PartDef WithOption(int k)
    {
        var o = Options[k];
        var c = Copy();
        c.Regions = o.Regions;
        c.Mask = o.Mask;
        c.Layer = o.Layer ?? Layer;
        c.Shade = o.Shade ?? Shade;
        c.Highlight = o.Highlight ?? Highlight;
        c.Elements = o.Elements ?? System.Array.Empty<string>();
        c.Chosen = k;
        return c;
    }

    [JsonProperty("code")] public string Code = "";
    /// <summary>Unwrap regions on the garment texture (texels, half-open). The first part listed owns overlapping texels.</summary>
    [JsonProperty("regions")] public int[][] Regions = System.Array.Empty<int[]>();
    /// <summary>
    /// Material families this part can be cut from (lane/garment-families): family codes (cloth, leather, pelt, wood,
    /// metal, bone, or a mod's) and/or group codes (textile = any textile family, hard = any hard family), see
    /// config/tailoring-materials.json. Omitted = cloth only.
    /// </summary>
    [JsonProperty("materials")] public string[] Materials = { MaterialKind.Cloth };
    /// <summary>Optional part: the garment can be sewn without it; its <see cref="Elements"/> are then removed from the model.</summary>
    [JsonProperty("optional")] public bool Optional;
    /// <summary>Shape elements (by name, children go with them) that belong to this part: hidden while it is empty.</summary>
    [JsonProperty("elements")] public string[] Elements = System.Array.Empty<string>();

    /// <summary>
    /// Part this one was split off (e.g. the tunic collar from the body): a stored garment without this part's
    /// code takes that part's assignment for it, so garments sewn before the split look unchanged.
    /// </summary>
    [JsonProperty("splitFrom")] public string? SplitFrom;

    /// <summary>
    /// Premade part (lane/garment-families), e.g. a brass buckle: texture asset (like a material texture, without
    /// <c>textures/</c> and <c>.png</c>) the part always shows, tiled 1:1. A fixed part has no sewing slot, takes no
    /// material, is never optional and always rendered; <see cref="Materials"/> is ignored.
    /// </summary>
    [JsonProperty("fixedTexture")] public string? FixedTexture;
    /// <summary>Texel rectangle [x1, y1, x2, y2] of <see cref="FixedTexture"/> tiled under the part; null = the whole texture.</summary>
    [JsonProperty("fixedCrop")] public int[]? FixedCrop;
    /// <summary>Flat colour of a fixed part when its texture cannot be loaded.</summary>
    [JsonProperty("fixedColor")] public string FixedColor = "#b8963c";

    /// <summary>
    /// Layer (lane/garment-layers, default 0 = base): a texel covered by parts of several layers shows the highest
    /// layer whose part is present (filled, required or fixed); an EMPTY optional part shows the layer below
    /// (pants → suspenders → buttons). Within one layer the first listed part owns shared texels (as before layers).
    /// </summary>
    [JsonProperty("layer")] public int Layer;
    /// <summary>
    /// Mask (lane/garment-layers): rectangles [x1,y1,x2,y2] (half-open texels) of the texels of <see cref="Regions"/> the
    /// part owns, e.g. the pearls drawn on a scarf. Null = every texel of the regions. The pattern / texture origin stays
    /// the regions' top-left (<see cref="GarmentLayout.PartOrigin"/>), so a mask never moves a pattern. Candidate masks:
    /// scripts/garment-segment.py.
    /// </summary>
    [JsonProperty("mask")] public int[][]? Mask;
    /// <summary>
    /// Shade the part (lane/garment-layers): the brightness of the garment's <see cref="GarmentDef.ShadingTexture"/> within
    /// this part (minus texels higher layers cover) is applied to whatever material is sewn. Only allowed where those
    /// vanilla texels are one material (<see cref="GarmentShading.Stats"/>, checked by the selftest).
    /// </summary>
    [JsonProperty("shade")] public bool Shade;
    /// <summary>Shading may also lighten (texels brighter than the part's median); default: darken only (folds, shadows).</summary>
    [JsonProperty("highlight")] public bool Highlight;

    public bool IsFixed => !string.IsNullOrEmpty(FixedTexture);

    /// <summary>Whether the part takes material family <paramref name="family"/> of group <paramref name="group"/> (listed itself or by its group).</summary>
    public bool Allows(string? family, string? group = null)
        => !IsFixed && ((family != null && System.Array.IndexOf(Materials, family) >= 0) || (!string.IsNullOrEmpty(group) && System.Array.IndexOf(Materials, group) >= 0));

    public bool Allows(SlotMaterial? m) => m != null && Allows(m.Kind, m.Group);
}

/// <summary>Texels painted with a fixed colour under the parts (e.g. the dark inner "Blackout" box of a shirt).</summary>
public class FixedFill
{
    [JsonProperty("rect")] public int[] Rect = System.Array.Empty<int>();
    [JsonProperty("color")] public string Color = "#000000";
}

/// <summary>
/// A garment recipe: one JSON file per garment under <c>assets/&lt;domain&gt;/config/garments/</c> (docs/garments.md).
/// Texture grid = the garment shape's texture (vanilla seraph clothing: 2 texels per shape unit, e.g. shortsleeve
/// 28x44 units = 56x88 texels). The output item <c>actailor:garment-{code}</c> is generated from these files into
/// <c>itemtypes/garment.json</c> by <c>scripts/garment-items.py</c> (dress type, warmth, shape, fallback textures).
/// </summary>
public class GarmentDef
{
    [JsonProperty("code")] public string Code = "";
    /// <summary>Output item code; default <c>actailor:garment-{code}</c>.</summary>
    [JsonProperty("item")] public string Item = "";
    /// <summary>Item codes of older stacks that are this garment (e.g. <c>actailor:garment-trousers</c> → pants).</summary>
    [JsonProperty("legacyItems")] public string[] LegacyItems = System.Array.Empty<string>();
    /// <summary>Order of the garment buttons (then code).</summary>
    [JsonProperty("order")] public int Order;
    /// <summary>Vanilla dress slot (EnumCharacterDressType, lower case): the item's <c>clothescategory</c>.</summary>
    [JsonProperty("dressType")] public string DressType = "";
    /// <summary>Fixed warmth of the garment (item attribute <c>warmth</c>), whatever the materials (for now).</summary>
    [JsonProperty("warmth")] public float Warmth;
    /// <summary>Model: usually a vanilla clothing shape.</summary>
    [JsonProperty("shape")] public string Shape = "";
    /// <summary>Optional extra elements (our generated shape, same texture units) added to <see cref="Shape"/>, e.g. buttons.</summary>
    [JsonProperty("extraShape")] public string? ExtraShape;
    /// <summary>Elements of the model never shown by this garment (e.g. the sleeves of a vest made from a coat shape).</summary>
    [JsonProperty("hideElements")] public string[] HideElements = System.Array.Empty<string>();
    /// <summary>
    /// Body areas the garment leaves bare (lane/garments-3), e.g. <c>["legs", "arm-left"]</c> for a toga: no shown
    /// element of the model may hang on their bones (selftest). Tokens: <see cref="BareBones"/>.
    /// </summary>
    [JsonProperty("bare")] public string[] Bare = System.Array.Empty<string>();

    /// <summary>Seraph bones of each <see cref="Bare"/> token (children of a step-parented element share its bone).</summary>
    public static readonly Dictionary<string, string[]> BareBones = new()
    {
        ["head"] = new[] { "Head" },
        ["neck"] = new[] { "Neck" },
        ["arms"] = new[] { "UpperArmL", "UpperArmR", "LowerArmL", "LowerArmR" },
        ["arm-left"] = new[] { "UpperArmL", "LowerArmL" },
        ["arm-right"] = new[] { "UpperArmR", "LowerArmR" },
        ["forearms"] = new[] { "LowerArmL", "LowerArmR" },
        ["legs"] = new[] { "UpperFootL", "UpperFootR", "LowerFootL", "LowerFootR" },
        ["thighs"] = new[] { "UpperFootL", "UpperFootR" },
        ["shins"] = new[] { "LowerFootL", "LowerFootR" },
    };

    /// <summary>Bones the garment must not cover (from <see cref="Bare"/>).</summary>
    public HashSet<string> BareBoneSet() => new(Bare.Where(BareBones.ContainsKey).SelectMany(t => BareBones[t]), System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Shape texture code the composed texture replaces (e.g. "shirt", "pants").</summary>
    [JsonProperty("textureKey")] public string TextureKey = "";
    [JsonProperty("textureWidth")] public int TextureWidth;
    [JsonProperty("textureHeight")] public int TextureHeight;
    // No per-garment density (lane/pattern-density): one cloth cell = one texel everywhere (GarmentLayout.CellTexels);
    // an old recipe's "cellTexels" field is ignored.
    [JsonProperty("parts")] public PartDef[] Parts = System.Array.Empty<PartDef>();
    /// <summary>Fixed-colour texels outside every part (no material).</summary>
    [JsonProperty("fixed")] public FixedFill[] Fixed = System.Array.Empty<FixedFill>();
    /// <summary>Item type fields copied by scripts/garment-items.py (textures fallback, transforms); ignored at runtime.</summary>
    [JsonProperty("itemType")] public JObject? ItemType;
    /// <summary>
    /// Shading source (lane/garment-layers): texture asset (like <see cref="PartDef.FixedTexture"/>) drawn on this
    /// garment's texture layout, at most the garment's size (top-left aligned), usually the vanilla texture of the model.
    /// Parts with <see cref="PartDef.Shade"/> take its brightness. Null = no shading.
    /// </summary>
    [JsonProperty("shadingTexture")] public string? ShadingTexture;
    /// <summary>How strongly shading applies (1 = the vanilla brightness ratios, 0 = none).</summary>
    [JsonProperty("shadeStrength")] public float ShadeStrength = 1f;
    /// <summary>
    /// Body-space wrap (lane/body-wrap): <c>continue</c> = sleeves (arm segments) take the torso's cloth placement and
    /// run on from it at the shoulder; <c>separate</c> = each arm segment has its own. Default of the sewing session; the
    /// dialog toggles it per garment in progress (<see cref="WrapLayout.Sleeves"/>).
    /// </summary>
    [JsonProperty("sleeves")] public string Sleeves = WrapLayout.SleevesContinue;

    int[]? owner;
    bool[][]? coverage;

    /// <summary>
    /// Modular parts (lane/modular-parts): option index per part (-1 = a plain part); length = <see cref="Parts"/>. The catalog def
    /// holds the first option of every part with options; <see cref="Resolve"/> returns the def of another choice.
    /// </summary>
    [JsonIgnore] public int[] Choice = System.Array.Empty<int>();
    /// <summary>The catalog def a resolved copy came from (null on the catalog def itself).</summary>
    [JsonIgnore] public GarmentDef? Source;
    [JsonIgnore] public GarmentDef Root => Source ?? this;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, GarmentDef> resolved = new();

    public bool HasOptions => Parts.Any(p => p.Options.Length > 0);
    /// <summary>Parts a player chooses a variant of (two or more options).</summary>
    public IEnumerable<int> ChoosableParts => Enumerable.Range(0, Parts.Length).Where(p => Parts[p].Options.Length > 1);

    /// <summary>Choice array of a choice read from a stack / session: wrong length or out-of-range entries fall back to the first option.</summary>
    public int[] NormalizeChoice(int[]? choice)
    {
        var r = new int[Parts.Length];
        for (int p = 0; p < r.Length; p++)
        {
            int n = Parts[p].Options.Length;
            r[p] = n == 0 ? -1 : choice != null && p < choice.Length && choice[p] >= 0 && choice[p] < n ? choice[p] : 0;
        }
        return r;
    }

    public static string ChoiceKey(int[] choice) => string.Join(",", choice);

    /// <summary>
    /// The def with option <c>choice[p]</c> of every part with options (null = the first of each). Cached per choice; the
    /// catalog def is returned for the first-option choice. Resolved defs share everything but <see cref="Parts"/> (regions,
    /// masks, layers, shading, elements of the chosen options) and have their own coverage caches.
    /// </summary>
    public GarmentDef Resolve(int[]? choice)
    {
        var root = Root;
        var c = root.NormalizeChoice(choice);
        if (root.Choice.SequenceEqual(c)) return root;
        return root.resolved.GetOrAdd(ChoiceKey(c), _ =>
        {
            var g = (GarmentDef)root.MemberwiseClone();
            g.owner = null;
            g.coverage = null;
            g.Source = root;
            g.Choice = c;
            g.Parts = root.Parts.Select((p, i) => c[i] >= 0 ? p.WithOption(c[i]) : p).ToArray();
            return g;
        });
    }

    /// <summary>Every choice of this recipe (the cartesian product of the parts' options); one all-default choice when it has none.</summary>
    public IEnumerable<int[]> AllChoices()
    {
        IEnumerable<int[]> acc = new[] { System.Array.Empty<int>() };
        foreach (var p in Parts)
        {
            int n = System.Math.Max(1, p.Options.Length);
            acc = acc.SelectMany(a => Enumerable.Range(0, n).Select(k => a.Append(p.Options.Length == 0 ? -1 : k).ToArray())).ToArray();
        }
        return acc;
    }

    /// <summary>Elements of the options not chosen: never shown (the model carries every option's elements).</summary>
    public string[] UnchosenElements()
    {
        var keep = new HashSet<string>(Parts.SelectMany(p => p.Elements), System.StringComparer.OrdinalIgnoreCase);
        return Parts.Where(p => p.Options.Length > 0)
            .SelectMany(p => p.Options.Where((_, k) => k != p.Chosen).SelectMany(o => o.Elements ?? System.Array.Empty<string>()))
            .Where(e => !keep.Contains(e)).Distinct().ToArray();
    }

    /// <summary>Texels part <paramref name="p"/> (with the chosen option) owns: what its material covers (fabric use of the choice).</summary>
    public int PartArea(int p) => Coverage[p].Count(b => b);

    /// <summary>
    /// Texels each part covers (row-major): regions &amp; mask, minus texels an earlier part of the same layer covers.
    /// Parts of different layers overlap freely.
    /// </summary>
    public bool[][] Coverage => coverage ??= GarmentLayout.BuildCoverage(this);

    /// <summary>Topmost part covering each texel (highest layer; row-major, -1 = no part): the part the canvas selects.</summary>
    public int[] Owner => owner ??= GarmentLayout.BuildOwnerMask(this);

    public int PartIndex(string code) => System.Array.FindIndex(Parts, p => p.Code == code);

    public bool IsItem(string itemCode) => Item == itemCode || System.Array.IndexOf(LegacyItems, itemCode) >= 0;

    /// <summary>
    /// Elements removed from the model: <see cref="HideElements"/> plus the elements of every optional part that is
    /// empty (<paramref name="filled"/> by part index). Required parts always show (grey while empty, preview only).
    /// </summary>
    public string[] HiddenElements(System.Func<int, bool> filled)
    {
        var list = new List<string>(HideElements);
        list.AddRange(UnchosenElements()); // lane/modular-parts
        for (int p = 0; p < Parts.Length; p++)
        {
            if (Parts[p].Optional && !filled(p)) list.AddRange(Parts[p].Elements);
        }
        return list.Distinct().ToArray();
    }
}

public class GarmentCatalog
{
    /// <summary>Category-relative folder of the garment recipes, any domain (other mods may add garments).</summary>
    public const string Folder = "garments/";
    const string CacheKey = TailorModSystem.Domain + "-garmentcatalog";

    /// <summary>Vanilla dress slots (EnumCharacterDressType, lower case) a garment may use.</summary>
    public static readonly string[] DressTypes =
        { "head", "face", "neck", "shoulder", "upperbody", "upperbodyover", "arm", "hand", "waist", "lowerbody", "foot", "emblem" };

    public GarmentDef[] Garments = System.Array.Empty<GarmentDef>();

    public GarmentDef? Find(string? code) => code == null ? null : Garments.FirstOrDefault(g => g.Code == code);
    public GarmentDef? FindByItem(string itemCode) => Garments.FirstOrDefault(g => g.IsItem(itemCode));

    /// <summary>One garment recipe (lenient JSON); defaults filled in.</summary>
    public static GarmentDef ParseGarment(string json)
    {
        var g = JsonConvert.DeserializeObject<GarmentDef>(json) ?? new GarmentDef();
        if (string.IsNullOrEmpty(g.Item)) g.Item = TailorModSystem.Domain + ":garment-" + g.Code;
        g.LegacyItems ??= System.Array.Empty<string>();
        g.HideElements ??= System.Array.Empty<string>();
        g.Bare ??= System.Array.Empty<string>();
        g.Sleeves ??= WrapLayout.SleevesContinue;
        foreach (var p in g.Parts)
        {
            p.Materials ??= new[] { MaterialKind.Cloth };
            p.Elements ??= System.Array.Empty<string>();
            p.Options ??= System.Array.Empty<PartOption>();
            foreach (var o in p.Options) // options complete: omitted fields = the part's
            {
                o.Regions ??= System.Array.Empty<int[]>();
                o.Mask ??= p.Mask;
                o.Layer ??= p.Layer;
                o.Shade ??= p.Shade;
                o.Highlight ??= p.Highlight;
                o.Elements ??= System.Array.Empty<string>();
            }
        }
        // The catalog def shows the first option of every part with options (so every consumer of the plain def, items, scripts, old stacks, is unchanged).
        for (int p = 0; p < g.Parts.Length; p++) if (g.Parts[p].Options.Length > 0) g.Parts[p] = g.Parts[p].WithOption(0);
        g.Choice = g.Parts.Select(p => p.Options.Length > 0 ? 0 : -1).ToArray();
        return g;
    }

    /// <summary>Catalog from recipe texts, sorted by order then code.</summary>
    public static GarmentCatalog FromTexts(IEnumerable<string> jsons)
        => new() { Garments = jsons.Select(ParseGarment).OrderBy(g => g.Order).ThenBy(g => g.Code, System.StringComparer.Ordinal).ToArray() };

    /// <summary>Catalog for this side, loaded once from <c>config/garments/*.json</c> of every domain.</summary>
    public static GarmentCatalog Get(ICoreAPI api) => ObjectCacheUtil.GetOrCreate(api, CacheKey, () =>
    {
        var assets = api.Assets.GetManyInCategory("config", Folder, null, true);
        if (assets.Count == 0) api.Logger.Error("[actailor] no garment recipes found in config/{0}", Folder);
        return FromTexts(assets.Select(a => a.ToText()));
    });

    static bool BadRect(int[] r, int w, int h) => r.Length != 4 || r[0] < 0 || r[1] < 0 || r[2] > w || r[3] > h || r[0] >= r[2] || r[1] >= r[3];

    /// <summary>
    /// Problems in the recipes alone (shape coverage and material items are checked by selftest / smoke).
    /// <paramref name="materials"/>: families catalog; part material tokens must be a family or group of it (null = not checked).
    /// </summary>
    public static IEnumerable<string> Validate(GarmentCatalog cat, MaterialCatalog? materials = null)
    {
        foreach (var dup in cat.Garments.GroupBy(g => g.Code).Where(x => x.Count() > 1)) yield return dup.Key + ": duplicate garment code";
        foreach (var g in cat.Garments)
        {
            if (g.Code.Length == 0) yield return "garment without code";
            if (System.Array.IndexOf(DressTypes, g.DressType) < 0) yield return $"{g.Code}: dressType '{g.DressType}' ({string.Join(", ", DressTypes)})";
            if (g.Shape.Length == 0) yield return g.Code + ": no shape";
            if (g.TextureKey.Length == 0) yield return g.Code + ": no textureKey";
            if (g.TextureWidth <= 0 || g.TextureHeight <= 0) yield return g.Code + ": texture size";
            if (g.Parts.Length == 0) yield return g.Code + ": no parts";
            if (g.Parts.Length > TailoringSession.MaxParts) yield return $"{g.Code}: {g.Parts.Length} parts (max {TailoringSession.MaxParts})";
            if (g.Parts.All(p => p.Optional || p.IsFixed)) yield return g.Code + ": no required part to sew (every part optional or fixed)";
            if (g.Warmth < 0) yield return g.Code + ": negative warmth";
            foreach (string b in g.Bare.Where(b => !GarmentDef.BareBones.ContainsKey(b))) yield return $"{g.Code}: bare '{b}' ({string.Join(", ", GarmentDef.BareBones.Keys)})";
            foreach (var f in g.Fixed)
            {
                if (BadRect(f.Rect, g.TextureWidth, g.TextureHeight)) yield return $"{g.Code}/fixed: bad rect [{string.Join(",", f.Rect)}]";
            }
            foreach (var dup in g.Parts.GroupBy(p => p.Code).Where(x => x.Count() > 1)) yield return $"{g.Code}/{dup.Key}: duplicate part code";
            foreach (var p in g.Parts)
            {
                if (p.Code.Length == 0) yield return g.Code + ": part without code";
                if (p.Materials.Length == 0 || (materials != null && p.Materials.Any(k => !materials.KnownToken(k))))
                {
                    string known = materials == null ? "" : string.Join(", ", materials.Families.Select(f => f.Code).Concat(materials.Groups));
                    yield return $"{g.Code}/{p.Code}: materials [{string.Join(",", p.Materials)}] (families or groups: {known})";
                }
                if (p.Options.Length > 0)
                {
                    if (p.IsFixed) yield return $"{g.Code}/{p.Code}: a fixed part cannot have options";
                    foreach (var dup in p.Options.GroupBy(o => o.Code).Where(x => x.Count() > 1)) yield return $"{g.Code}/{p.Code}: duplicate option code '{dup.Key}'";
                    foreach (var o in p.Options.Where(o => o.Code.Length == 0)) yield return $"{g.Code}/{p.Code}: option without code";
                }
                for (int k = 0; k < System.Math.Max(1, p.Options.Length); k++)
                {
                    var v = p.Options.Length == 0 ? p : p.WithOption(k);
                    string at = p.Options.Length == 0 ? $"{g.Code}/{p.Code}" : $"{g.Code}/{p.Code}[{p.Options[k].Code}]";
                    foreach (string problem in ValidateVariant(g, v, at)) yield return problem;
                }
            }
            if (g.ShadeStrength < 0 || g.ShadeStrength > 1) yield return $"{g.Code}: shadeStrength {g.ShadeStrength} (0..1)";
            if (g.Sleeves != WrapLayout.SleevesContinue && g.Sleeves != WrapLayout.SleevesSeparate)
                yield return $"{g.Code}: sleeves '{g.Sleeves}' ({WrapLayout.SleevesContinue} | {WrapLayout.SleevesSeparate})";
        }
    }

    /// <summary>Checks of one part, or of one option of a part (<paramref name="p"/> = the part with that option's fields).</summary>
    static IEnumerable<string> ValidateVariant(GarmentDef g, PartDef p, string at)
    {
        if (p.Regions.Length == 0) yield return $"{at}: no regions";
        foreach (var r in p.Regions)
        {
            if (BadRect(r, g.TextureWidth, g.TextureHeight)) yield return $"{at}: region [{string.Join(",", r)}] outside {g.TextureWidth}x{g.TextureHeight}";
        }
        if (p.IsFixed && p.Optional) yield return $"{at}: a fixed part (fixedTexture) cannot be optional";
        var fc = p.FixedCrop;
        if (fc != null && (!p.IsFixed || fc.Length != 4 || fc[0] < 0 || fc[1] < 0 || fc[0] >= fc[2] || fc[1] >= fc[3])) yield return $"{at}: bad fixedCrop (or no fixedTexture)";
        if (p.Elements.Length > 0 && !p.Optional && p.Chosen < 0) yield return $"{at}: elements listed on a required part (only optional parts hide elements)";
        if (p.SplitFrom != null && g.PartIndex(p.SplitFrom) < 0) yield return $"{at}: splitFrom '{p.SplitFrom}' unknown";
        if (p.Layer < 0) yield return $"{at}: layer {p.Layer} (0 = base, higher = on top)";
        if (p.Shade && string.IsNullOrEmpty(g.ShadingTexture)) yield return $"{at}: shade without the garment's shadingTexture";
        if (p.Mask != null)
        {
            if (p.Mask.Length == 0) yield return $"{at}: empty mask (omit it to own the whole regions)";
            foreach (var r in p.Mask)
            {
                if (BadRect(r, g.TextureWidth, g.TextureHeight)) yield return $"{at}: mask rect [{string.Join(",", r)}] outside {g.TextureWidth}x{g.TextureHeight}";
            }
            if (p.Mask.All(r => !BadRect(r, g.TextureWidth, g.TextureHeight)) && p.Regions.All(r => !BadRect(r, g.TextureWidth, g.TextureHeight)))
            {
                int outside = GarmentLayout.MaskOutsideRegions(p);
                if (outside > 0) yield return $"{at}: {outside} mask texels outside the part's regions";
            }
        }
    }
}
