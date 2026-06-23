using System.Globalization;
using System.Text.RegularExpressions;

namespace Vivre.Core.Updates;

/// <summary>
/// Classifies a Windows Update Agent (WUA) failure HRESULT as a <b>transient reach failure</b>
/// ("the network path to Windows Update hiccuped — try the whole thing again") versus a
/// <b>terminal</b> failure (a real install / config / auth error that re-running won't fix).
///
/// <para><b>Proven root cause</b> (see docs/windows-patching-lane.md ▸ "Transient WUA reach failures"): on a
/// failed box the on-box WUA's very first network call — the <c>SLS</c> (Service Locator Service)
/// lookup during "Processing auto/pending service registrations", <em>before</em> search, download
/// or install — timed out with <c>0x80072EE2</c> (no HTTP response at all) during a brief network
/// blip, and the identical call to the identical URL succeeded cleanly in 0.5 s an hour later.
/// Windows' own internal 3 retries were exhausted inside the bad ~2.5 min window. The failure is
/// keyed on the <b>HRESULT, not the phase</b>: a transient code surfacing in ANY phase
/// (service-registration, search, or download) means "re-dispatch the whole operation."</para>
///
/// <para>Pure and host-free so the retry policy is unit-testable without real boxes. The transient
/// set is the documented WININET/WinHTTP transport family and the WU_E_PT (HTTP protocol-talker)
/// timeout / 5xx-server family — all of which mean "the path hiccuped, try again." Auth/config
/// codes (407 proxy-auth, 401 denied), HTTP-4xx, and real install failures are deliberately
/// EXCLUDED so they surface immediately without a pointless retry.</para>
/// </summary>
public static class TransientWuaError
{
    // Each entry is a documented "the path hiccuped, try again" condition. Stored as int (the CLR
    // HRESULT representation) so both the int and the string overloads compare against one set.
    // HRESULT literals are uint (high bit set), so cast unchecked to the signed int the CLR uses.
    private static readonly HashSet<int> Transient =
    [
        // --- WININET / WinHTTP transport (Win32 facility 0x8007, code 12xxx) ---
        unchecked((int)0x80072EE2), // ERROR_INTERNET_TIMEOUT (12002) — the PROVEN SLS timeout
        unchecked((int)0x80072EE4), // ERROR_INTERNET_INTERNAL_ERROR (12004)
        unchecked((int)0x80072EE7), // ERROR_INTERNET_NAME_NOT_RESOLVED (12007) — DNS hiccup
        unchecked((int)0x80072EFD), // ERROR_INTERNET_CANNOT_CONNECT (12029)
        unchecked((int)0x80072EFE), // ERROR_INTERNET_CONNECTION_ABORTED (12030)
        unchecked((int)0x80072EFF), // ERROR_INTERNET_CONNECTION_RESET (12031)
        unchecked((int)0x80072F78), // ERROR_HTTP_INVALID_SERVER_RESPONSE (12152)
        unchecked((int)0x80072F8F), // ERROR_INTERNET_SECURE_FAILURE (12175) — transient TLS / time skew

        // --- WU_E_PT HTTP protocol-talker: server said "busy / try later" or timed out ---
        unchecked((int)0x8024401C), // WU_E_PT_HTTP_STATUS_REQUEST_TIMEOUT  (408)
        unchecked((int)0x8024401F), // WU_E_PT_HTTP_STATUS_SERVER_ERROR     (500)
        unchecked((int)0x80244021), // WU_E_PT_HTTP_STATUS_BAD_GATEWAY      (502)
        unchecked((int)0x80244022), // WU_E_PT_HTTP_STATUS_SERVICE_UNAVAIL  (503)
        unchecked((int)0x80244023), // WU_E_PT_HTTP_STATUS_GATEWAY_TIMEOUT  (504)
        unchecked((int)0x8024402C), // WU_E_PT_WINHTTP_NAME_NOT_RESOLVED — DNS via WinHTTP

        // --- the "second face": search returned WITHOUT throwing but did not cleanly succeed ---
        unchecked((int)0x80240438), // WU search did not complete / source not fully reached — the
                                    // SucceededWithErrors masquerade that BatchPatch fake-greens as
                                    // "no updates". A live capture proved it; treat it as transient.
    ];

    // Matches an "0x"-prefixed 8-hex HRESULT token — the exact form .NET produces in
    // "Exception from HRESULT: 0x80072EE2" (the surfaced COMException message) and the form WUA
    // codes are rendered in. We deliberately do NOT match bare hex (e.g. a GUID fragment) to avoid
    // false positives; the surfaced messages always carry the 0x prefix.
    private static readonly Regex HresultToken = new(@"0x[0-9A-Fa-f]{8}", RegexOptions.Compiled);

    /// <summary>The transient code set, exposed read-only for tests / diagnostics.</summary>
    public static IReadOnlyCollection<int> TransientCodes => Transient;

    /// <summary>True when <paramref name="hresult"/> is a known transient reach-failure code.</summary>
    public static bool IsTransient(int hresult) => Transient.Contains(hresult);

    /// <summary>
    /// True when <paramref name="message"/> contains a transient HRESULT token (e.g. the surfaced
    /// "Exception from HRESULT: 0x80072EE2"). Scans every <c>0x</c>-prefixed code in the text; any
    /// transient hit ⇒ transient (a real install failure carries its own terminal code, never a
    /// WININET/WU_E_PT transport code, so this never mis-flags one). Null/blank ⇒ false — no
    /// evidence of a transient reach failure, so the error surfaces immediately.
    /// </summary>
    public static bool IsTransient(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        foreach (Match m in HresultToken.Matches(message))
        {
            // m.Value is "0x" + 8 hex digits; parse the 8 digits as the unsigned HRESULT.
            if (uint.TryParse(m.Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint code)
                && Transient.Contains(unchecked((int)code)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The first transient HRESULT token (e.g. <c>"0x80072EE2"</c>) in <paramref name="message"/>, or
    /// <c>null</c> when none — used to name the code in the honest "couldn't reach Windows Update
    /// (0x…) after N tries" message after the retries are exhausted.
    /// </summary>
    public static string? FirstTransientToken(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        foreach (Match m in HresultToken.Matches(message))
        {
            if (uint.TryParse(m.Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint code)
                && Transient.Contains(unchecked((int)code)))
            {
                return m.Value;
            }
        }

        return null;
    }
}
