using System;
using System.Collections.Generic;
using ArtsAndCrafts.Tailor.Loom;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// Everything the loom mesh is built from (live BE state; read on the client main thread).
/// <paramref name="HideCurrentRow"/>: draw no cells for the current row, only the bare warp threads across the fell (during the
/// beat cycle, lane/loom-feedback-4; bare threads instead of dark empty cells since lane/playtest-fixes-5).
/// <paramref name="HideShuttles"/>: draw no shuttles (while the cycle swings the beater, lane/shuttle-fix).
/// <paramref name="GrowFraction"/> (lane/loom-feedback-6, like Immersive Fibercraft's cloth growing pick by pick): during the
/// beat cycle the row just woven is drawn back at the fell, its first <c>GrowFraction</c> (0..1, older edge first) woven and
/// the rest bare warp; the cloth advances one slot when the cycle ends. Negative = off (normal layout).
/// </summary>
public sealed record LoomLook(WeavingState Weaving, SpoolRack Shuttles, bool Unrolled, System.Func<string, string> TwineHex, bool HideCurrentRow = false,
    bool HideShuttles = false, float GrowFraction = -1f);

/// <summary>
/// Client: the cloth, warp threads and shuttles on the loom as a chunk mesh, laid out by <see cref="LoomGeometry"/>
/// (geometry frame, turned like the selection boxes). Cloth: one flat element per cell (two-sided) along the cloth path:
/// upper warp from the fell to the breast beam, over its front, back under the warp, around the cloth beam roll; or,
/// unrolled, the rows beyond the warp run on over the bench and down its front (no plain drape). Path slots without a
/// woven row show plain leader cloth (the block's <c>warpnewnewbase</c> texture), as the model's hidden cloth sheets did.
/// Warp (lane/warp-shuttles-import): per end a thread roll on the warp beam and its string to the fell, in the end's
/// colour. Shuttles: a wooden body each, with the twine on it.
/// Each face uses a tiny per-colour texture inserted into the block atlas (bounded by distinct colours x 2
/// variants, not by patterns). Build on the main thread only: atlas insertion uploads to the GPU.
/// </summary>
public static class LoomClothMesh
{
    const string TexCacheKey = TailorModSystem.Domain + "-loomcell-tex";
    const string EmptyKey = "e";
    /// <summary>Block texture key (blocktypes/loom.json) used through the fallback source: plain leader cloth.</summary>
    const string LeaderTexture = "warpnewnewbase";
    /// <summary>Highlight of the active shuttle and the warp tags (not the cloth): mix this much white into the twine colour.</summary>
    const float HighlightMix = 0.35f;

    static string Key(string hex, bool highlight) => (highlight ? "h" : "w") + hex;

