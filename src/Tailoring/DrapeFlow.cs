using System;
using System.Collections.Generic;
using System.Linq;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>Where a part of the garment in progress stands in the in-world flow (lane/drape-cut-sew).</summary>
public enum PartState
{
    /// <summary>Premade (recipe texture): nothing to do.</summary>
    Fixed,
    /// <summary>Optional, empty and not wanted: left off the garment (its elements hidden).</summary>
    Off,
    /// <summary>Required, or optional switched on, and still empty: drape (textile) or attach (hard item) something.</summary>
    Wanted,
    /// <summary>Textile draped, placement still free (anchor, nudge, turn): cut it with shears or a knife.</summary>
    Draped,
    /// <summary>Textile cut: placement locked until uncut (sneak + shears).</summary>
    Cut,
    /// <summary>Hard item (bone, wood, metal) attached: nothing to cut.</summary>
    Attached,
}

/// <summary>What a held item does on a mannequin in the in-world flow.</summary>
public enum FlowTool { None, Empty, Material, Twine, Shears, SewingKit }

/// <summary>Sneak + empty hand on an optional part.</summary>
public enum ToggleAction { Refuse, SwitchOn, SwitchOff, TakeOut }

/// <summary>
/// Pure rules of the physical tailoring flow on the mannequin (lane/drape-cut-sew, docs/tailoring.md "In-world tailoring
/// flow"; offline-tested): pick → drape → cut → sew. Part states, refusals (lang keys without domain), the next step for the
/// HUD, which parts show (ghost of empty wanted parts), locked body segments of cut parts, and the body region under
/// the crosshair for the garment picker. The host passes slot materials and the session's cut / on flags as delegates.
/// </summary>
public static class DrapeFlow
{
    /// <summary>Vanilla sewing kit (no durability in 1.22.7: maxstacksize 32; clothing grid recipes consume 1).</summary>
    public const string SewingKitCode = "game:sewingkit";

    /// <summary>
    /// Wear a tailoring step puts on the held tool (owner 2026-10-25, lane/sewing-polish: garments cost raw materials
    /// only): none. Shears / knives cut and the sewing kit sews without losing durability or being used up. The host
    /// damages / consumes by this amount, so a later change of mind is one number.
    /// </summary>
    public static int ToolWear(FlowTool tool) => 0;

    /// <summary>Alpha and colour of a ghost texel (empty wanted part on the preview).</summary>
    public const int GhostArgb = 0x58d8d8d8;

    public static bool IsHard(SlotMaterial m)
        => m.Group == MaterialKind.Hard || m.Kind is MaterialKind.Hard or MaterialKind.Wood or MaterialKind.Metal or MaterialKind.Bone;

    /// <summary>Textiles are cut before sewing; hard items are only attached.</summary>
    public static bool NeedsCut(SlotMaterial m) => !IsHard(m);

    public static PartState State(PartDef part, SlotMaterial? m, bool cut, bool on)
    {
        if (part.IsFixed) return PartState.Fixed;
        if (m == null) return part.Optional && !on ? PartState.Off : PartState.Wanted;
        if (!NeedsCut(m)) return PartState.Attached;
        return cut ? PartState.Cut : PartState.Draped;
    }

    public static PartState State(GarmentDef g, int p, Func<int, SlotMaterial?> materialOf, Func<int, bool> cutOf, Func<int, bool> onOf)
        => State(g.Parts[p], g.Parts[p].IsFixed ? null : materialOf(p), cutOf(p), onOf(p));

    /// <summary>Whether a part's model elements show on the mannequin: filled, required, fixed, switched on or <paramref name="ghost"/>.</summary>
    public static bool Shown(PartDef part, SlotMaterial? m, bool on, bool ghost = false) => !part.Optional || part.IsFixed || m != null || on || ghost;

    /// <summary>
    /// Why the sewing kit cannot finish the garment yet: (lang key, part) or null = ready. Order: a material a part does
    /// not take; a wanted part still empty; a textile not cut yet. Optional parts that are off are skipped.
    /// </summary>
    public static (string key, int part)? SewProblem(GarmentDef? g, Func<int, SlotMaterial?> materialOf, Func<int, bool> cutOf, Func<int, bool> onOf)
    {
        if (g == null) return ("tailor-err-nogarment", -1);
        for (int p = 0; p < g.Parts.Length; p++)
            if (!g.Parts[p].IsFixed && materialOf(p) is { } m && !g.Parts[p].Allows(m)) return ("tailor-err-material", p);
        for (int p = 0; p < g.Parts.Length; p++)
            if (State(g, p, materialOf, cutOf, onOf) == PartState.Wanted) return ("tailoring-flow-sew-empty", p);
        for (int p = 0; p < g.Parts.Length; p++)
            if (State(g, p, materialOf, cutOf, onOf) == PartState.Draped) return ("tailoring-flow-sew-uncut", p);
        return null;
    }

