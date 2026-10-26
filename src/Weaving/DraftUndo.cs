using System;
using System.Collections.Generic;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// Undo / redo of the draft window's plan edits (lane/draft-polish, owner item 4): a stack of
/// <see cref="DraftWindowModel.Snapshot"/>s. <see cref="Do"/> wraps every edit: it records the state before, and only when the
/// edit succeeded and changed something. Warp loads are server actions and are not undone (docs/weaving.md). Pure (selftest).
/// </summary>
public sealed class DraftUndo
{
    /// <summary>Steps kept (oldest dropped).</summary>
    public const int Limit = 100;

    readonly List<DraftWindowModel.Snapshot> undo = new(), redo = new();

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public int UndoCount => undo.Count;

    /// <summary>Run <paramref name="edit"/> on <paramref name="m"/>; a successful change becomes one undo step (redo cleared). Returns the edit's result.</summary>
    public bool Do(DraftWindowModel m, Func<bool> edit)
    {
        var before = m.TakeSnapshot();
        bool ok = edit();
        if (ok && !m.TakeSnapshot().SameAs(before)) Push(before);
        return ok;
    }

    /// <summary>Record <paramref name="before"/> as one step (for edits made outside <see cref="Do"/>).</summary>
    public void Push(DraftWindowModel.Snapshot before)
    {
        undo.Add(before);
        if (undo.Count > Limit) undo.RemoveAt(0);
        redo.Clear();
    }

    public bool Undo(DraftWindowModel m) => Step(m, undo, redo);

    public bool Redo(DraftWindowModel m) => Step(m, redo, undo);

    static bool Step(DraftWindowModel m, List<DraftWindowModel.Snapshot> from, List<DraftWindowModel.Snapshot> to)
    {
        // Skip steps that no longer change anything (e.g. rows woven since) or are of another size.
        while (from.Count > 0)
        {
            var s = from[^1];
            from.RemoveAt(from.Count - 1);
            var now = m.TakeSnapshot();
            if (!m.Restore(s)) continue;
            if (m.TakeSnapshot().SameAs(now)) continue;
            to.Add(now);
            return true;
        }
        return false;
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
    }
}