    public static MeshData? Build(ICoreClientAPI capi, Block block, LoomLook look)
    {
        var state = look.Weaving;
        var elements = new List<ShapeElement>();
        var keys = new HashSet<string>();

        // Cloth cells are never highlighted (lane/loom-feedback-8, owner: the row being woven looked lighter than the cloth):
        // the fell row and the row growing during the beat use the twine colours as woven.
        string CellKey(byte cell)
        {
            string key = cell == WeavingState.Empty ? EmptyKey : Key(state.ColorOf(cell), false);
            keys.Add(key);
            return key;
        }

        // Growing row (see LoomLook.GrowFraction): the layout is the one before the beat, the just-woven row at the fell.
        bool growing = look.GrowFraction >= 0 && state.WovenRows > 0;
        int shownWoven = growing ? state.WovenRows - 1 : state.WovenRows;

        void AddRow(int row, ClothPiece piece)
        {
            var strip = piece.Strip;
            if (growing && row == shownWoven)
            {
                var (woven, bare) = LoomGeometry.GrowStrips(piece, look.GrowFraction);
                if (bare is { } b) AddBareWarp(look, b, elements, keys);
                if (woven is not { } wv) return;
                float cw = LoomGeometry.CellWidthFor(state.Width);
                for (int c = 0; c < state.Width; c++)
                    elements.Add(CellElement(LoomGeometry.ClothX0 + c * cw, LoomGeometry.ClothX0 + (c + 1) * cw, wv, CellKey(state.Woven[row * state.Width + c])));
                return;
            }
            bool current = row == state.WovenRows;
            if (current && look.HideCurrentRow)
            {
                // During the beat (lane/playtest-fixes-5, owner: the hidden row showed dark cells, read as black): no
                // cells at the fell, only the bare warp threads running on across it.
                AddBareWarp(look, strip, elements, keys);
                return;
            }
            float w = LoomGeometry.CellWidthFor(state.Width);
            for (int c = 0; c < state.Width; c++)
            {
                // Draft before the first beat with no treadle pressed: the fell shows the warp (lane/draft-mode).
                byte cell = current ? state.FellRow[c] : state.Woven[row * state.Width + c];
                elements.Add(CellElement(LoomGeometry.ClothX0 + c * w, LoomGeometry.ClothX0 + (c + 1) * w, strip, CellKey(cell)));
            }
        }

        void AddLeader(Strip strip) => elements.Add(CellElement(LoomGeometry.ClothX0, LoomGeometry.ClothX1, strip, LeaderTexture, LeaderUv(strip)));

        // Warp slots (fell + newest rows), then over the breast beam and back under the warp (or, unrolled, the hang
        // path: one continuous cloth, newest first, ending after the first woven row), then the roll: woven rows, else
        // leader (LoomGeometry.ClothLayout).
        var layout = LoomGeometry.ClothLayout(shownWoven, state.Rows, look.Unrolled, state.Width);
        foreach (var piece in layout)
        {
            if (piece.Row >= 0) AddRow(piece.Row, piece);
            else AddLeader(piece.Strip);
        }

        AddWarpStrings(look, elements, keys);
        AddWarpRolls(look, elements, keys);
        if (state.DraftMode) AddTieUpBoard(state.Draft, state.PlannedTreadle, elements, keys);
        if (!look.HideShuttles) AddShuttles(look, elements, keys);

        if (elements.Count == 0) return null;
        var tex = Textures(capi);
        foreach (string key in keys) EnsureTexture(capi, tex, key);

        var shape = new Shape
        {
            Elements = elements.ToArray(),
            Textures = new Dictionary<string, AssetLocation>(),
            TextureSizes = new Dictionary<string, int[]>(),
            TextureWidth = 16,
            TextureHeight = 16,
        };
        shape.ResolveReferences(capi.World.Logger, "actailor-loomcloth");

        var fallback = capi.Tesselator.GetTextureSource(block);
        var source = new CellTexSource(tex, fallback, capi.BlockTextureAtlas.Size);
        float rotY = LoomGeometry.GeometryRotateY(block.Shape?.rotateY ?? 0f);
        capi.Tesselator.TesselateShape("actailor-loomcloth", shape, out MeshData mesh, source, new Vec3f(0, rotY, 0));
        return mesh;
    }

    /// <summary>
    /// Colour of warp end <paramref name="column"/> (its thread roll and string): the twine loaded on it
    /// (lane/warp-shuttles-import: <see cref="DraftState.Warp"/> in every mode), plain flax where none is.
    /// </summary>
    public static string WarpHex(WeavingState w, int column)
    {
        if (column < 0 || column >= w.Draft.Warp.Length) return TwineColors.PlainHex;
        byte c = w.Draft.Warp[column];
        return c == WeavingState.Empty || c >= w.PaletteColors.Count ? TwineColors.PlainHex : w.ColorOf(c);
    }

    static string WarpHex(LoomLook look, int column) => WarpHex(look.Weaving, column);

    /// <summary>
    /// Per warp end its thread roll on the warp beam (two crossed boxes, the end's colour). Imported draft: a tag on top
    /// of each end whose loaded twine differs from the one the draft needs, in the needed colour (highlighted).
    /// </summary>
    static void AddWarpRolls(LoomLook look, List<ShapeElement> elements, HashSet<string> keys)
    {
        int width = look.Weaving.Width;
        for (int x = 0; x < width; x++)
        {
            string key = Key(WarpHex(look, x), false);
            keys.Add(key);
            var (x0, x1) = LoomGeometry.WarpThreadSpan(x, width);
            elements.Add(RollElement(x0, x1, LoomGeometry.ThreadRollRadius, 0, key));
            elements.Add(RollElement(x0, x1, LoomGeometry.ThreadRollRadius, 45, key));
            if (look.Weaving.WarpNeeded(x) is not { } needed) continue;
            string tag = Key(look.TwineHex(needed), true);
            keys.Add(tag);
            elements.Add(BoxElement(x0, LoomGeometry.TagY0, LoomGeometry.TagZ0, x1, LoomGeometry.TagY1, LoomGeometry.TagZ1, tag));
        }
    }

