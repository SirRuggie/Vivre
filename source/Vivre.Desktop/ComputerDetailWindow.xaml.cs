using Vivre.Core.Logging;
using Vivre.Core.Models;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>Per-machine detail window: full update picture for one box (applicable + installed
/// updates, live progress/state, and that machine's activity-log messages). Modeless — binds the
/// live <see cref="Computer"/> so it tracks scans/installs as they happen.</summary>
public partial class ComputerDetailWindow : FluentWindow
{
    public ComputerDetailWindow(Computer computer, IActivityLog log)
    {
        InitializeComponent();
        Title = $"Details — {computer.Name}";
        DataContext = new ComputerDetailViewModel(computer, log);
    }
}
