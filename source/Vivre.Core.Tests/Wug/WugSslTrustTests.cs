using Vivre.Core.Wug;
using Xunit;

namespace Vivre.Core.Tests.Wug;

/// <summary>
/// Pure (no-process) string-lock tests for the SSL-trust wiring across the four WUG on-host scripts. The
/// <c>WhatsUpGoldPS</c> request wrapper re-arms a PowerShell SCRIPTBLOCK
/// <c>ServerCertificateValidationCallback</c> on every API call while the module's ignore-SSL flag is set
/// (by <c>Connect-WUGServer</c> with that flag); a scriptblock callback dies on the I/O-completion threads
/// that service cold TLS handshakes, producing mass per-row LookupErrors. The fix NEVER passes the flag (so
/// no module callback site ever fires) and installs our own COMPILED delegate (<c>VivreWugCertValidator</c>)
/// once, before any TLS. These locks assert that contract on the composed const strings so a future edit — or
/// a module update that reintroduces the flag — is caught by these compiler-blind tests, not in the field.
/// <para>REPLACES WugTrustAllCallbackTests, which locked the OLD design (an ICertificatePolicy trust-all
/// installed AFTER Connect's ignore-SSL flag, plus a per-worker callback re-null). That whole shape is gone:
/// the flag is never passed, so there is no module scriptblock callback to override or re-null.</para>
/// </summary>
public class WugSslTrustTests
{
    // The exact ignore-SSL switch token the module exposes; its absence in every script is the tripwire.
    private const string Flag = "-IgnoreSSLErrors";
    private const string Validator = "VivreWugCertValidator";
    private const string Connect = "Connect-WUGServer";
    private const string HardFail = "Couldn't establish a trusted connection to WhatsUp Gold";
    private const string SslErrCheck = "if ($sslTrustErr)";

    // ── (a) NO-FLAG invariant: the module-update tripwire the operator asked for ─────────────────────

    [Fact]
    public void No_script_passes_the_ignore_ssl_flag()
    {
        // If a module update or a careless edit ever puts the ignore-SSL flag back on a Connect line, the
        // scriptblock-callback bug returns; these four asserts fail the build before it can ship.
        Assert.DoesNotContain(Flag, WugMaintenance.Script);
        Assert.DoesNotContain(Flag, WugMaintenance.StateScript);
        Assert.DoesNotContain(Flag, WugMaintenance.StateWorkerTailBody);
        Assert.DoesNotContain(Flag, WugMaintenance.PreflightScript);
    }

    // ── (b) The compiled delegate is installed BEFORE any connect (and before the state-read fan-out) ─

    [Fact]
    public void Script_installs_the_compiled_delegate_before_connect()
    {
        int install = WugMaintenance.Script.IndexOf(Validator, StringComparison.Ordinal);
        int connect = WugMaintenance.Script.IndexOf(Connect, StringComparison.Ordinal);
        Assert.True(install >= 0, "Script must install the compiled VivreWugCertValidator.");
        Assert.True(connect > install, $"Delegate install (index {install}) must precede connect (index {connect}).");
    }

    [Fact]
    public void PreflightScript_installs_the_compiled_delegate_before_connect()
    {
        int install = WugMaintenance.PreflightScript.IndexOf(Validator, StringComparison.Ordinal);
        int connect = WugMaintenance.PreflightScript.IndexOf(Connect, StringComparison.Ordinal);
        Assert.True(install >= 0, "PreflightScript must install the compiled VivreWugCertValidator.");
        Assert.True(connect > install, $"Delegate install (index {install}) must precede connect (index {connect}).");
    }

    [Fact]
    public void StateScript_installs_the_compiled_delegate_before_connect_and_before_fanout()
    {
        int install = WugMaintenance.StateScript.IndexOf(Validator, StringComparison.Ordinal);
        int connect = WugMaintenance.StateScript.IndexOf(Connect, StringComparison.Ordinal);
        int fanout = WugMaintenance.StateScript.IndexOf("$resolverText = @'", StringComparison.Ordinal);
        Assert.True(install >= 0, "StateScript must install the compiled VivreWugCertValidator in the HEAD.");
        Assert.True(connect > install, $"Delegate install (index {install}) must precede the HEAD connect (index {connect}).");
        Assert.True(fanout > install, $"Delegate install (index {install}) must precede the fan-out here-string (index {fanout}).");
    }

