using System;
using System.Collections.Generic;
using System.Linq;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Pending (unapplied) embroidery of a mannequin, ONE shared state for the sewing window and in-world stitching
/// (lane/sewing-polish, owner 2026-10-25; docs/embroidery.md "Shared pending stitches"). Pure; offline-tested.
/// <para>
/// Per target (<see cref="GarmentEmbroidery.TargetSession"/> = garment in progress, else a mannequin worn slot): the
/// pending layer (applied stitches + unapplied changes, carries the applied layer's paid set) and an undo / redo history
/// of whole strokes. Lives on the server in the session (saved and synced in the block entity tree); the history is
/// server memory only (lost on unload / restart; the pending layer stays). Every stroke, from the window or from a right
/// click on the mannequin, lands here; undo / redo / discard / apply act on it whatever made the stroke. Leaving the
/// Embroider tab or closing the window touches nothing.
/// </para>
/// <para>
/// Each draft remembers a key of what it was painted on (<see cref="SessionKey"/> / <see cref="WornKey"/>): a draft whose
/// key no longer matches (another garment in progress, the worn garment taken off or swapped) is ignored and dropped
/// on the next write.
/// </para>
/// </summary>
public sealed class EmbroideryDrafts
{
    public const string TreeKey = "embDrafts";
    public const int HistoryCapacity = 200;

    sealed class Draft
    {
        public required string Key;
        public required Embroidery Layer;
        public readonly UndoStack<Embroidery> History = new(HistoryCapacity);
    }

    readonly Dictionary<int, Draft> drafts = new();

    public static string SessionKey(string garmentCode) => "session:" + garmentCode;

    /// <summary>Key of a worn tailored garment: recipe + its data without the stitches (applying stitches keeps the key).</summary>
    public static string WornKey(GarmentDef def, GarmentData data)
    {
        var applied = data.Embroidery;
        data.Embroidery = null;
        try { return "worn:" + def.Code + ":" + data.Hash(); }
        finally { data.Embroidery = applied; }
    }

    /// <summary>Targets that have a draft (any key).</summary>
    public IEnumerable<int> Targets => drafts.Keys.OrderBy(t => t);

    /// <summary>The pending layer of <paramref name="target"/> when it was painted on <paramref name="key"/>; null = none.</summary>
    public Embroidery? Get(int target, string key) => drafts.TryGetValue(target, out var d) && d.Key == key ? d.Layer : null;

    /// <summary>Unapplied changes: a draft that differs from <paramref name="applied"/>.</summary>
    public bool HasChanges(int target, string key, Embroidery? applied)
        => Get(target, key) is { } layer && !GarmentEmbroidery.Same(applied, layer);

    public int UndoCount(int target, string key) => drafts.TryGetValue(target, out var d) && d.Key == key ? d.History.UndoCount : 0;
    public int RedoCount(int target, string key) => drafts.TryGetValue(target, out var d) && d.Key == key ? d.History.RedoCount : 0;

    /// <summary>
    /// A stroke (or its continuation) made <paramref name="next"/> the pending layer of <paramref name="target"/>.
    /// <paramref name="newStroke"/> = the layer before it becomes one undo step (a drag sends one stroke in several parts).
    /// <paramref name="applied"/> is the base when there is no draft yet. False when nothing changed.
    /// </summary>
    public bool Stroke(int target, string key, Embroidery? applied, Embroidery next, bool newStroke)
    {
        var before = Get(target, key);
        var baseLayer = before ?? applied;
        if (GarmentEmbroidery.Same(baseLayer, next) && before != null) return false;
        if (before == null)
        {
            if (GarmentEmbroidery.Same(applied, next)) return false;
            drafts[target] = new Draft { Key = key, Layer = next.Clone() };
            if (newStroke) drafts[target].History.Push(applied?.Clone() ?? new Embroidery(next.Width, next.Height));
            return true;
        }
        var d = drafts[target];
        if (newStroke) d.History.Push(d.Layer.Clone());
        d.Layer = next.Clone();
        return true;
    }

    /// <summary>Back one stroke; false when there is nothing to undo.</summary>
    public bool Undo(int target, string key)
    {
        if (!drafts.TryGetValue(target, out var d) || d.Key != key) return false;
        var s = d.History.Undo(d.Layer);
        if (s == null) return false;
        d.Layer = s;
        return true;
    }

    /// <summary>Forward one undone stroke; false when there is nothing to redo.</summary>
    public bool Redo(int target, string key)
    {
        if (!drafts.TryGetValue(target, out var d) || d.Key != key) return false;
        var s = d.History.Redo(d.Layer);
        if (s == null) return false;
        d.Layer = s;
        return true;
    }

    /// <summary>Drops the draft and its history (Discard, or after Apply); false when there was none.</summary>
    public bool Clear(int target) => drafts.Remove(target);

    public void ClearAll() => drafts.Clear();

    /// <summary>Drops drafts whose key no longer matches (<paramref name="keyOf"/> null = target gone); true when one went.</summary>
    public bool Prune(Func<int, string?> keyOf)
    {
        var stale = drafts.Where(p => keyOf(p.Key) != p.Value.Key).Select(p => p.Key).ToList();
        foreach (int t in stale) drafts.Remove(t);
        return stale.Count > 0;
    }

    /// <summary>Layers and keys (not the history).</summary>
    public void ToTree(ITreeAttribute tree)
    {
        tree.RemoveAttribute(TreeKey);
        if (drafts.Count == 0) return;
        var all = new TreeAttribute();
        foreach (var (target, d) in drafts.OrderBy(p => p.Key))
        {
            var t = new TreeAttribute();
            t.SetString("key", d.Key);
            var layer = new TreeAttribute();
            d.Layer.ToTree(layer); // not compacted: the palette indices stay as painted
            t["layer"] = layer;
            all[target.ToString(System.Globalization.CultureInfo.InvariantCulture)] = t;
        }
        tree[TreeKey] = all;
    }

    /// <summary>
    /// Reads layers and keys back. A layer equal to the stored one keeps its history (the client gets a sync per stroke;
    /// the server reads only on load), any other change starts a fresh history.
    /// </summary>
    public void FromTree(ITreeAttribute tree)
    {
        var all = tree.GetTreeAttribute(TreeKey);
        var seen = new HashSet<int>();
        if (all != null)
        {
            foreach (var (name, value) in all)
            {
                if (!int.TryParse(name, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int target)) continue;
                if (value is not ITreeAttribute t) continue;
                string key = t.GetString("key", "") ?? "";
                var layer = Embroidery.FromTree(t.GetTreeAttribute("layer"));
                if (key.Length == 0 || layer == null) continue;
                seen.Add(target);
                if (drafts.TryGetValue(target, out var d) && d.Key == key && GarmentEmbroidery.Same(d.Layer, layer) && d.Layer.Paid.SetEquals(layer.Paid)) continue;
                drafts[target] = new Draft { Key = key, Layer = layer };
            }
        }
        foreach (int t in drafts.Keys.Where(k => !seen.Contains(k)).ToList()) drafts.Remove(t);
    }
}
