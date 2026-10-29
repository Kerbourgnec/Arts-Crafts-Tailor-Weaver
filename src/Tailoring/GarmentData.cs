using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// What a tailored garment is made of, stored in its stack attributes under <c>tailoring</c>:
/// string <c>garment</c>; int <c>clothCount</c> (= material entries); per entry i string <c>kind{i}</c>
/// (material family: cloth, leather, pelt, wood, metal, bone...; "hard" before lane/garment-families; missing = cloth,
/// stacks from before lane/tailoring-pelts) and either bytes
/// <c>pattern{i}</c> (cloth cells at its own size; int <c>clothWidth{i}</c> when not 16) + string[] <c>colors{i}</c> (patterned woven cloth) or string <c>material{i}</c> (catalog item
/// code: leather, pelt, vanilla cloth, hard); string[] <c>pieceCodes</c> (part codes) + int[] <c>pieceCloth</c> (material
/// index, -1 = part without material), <c>pieceDx</c>, <c>pieceDy</c>, <c>pieceRot</c> (same order). Since
/// lane/garment-system every filled part has its own material entry (1 item each); older stacks share entries.
/// The texture is recomposed from this on the client (deterministic), so stacks stay small (~300 bytes per cloth).
/// lane/mannequin-embroidery: optional tree <c>embroidery</c> (<see cref="Weaving.Embroidery"/> layer of the garment
/// texture's size; absent = no stitches, so older stacks and their hashes are unchanged).
/// lane/modular-parts: string[] <c>pieceOption</c> (option code per part, same order as <c>pieceCodes</c>, "" = a plain part);
/// absent (older stacks, garments without options) = the first option of every part, except that a part that may be left off (lists a "none" option) is present exactly when it has a material ("none" = left off, <see cref="ChoiceFor"/>).
/// </summary>
public sealed class GarmentData
{
    public const string AttrRoot = "tailoring";

    public string Garment = "";
    public List<SlotMaterial> Materials = new();
    /// <summary>Indexed like <see cref="GarmentDef.Parts"/>; <see cref="PieceAssignment.Cloth"/> indexes <see cref="Materials"/>.</summary>
    public PieceAssignment[] Pieces = System.Array.Empty<PieceAssignment>();
    /// <summary>Stitches on the garment texture (lane/mannequin-embroidery, <see cref="GarmentEmbroidery"/>); null = none.</summary>
    public Weaving.Embroidery? Embroidery;
    /// <summary>Option index per part (<see cref="GarmentDef.Choice"/>; -1 = plain part); null = the first option everywhere.</summary>
    public int[]? Choice;
    /// <summary>Body-space wrap (lane/body-wrap): segment placements + sleeves mode; null = UV space (sewn before the lane).</summary>
    public WrapLayout? Wrap;

    /// <summary>The recipe def of this garment's options (the catalog def itself for the first options).</summary>
    public GarmentDef Resolve(GarmentDef def) => def.Resolve(ChoiceFor(def));

    /// <summary>
    /// Option per part as shown: <see cref="Choice"/> normalised, with a part that may be left off present exactly when it
    /// has a material. Stacks sewn before the none option (owner 2026-10-29: optional parts = parts with a none option)
    /// have no stored option for such a part: a filled one is its first present option, an empty one is left off (an old
    /// parka collar stored "fur" while empty).
    /// </summary>
    public int[] ChoiceFor(GarmentDef def)
    {
        var ch = def.NormalizeChoice(Choice);
        for (int p = 0; p < ch.Length && p < def.Parts.Length; p++)
        {
            int none = def.Parts[p].NoneIndex;
            if (none < 0) continue;
            if (PartMaterial(p) == null) ch[p] = none;
            else if (ch[p] == none) ch[p] = def.Parts[p].FirstPresent;
        }
        return ch;
    }

    public SlotMaterial? Material(int i) => i >= 0 && i < Materials.Count ? Materials[i] : null;

    /// <summary>Material of part <paramref name="part"/> (null = empty part, or one left off).</summary>
    public SlotMaterial? PartMaterial(int part) => part >= 0 && part < Pieces.Length ? Material(Pieces[part].Cloth) : null;

    /// <summary>Shape elements this garment does not show (fixed ones + the options not chosen, so the parts left off).</summary>
    public string[] HiddenElements(GarmentDef def) => Resolve(def).HiddenElements();

