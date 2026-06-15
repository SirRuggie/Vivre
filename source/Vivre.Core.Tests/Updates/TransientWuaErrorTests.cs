using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// The classifier that decides whether a WUA failure is a transient reach hiccup (retry the whole
/// operation) or a terminal failure (surface immediately). Locks the proven 0x80072EE2 in as
/// transient and keeps auth/config/install failures OUT of the retry set so they never get masked.
/// </summary>
public class TransientWuaErrorTests
{
    [Theory]
    // WININET / WinHTTP transport family.
    [InlineData(unchecked((int)0x80072EE2))] // ERROR_INTERNET_TIMEOUT — the proven SLS timeout
    [InlineData(unchecked((int)0x80072EE4))] // ERROR_INTERNET_INTERNAL_ERROR
    [InlineData(unchecked((int)0x80072EE7))] // ERROR_INTERNET_NAME_NOT_RESOLVED
    [InlineData(unchecked((int)0x80072EFD))] // ERROR_INTERNET_CANNOT_CONNECT
    [InlineData(unchecked((int)0x80072EFE))] // ERROR_INTERNET_CONNECTION_ABORTED
    [InlineData(unchecked((int)0x80072EFF))] // ERROR_INTERNET_CONNECTION_RESET
    [InlineData(unchecked((int)0x80072F78))] // ERROR_HTTP_INVALID_SERVER_RESPONSE
    [InlineData(unchecked((int)0x80072F8F))] // ERROR_INTERNET_SECURE_FAILURE
    // WU_E_PT HTTP protocol-talker timeout / 5xx family.
    [InlineData(unchecked((int)0x8024401C))] // 408 request timeout
    [InlineData(unchecked((int)0x8024401F))] // 500 server error
    [InlineData(unchecked((int)0x80244021))] // 502 bad gateway
    [InlineData(unchecked((int)0x80244022))] // 503 service unavailable
    [InlineData(unchecked((int)0x80244023))] // 504 gateway timeout
    [InlineData(unchecked((int)0x8024402C))] // WinHTTP name not resolved
    [InlineData(unchecked((int)0x80240438))] // search did not complete / source not fully reached (face 2)
    public void IsTransient_int_is_true_for_the_transient_family(int hresult) =>
        Assert.True(TransientWuaError.IsTransient(hresult));

    [Theory]
    [InlineData(unchecked((int)0x80240022))] // WU_E_ALL_UPDATES_FAILED — a real install failure
    [InlineData(unchecked((int)0x8024401B))] // WU_E_PT_HTTP_STATUS_PROXY_AUTH_REQ (407) — config/auth, not transient
    [InlineData(unchecked((int)0x80244017))] // WU_E_PT_HTTP_STATUS_DENIED (401) — auth
    [InlineData(unchecked((int)0x80244019))] // WU_E_PT_HTTP_STATUS_NOT_FOUND (404)
    [InlineData(unchecked((int)0x80072EE5))] // ERROR_INTERNET_INVALID_URL — a config error, not a hiccup
    [InlineData(unchecked((int)0x80072EF1))] // ERROR_INTERNET_OPERATION_CANCELLED — not a network hiccup
    [InlineData(unchecked((int)0x800F0922))] // CBS install failure
    [InlineData(unchecked((int)0x80070005))] // E_ACCESSDENIED
    [InlineData(0)]                           // S_OK
    public void IsTransient_int_is_false_for_terminal_and_unrelated_codes(int hresult) =>
        Assert.False(TransientWuaError.IsTransient(hresult));

    [Theory]
    // The exact forms the lane surfaces (COMException message / wrapped PS error).
    [InlineData("Exception from HRESULT: 0x80072EE2")]
    [InlineData("Update failed on APVWUG: Exception from HRESULT: 0x80072EE2")]
    [InlineData("Scan failed: Exception calling \"Search\" with \"1\" argument(s): \"Exception from HRESULT: 0x80072EE2\"")]
    [InlineData("0x80072ee2")]                 // case-insensitive
    [InlineData("server busy 0x80244022")]     // 503 surfaced mid-message
    [InlineData("Exception from HRESULT: 0x8024402C")]
    [InlineData("Windows Update search didn't complete cleanly (result code 3, HRESULT 0x80240438)")] // face 2
    public void IsTransient_message_is_true_when_a_transient_code_is_present(string message) =>
        Assert.True(TransientWuaError.IsTransient(message));

    [Theory]
    [InlineData("Exception from HRESULT: 0x80240022")]  // install failure — never retry
    [InlineData("Exception from HRESULT: 0x8024401B")]  // proxy auth required — config, not transient
    [InlineData("Access is denied (0x80070005)")]
    [InlineData("SLS completed with [00000000] and http status code[200]")] // success code only
    [InlineData("No applicable updates")]
    // The re-entry guard message must stay non-transient, or a post-install transient would re-run.
    [InlineData("Install was interrupted after it began on HOST — some updates may have installed. Re-scan to confirm; not retried, to avoid dropping the installed count.")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsTransient_message_is_false_for_terminal_success_or_codeless_text(string? message) =>
        Assert.False(TransientWuaError.IsTransient(message));

    [Fact]
    public void The_proven_SLS_timeout_code_is_in_the_transient_set() =>
        Assert.Contains(unchecked((int)0x80072EE2), TransientWuaError.TransientCodes);
}
