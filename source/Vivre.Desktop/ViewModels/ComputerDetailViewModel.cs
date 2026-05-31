using System.ComponentModel;
using System.Windows.Data;
using Vivre.Core.Logging;
using Vivre.Core.Models;

namespace Vivre.Desktop.ViewModels;

/// <summary>
/// Backs the per-machine detail window: the live <see cref="Computer"/> (so the window updates as
/// scans/installs progress) plus a <em>private</em> view of the activity log filtered to this one
/// machine. Uses its own <see cref="CollectionViewSource"/> — not the shared default view the
/// activity panel uses — so filtering here never disturbs the global log panel.
/// </summary>
public sealed class ComputerDetailViewModel
{
    public ComputerDetailViewModel(Computer computer, IActivityLog log)
    {
        Computer = computer;

        var source = new CollectionViewSource { Source = log.Entries };
        source.Filter += (_, e) =>
            e.Accepted = e.Item is LogEntry entry
                && string.Equals(entry.Machine, computer.Name, StringComparison.OrdinalIgnoreCase);
        Messages = source.View;
    }

    /// <summary>The live machine model bound throughout the window.</summary>
    public Computer Computer { get; }

    /// <summary>This machine's activity-log entries only (newest-first), kept live.</summary>
    public ICollectionView Messages { get; }
}
