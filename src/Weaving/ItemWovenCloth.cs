using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// Patterned cloth cut from the loom. Stack attributes: <c>pattern</c> (width x height palette indices, row-major,
/// row 0 = first woven), <c>clothWidth</c>/<c>clothHeight</c> (16 when missing; square 16 or 32, lane/square-cloth;
/// old long cloths of lane/cloth-formats may still exist in dev worlds), <c>paletteColors</c>
/// ("#rrggbb"), <c>paletteCodes</c> (twine codes), optional <c>name</c>. Everything reads the cloth at its own size
/// (lane/pattern-density: no 16x16 view, woven patterns are never resampled; decor, tailoring, item, ground pile, shelves).
/// <see cref="TryReadCloth(ItemStack, out byte[], out string[], out int, out int)"/>.
/// Client: the pattern becomes the shape's "color" texture, inserted into the item atlas and cached by hash.
/// Placed (ground pile, shelf, display case): <see cref="IContainedMeshSource"/>; see the ground storage region.
/// </summary>
public partial class ItemWovenCloth : Item, IContainedMeshSource
{
    public const string AttrPattern = "pattern";
    public const string AttrColors = "paletteColors";
    public const string AttrCodes = "paletteCodes";
    public const string AttrName = "name";
    public const string AttrWidth = "clothWidth";
    public const string AttrHeight = "clothHeight";
    /// <summary>Draft of a cloth woven in draft mode (lane/draft-mode, <see cref="DraftExport"/>); absent for tapestry cloth.</summary>
    public const string AttrDraft = "draft";
    const string MeshCacheKey = TailorModSystem.Domain + "-wovencloth-meshes";
    /// <summary>
    /// Texels per pattern cell in every generated cloth texture (lane/cloth-texel): 1, so a 32 x 32 cloth is a 32 x 32 texture
    /// (the size of a vanilla block face texture) and a 16 x 16 cloth a 16 x 16 one. It was 2 (a twill-like over / under shade
    /// per texel pair), which made a 32 cloth 64 x 64 on a wall: four texels per draft cell.
    /// </summary>
    public const int CellPx = 1;
    /// <summary>
    /// Per-cell brightness (lane/cloth-cell-shade) so the weave reads at 1 texel per cell: a cell where the warp shows
    /// (draft: lifted) keeps its colour, a cell where the weft shows is <see cref="WeftShade"/> darker. Without a draft the
    /// cells alternate on parity ((x + y) and 1: even = warp factor), the same two factors.
    /// </summary>
    public const float WarpShade = 1f, WeftShade = 0.88f;

    /// <summary>Brightness of cell (x, y): from <paramref name="warpUp"/> (row-major, w x h) when given, else the parity checker. Pure (selftest).</summary>
    public static float CellShade(int x, int y, int w, bool[]? warpUp)
        => (warpUp != null ? warpUp[y * w + x] : ((x + y) & 1) == 0) ? WarpShade : WeftShade;

    /// <summary>
    /// The cloth stack's lifted-warp mask (<see cref="DraftExport.WarpUp"/>) when it carries a draft matching its size, else null
    /// (tapestry / hand loom / painted cloth: parity shading). Pure (selftest).
    /// </summary>
    public static bool[]? WarpUpOf(ItemStack stack, int w, int h) => ReadWarpUp(stack.Attributes, w, h);

    /// <summary>Attribute holding a decor / garment pattern's warp-up mask (one byte per cell, 1 = warp shows); absent = parity shading.</summary>
    public const string AttrWarpUp = "warpUp";

    /// <summary>
    /// The mask stored in <paramref name="attrs"/> (<see cref="AttrWarpUp"/>, a decor pattern) or, for a cloth stack, derived from its
    /// draft; null when neither matches <paramref name="w"/> x <paramref name="h"/>. Pure (selftest).
    /// </summary>
    public static bool[]? ReadWarpUp(ITreeAttribute? attrs, int w, int h)
    {
        if (attrs == null) return null;
        if (attrs.GetBytes(AttrWarpUp) is { } b) return b.Length == w * h ? b.Select(v => v != 0).ToArray() : null;
        return DraftExport.FromTree(attrs.GetTreeAttribute(AttrDraft)) is { } d && d.WarpCodes.Length == w && d.Treadling.Length == h ? d.WarpUp() : null;
    }

