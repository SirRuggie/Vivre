using CommunityToolkit.Mvvm.ComponentModel;
using Vivre.Core.Rdp;

namespace Vivre.Desktop.ViewModels;

public enum RdpConnectionState
{
    Connecting,
    Connected,
    Disconnected,
    Failed,
}

/// <summary>
/// One live (or attempted) RDP session — a tab on the right of the Cross-Domain RDP view. Holds the connection
/// state + status text; the <c>RdpSessionView</c> owns the actual ActiveX control and drives this VM from
/// the control's events. <see cref="FullScreen"/> is toggled by the toolbar and watched by the view.
/// </summary>
public partial class RdpSessionViewModel : ObservableObject
{
    public RdpSessionViewModel(string title, RdpConnectionSettings settings)
    {
        Title = title;
        Settings = settings;
    }

    /// <summary>Session tab header (the host's display name).</summary>
    public string Title { get; }

    /// <summary>The resolved connection bundle the view hands to the RDP control on connect.</summary>
    public RdpConnectionSettings Settings { get; }

    [ObservableProperty]
    public partial RdpConnectionState State { get; set; } = RdpConnectionState.Connecting;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Connecting…";

    /// <summary>Whether this is the session currently shown (the others stay connected but hidden).</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Toggled by the toolbar; the session view watches it and flips the control's full-screen.</summary>
    [ObservableProperty]
    public partial bool FullScreen { get; set; }

    public bool IsConnected => State == RdpConnectionState.Connected;

    /// <summary>Show the status/overlay bar whenever we're not cleanly connected.</summary>
    public bool ShowStatusBar => State != RdpConnectionState.Connected;

    public bool IsConnecting => State == RdpConnectionState.Connecting;

    public bool CanReconnect => State is RdpConnectionState.Disconnected or RdpConnectionState.Failed;

    /// <summary>Raised when the user asks to reconnect a disconnected/timed-out session (the Reconnect button
    /// or the tab's right-click menu); the view re-runs its connect sequence.</summary>
    public event Action? ReconnectRequested;

    public void RequestReconnect() => ReconnectRequested?.Invoke();

    /// <summary>Raised when the session has ended for good (e.g. the remote user logged off) so the tab should
    /// close instead of lingering with a Reconnect prompt. The owning view-model removes the session.</summary>
    public event Action? CloseRequested;

    public void RequestClose() => CloseRequested?.Invoke();

    partial void OnStateChanged(RdpConnectionState value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ShowStatusBar));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(CanReconnect));
    }
}
