using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Sewing state of a garment recipe (pure; covered by tests/tailoring-selftest): the selected garment and one
/// placement per part (offset + turn; <see cref="PieceAssignment.Cloth"/> unused). Part i takes the material in sewing
/// slot i (one item per part, like the ingredient slots of a cooking pot). The host owns the slots and passes their
/// contents in as delegates.
/// </summary>
public sealed class TailoringSession
{
    /// <summary>Sewing slots of a mannequin = the most parts a garment recipe may have.</summary>
    public const int MaxParts = 10;

    public string GarmentCode { get; private set; } = "";
    public PieceAssignment[] Pieces { get; private set; } = Array.Empty<PieceAssignment>();
    /// <summary>
    /// Modular parts (lane/modular-parts): chosen option per part (<see cref="GarmentDef.Choice"/>, -1 = plain part). The part's slot,
    /// material and placement stay when the choice changes. Reset with the garment (first options).
    /// </summary>
    public int[] Choice { get; private set; } = Array.Empty<int>();
    /// <summary>
    /// Applied (paid) stitches on the garment in progress (lane/mannequin-embroidery), garment texture size; null = none.
    /// Cleared with the garment (another garment, cancel; a cut moves them onto the sewn garment).
    /// </summary>
    public Weaving.Embroidery? Embroidery { get; set; }

    /// <summary>
    /// Body-space wrap of the garment in progress (lane/body-wrap): per-segment cloth placement and the sleeves mode
    /// ("" = recipe default). Reset with the garment; a cut copies it onto the sewn garment.
    /// </summary>
    public WrapLayout Wrap { get; private set; } = new();

    /// <summary>
    /// In-world flow (lane/drape-cut-sew): part i cut with shears (placement locked until uncut). Only meaningful while
    /// its slot is filled with a textile; the host clears it when the slot empties. Reset with the garment.
    /// </summary>
    public bool[] PartCut { get; private set; } = Array.Empty<bool>();
    /// <summary>In-world flow: optional part i switched on while empty (shown as ghost, waits to be draped). Reset with the garment.</summary>
    public bool[] PartOn { get; private set; } = Array.Empty<bool>();

    public bool IsCut(int part) => part >= 0 && part < PartCut.Length && PartCut[part];
    public bool IsOn(int part) => part >= 0 && part < PartOn.Length && PartOn[part];

    /// <summary>Sets part <paramref name="part"/>'s cut flag; false when unchanged or no such part.</summary>
    public bool SetCut(int part, bool cut)
    {
        if (part < 0 || part >= PartCut.Length || PartCut[part] == cut) return false;
        PartCut[part] = cut;
        return true;
    }

    /// <summary>Sets part <paramref name="part"/>'s "on" flag; false when unchanged or no such part.</summary>
    public bool SetOn(int part, bool on)
    {
        if (part < 0 || part >= PartOn.Length || PartOn[part] == on) return false;
        PartOn[part] = on;
        return true;
    }

    public bool AnyCut => PartCut.Any(c => c);

    /// <summary>Places the cloth on body segment <paramref name="seg"/>; false for an unknown segment or no garment.</summary>
    public bool SetSegment(int seg, SegmentPlacement p) => GarmentCode.Length > 0 && Wrap.Set(seg, p);

    /// <summary>Sleeves mode: "" (recipe default), <see cref="WrapLayout.SleevesContinue"/> or <see cref="WrapLayout.SleevesSeparate"/>; false when unchanged or invalid.</summary>
    public bool SetSleeves(string mode)
    {
        if (GarmentCode.Length == 0 || (mode != "" && mode != WrapLayout.SleevesContinue && mode != WrapLayout.SleevesSeparate) || mode == Wrap.Sleeves) return false;
        Wrap.Sleeves = mode;
        return true;
    }