    /// <summary>Writes (or removes, for null) the mask under <see cref="AttrWarpUp"/>.</summary>
    public static void WriteWarpUp(ITreeAttribute attrs, bool[]? warpUp)
    {
        if (warpUp == null) attrs.RemoveAttribute(AttrWarpUp);
        else attrs.SetBytes(AttrWarpUp, warpUp.Select(v => (byte)(v ? 1 : 0)).ToArray());
    }

    /// <summary>Cache key suffix of a shading mask: empty for parity (so those keys are unchanged), else a hash of the mask.</summary>
    public static string ShadeKey(bool[]? warpUp)
    {
        if (warpUp == null) return "";
        ulong h = 14695981039346656037UL;
        foreach (bool b in warpUp) { h ^= b ? 1UL : 0UL; h *= 1099511628211UL; }
        return "-d" + h.ToString("x");
    }

    public static void WritePattern(ItemStack stack, byte[] pattern, string[] codes, string[] colors, string? name,
        int width = WeavingState.DefaultWidth, int height = WeavingState.DefaultRows)
    {
        stack.Attributes.SetBytes(AttrPattern, pattern);
        stack.Attributes.SetInt(AttrWidth, width);
        stack.Attributes.SetInt(AttrHeight, height);
        stack.Attributes[AttrColors] = new StringArrayAttribute(colors);
        stack.Attributes[AttrCodes] = new StringArrayAttribute(codes);
        if (!string.IsNullOrWhiteSpace(name)) stack.Attributes.SetString(AttrName, name.Trim());
    }

    /// <summary>Store the draft the cloth was woven from (draft mode); the cells stay the source of truth for decor and tailoring.</summary>
    public static void WriteDraft(ItemStack stack, DraftExport draft)
    {
        var tree = new TreeAttribute();
        draft.ToTree(tree);
        stack.Attributes[AttrDraft] = tree;
    }

    public static DraftExport? ReadDraft(ItemStack stack) => DraftExport.FromTree(stack.Attributes.GetTreeAttribute(AttrDraft));

    /// <summary>
    /// The cloth as shown at its own size (<paramref name="width"/> x <paramref name="height"/>): the woven pattern with its
    /// embroidery composited (lane/loom-embroidery, <see cref="WovenTexture.Composite(ITreeAttribute)"/>).
    /// </summary>
    public static bool TryReadCloth(ITreeAttribute attrs, out byte[] pattern, out string[] colors, out int width, out int height)
    {
        if (WovenTexture.Composite(attrs) is { } p)
        {
            (pattern, colors, width, height) = (p.Pattern, p.Colors, p.Width, p.Height);
            return true;
        }
        return TryReadWoven(attrs, out pattern, out colors, out width, out height);
    }

    /// <summary>The woven layer alone (no embroidery) at its own size.</summary>
    public static bool TryReadWoven(ITreeAttribute attrs, out byte[] pattern, out string[] colors, out int width, out int height)
    {
        width = attrs.GetInt(AttrWidth, WeavingState.DefaultWidth);
        height = attrs.GetInt(AttrHeight, WeavingState.DefaultRows);
        pattern = attrs.GetBytes(AttrPattern) ?? System.Array.Empty<byte>();
        colors = (attrs[AttrColors] as StringArrayAttribute)?.value ?? System.Array.Empty<string>();
        return width > 0 && height > 0 && pattern.Length == width * height && colors.Length > 0;
    }

    public static bool TryReadCloth(ItemStack stack, out byte[] pattern, out string[] colors, out int width, out int height)
        => TryReadCloth(stack.Attributes, out pattern, out colors, out width, out height);

    // lane/pattern-density: the 16x16 view (TryReadPattern) and the nearest-cell Resample are gone. A 32x32 cloth read
    // as 16x16 kept every other cell: a one-cell weave (tweed, plain-weave colour-and-weave) turned into stripes.

    /// <summary>Stable key for the texture/mesh cache: FNV-1a over pattern bytes and colours.</summary>
    public static string PatternHash(byte[] pattern, string[] colors, int width = WeavingState.DefaultWidth)
    {
        ulong h = 14695981039346656037UL;
        void Mix(byte b) { h ^= b; h *= 1099511628211UL; }
        if (width != WeavingState.DefaultWidth) Mix((byte)width); // same bytes, other shape: another texture
        foreach (byte b in pattern) Mix(b);
        foreach (string c in colors) foreach (char ch in c) Mix((byte)ch);
        return h.ToString("x16");
    }

