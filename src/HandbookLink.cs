using Vintagestory.API.Client;

namespace ArtsAndCrafts.Tailor;

/// <summary>
/// Open a handbook page from a dialog (lane/draft-polish). The survival handbook registers the <c>handbook://</c> link
/// protocol (vssurvivalmod <c>ModSystemSurvivalHandbook</c>: <c>capi.RegisterLinkProtocol("handbook", ...)</c>); calling it
/// with a <see cref="LinkTextComponent"/> opens the handbook (if closed) on that page, exactly as a VTML link does.
/// Page codes are the <c>pageCode</c> of a guide page in <c>config/handbook/*.json</c> (e.g. <c>actailor:weaving-draft-guide</c>)
/// or an item page code. Kept in its own file so other dialogs (sewing, embroidery) can share it.
/// </summary>
public static class HandbookLink
{
    /// <summary>Open the handbook on <paramref name="pageCode"/>; false when no handbook link protocol is registered.</summary>
    public static bool Open(ICoreClientAPI capi, string pageCode)
    {
        if (!capi.LinkProtocols.TryGetValue("handbook", out var open) || open == null) return false;
        open(new LinkTextComponent("handbook://" + pageCode));
        return true;
    }
}