    /// <summary>
    /// Next step for the HUD: (lang key, parts concerned). pick → drape (wanted parts) → cut (draped parts) → sew.
    /// </summary>
    public static (string key, int[] parts) NextAction(GarmentDef? g, Func<int, SlotMaterial?> materialOf, Func<int, bool> cutOf, Func<int, bool> onOf)
    {
        if (g == null) return ("tailoring-flow-next-pick", Array.Empty<int>());
        var states = Enumerable.Range(0, g.Parts.Length).Select(p => State(g, p, materialOf, cutOf, onOf)).ToArray();
        int[] Of(PartState s) => Enumerable.Range(0, states.Length).Where(p => states[p] == s).ToArray();
        var wanted = Of(PartState.Wanted);
        if (wanted.Length > 0) return ("tailoring-flow-next-drape", wanted);
        var draped = Of(PartState.Draped);
        if (draped.Length > 0) return ("tailoring-flow-next-cut", draped);
        return ("tailoring-flow-next-sew", Array.Empty<int>());
    }

    /// <summary>Refusal of shears / knife on part <paramref name="part"/> (lang key) or null = cut it.</summary>
    public static string? CutRefusal(PartDef part, SlotMaterial? m, bool cut) => State(part, m, cut, true) switch
    {
        PartState.Draped => null,
        PartState.Cut => "tailoring-flow-cut-already",
        PartState.Attached => "tailoring-flow-cut-hard",
        PartState.Fixed => "tailoring-flow-cut-fixed",
        _ => "tailoring-flow-cut-empty",
    };

    /// <summary>Refusal of sneak + shears on a part (lang key) or null = uncut it.</summary>
    public static string? UncutRefusal(PartDef part, SlotMaterial? m, bool cut) => State(part, m, cut, true) == PartState.Cut ? null : "tailoring-flow-uncut-not";

    /// <summary>Optional part to toggle at texel <paramref name="i"/>: the highest-layer optional part covering it, -1 = none.</summary>
    public static int TogglePart(GarmentDef g, int i)
        => Enumerable.Range(0, g.Parts.Length).Where(p => g.Parts[p].Optional && !g.Parts[p].IsFixed && i >= 0 && i < g.Coverage[p].Length && g.Coverage[p][i])
            .OrderByDescending(p => g.Parts[p].Layer).DefaultIfEmpty(-1).First();

    /// <summary>Sneak + empty hand on part <paramref name="part"/>: filled → material back (off), empty → switch on / off.</summary>
    public static ToggleAction Toggle(PartDef part, SlotMaterial? m, bool on)
    {
        if (!part.Optional || part.IsFixed) return ToggleAction.Refuse;
        if (m != null) return ToggleAction.TakeOut;
        return on ? ToggleAction.SwitchOff : ToggleAction.SwitchOn;
    }

    /// <summary>Body segments whose placement moves part <paramref name="part"/> (the torso for sleeves that continue).</summary>
    public static HashSet<int> PartSegments(GarmentDef g, BodyWrapMap map, int part, bool continues)
    {
        var set = new HashSet<int>();
        if (part < 0 || part >= g.Parts.Length) return set;
        var cov = g.Coverage[part];
        for (int i = 0; i < cov.Length && i < map.Seg.Length; i++)
        {
            if (!cov[i]) continue;
            int s = MannequinPaint.PlacementSegment(map, i, continues);
            if (s >= 0) set.Add(s);
        }
        return set;
    }

    /// <summary>Segments locked by a cut part (its cloth placement is fixed until uncut).</summary>
    public static bool[] LockedSegments(GarmentDef g, BodyWrapMap? map, Func<int, SlotMaterial?> materialOf, Func<int, bool> cutOf, bool continues)
    {
        var locked = new bool[BodySegments.Count];
        if (map == null) return locked;
        for (int p = 0; p < g.Parts.Length; p++)
        {
            if (State(g, p, materialOf, cutOf, _ => true) != PartState.Cut) continue;
            foreach (int s in PartSegments(g, map, p, continues)) locked[s] = true;
        }
        return locked;
    }

    /// <summary>
    /// Whether moving part <paramref name="part"/>'s cloth (part offset / turn, or segment <paramref name="seg"/>, -1 = none)
    /// is refused: the part is cut, or the segment carries a cut part. Lang key or null.
    /// </summary>
    public static string? PlacementRefusal(GarmentDef g, int part, int seg, Func<int, SlotMaterial?> materialOf, Func<int, bool> cutOf, bool[] lockedSegments)
    {
        if (part >= 0 && part < g.Parts.Length && State(g, part, materialOf, cutOf, _ => true) == PartState.Cut) return "tailoring-flow-locked";
        if (seg >= 0 && seg < lockedSegments.Length && lockedSegments[seg]) return "tailoring-flow-locked";
        return null;
    }

