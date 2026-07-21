namespace Vivre.Core.Updates;

/// <summary>
/// The base display label for each <see cref="PatchState"/> — the single source of truth shared by
/// the Patching grid's Status chip (via the Desktop converter) and the shown-rows CSV export, so
/// the file always reads like the grid. Friendly overrides ("Up to date", "Updates available",
/// "Scheduled", phase-specific labels) layer on top — see <c>GridReportCsv.StatusLabel</c>.
/// </summary>
public static class PatchStateLabels
{
    public static string For(PatchState s) => s switch
    {
        PatchState.Idle => "Idle",
        PatchState.Scanning => "Scanning",
        PatchState.Available => "Available",
        PatchState.Downloading => "Downloading",
        PatchState.Installing => "Installing",
        PatchState.Uninstalling => "Uninstalling",
        PatchState.RebootPending => "Reboot pending",
        PatchState.Done => "Done",
        PatchState.Unverified => "Unverified",
        PatchState.Error => "Error",
        _ => string.Empty,
    };
}