    public override string GetHeldItemName(ItemStack itemStack)
    {
        string? name = itemStack.Attributes.GetString(AttrName);
        string baseName = base.GetHeldItemName(itemStack);
        return string.IsNullOrEmpty(name) ? baseName : Lang.Get(TailorModSystem.Domain + ":wovencloth-named", name, baseName);
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        var stack = inSlot.Itemstack;
        if (stack == null || !TryReadCloth(stack, out byte[] pattern, out string[] colors, out int cw, out int ch))
        {
            dsc.AppendLine(Lang.Get(TailorModSystem.Domain + ":wovencloth-nopattern"));
            return;
        }
        dsc.AppendLine(Lang.Get(TailorModSystem.Domain + ":wovencloth-size", cw, ch));
        if (ReadDraft(stack) is { } draft)
            dsc.AppendLine(Lang.Get(TailorModSystem.Domain + ":wovencloth-draft", Lang.Get(TailorModSystem.Domain + ":draft-tieup-" + draft.TieUpCode),
                Lang.Get(TailorModSystem.Domain + ":draft-threading-" + draft.ThreadingCode)));
        AppendProducts(stack, cw, ch, dsc);
        if (Embroidery.Read(stack.Attributes, cw, ch) is { IsEmpty: false } emb)
            dsc.AppendLine(Lang.Get(TailorModSystem.Domain + ":wovencloth-embroidered", emb.StitchCount, emb.UsedCodes().Count));
        string[] codes = WovenTexture.Composite(stack)?.Codes ?? System.Array.Empty<string>();
        dsc.AppendLine(Lang.Get(TailorModSystem.Domain + ":wovencloth-colors"));
        for (int i = 0; i < colors.Length; i++)
        {
            int cells = pattern.Count(c => c == i);
            string twineName = i < codes.Length ? TwineName(world, codes[i]) : "?";
            dsc.AppendLine($"<font color=\"{colors[i]}\">■■</font> {twineName} ({cells}/{pattern.Length})");
        }
        if (withDebugInfo) dsc.AppendLine("pattern " + PatternHash(pattern, colors));
    }

    /// <summary>Tooltip lines on what the stack makes (woven cloth: carpets by size; woven linen overrides, lane/linen-output).</summary>
    protected virtual void AppendProducts(ItemStack stack, int cw, int ch, StringBuilder dsc)
    {
        // What this size makes in a 1x1 grid (lane/square-cloth, lane/linen-output; rules: recipes/grid/decor/wovencarpet.json).
        if (SizeMatches(cw, ch, SmallCarpetWidth, SmallCarpetRows)) dsc.AppendLine(Lang.Get(TailorModSystem.Domain + ":wovencloth-fits-smallcarpet"));
    }

    /// <summary>Right click help line: woven cloth needs a hammer; woven linen overrides (no tool, lane/loom-feedback-6).</summary>
    protected virtual string PlaceHelpLangCode => TailorModSystem.Domain + ":decor-help-hangtapestry";

