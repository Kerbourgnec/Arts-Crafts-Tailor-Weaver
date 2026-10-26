using System;
using System.Linq;
using ArtsAndCrafts.Tailor.Loom;
using ArtsAndCrafts.Tailor.Pattern;
using ArtsAndCrafts.Tailor.Weaving;
using Vintagestory.API.Datastructures;

namespace ArtsAndCrafts.Tailor.Tapestry;

/// <summary>
/// A finished cloth put on an empty tapestry loom for embroidery (lane/loom-embroidery, docs/embroidery.md). Holds the
/// woven layer (never changed here), the stitch layer being worked, the layer it came with (for the cost) and what to give
/// back. Pure data (selftest); the block entity does the inventory and the packets.
/// <para>
/// Accepted: woven cloth / woven linen (square 16 or 32; given back as the same item with its attributes, embroidery
/// replaced), and plain vanilla linen (<c>game:linen-*</c>) / cloth (<c>game:cloth-{color}</c>): a plain base of flax (linen,
/// plain cloth) or the cloth colour's twine at the world's cloth size, given back as woven linen / woven cloth once it
/// has a stitch (unchanged: the vanilla item itself).
/// </para>
/// </summary>
public sealed class EmbroideryHoop
{
    public const string VanillaLinenPrefix = "game:linen-", VanillaClothPrefix = "game:cloth-";

    public PatternData Woven { get; }
    public Embroidery Layer { get; }
    /// <summary>The stitches the cloth had when it was put on (null: none); unchanged stitches cost nothing.</summary>
    public Embroidery? Before { get; }
    /// <summary>Code of the item put on the loom (dropped back as is when the loom breaks).</summary>
    public string SourceCode { get; }
    /// <summary>Its attributes (woven cloth / linen; empty for a vanilla item).</summary>
    public ITreeAttribute SourceAttrs { get; }
    /// <summary>Item code taken off: the woven item itself, or woven linen / cloth for a vanilla item.</summary>
    public string OutputCode { get; }
    public bool FromVanilla { get; }

    public int Size => Woven.Width;

    EmbroideryHoop(PatternData woven, Embroidery layer, Embroidery? before, string sourceCode, ITreeAttribute sourceAttrs, string outputCode, bool fromVanilla)
    {
        Woven = woven;
        Layer = layer;
        Before = before;
        SourceCode = sourceCode;
        SourceAttrs = sourceAttrs;
        OutputCode = outputCode;
        FromVanilla = fromVanilla;
    }

    /// <summary>What a cloth looks like on the frame now: woven + stitches.</summary>
    public PatternData Shown => Embroidery.Composite(Woven, Layer);

    /// <summary>Plain base twine and output item of a vanilla linen / cloth code, or null for anything else.</summary>
    public static (string baseCode, string outputCode)? VanillaBase(string code)
    {
        if (code.StartsWith(VanillaLinenPrefix)) return (LoomTwine.PlainTwineCode, ClothOutput.WovenLinenCode);
        if (!code.StartsWith(VanillaClothPrefix)) return null;
        string color = code.Substring(VanillaClothPrefix.Length);
        if (color == "plain") return (LoomTwine.PlainTwineCode, ClothOutput.WovenClothCode);
        return Array.IndexOf(ClothOutput.VanillaClothColors, color) >= 0 ? (LoomTwine.DyedTwinePrefix + color, ClothOutput.WovenClothCode) : null;
    }

    public enum MountResult { Ok, NotCloth, Size }