    /// <summary>Empty optional parts that are off but take <paramref name="held"/>: shown as ghost while it is in hand.</summary>
    public static bool[] GhostParts(GarmentDef g, Func<int, SlotMaterial?> materialOf, Func<int, bool> onOf, SlotMaterial? held)
        => Enumerable.Range(0, g.Parts.Length).Select(p => held != null && State(g, p, materialOf, _ => false, onOf) == PartState.Off && g.Parts[p].Allows(held)).ToArray();

    /// <summary>Sneak + empty hand: every optional part that is off shows as ghost, so it can be found and switched on.</summary>
    public static bool[] ToggleGhostParts(GarmentDef g, Func<int, SlotMaterial?> materialOf, Func<int, bool> onOf)
        => Enumerable.Range(0, g.Parts.Length).Select(p => State(g, p, materialOf, _ => false, onOf) == PartState.Off).ToArray();

    /// <summary>Texels where part <paramref name="part"/> shows (visible owner with <paramref name="shown"/> parts): the shears highlight.</summary>
    public static int[] PartTexels(GarmentDef g, int part, Func<int, SlotMaterial?> materialOf, Func<int, bool> shown)
    {
        int[] visible = GarmentLayout.VisibleOwners(g, p => g.Parts[p].IsFixed || materialOf(p) != null || shown(p));
        return Enumerable.Range(0, visible.Length).Where(i => visible[i] == part).ToArray();
    }

    /// <summary>Part under texel <paramref name="i"/> for shears and toggles: the visible one among <paramref name="shown"/> parts, -1 = none.</summary>
    public static int VisiblePart(GarmentDef g, int i, Func<int, SlotMaterial?> materialOf, Func<int, bool> shown)
    {
        int[] visible = GarmentLayout.VisibleOwners(g, p => g.Parts[p].IsFixed || materialOf(p) != null || shown(p));
        return i >= 0 && i < visible.Length ? visible[i] : -1;
    }

    /// <summary>
    /// Preview pixels: texels whose visible part (shown parts, layer order) is empty become a faint ghost
    /// (<see cref="GhostArgb"/>): a new garment starts as an outline on the mannequin and fills up as it is draped.
    /// </summary>
    public static int Ghost(GarmentDef g, int[] px, Func<int, SlotMaterial?> materialOf, Func<int, bool> shown)
    {
        int[] visible = GarmentLayout.VisibleOwners(g, p => g.Parts[p].IsFixed || materialOf(p) != null || shown(p));
        int n = 0;
        for (int i = 0; i < px.Length && i < visible.Length; i++)
        {
            int p = visible[i];
            if (p < 0 || g.Parts[p].IsFixed || materialOf(p) != null) continue;
            px[i] = GhostArgb;
            n++;
        }
        return n;
    }

    /// <summary>
    /// Body region under the crosshair, by height on the mannequin's dressable shape (mesh space, blocks; the seraph
    /// static pose: feet 0, hips ~0.94, shoulders ~1.5, head 1.6+). Dress types of that region, picker order.
    /// </summary>
    public static string[] RegionSlots(double meshY) => meshY switch
    {
        >= 1.6 => new[] { "head", "face" },
        >= 1.48 => new[] { "neck", "face", "shoulder" },
        >= 1.0 => new[] { "upperbody", "upperbodyover", "shoulder", "arm", "hand", "emblem" },
        >= 0.88 => new[] { "waist", "lowerbody", "upperbody" },
        >= 0.25 => new[] { "lowerbody", "waist" },
        _ => new[] { "foot", "lowerbody" },
    };

    /// <summary>
    /// Body parts the picker offers: those of <paramref name="slots"/> (the mannequin's, with a garment) in the region at
    /// <paramref name="meshY"/>; all of them when null (F) or when the region has none.
    /// </summary>
    public static string[] PickerSlots(string[] slots, double? meshY)
    {
        if (meshY == null) return slots;
        var region = RegionSlots(meshY.Value);
        var hit = slots.Where(region.Contains).ToArray();
        return hit.Length > 0 ? hit : slots;
    }

    /// <summary>
    /// What a right click with <paramref name="tool"/> on a mannequin means when the crosshair is not on a garment
    /// (draping and stitching on the garment are handled before): picker, cancel, sew, or nothing (vanilla).
    /// </summary>
    public static string OffGarmentAction(FlowTool tool, bool sneak, bool garmentInProgress, bool sessionActive)
    {
        if (tool == FlowTool.SewingKit && !sneak && garmentInProgress) return "sew";
        if (tool == FlowTool.Shears && sneak && sessionActive) return "cancel";
        if ((tool == FlowTool.Empty || tool == FlowTool.Material) && !sneak && !garmentInProgress) return "pick";
        return "";
    }
}
