namespace ArtsAndCrafts.Tailor.Tailoring;

/// <summary>
/// lane/body-wrap: the sleeves toggle of the Sew step (docs/garments.md "Body-space wrap"). "Sleeves: joined" = arm
/// segments continue the torso's cloth placement; "Sleeves: own" = each arm segment is placed on its own. Optimistic:
/// the local session copy flips at once (canvas + mannequin), the server sync confirms it.
/// </summary>
public partial class GuiDialogTailoring
{
    string SleevesLabel(GarmentDef? g)
        => g == null ? L("tailoring-gui-sleeves-none") : L(host.Session.Wrap.Continues(g) ? "tailoring-gui-sleeves-continue" : "tailoring-gui-sleeves-separate");

    bool OnToggleSleeves()
    {
        var g = host.Garment;
        if (g == null) return true;
        string mode = host.Session.Wrap.Continues(g) ? WrapLayout.SleevesSeparate : WrapLayout.SleevesContinue;
        host.Session.SetSleeves(mode);
        host.SendSetSleeves(mode);
        host.SetLocalPreview(local);
        Rebuild();
        return true;
    }
}