    /// <summary>
    /// The hoop for a woven cloth / linen stack (<paramref name="isWoven"/>: its collectible is <see cref="ItemWovenCloth"/>)
    /// or a vanilla linen / cloth (<paramref name="worldSize"/> cells, base colour <paramref name="twineHex"/>(base code)).
    /// </summary>
    public static MountResult TryMount(string code, ITreeAttribute attrs, bool isWoven, int worldSize, Func<string, string> twineHex, out EmbroideryHoop? hoop)
    {
        hoop = null;
        if (isWoven)
        {
            if (WovenTexture.Woven(attrs) is not { } woven) return MountResult.NotCloth;
            if (woven.Width != woven.Height || !WeavingState.IsAllowedSize(woven.Width)) return MountResult.Size;
            var before = Embroidery.Read(attrs, woven.Width, woven.Height);
            hoop = new EmbroideryHoop(woven, before?.Clone() ?? new Embroidery(woven.Width, woven.Height), before, code, attrs.Clone(), code, false);
            return MountResult.Ok;
        }
        if (VanillaBase(code) is not { } vb) return MountResult.NotCloth;
        int n = WeavingState.IsAllowedSize(worldSize) ? worldSize : WeavingState.DefaultWidth;
        var plain = new PatternData(new byte[n * n], new[] { twineHex(vb.baseCode) }, new[] { vb.baseCode }, n, n);
        hoop = new EmbroideryHoop(plain, new Embroidery(n, n), null, code, new TreeAttribute(), vb.outputCode, true);
        return MountResult.Ok;
    }

    /// <summary>Twine the take-off costs (<see cref="Embroidery.Cost"/>: 1 per twine code not yet paid on the cloth).</summary>
    public System.Collections.Generic.Dictionary<string, int> Cost() => Layer.Cost();

    /// <summary>A vanilla item without a stitch goes back unchanged.</summary>
    public bool GivesSource => FromVanilla && Layer.IsEmpty;

    /// <summary>Item code given on take-off.</summary>
    public string TakeOffCode => GivesSource ? SourceCode : OutputCode;

    /// <summary>
    /// Attributes of the stack given on take-off: the source's own (name, draft, ...) or, for a vanilla source, the plain
    /// woven pattern; the embroidery replaced by the worked layer (removed when empty).
    /// </summary>
    public TreeAttribute TakeOffAttributes()
    {
        if (GivesSource) return new TreeAttribute();
        var t = FromVanilla ? new TreeAttribute() : SourceAttrs.Clone() as TreeAttribute ?? new TreeAttribute();
        if (FromVanilla) Woven.ToAttributes(t);
        var worked = Layer.Clone();
        worked.MarkPaid(); // the take-off charges the cost: those codes are paid on the cloth from now on
        Embroidery.Write(t, worked);
        return t;
    }

    public void ToTree(ITreeAttribute tree)
    {
        var w = new TreeAttribute();
        Woven.ToAttributes(w);
        tree["woven"] = w;
        var l = new TreeAttribute();
        Layer.ToTree(l);
        tree["layer"] = l;
        if (Before != null)
        {
            var b = new TreeAttribute();
            Before.ToTree(b);
            tree["before"] = b;
        }
        tree.SetString("source", SourceCode);
        tree["sourceAttrs"] = SourceAttrs.Clone();
        tree.SetString("output", OutputCode);
        tree.SetBool("vanilla", FromVanilla);
    }

    public static EmbroideryHoop? FromTree(ITreeAttribute? tree)
    {
        if (tree == null) return null;
        if (WovenTexture.Woven(tree.GetTreeAttribute("woven")) is not { } woven) return null;
        var layer = Embroidery.FromTree(tree.GetTreeAttribute("layer"));
        if (layer == null || layer.Width != woven.Width || layer.Height != woven.Height) layer = new Embroidery(woven.Width, woven.Height);
        var before = Embroidery.FromTree(tree.GetTreeAttribute("before"));
        string? source = tree.GetString("source"), output = tree.GetString("output");
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(output)) return null;
        return new EmbroideryHoop(woven, layer, before, source, tree.GetTreeAttribute("sourceAttrs") ?? new TreeAttribute(), output, tree.GetBool("vanilla"));
    }

    /// <summary>Cell index (selection box index of an embroidery cell) → (x, y), row 0 at the bottom of the frame.</summary>
    public static (int x, int y) CellXY(int index, int size) => (index % size, index / size);

    public bool ValidCell(int index) => index >= 0 && index < Size * Size;

    /// <summary>Distinct twine codes with a stitch (block info).</summary>
    public string[] StitchCodes() => Layer.UsedCodes().OrderBy(c => c).ToArray();
}