    /// <summary>Cloth in the active hand, hammer in the off hand: right click a block face hangs it as a tapestry.</summary>
    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        if (firstEvent && Decor.TapestryHanging.IsHammer(byEntity.LeftHandItemSlot) && Decor.TapestryHanging.TryHang(byEntity, slot, blockSel))
        {
            handling = EnumHandHandling.PreventDefault;
            return;
        }
        base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
    }

    public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
        => new[]
        {
            new WorldInteraction { ActionLangCode = PlaceHelpLangCode, MouseButton = EnumMouseButton.Right },
        }.Append(base.GetHeldInteractionHelp(inSlot));

    static string TwineName(IWorldAccessor world, string code)
    {
        var item = world.GetItem(new AssetLocation(code));
        return item != null ? item.GetHeldItemName(new ItemStack(item)) : code;
    }

    #region Client rendering

    Dictionary<string, MultiTextureMeshRef> MeshRefs(ICoreClientAPI capi)
        => ObjectCacheUtil.GetOrCreate(capi, MeshCacheKey, () => new Dictionary<string, MultiTextureMeshRef>());

    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        if (TryReadCloth(itemstack, out byte[] pattern, out string[] colors, out int cw, out int ch))
        {
            string hash = PatternHash(pattern, colors, cw);
            var warpUp = WarpUpOf(itemstack, cw, ch);
            string key = RenderCacheKey(Code.ToString(), hash + ShadeKey(warpUp));
            var refs = MeshRefs(capi);
            if (!refs.TryGetValue(key, out var meshRef))
            {
                MeshData? mesh = GenMesh(capi, pattern, colors, hash, cw, ch, warpUp);
                if (mesh != null) refs[key] = meshRef = capi.Render.UploadMultiTextureMesh(mesh);
            }
            if (meshRef != null) renderinfo.ModelRef = meshRef;
        }
        base.OnBeforeRender(capi, itemstack, target, ref renderinfo);
    }

    /// <summary>
    /// Item mesh cache key. Per item as well as per pattern (lane/cloth-fixes): woven linen and the woven cloth made from it
    /// carry the same pattern but have different shapes, and one shared key gave the cloth the linen's folded sheet in hand,
    /// GUI and inventory (or the reverse, whichever rendered first). Pure (selftest).
    /// </summary>
    public static string RenderCacheKey(string itemCode, string patternHash) => itemCode + "|" + patternHash;

    MeshData? GenMesh(ICoreClientAPI capi, byte[] pattern, string[] colors, string hash, int width, int height, bool[]? warpUp = null)
    {
        var loc = new AssetLocation(TailorModSystem.Domain, "wovencloth/" + hash + ShadeKey(warpUp));
        if (!capi.ItemTextureAtlas.GetOrInsertTexture(loc, out _, out TextureAtlasPosition texPos, () => BuildBitmap(pattern, colors, width, height, warpUp)))
        {
            return null;
        }
        var texSource = new PatternTexSource(capi.Tesselator.GetTextureSource(this), texPos, capi.ItemTextureAtlas.Size);
        capi.Tesselator.TesselateItem(this, out MeshData mesh, texSource);
        return mesh;
    }

    /// <summary>
    /// Width x height pattern, one texel per cell (<see cref="CellPx"/>), each cell shaded by <see cref="CellShade"/>
    /// (<paramref name="warpUp"/>: draft lift state, null = parity). Embroidery is already in the pattern, so stitches are shaded like cells.
    /// </summary>
    public static IBitmap BuildBitmap(byte[] pattern, string[] colors, int w = WeavingState.DefaultWidth, int h = WeavingState.DefaultRows, bool[]? warpUp = null)
    {
        if (warpUp != null && warpUp.Length != w * h) warpUp = null;
        int sw = w * CellPx, sh = h * CellPx;
        var pixels = new int[sw * sh];
        for (int py = 0; py < sh; py++)
        {
            for (int px = 0; px < sw; px++)
            {
                byte cell = pattern[(py / CellPx) * w + (px / CellPx)];
                string hex = cell < colors.Length ? colors[cell] : TwineColors.PlainHex;
                pixels[py * sw + px] = TwineColors.ToArgb(hex, CellShade(px / CellPx, py / CellPx, w, warpUp));
            }
        }
        return new BakedBitmap { TexturePixels = pixels, Width = sw, Height = sh };
    }

    /// <summary>
    /// Side in texels of a fitted pattern texture: 1 texel per cell over the uv rectangle (16 units across the texture), so a cloth
    /// of <paramref name="w"/> x <paramref name="h"/> cells in a rectangle <paramref name="uv"/> wide gets ceil(16 w / uv width)
    /// texels (12 cells in 12 units: 16). At least the cloth's own size. Pure (selftest).
    /// </summary>
    public static int FitSize(int w, int h, float[] uv)
        => System.Math.Max(System.Math.Max(w, h), (int)System.MathF.Ceiling(System.MathF.Max(16f * w / (uv[2] - uv[0]), 16f * h / (uv[3] - uv[1])) - 1e-4f));

    /// <summary>
    /// Cell (x, y) shown at texture point (u, v) (0..16) when a <paramref name="w"/> x <paramref name="h"/> pattern is
    /// fitted into the uv rectangle <paramref name="uv"/> (u0, v0, u1, v1); outside it the nearest edge cell (the
    /// cushion's sides sample there). Pure (selftest).
    /// </summary>
    public static (int x, int y) FitCell(float u, float v, int w, int h, float[] uv)
    {
        int x = (int)System.MathF.Floor((u - uv[0]) / (uv[2] - uv[0]) * w);
        int y = (int)System.MathF.Floor((v - uv[1]) / (uv[3] - uv[1]) * h);
        return (System.Math.Clamp(x, 0, w - 1), System.Math.Clamp(y, 0, h - 1));
    }

    /// <summary>Square texture (<see cref="FitSize"/>): the pattern fitted into uv rectangle <paramref name="uv"/> (see <see cref="FitCell"/>), parity-shaded like <see cref="BuildBitmap"/>.</summary>
    public static IBitmap BuildFitBitmap(byte[] pattern, string[] colors, int w, int h, float[] uv)
    {
        int size = FitSize(w, h, uv);
        float perUnit = size / 16f;
        var pixels = new int[size * size];
        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                var (cx, cy) = FitCell((px + 0.5f) / perUnit, (py + 0.5f) / perUnit, w, h, uv);
                byte cell = pattern[cy * w + cx];
                string hex = cell < colors.Length ? colors[cell] : TwineColors.PlainHex;
                pixels[py * size + px] = TwineColors.ToArgb(hex, CellShade(cx, cy, w, null));
            }
        }
        return new BakedBitmap { TexturePixels = pixels, Width = size, Height = size };
    }

    #region Products by size (lane/cloth-formats, lane/square-cloth)

    /// <summary>
    /// 1x1 grid product (recipes/grid/decor/wovencarpet.json, kept in sync by the selftest): any square cloth (16 x 16 or
    /// 32 x 32) makes a small carpet (lane/linen-output: the 2x2 large carpet from one 32 x 32 cloth is gone; the large
    /// carpet is 1x2 now, from two cloths or woven on the loom).
    /// </summary>
    public static readonly int[] SmallCarpetWidth = { 16, 32 }, SmallCarpetRows = { 16, 32 };

    /// <summary>
    /// Grid recipe ingredient rule (ingredient <c>recipeAttributes</c>): <c>clothWidth: [min, max]</c>,
    /// <c>clothRows: [min, max]</c>; missing = any. Pure (selftest).
    /// </summary>
    public static bool SizeMatches(int width, int rows, int[]? widthRange, int[]? rowsRange)
        => InRange(width, widthRange) && InRange(rows, rowsRange) && (widthRange == null || rowsRange == null || width == rows); // both ranges: square

    static bool InRange(int n, int[]? r) => r is not { Length: 2 } || (n >= r[0] && n <= r[1]);

    /// <summary>Products accept cloths by size: the ingredient's recipeAttributes clothWidth / clothRows ranges.</summary>
    public override bool MatchesForCrafting(ItemStack inputStack, IRecipeBase recipe, IRecipeIngredient ingredient)
    {
        var ra = ingredient.RecipeAttributes;
        if (ra != null && ra.Exists)
        {
            int w = inputStack.Attributes.GetInt(AttrWidth, WeavingState.DefaultWidth);
            int h = inputStack.Attributes.GetInt(AttrHeight, WeavingState.DefaultRows);
            if (!SizeMatches(w, h, ra["clothWidth"].AsArray<int>(null), ra["clothRows"].AsArray<int>(null))) return false;
        }
        return base.MatchesForCrafting(inputStack, recipe, ingredient);
    }

    #endregion

    public override void OnUnloaded(ICoreAPI api)
    {
        if (api is ICoreClientAPI capi && api.ObjectCache.ContainsKey(MeshCacheKey))
        {
            foreach (var meshRef in MeshRefs(capi).Values) meshRef.Dispose();
            ObjectCacheUtil.Delete(api, MeshCacheKey);
        }
        base.OnUnloaded(api);
    }

    class PatternTexSource : ITexPositionSource
    {
        readonly ITexPositionSource fallback;
        readonly TextureAtlasPosition pattern;
        public Size2i? AtlasSize { get; }

        public PatternTexSource(ITexPositionSource fallback, TextureAtlasPosition pattern, Size2i atlasSize)
        {
            this.fallback = fallback;
            this.pattern = pattern;
            AtlasSize = atlasSize;
        }

        public TextureAtlasPosition? this[string textureCode] => textureCode == "color" ? pattern : fallback[textureCode];
    }

    #endregion
}