    /// <summary>Wood of an empty shuttle body.</summary>
    public const string ShuttleWoodHex = "#8a6a45";

    /// <summary>Wood of the risers under the back shuttles (drop box cells, darker).</summary>
    public const string RiserWoodHex = "#5e4630";

    /// <summary>
    /// The 4 shuttles in the shuttle boxes at the beater ends (lane/shuttle-fix): a wooden body each lying along the weft,
    /// the back one of each end on a riser; a loaded one carries its twine (pirn) on top (the active one highlighted
    /// and lifted a bit, <see cref="LoomGeometry.ActiveShuttleLift"/>). Empty shuttles show their bare wood.
    /// </summary>
    static void AddShuttles(LoomLook look, List<ShapeElement> elements, HashSet<string> keys)
    {
        string wood = Key(ShuttleWoodHex, false), riser = Key(RiserWoodHex, false);
        keys.Add(wood);
        keys.Add(riser);
        for (int end = 0; end < 2; end++)
        {
            var r = LoomGeometry.ShuttleRiser(end);
            elements.Add(BoxElement(r.x0, r.y0, r.z0, r.x1, r.y1, r.z1, riser));
        }
        for (int i = 0; i < LoomGeometry.ShuttleCount; i++)
        {
            var b = LoomGeometry.ShuttleBody(i);
            string? code = look.Shuttles.CodeAt(i);
            bool active = code != null && i == look.Shuttles.Selected;
            float lift = active ? LoomGeometry.ActiveShuttleLift : 0f;
            float y0 = b.y0 + lift, y1 = b.y1 + lift;
            float mid = y0 + (y1 - y0) * 0.45f;
            elements.Add(BoxElement(b.x0, y0, b.z0, b.x1, mid, b.z1, wood));
            if (code == null)
            {
                // Empty: the shuttle's pointed tips only, so it still reads as a shuttle.
                elements.Add(BoxElement(b.x0, mid, b.z0 + 0.1f, b.x0 + 0.4f, y1 - 0.15f, b.z1 - 0.1f, wood));
                elements.Add(BoxElement(b.x1 - 0.4f, mid, b.z0 + 0.1f, b.x1, y1 - 0.15f, b.z1 - 0.1f, wood));
                continue;
            }
            string key = Key(look.TwineHex(code), active);
            keys.Add(key);
            // The pirn: twine wound in the middle of the shuttle (along x), longer as it holds more.
            float fill = 0.4f + 0.6f * System.Math.Min(1f, look.Shuttles.AmountAt(i) / (float)SpoolRack.Capacity);
            float xc = (b.x0 + b.x1) / 2, half = (b.x1 - b.x0) * 0.4f * fill;
            elements.Add(BoxElement(xc - half, mid, b.z0 + 0.08f, xc + half, y1, b.z1 - 0.08f, key));
        }
    }

    /// <summary>Threads per column of a <paramref name="width"/>-wide warp and the x span of each (same as <see cref="AddWarpStrings"/>).</summary>
    public static IEnumerable<(int column, float x0, float x1)> ThreadSpans(int width)
    {
        // A 32-wide cloth: one thread per column (same thread count as 16 x 2).
        int threads = width > WeavingState.DefaultWidth ? 1 : LoomGeometry.ThreadsPerColumn;
        for (int c = 0; c < width; c++)
            for (int j = 0; j < threads; j++)
            {
                var (x0, x1) = threads == 1 ? LoomGeometry.ThreadSpan(c, 0, width, 1) : LoomGeometry.ThreadSpan(c, j, width);
                yield return (c, x0, x1);
            }
    }