    /// <summary>
    /// Twine codes charged for this garment in progress (one twine each, lane/embroidery-paid): refunded when the garment is
    /// cancelled or replaced; dropped without refund by a cut (the garment keeps them as paid). Not touched by edits of a finished garment.
    /// </summary>
    public HashSet<string> Charged { get; } = new();

    /// <summary>
    /// Pending (unapplied) stitches of this mannequin, shared by the window and in-world stitching (lane/sewing-polish):
    /// the garment in progress and worn garments. The garment in progress' draft goes with the garment (another garment,
    /// cancel, reset); worn drafts stay until applied or discarded (or their garment leaves the slot).
    /// </summary>
    public EmbroideryDrafts Drafts { get; } = new();

    /// <summary>The codes to refund (sorted) and forgets them: call on cancel / another garment, never on a cut.</summary>
    public string[] TakeCharged()
    {
        var codes = Charged.OrderBy(c => c, StringComparer.Ordinal).ToArray();
        Charged.Clear();
        return codes;
    }

    /// <summary>
    /// Selects <paramref name="g"/> (null = none). Same garment again: layout kept, false. New garment: every part
    /// at offset 0, no turn. Returns whether the state changed (the host gives back the slots' materials first).
    /// </summary>
    public bool SelectGarment(GarmentDef? g)
    {
        string code = g?.Code ?? "";
        if (code == GarmentCode) return false;
        GarmentCode = code;
        Embroidery = null;
        Drafts.Clear(GarmentEmbroidery.TargetSession);
        Wrap = new WrapLayout();
        Charged.Clear();
        Pieces = (g?.Parts ?? Array.Empty<PartDef>()).Select(_ => new PieceAssignment(-1)).ToArray();
        Choice = g?.Root.Choice.ToArray() ?? Array.Empty<int>();
        PartCut = new bool[Pieces.Length];
        PartOn = new bool[Pieces.Length];
        return true;
    }

    /// <summary>Sets one part's placement; false (unchanged) for an unknown part.</summary>
    public bool SetPiece(int part, PieceAssignment a)
    {
        if (part < 0 || part >= Pieces.Length) return false;
        Pieces[part] = new PieceAssignment(-1, a.Dx, a.Dy, a.Rot);
        return true;
    }

    /// <summary>
    /// Chooses option <paramref name="option"/> of part <paramref name="part"/> (lane/modular-parts). False (unchanged) for a part
    /// without that option, no garment, or the same choice. The host refuses it while the part is cut (placement locked).
    /// </summary>
    public bool SetOption(GarmentDef g, int part, int option)
    {
        if (g.Code != GarmentCode || part < 0 || part >= Choice.Length || part >= g.Parts.Length) return false;
        if (option < 0 || option >= g.Parts[part].Options.Length || Choice[part] == option) return false;
        Choice[part] = option;
        return true;
    }

    public void Reset()
    {
        Choice = Array.Empty<int>();
        Embroidery = null;
        Drafts.Clear(GarmentEmbroidery.TargetSession);
        Wrap = new WrapLayout();
        Charged.Clear();
        GarmentCode = "";
        Pieces = Array.Empty<PieceAssignment>();
        PartCut = Array.Empty<bool>();
        PartOn = Array.Empty<bool>();
    }

    /// <summary>Whether sewing slot <paramref name="part"/> may hold <paramref name="m"/> for garment <paramref name="g"/>.</summary>
    public static bool SlotAccepts(GarmentDef? g, int part, SlotMaterial? m)
        => g != null && m != null && part >= 0 && part < g.Parts.Length && g.Parts[part].Allows(m);

    /// <summary>
    /// Why the garment cannot be sewn yet (lang key without domain), null = ready: no garment, a required part empty,
    /// or a part holding a material it does not take. <paramref name="materialOf"/>: material in slot i (null = empty).
    /// </summary>
    public static string? CutProblem(GarmentDef? g, Func<int, SlotMaterial?> materialOf)
    {
        if (g == null) return "tailor-err-nogarment";
        for (int p = 0; p < g.Parts.Length; p++)
        {
            if (g.Parts[p].IsFixed) continue; // premade part: no slot, no material
            var m = materialOf(p);
            if (m == null)
            {
                if (!g.Parts[p].Optional) return "tailor-err-partempty";
                continue;
            }
            if (!g.Parts[p].Allows(m)) return "tailor-err-material";
        }
        return null;
    }

