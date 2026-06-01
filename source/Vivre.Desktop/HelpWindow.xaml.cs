using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>The searchable, collapsible "How to use Vivre" guide (Help ▸ How to use Vivre, or F1).</summary>
public partial class HelpWindow : FluentWindow
{
    public HelpWindow()
    {
        InitializeComponent();
        DataContext = new HelpViewModel();
    }
}
