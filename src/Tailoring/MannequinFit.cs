using System;
using System.Collections.Generic;
using System.Linq;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Which garments a vanilla mannequin can be sewn on and where the finished garment goes (pure; selftested).
/// <c>allowed</c> = the mannequin block's <c>allowedDresstypes</c> attribute; BEBehaviorMannequin creates one wearable
/// slot per entry, in that order, so the index of a clothes category is its slot.
/// </summary>
public static class MannequinFit
{
    public static bool Fits(IReadOnlyList<string> allowed, string? category) => category != null && allowed.Contains(category);

    /// <summary>Mannequin slot that wears <paramref name="category"/>, -1 when the mannequin has none.</summary>
    public static int WornSlot(IReadOnlyList<string> allowed, string? category)
    {
        if (category == null) return -1;
        for (int i = 0; i < allowed.Count; i++) if (allowed[i] == category) return i;
        return -1;
    }

    /// <summary>True: the finished garment is dressed on the mannequin; false: it goes to the player (slot taken).</summary>
    public static bool DressOnMannequin(IReadOnlyList<string> allowed, string? category, Func<int, bool> slotEmpty)
    {
        int slot = WornSlot(allowed, category);
        return slot >= 0 && slotEmpty(slot);
    }

    /// <summary>Garments of the catalog this mannequin can wear, in catalog order.</summary>
    public static GarmentDef[] Garments(GarmentCatalog cat, IReadOnlyList<string> allowed, Func<GarmentDef, string?> categoryOf)
        => cat.Garments.Where(g => Fits(allowed, categoryOf(g))).ToArray();

    /// <summary>
    /// Sewing dialog step 1 (lane/garments-3): the dress slots (body parts) that have at least one of
    /// <paramref name="garments"/>, in <see cref="GarmentCatalog.DressTypes"/> order. Slots without a garment are not offered.
    /// </summary>
    public static string[] Slots(IEnumerable<GarmentDef> garments)
    {
        var have = new HashSet<string>(garments.Select(g => g.DressType));
        return GarmentCatalog.DressTypes.Where(have.Contains).ToArray();
    }

    /// <summary>Step 2: the garments of one dress slot, in catalog order.</summary>
    public static GarmentDef[] ForSlot(IEnumerable<GarmentDef> garments, string? slot)
        => garments.Where(g => g.DressType == slot).ToArray();

    /// <summary>Number of pages of <paramref name="count"/> buttons, <paramref name="perPage"/> per page (at least 1).</summary>
    public static int Pages(int count, int perPage) => Math.Max(1, (count + perPage - 1) / perPage);

    /// <summary>Page shown first: the one holding <paramref name="index"/> (the picked garment), else 0.</summary>
    public static int PageOf(int index, int perPage) => index < 0 ? 0 : index / perPage;
}
