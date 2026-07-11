namespace Vivre.Desktop.ViewModels;

internal static class WinRmDeadEnd
{
    // Appended ONLY on a Kerberos rejection — the durable, cached state where the software check's
    // DCOM fallback is guaranteed to have a working channel. Describes the MECHANISM, not liveness
    // (a cached rejection can fire without contacting the box).
    public const string SoftwareRedirect =
        " For installed software, use right-click ▸ Software ▸ Check software… — it reads"
        + " over the DCOM backup channel, so it works on Kerberos-broken boxes like this.";
}