    /// <summary>The warp threads across one cloth strip (the fell while its row is hidden): thread colours as the warp strings.</summary>
    static void AddBareWarp(LoomLook look, Strip strip, List<ShapeElement> elements, HashSet<string> keys)
    {
        foreach (var (c, x0, x1) in ThreadSpans(look.Weaving.Width))
        {
            string key = Key(WarpHex(look, c), false);
            keys.Add(key);
            elements.Add(CellElement(x0, x1, strip, key));
        }
    }

    /// <summary>Thin two-sided threads per column from the fell to the warp beam (the model's white string sheets are hidden).</summary>
    static void AddWarpStrings(LoomLook look, List<ShapeElement> elements, HashSet<string> keys)
    {
        var pts = LoomGeometry.WarpStringPoints();
        foreach (var (c, x0, x1) in ThreadSpans(look.Weaving.Width))
        {
            string key = Key(WarpHex(look, c), false);
            keys.Add(key);
            for (int i = 1; i < pts.Length; i++)
                elements.Add(CellElement(x0, x1, new Strip(pts[i - 1].y, pts[i - 1].z, pts[i].y, pts[i].z), key));
        }
    }

    /// <summary>
    /// Tie-up board colours: board, pressed treadle, lifted / resting shaft square; the imported draft's treadle for the
    /// next row while another (or none) is pressed (lane/warp-shuttles-import).
    /// </summary>
    public const string BoardHex = "#5a4632", PressedHex = "#c9a227", LiftedHex = "#efe6cf", RestingHex = "#2a2420", PlannedHex = "#3f7fbf";

    /// <summary>Board plate colour of treadle <paramref name="t"/>: pressed (gold), else planned for the next row (blue), else wood.</summary>
    public static string PlateHex(int t, int pressed, int planned) => t == pressed ? PressedHex : t == planned ? PlannedHex : BoardHex;

    /// <summary>
    /// Draft mode: the tie-up board on the heddle top frame (LoomGeometry.TreadleSpan / ShaftSquare): one plate per
    /// treadle (the pressed one gold), 4 squares each, light = shaft lifted by that treadle (the explainer's tie-up grid).
    /// </summary>
    static void AddTieUpBoard(DraftState draft, int planned, List<ShapeElement> elements, HashSet<string> keys)
    {
        string K(string hex) { string k = Key(hex, false); keys.Add(k); return k; }
        float z = LoomGeometry.BoardZ, zs = z - LoomGeometry.SquareLift;
        for (int t = 0; t < LoomGeometry.BoardTreadles; t++)
        {
            var (x0, x1) = LoomGeometry.TreadleSpan(t);
            string plate = K(PlateHex(t, draft.CurrentTreadle == DraftState.NoTreadle ? -1 : draft.CurrentTreadle, planned));
            elements.Add(CellElement(x0, x1, new Strip(LoomGeometry.BoardY0, z, LoomGeometry.BoardY1, z), plate));
            int mask = draft.LiftMask(t);
            for (int sh = 0; sh < LoomGeometry.BoardShafts; sh++)
            {
                var q = LoomGeometry.ShaftSquare(t, sh);
                elements.Add(CellElement(q.x0, q.x1, new Strip(q.y0, zs, q.y1, zs), K(((mask >> sh) & 1) != 0 ? LiftedHex : RestingHex)));
            }
        }
    }

    /// <summary>Leader cloth UV: the whole texture across the cloth width, the strip's length at the same scale.</summary>
    static float[] LeaderUv(Strip s)
        => new float[] { 0, 0, 16, Math.Min(16f, 16f * s.Length / (LoomGeometry.ClothX1 - LoomGeometry.ClothX0)) };

    /// <summary>Zero-thickness cell laid along the strip (local +z), both faces textured.</summary>
    static ShapeElement CellElement(float x0, float x1, Strip s, string texKey, float[]? uv = null)
    {
        var el = new ShapeElement
        {
            Name = "c",
            From = new double[] { x0, s.Y0, s.Z0 },
            To = new double[] { x1, s.Y0, s.Z0 + s.Length },
            RotationOrigin = new double[] { x0, s.Y0, s.Z0 },
            RotationX = s.RotationXDeg,
        };
        el.FacesResolved![BlockFacing.UP.Index] = Face(texKey, uv);
        el.FacesResolved![BlockFacing.DOWN.Index] = Face(texKey, uv);
        return el;
    }