    public void ToTree(ITreeAttribute tree, GarmentDef def)
    {
        tree.SetString("garment", Garment);
        tree.SetInt("clothCount", Materials.Count);
        for (int i = 0; i < Materials.Count; i++)
        {
            var m = Materials[i];
            tree.SetString("kind" + i, m.Kind);
            if (m.Cloth != null)
            {
                tree.SetBytes("pattern" + i, m.Cloth.Pattern);
                tree["colors" + i] = new StringArrayAttribute(m.Cloth.Colors);
                // Own size (lane/pattern-density); 16 x 16 stays as before (no key).
                if (m.Cloth.Width != GarmentLayout.Tile || m.Cloth.Height != GarmentLayout.Tile) tree.SetInt("clothWidth" + i, m.Cloth.Width);
                if (m.Cloth.WarpUp != null) tree.SetBytes("warpUp" + i, m.Cloth.WarpUp.Select(v => (byte)(v ? 1 : 0)).ToArray());
            }
            else
            {
                tree.SetString("material" + i, m.Code);
            }
        }
        tree["pieceCodes"] = new StringArrayAttribute(def.Parts.Select(p => p.Code).ToArray());
        tree["pieceCloth"] = new IntArrayAttribute(Pieces.Select(p => p.Cloth).ToArray());
        tree["pieceDx"] = new IntArrayAttribute(Pieces.Select(p => p.Dx).ToArray());
        tree["pieceDy"] = new IntArrayAttribute(Pieces.Select(p => p.Dy).ToArray());
        tree["pieceRot"] = new IntArrayAttribute(Pieces.Select(p => p.Rot).ToArray());
        if (def.HasOptions)
        {
            var ch = ChoiceFor(def);
            tree["pieceOption"] = new StringArrayAttribute(def.Parts.Select((p, i) => ch[i] >= 0 ? p.Options[ch[i]].Code : "").ToArray());
        }
        Weaving.Embroidery.Write(tree, Embroidery); // empty → no tree
        Wrap?.ToTree(tree); // keys wrap, sleeves, segDu, segDv, segRot; absent = UV space
    }

