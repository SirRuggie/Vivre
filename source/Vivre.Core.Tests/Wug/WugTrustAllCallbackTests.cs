using Vivre.Core.Wug;
using Xunit;

namespace Vivre.Core.Tests.Wug;

/// <summary>
/// Pure (no-process) string-lock tests for the trust-all certificate wiring in the WUG state-read and
/// maintenance-set scripts. The module's <c>-IgnoreSSLErrors</c> installs a PowerShell SCRIPTBLOCK as the
/// process-wide <c>ServerCertificateValidationCallback</c>, and a scriptblock callback dies on a COLD TLS
/// handshake (the mass per-row "state unknown" LookupError bug). The fix installs a COMPILED trust-all
/// policy and nulls that callback so the compiled policy is the active validator — at HEAD before any
/// fan-out, and re-nulled in every pooled worker after its own Connect re-installs the scriptblock, but
/// ONLY when the compiled policy is actually in place. These locks assert the null-out lives where it must,
/// in the right order, so a future edit can't silently drop it (the compiler can't catch a script string).
/// </summary>
public class WugTrustAllCallbackTests
{
    private const string NullOut = "ServerCertificateValidationCallback = $null";
    private const string CompiledInstall = "CertificatePolicy = New-Object VivreWugTrustAll";

    // ── HEAD: the null-out lives in the head, before any fan-out here-string ─────────────────────────

    [Fact]
    public void Head_nulls_scriptblock_callback_before_any_fanout_text()
    {
        // The HEAD null-out must precede $resolverText — i.e. it is done ONCE up front, before the worker
        // tail / resolver here-strings are emitted, so no fan-out handshake ever rides the scriptblock.
        int nullOut = WugMaintenance.StateScript.IndexOf(NullOut, StringComparison.Ordinal);
        int fanout  = WugMaintenance.StateScript.IndexOf("$resolverText = @'", StringComparison.Ordinal);
        Assert.True(nullOut >= 0, "StateScript must null the callback in the HEAD.");
        Assert.True(fanout > 0, "StateScript must fan out via the $resolverText here-string.");
        Assert.True(nullOut < fanout,
            $"HEAD null-out (index {nullOut}) must come before the fan-out text (index {fanout}).");
    }

    [Fact]
    public void Head_nulls_scriptblock_callback_after_installing_compiled_policy()
    {
        // Order inside the try: install the compiled policy FIRST, then null the callback, then flag ready.
        // Nulling before/without a successful install would leave the scriptblock as the only trust-all.
        int install = WugMaintenance.StateScript.IndexOf(CompiledInstall, StringComparison.Ordinal);
        int nullOut = WugMaintenance.StateScript.IndexOf(NullOut, StringComparison.Ordinal);
        int ready   = WugMaintenance.StateScript.IndexOf("$vivreTrustAllReady = $true", StringComparison.Ordinal);
        Assert.True(install >= 0, "StateScript must install the compiled VivreWugTrustAll policy.");
        Assert.True(ready > 0, "StateScript must flag the compiled policy ready.");
        Assert.True(install < nullOut, "The null-out must come AFTER the compiled-policy install.");
        Assert.True(nullOut < ready, "The null-out must come BEFORE $vivreTrustAllReady = $true (inside the try).");
    }

    // ── Pooled worker tail: re-null after Connect, guarded by the compiled policy being present ─────────

    [Fact]
    public void Worker_tail_renulls_callback_after_connect_guarded_by_compiled_policy()
    {
        int connect = WugMaintenance.StateWorkerTailBody.IndexOf("Connect-WUGServer", StringComparison.Ordinal);
        int guard   = WugMaintenance.StateWorkerTailBody.IndexOf("VivreWugTrustAll", StringComparison.Ordinal);
        int nullOut = WugMaintenance.StateWorkerTailBody.IndexOf(NullOut, StringComparison.Ordinal);
        Assert.True(connect >= 0, "The worker tail must connect per runspace.");
        Assert.True(guard > connect,
            $"The compiled-policy guard (VivreWugTrustAll) must come after Connect (guard {guard}, connect {connect}).");
        Assert.True(nullOut > connect,
            $"The re-null must come after Connect re-installs the scriptblock (null-out {nullOut}, connect {connect}).");
    }

    // ── Set path: the maintenance-set script installs the compiled policy + nulls the callback too ──────

    [Fact]
    public void Set_path_script_installs_compiled_policy_and_nulls_callback()
    {
        Assert.Contains("VivreWugTrustAll", WugMaintenance.Script);
        Assert.Contains(NullOut, WugMaintenance.Script);
    }
}