    /// <summary>Square box around the warp beam axis; two of them (0 and 45 degrees) make an octagonal thread roll.</summary>
    static ShapeElement RollElement(float x0, float x1, float r, float rotX, string texKey)
    {
        float y = LoomGeometry.BeamY, z = LoomGeometry.BeamZ;
        var el = BoxElement(x0, y - r, z - r, x1, y + r, z + r, texKey);
        el.RotationOrigin = new double[] { x0, y, z };
        el.RotationX = rotX;
        return el;
    }

    /// <summary>Axis-aligned box, every face the same texture.</summary>
    static ShapeElement BoxElement(float x0, float y0, float z0, float x1, float y1, float z1, string texKey)
    {
        var el = new ShapeElement
        {
            Name = "box",
            From = new double[] { x0, y0, z0 },
            To = new double[] { x1, y1, z1 },
        };
        for (int f = 0; f < 6; f++) el.FacesResolved![f] = Face(texKey);
        return el;
    }

    static ShapeElementFace Face(string texKey, float[]? uv = null) => new() { Texture = texKey, Uv = uv ?? new float[] { 0, 0, 16, 16 } };

    static Dictionary<string, TextureAtlasPosition> Textures(ICoreClientAPI capi)
        => ObjectCacheUtil.GetOrCreate(capi, TexCacheKey, () => new Dictionary<string, TextureAtlasPosition>());

    static void EnsureTexture(ICoreClientAPI capi, Dictionary<string, TextureAtlasPosition> tex, string key)
    {
        if (tex.ContainsKey(key)) return;
        var loc = new AssetLocation(TailorModSystem.Domain, "loomcell/" + (key == EmptyKey ? "empty" : key.Substring(0, 1) + key.Substring(2)));
        if (capi.BlockTextureAtlas.GetOrInsertTexture(loc, out _, out TextureAtlasPosition pos, () => CellBitmap(key)))
        {
            tex[key] = pos;
        }
    }

    /// <summary>2x2 px: two diagonal pixels shaded, the look of a plain over/under weave.</summary>
    static IBitmap CellBitmap(string key)
    {
        string hex = key == EmptyKey ? "#3a342c" : key.Substring(1);
        float mix = key.StartsWith("h") ? HighlightMix : 0f;
        var px = new int[4];
        for (int i = 0; i < 4; i++)
        {
            float shade = i == 0 || i == 3 ? 1f : 0.86f;
            px[i] = TwineColors.ToArgb(hex, shade, mix);
        }
        return new BakedBitmap { TexturePixels = px, Width = 2, Height = 2 };
    }

    /// <summary>
    /// The loom model's own texture source with its <c>plain</c> key (the model's weft: pirn <c>shuttle12</c> and the
    /// <c>shuttlethread*</c> chain to the fell, lane/shuttle-fix) in twine colour <paramref name="hex"/>. Main thread only.
    /// </summary>
    public static ITexPositionSource WeftTexSource(ICoreClientAPI capi, Block block, string hex)
    {
        var tex = Textures(capi);
        string key = Key(hex, false);
        EnsureTexture(capi, tex, key);
        var fallback = capi.Tesselator.GetTextureSource(block);
        var map = new Dictionary<string, TextureAtlasPosition>();
        if (tex.TryGetValue(key, out var pos)) map["plain"] = pos;
        return new CellTexSource(map, fallback, capi.BlockTextureAtlas.Size);
    }

    class CellTexSource : ITexPositionSource
    {
        readonly Dictionary<string, TextureAtlasPosition> tex;
        readonly ITexPositionSource fallback;
        public Size2i AtlasSize { get; }

        public CellTexSource(Dictionary<string, TextureAtlasPosition> tex, ITexPositionSource fallback, Size2i atlasSize)
        {
            this.tex = tex;
            this.fallback = fallback;
            AtlasSize = atlasSize;
        }

        public TextureAtlasPosition this[string textureCode]
            => tex.TryGetValue(textureCode, out var pos) ? pos : fallback[textureCode] ?? fallback["plain"]!;
    }
}