    [Fact]
    public void Worker_tail_connects_but_never_touches_the_validation_callback()
    {
        // Each worker runspace connects (per runspace) but relies on the HEAD-installed process-wide delegate:
        // it must NOT install or null a callback itself — nulling would REMOVE our delegate under the new design.
        Assert.Contains(Connect, WugMaintenance.StateWorkerTailBody);
        Assert.DoesNotContain("ServerCertificateValidationCallback", WugMaintenance.StateWorkerTailBody);
    }

    // ── (c) Hard-fail: every connecting script refuses to connect on a failed delegate install ───────

    [Fact]
    public void Script_hard_fails_on_a_failed_delegate_install_before_connect()
        => AssertHardFailBeforeConnect(WugMaintenance.Script);

    [Fact]
    public void StateScript_hard_fails_on_a_failed_delegate_install_before_connect()
        => AssertHardFailBeforeConnect(WugMaintenance.StateScript);

    [Fact]
    public void PreflightScript_hard_fails_on_a_failed_delegate_install_before_connect()
        => AssertHardFailBeforeConnect(WugMaintenance.PreflightScript);

    private static void AssertHardFailBeforeConnect(string script)
    {
        Assert.Contains(HardFail, script);
        int check = script.IndexOf(SslErrCheck, StringComparison.Ordinal);
        int connect = script.IndexOf(Connect, StringComparison.Ordinal);
        Assert.True(check >= 0, "The script must check $sslTrustErr before connecting.");
        Assert.True(connect > check, $"The $sslTrustErr hard-fail (index {check}) must come before connect (index {connect}).");
    }

    // ── (d) The old ICertificatePolicy trust-all is fully gone ──────────────────────────────────────

    [Fact]
    public void The_old_trust_all_policy_is_gone_from_every_script()
    {
        // The new class name deliberately omits "VivreWugTrustAll" so this absence check is meaningful.
        foreach (string script in new[]
        {
            WugMaintenance.Script,
            WugMaintenance.StateScript,
            WugMaintenance.StateWorkerTailBody,
            WugMaintenance.PreflightScript,
        })
        {
            Assert.DoesNotContain("VivreWugTrustAll", script);
            Assert.DoesNotContain("CertificatePolicy = New-Object", script);
        }
    }

    // ── (e) The composed install carries the RUNTIME-resolved reference set (the field-fix lock) ──────

    private const string Refs = "-ReferencedAssemblies";
    private const string RuntimeMarker = "X509Chain].Assembly.Location";
    private const string Install = "[VivreWugCertValidator]::Install()";

    [Fact]
    public void Ssl_install_resolves_references_at_runtime_before_installing()
    {
        // The Add-Type that compiles VivreWugCertValidator MUST pass -ReferencedAssemblies built from the
        // LIVE types (the "X509Chain].Assembly.Location" marker). On boxes where X509Chain / X509Certificate
        // are type-forwarded OUT of System.dll a reference-less Add-Type FAILS TO COMPILE AT RUNTIME (the
        // field failure a green build never caught). Reflection type resolution follows the forward, so
        // each type's Assembly.Location lands on the real holder. This locks the runtime-resolved reference
        // set into the shared const AND every consumer, so a future edit can't silently drop it — and the
        // -ReferencedAssemblies must appear BEFORE the Install() call it feeds.
        foreach (string script in new[]
        {
            WugMaintenance.SslTrustInstallScript,
            WugMaintenance.Script,
            WugMaintenance.StateScript,
            WugMaintenance.PreflightScript,
        })
        {
            Assert.Contains(Refs, script);
            Assert.Contains(RuntimeMarker, script);
            int refs = script.IndexOf(Refs, StringComparison.Ordinal);
            int install = script.IndexOf(Install, StringComparison.Ordinal);
            Assert.True(install >= 0, "The script must call the compiled delegate's Install().");
            Assert.True(refs < install, $"-ReferencedAssemblies (index {refs}) must precede Install() (index {install}).");
        }
    }
}
