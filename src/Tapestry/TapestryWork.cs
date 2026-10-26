using System.Collections.Generic;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Tapestry;

/// <summary>
/// What a tapestry loom holds (lane/tapestry-loom): the cloth (<see cref="WeavingState"/>, always in tapestry mode: every
/// cell painted by hand, row by row) and its <see cref="TapestryGeometry.BobbinCount"/> bobbins (<see cref="SpoolRack"/>:
/// one twine code each, up to <see cref="SpoolRack.Capacity"/>, one active). Pure data: no game API calls (selftest).
/// <para>
/// Rules (docs/tapestry-loom.md): paint the current row with the held twine, or with an empty hand from the active bobbin
/// (brush 1/2/4/8 cells, sneak fills the row); beat when every cell is painted (the next row starts as a copy); roll the
/// last row back; copy any woven row into the current row; cut when the square is complete (twine charged then:
/// <see cref="ClothCost"/>, bobbins first). The next cloth keeps the last row's colours and takes the world's size.
/// </para>
/// </summary>
public sealed class TapestryWork
{
    public readonly WeavingState Cloth = new();
    public readonly SpoolRack Bobbins = new(TapestryGeometry.BobbinCount);
    /// <summary>A finished cloth on the frame for embroidery (lane/loom-embroidery), or null; the weaving waits meanwhile.</summary>
    public EmbroideryHoop? Hoop;

    /// <summary>A cloth can go on the frame: no hoop and no row woven (lane/loom-embroidery).</summary>
    public bool CanMount => Hoop == null && Cloth.WovenRows == 0;

    public TapestryWork() : this(WeavingState.DefaultWidth) { }

    public TapestryWork(int size)
    {
        Cloth.Reset(size);
        ForceTapestry();
    }

    /// <summary>The cloth is woven as a tapestry only (a new <see cref="WeavingState"/> starts in draft mode).</summary>
    void ForceTapestry()
    {
        Cloth.Draft.Mode = LoomMode.Tapestry;
        Cloth.Draft.CurrentTreadle = DraftState.NoTreadle;
        Cloth.Draft.Plan = null;
    }

    public int Size => Cloth.Width;
    public int WovenRows => Cloth.WovenRows;
    public bool Complete => Cloth.Complete;

    public enum PaintResult { Ok, Complete, OutOfRange, PaletteFull }

    /// <summary>
    /// Paint the brush span (<see cref="WeavingState.BrushSpan"/>) around <paramref name="cell"/> of the current row, or
    /// the whole row (<paramref name="fill"/>), with twine <paramref name="code"/> (colour <paramref name="hex"/> frozen at
    /// its first use in this cloth).
    /// </summary>
    public PaintResult Paint(int cell, string code, string hex, int brush, bool fill)
    {
        if (Complete) return PaintResult.Complete;
        if (!fill && (cell < 0 || cell >= Size)) return PaintResult.OutOfRange;
        int idx = Cloth.PaletteIndexFor(code, hex);
        if (idx < 0) return PaintResult.PaletteFull;
        bool ok = fill ? Cloth.Paint(0, (byte)idx, fill: true) : Cloth.PaintBrush(cell, brush, (byte)idx);
        return ok ? PaintResult.Ok : PaintResult.OutOfRange;
    }

    public enum BeatResult { Ok, Complete, RowIncomplete }

    /// <summary>Beat the current row in (every cell painted); the next row starts as a copy of it.</summary>
    public BeatResult Beat()
    {
        if (Complete) return BeatResult.Complete;
        if (!Cloth.CurrentRowFilled) return BeatResult.RowIncomplete;
        Cloth.CommitCurrentRow();
        return BeatResult.Ok;
    }

    /// <summary>Roll the last woven row back: it becomes the current row again (the painted current row is dropped).</summary>
    public bool RollBack() => Cloth.Unweave(out _);

    /// <summary>Copy woven row <paramref name="row"/> into the current row.</summary>
    public bool CopyRow(int row) => Cloth.CopyRow(row);

    /// <summary>Twine per code the cloth costs if cut now (<see cref="ClothCost"/> over the woven rows).</summary>
    public Dictionary<string, int> CutCost() => ClothCost.TwineByCode(Cloth.CellsByCode(Cloth.WovenCells), Cloth.Product);

    public bool CanCut => Cloth.CanCut;

    /// <summary>Next cloth after a cut (or a world size change on an empty frame): its first row = the last row's colours.</summary>
    public void StartNextCloth(int size)
    {
        Cloth.StartNextCloth(size);
        ForceTapestry();
    }

    /// <summary>What a click on a bobbin does (client picks it from held item + sneak; the server re-checks the hand).</summary>
    public enum BobbinAction { WindOne = 0, WindStack = 1, Select = 2, Unwind = 3 }

    public enum BobbinResult { Ok, Invalid, OtherTwine, Full, Empty }

    /// <summary>Wind up to <paramref name="n"/> twine of <paramref name="code"/> onto bobbin <paramref name="i"/>; the first filled bobbin becomes active.</summary>
    public BobbinResult Wind(int i, string code, int n, out int added)
    {
        added = 0;
        if (i < 0 || i >= Bobbins.Count || n <= 0) return BobbinResult.Invalid;
        if (Bobbins.CodeAt(i) is { } other && other != code) return BobbinResult.OtherTwine;
        added = Bobbins.Load(i, code, n);
        if (added <= 0) return BobbinResult.Full;
        if (Bobbins.SelectedCode == null) Bobbins.Select(i);
        return BobbinResult.Ok;
    }

    public BobbinResult Select(int i)
    {
        if (i < 0 || i >= Bobbins.Count) return BobbinResult.Invalid;
        return Bobbins.Select(i) ? BobbinResult.Ok : BobbinResult.Empty;
    }

    /// <summary>Take the twine off bobbin <paramref name="i"/> (code null when it was empty).</summary>
    public (string? code, int amount) Unwind(int i) => Bobbins.Unload(i);

    public void ToTree(ITreeAttribute tree)
    {
        var w = new TreeAttribute();
        Cloth.ToTree(w);
        tree["cloth"] = w;
        var b = new TreeAttribute();
        Bobbins.ToTree(b);
        tree["bobbins"] = b;
        if (Hoop != null)
        {
            var h = new TreeAttribute();
            Hoop.ToTree(h);
            tree["hoop"] = h;
        }
    }

    public void FromTree(ITreeAttribute? tree)
    {
        Cloth.FromTree(tree?.GetTreeAttribute("cloth"));
        ForceTapestry();
        Bobbins.FromTree(tree?.GetTreeAttribute("bobbins"));
        Hoop = EmbroideryHoop.FromTree(tree?.GetTreeAttribute("hoop"));
    }
}