    /// <summary>Slots a cut consumes, 1 item each: every filled part (lane/garment-system: fabric cost ignored for now).</summary>
    public static int[] ConsumedSlots(GarmentDef g, Func<int, SlotMaterial?> materialOf)
        => Enumerable.Range(0, g.Parts.Length).Where(p => !g.Parts[p].IsFixed && materialOf(p) != null).ToArray();

    /// <summary>
    /// Garment data of a cut: one material entry per filled part (part order); empty optional parts get -1. Item
    /// materials (not patterned) keep only their turn (no pattern offset).
    /// </summary>
    /// <param name="g">The recipe resolved for the session's choice (<see cref="GarmentDef.Resolve"/>): the options go onto the garment.</param>
    /// <param name="embroidery">The session's stitches: kept where the sewn materials take stitches (others dropped).</param>
    public static GarmentData BuildGarment(GarmentDef g, IReadOnlyList<PieceAssignment> placements, Func<int, SlotMaterial?> materialOf,
        Weaving.Embroidery? embroidery = null, WrapLayout? wrap = null)
    {
        var data = new GarmentData { Garment = g.Code, Pieces = new PieceAssignment[g.Parts.Length], Wrap = wrap?.Clone(), Choice = g.HasOptions ? g.Choice.ToArray() : null };
        for (int p = 0; p < g.Parts.Length; p++)
        {
            var m = g.Parts[p].IsFixed ? null : materialOf(p); // fixed parts store nothing: the recipe renders them
            var a = p < placements.Count ? placements[p] : PieceAssignment.None;
            if (m == null)
            {
                data.Pieces[p] = PieceAssignment.None;
                continue;
            }
            data.Materials.Add(m);
            int i = data.Materials.Count - 1;
            data.Pieces[p] = m.IsPatterned ? new PieceAssignment(i, a.Dx, a.Dy, a.Rot) : new PieceAssignment(i, 0, 0, a.Rot);
        }
        if (embroidery != null) data.Embroidery = GarmentEmbroidery.Prune(embroidery, GarmentEmbroidery.Mask(g, materialOf));
        return data;
    }

    public void ToTree(ITreeAttribute tree)
    {
        tree.SetString("garment", GarmentCode);
        tree["pieceDx"] = new IntArrayAttribute(Pieces.Select(p => p.Dx).ToArray());
        tree["pieceDy"] = new IntArrayAttribute(Pieces.Select(p => p.Dy).ToArray());
        tree["pieceRot"] = new IntArrayAttribute(Pieces.Select(p => p.Rot).ToArray());
        Weaving.Embroidery.Write(tree, Embroidery?.Clone()); // tree "embroidery"; none when empty
        Wrap.ToTree(tree); // lane/body-wrap
        tree["partOption"] = new IntArrayAttribute(Choice.ToArray());
        tree["partCut"] = new IntArrayAttribute(PartCut.Select(c => c ? 1 : 0).ToArray()); // lane/drape-cut-sew
        tree["partOn"] = new IntArrayAttribute(PartOn.Select(c => c ? 1 : 0).ToArray());
        if (Charged.Count > 0) tree["embCharged"] = new StringArrayAttribute(Charged.OrderBy(c => c, StringComparer.Ordinal).ToArray());
        Drafts.ToTree(tree); // lane/sewing-polish: pending stitches (tree "embDrafts")
    }

