namespace Vivre.Core.Updates;

/// <summary>
/// Which update service the Windows Update Agent (WUA) searcher targets. The user
/// flips this with the Source toggle; it controls the COM searcher's
/// <c>ServerSelection</c> / <c>ServiceID</c> so "what's applicable" reflects the
/// chosen catalogue (the existing scripts left this at the box's default).
/// </summary>
public enum UpdateSource
{
    /// <summary>Windows Update only (OS + drivers). WUA <c>ServerSelection = 2</c>.</summary>
    WindowsUpdate,

    /// <summary>Microsoft Update (adds SQL / Office / .NET / other MS products). Registers the
    /// Microsoft Update service and uses <c>ServerSelection = 3</c> + that <c>ServiceID</c>.</summary>
    MicrosoftUpdate,

    /// <summary>The box's managed server (WSUS / SCCM Software Update Point). WUA <c>ServerSelection = 1</c>.</summary>
    Managed,
}

/// <summary>WUA <c>ServerSelection</c> / <c>ServiceID</c> for a <see cref="UpdateSource"/>.</summary>
/// <param name="ServerSelection">The COM <c>ServerSelectionEnum</c> value (1 managed, 2 Windows Update, 3 other-by-ServiceID).</param>
/// <param name="ServiceId">The service GUID to register/select, or null when <see cref="ServerSelection"/> is self-sufficient.</param>
public readonly record struct WuaServerSelection(int ServerSelection, string? ServiceId)
{
    /// <summary>The Microsoft Update service GUID (constant), registered to scan the broader MS catalogue.</summary>
    public const string MicrosoftUpdateServiceId = "7971f918-a847-4430-9279-4a52d1efe18d";

    /// <summary>Maps a <see cref="UpdateSource"/> to the WUA searcher settings.</summary>
    public static WuaServerSelection For(UpdateSource source) => source switch
    {
        UpdateSource.WindowsUpdate => new WuaServerSelection(2, null),
        UpdateSource.MicrosoftUpdate => new WuaServerSelection(3, MicrosoftUpdateServiceId),
        UpdateSource.Managed => new WuaServerSelection(1, null),
        _ => new WuaServerSelection(2, null),
    };
}