    /// <summary>Reads the data back, mapping parts by code onto <paramref name="def"/> (missing → no material).</summary>
    public static GarmentData? FromTree(ITreeAttribute? tree, GarmentDef def)
    {
        if (tree == null) return null;
        var d = new GarmentData { Garment = tree.GetString("garment", "") };
        int n = tree.GetInt("clothCount");
        for (int i = 0; i < n; i++)
        {
            string kind = tree.GetString("kind" + i, MaterialKind.Cloth);
            string? code = tree.GetString("material" + i);
            if (kind != MaterialKind.Cloth || !string.IsNullOrEmpty(code))
            {
                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(kind)) return null;
                d.Materials.Add(SlotMaterial.OfItem(kind, code));
                continue;
            }
            byte[]? pat = tree.GetBytes("pattern" + i);
            string[]? colors = (tree["colors" + i] as StringArrayAttribute)?.value;
            int cw = tree.GetInt("clothWidth" + i, GarmentLayout.Tile);
            if (pat == null || colors == null || cw <= 0 || pat.Length == 0 || pat.Length % cw != 0) return null;
            d.Materials.Add(SlotMaterial.OfCloth(new ClothPattern(pat, colors, cw, pat.Length / cw, tree.GetBytes("warpUp" + i) is { } wu && wu.Length == pat.Length ? wu.Select(v => v != 0).ToArray() : null)));
        }
        string[] codes = (tree["pieceCodes"] as StringArrayAttribute)?.value ?? System.Array.Empty<string>();
        int[] cloth = (tree["pieceCloth"] as IntArrayAttribute)?.value ?? System.Array.Empty<int>();
        int[] dx = (tree["pieceDx"] as IntArrayAttribute)?.value ?? System.Array.Empty<int>();
        int[] dy = (tree["pieceDy"] as IntArrayAttribute)?.value ?? System.Array.Empty<int>();
        int[] rot = (tree["pieceRot"] as IntArrayAttribute)?.value ?? System.Array.Empty<int>();
        d.Pieces = new PieceAssignment[def.Parts.Length];
        for (int p = 0; p < def.Parts.Length; p++)
        {
            int k = System.Array.IndexOf(codes, def.Parts[p].Code);
            if (k < 0 && def.Parts[p].SplitFrom is string from) k = System.Array.IndexOf(codes, from);
            d.Pieces[p] = k < 0 || k >= cloth.Length || k >= dx.Length || k >= dy.Length || k >= rot.Length
                ? PieceAssignment.None
                : new PieceAssignment(cloth[k], dx[k], dy[k], rot[k]);
        }
        if (def.HasOptions)
        {
            string[] optCodes = (tree["pieceOption"] as StringArrayAttribute)?.value ?? System.Array.Empty<string>();
            d.Choice = def.Parts.Select(p =>
            {
                if (p.Options.Length == 0) return -1;
                int k = System.Array.IndexOf(codes, p.Code);
                int o = k >= 0 && k < optCodes.Length ? System.Array.FindIndex(p.Options, x => x.Code == optCodes[k]) : -1;
                return o < 0 ? 0 : o;
            }).ToArray();
            d.Choice = d.ChoiceFor(def); // parts that may be left off: present exactly when filled (also for stacks without a stored option)
        }
        d.Embroidery = Weaving.Embroidery.ReadGrown(tree, def.TextureWidth, def.TextureHeight); // other width / shorter height than stored (recipe changed) → ignored; a taller recipe pads (lane/modular-parts)
        d.Wrap = WrapLayout.FromTree(tree);
        return d;
    }

    public static GarmentData? FromStack(ItemStack? stack, GarmentDef def)
        => stack == null ? null : FromTree(stack.Attributes.GetTreeAttribute(AttrRoot), def);

    public void WriteTo(ItemStack stack, GarmentDef def)
    {
        var tree = new TreeAttribute();
        ToTree(tree, def);
        stack.Attributes[AttrRoot] = tree;
    }

    /// <summary>Vanilla wearable condition (stack attribute, 0..1; warmth = min(max, condition * 2 * max)).</summary>
    public const string ConditionAttr = "condition";

    /// <summary>
    /// A freshly sewn garment (lane/sewing-polish): tailoring data plus full condition. Vanilla
    /// <c>CollectibleBehaviorWearable.ensureConditionExists</c> gives a stack without <c>condition</c> a random 0..0.4 on the
    /// server (looted-clothes rule; crafting sets 1 in <c>OnCreatedByCrafting</c>, which our sewing never calls).
    /// Tailored garments have no durability; condition is the only wear field.
    /// </summary>
    public void WriteNew(ItemStack stack, GarmentDef def)
    {
        WriteTo(stack, def);
        SetBrandNew(stack.Attributes);
    }

    public static void SetBrandNew(ITreeAttribute stackAttributes) => stackAttributes.SetFloat(ConditionAttr, 1f);

    /// <summary>Stable FNV-1a key over garment, materials and placements (texture/mesh caches, texture prefix).</summary>
    public string Hash()
    {
        ulong h = 14695981039346656037UL;
        void Mix(int b) { h ^= (byte)b; h *= 1099511628211UL; }
        void MixInt(int v) { Mix(v); Mix(v >> 8); Mix(v >> 16); Mix(v >> 24); }
        foreach (char c in Garment) Mix(c);
        foreach (var m in Materials)
        {
            if (m.Cloth != null)
            {
                // Cloth entries hash exactly as before materials existed (garments sewn earlier keep their atlas names).
                foreach (byte b in m.Cloth.Pattern) Mix(b);
                foreach (string c in m.Cloth.Colors) foreach (char ch in c) Mix(ch);
            }
            else
            {
                foreach (char ch in m.Kind) Mix(ch);
                Mix(0xfe);
                foreach (char ch in m.Code) Mix(ch);
            }
            Mix(0xff);
        }
        foreach (var p in Pieces) { MixInt(p.Cloth); MixInt(p.Dx); MixInt(p.Dy); MixInt(p.Rot); }
        if (Embroidery != null && !Embroidery.IsEmpty)
        {
            // Only embroidered garments mix the layer in: every garment sewn before keeps its hash (atlas names).
            Mix(0xfd);
            foreach (byte b in Embroidery.Cells) Mix(b);
            foreach (string c in Embroidery.Colors) { foreach (char ch in c) Mix(ch); Mix(0xff); }
        }
        if (Choice != null) // lane/modular-parts: only a non-first option mixes in (garments without options keep their hash)
            for (int p = 0; p < Choice.Length; p++) if (Choice[p] > 0) { Mix(0xfc); MixInt(p); MixInt(Choice[p]); }
        Wrap?.Mix(MixInt); // body-space garments only: older hashes unchanged
        return h.ToString("x16");
    }
}
