using System.Collections.Generic;
using System.Linq;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// Client side of the live draft (lane/loom-live-draft): every edit of the draft window is sent to the server at once, and
/// every loom sync comes back. The window keeps the states it sent that the loom has not echoed yet, so a sync is either
/// the echo of one of them (nothing to do), a stale state while a newer edit is on its way (ignored), or a change made by
/// someone else (adopted). A sent state the server never echoes (refused) expires after <see cref="TimeoutMs"/>.
/// Pure (selftest).
/// </summary>
public sealed class DraftLiveSync
{
    public const long TimeoutMs = 3000;

    readonly List<(DraftWindowModel.Snapshot state, long at)> sent = new();

    public int Pending => sent.Count;

    /// <summary>Record the state just sent to the server at <paramref name="now"/> (ms).</summary>
    public void Sent(DraftWindowModel.Snapshot state, long now) => sent.Add((state, now));

    /// <summary>
    /// A loom sync with draft state <paramref name="loom"/> arrived while the window shows <paramref name="local"/>:
    /// true when the window must take the loom's state.
    /// </summary>
    public bool ShouldAdopt(DraftWindowModel.Snapshot loom, DraftWindowModel.Snapshot local, long now)
    {
        sent.RemoveAll(e => now - e.at > TimeoutMs);
        if (loom.SameAs(local)) { sent.Clear(); return false; } // in step (sent states older than this one are done)
        int i = sent.FindLastIndex(e => e.state.SameAs(loom));
        if (i >= 0) { sent.RemoveRange(0, i + 1); return false; } // the echo of an older edit; a newer one follows
        return sent.Count == 0; // an unknown state: someone else's change, unless our own edits are still on their way
    }

    public void Clear() => sent.Clear();
}
