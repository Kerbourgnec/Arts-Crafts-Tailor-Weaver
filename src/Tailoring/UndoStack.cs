using System.Collections.Generic;

namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// Snapshot undo / redo (lane/sewing-editor-xl): the embroider step pushes a copy of the working layer before each stroke.
/// The caller owns the snapshots (pass copies). A new push clears redo; the oldest snapshot drops past the capacity.
/// Pure (selftest: EditorViewChecks).
/// </summary>
public sealed class UndoStack<T> where T : class
{
    readonly List<T> undo = new(), redo = new();
    readonly int capacity;

    public UndoStack(int capacity = 200) => this.capacity = capacity < 1 ? 1 : capacity;

    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>State before a change.</summary>
    public void Push(T before)
    {
        undo.Add(before);
        if (undo.Count > capacity) undo.RemoveAt(0);
        redo.Clear();
    }

    /// <summary>Previous state, or null; <paramref name="current"/> goes on the redo stack.</summary>
    public T? Undo(T current)
    {
        if (undo.Count == 0) return null;
        var s = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        redo.Add(current);
        return s;
    }

    /// <summary>State undone last, or null; <paramref name="current"/> goes back on the undo stack.</summary>
    public T? Redo(T current)
    {
        if (redo.Count == 0) return null;
        var s = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        undo.Add(current);
        return s;
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
    }
}
