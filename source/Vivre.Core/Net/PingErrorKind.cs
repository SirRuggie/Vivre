using System.Net.Sockets;

namespace Vivre.Core.Net;

/// <summary>
/// The operational shape of a ping failure — enough to tell a box that is merely offline because its
/// name no longer resolves apart from a box that failed for any other reason. Kept deliberately coarse:
/// the UI only needs "is this a name-can't-be-resolved offline, or something real to surface".
/// </summary>
public enum PingErrorKind
{
    /// <summary>
    /// Any failure that is NOT a name-resolution failure — a timeout, a refused/unreachable host, a
    /// non-<see cref="SocketException"/> fault, or a null inner exception. The default (value 0) so an
    /// unclassified result can never accidentally suppress a real error.
    /// </summary>
    Other = 0,

    /// <summary>
    /// The host name could not be resolved (the "name can't be resolved" DNS family). On an
    /// FQDN-registered fleet this is the ordinary offline-consequence class: a decommissioned or
    /// renamed box simply stops resolving. Treated as a calm offline state, not a scary error.
    /// </summary>
    NameResolution,
}

/// <summary>
/// Pure classification of a ping failure's inner exception into a <see cref="PingErrorKind"/>. Classifies
/// strictly by exception TYPE and <see cref="SocketError"/> code — NEVER by message text, which is
/// localized and unstable. Any shape it does not recognize defaults to <see cref="PingErrorKind.Other"/>,
/// so a genuine error is never suppressed by an unclassified failure. UI-free, so it lives in Core and is
/// unit-tested directly.
/// </summary>
public static class PingErrorClassification
{
    /// <summary>
    /// Classifies the inner exception of a ping failure. Returns <see cref="PingErrorKind.NameResolution"/>
    /// ONLY when <paramref name="inner"/> is a <see cref="SocketException"/> whose
    /// <see cref="SocketException.SocketErrorCode"/> is one of the "name can't be resolved" family:
    /// <see cref="SocketError.HostNotFound"/> (11001 — confirmed by a live fleet probe 2026-07-16),
    /// <see cref="SocketError.NoData"/> (11004), <see cref="SocketError.TryAgain"/> (11002), or
    /// <see cref="SocketError.NoRecovery"/> (11003). Everything else — a null inner, a
    /// non-<see cref="SocketException"/>, or any other socket code (timeout, refused, unreachable…) —
    /// returns <see cref="PingErrorKind.Other"/> so nothing real is ever suppressed.
    /// </summary>
    /// <param name="inner">The ping failure's inner exception (typically a <c>PingException.InnerException</c>).</param>
    public static PingErrorKind Classify(Exception? inner) =>
        inner is SocketException se && se.SocketErrorCode is
            SocketError.HostNotFound or
            SocketError.NoData or
            SocketError.TryAgain or
            SocketError.NoRecovery
            ? PingErrorKind.NameResolution
            : PingErrorKind.Other;
}