    /// <summary>
    /// Reads the state back. <paramref name="find"/> resolves the garment code (null when no catalog yet): when the
    /// recipe's part count differs from the saved one, the placements restart at 0.
    /// </summary>
    public void FromTree(ITreeAttribute tree, Func<string, GarmentDef?>? find)
    {
        GarmentCode = tree.GetString("garment", "");
        int[] dx = (tree["pieceDx"] as IntArrayAttribute)?.value ?? Array.Empty<int>();
        int[] dy = (tree["pieceDy"] as IntArrayAttribute)?.value ?? Array.Empty<int>();
        int[] rot = (tree["pieceRot"] as IntArrayAttribute)?.value ?? Array.Empty<int>();
        int n = new[] { dx.Length, dy.Length, rot.Length }.Min();
        Pieces = Enumerable.Range(0, n).Select(i => new PieceAssignment(-1, dx[i], dy[i], rot[i])).ToArray();
        var g = find?.Invoke(GarmentCode);
        if (g != null && Pieces.Length != g.Parts.Length) Pieces = g.Parts.Select(_ => new PieceAssignment(-1)).ToArray();
        bool[] Flags(string key)
        {
            int[] v = (tree[key] as IntArrayAttribute)?.value ?? Array.Empty<int>();
            return Enumerable.Range(0, Pieces.Length).Select(i => i < v.Length && v[i] != 0).ToArray(); // older saves: none
        }
        PartCut = Flags("partCut");
        PartOn = Flags("partOn");
        int[] opt = (tree["partOption"] as IntArrayAttribute)?.value ?? Array.Empty<int>(); // older saves: first options
        Choice = g != null ? g.NormalizeChoice(opt) : opt.ToArray();
        // Stitches: checked against the garment texture size when the recipe is known (other size = recipe changed → dropped).
        Embroidery = g != null ? Weaving.Embroidery.ReadGrown(tree, g.TextureWidth, g.TextureHeight)
            : Weaving.Embroidery.FromTree(tree[Weaving.Embroidery.AttrTree] as ITreeAttribute);
        Wrap = WrapLayout.FromTree(tree) ?? new WrapLayout(); // sessions saved before lane/body-wrap: body space, no offsets
        Charged.Clear();
        if (tree["embCharged"] is StringArrayAttribute charged) Charged.UnionWith(charged.value);
        Drafts.FromTree(tree);
        if (g == null && find != null && GarmentCode.Length > 0) Reset(); // recipe removed
    }
}

/// <summary>
/// Who may open a mannequin's sewing session (lane/playtest-fixes-5, docs/tailoring.md "Session lifetime"). Pure; covered
/// by tests/tailoring-selftest. The session (cloths, garment, layout, live preview) outlives the dialog: closing the
/// dialog keeps it, Cancel or the last cut + close ends it. Owner = UID of the player sewing ("" = nobody).
/// </summary>
public static class SewingLock
{
    /// <summary>The owner's lock holds while they are online and within this many blocks of the mannequin.</summary>
    public const double LockDistance = 32;

    /// <summary>Something to come back to: a garment picked or materials in the slots.</summary>
    public static bool SessionActive(string garmentCode, bool anyMaterial) => garmentCode.Length > 0 || anyMaterial;

    /// <summary>Another player holds the session: they own it and are still around (online, within <see cref="LockDistance"/>).</summary>
    public static bool LockedFor(string owner, string player, bool ownerActive) => owner.Length > 0 && owner != player && ownerActive;

    /// <summary>
    /// A right click without a sewing material (any other item or an empty hand, not sneaking) reopens the dialog: for the
    /// owner, or for anyone when the session has no owner (an old save). Sneak stays vanilla undressing.
    /// </summary>
    public static bool Reopens(string owner, string player, bool sessionActive, bool sneak)
        => !sneak && sessionActive && (owner.Length == 0 || owner == player);

    /// <summary>The dialog closed: the session ends (owner cleared) only when nothing is left to come back to.</summary>
    public static bool EndsOnClose(bool sessionActive) => !sessionActive;
}
