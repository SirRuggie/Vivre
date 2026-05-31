using System.Windows;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>Small modal: pick a future date + time for a scheduled install. Returns the chosen
/// <see cref="Value"/> when <c>ShowDialog()</c> is true.</summary>
public partial class ScheduleWindow : FluentWindow
{
    /// <param name="action">What's being scheduled ("install" or "reboot") — tunes the title + blurb.</param>
    public ScheduleWindow(string action = "install")
    {
        InitializeComponent();

        bool reboot = string.Equals(action, "reboot", StringComparison.OrdinalIgnoreCase);
        Title = reboot ? "Schedule reboot" : "Schedule install";
        Bar.Title = Title;
        Intro.Text = reboot
            ? "Pick when the reboot should run on the selected machine(s). A one-time task runs as "
              + "SYSTEM at that time and force-restarts the box — any unsaved work on it is lost."
            : "Pick when the install should run on the selected machine(s). A one-time task runs as "
              + "SYSTEM at that time; reboot is reported, not forced.";

        for (int h = 0; h < 24; h++)
        {
            HourBox.Items.Add(h.ToString("D2"));
        }

        foreach (int m in new[] { 0, 15, 30, 45 })
        {
            MinuteBox.Items.Add(m.ToString("D2"));
        }

        // Default to roughly an hour out, on the hour.
        DateTime def = DateTime.Now.AddHours(1);
        DatePick.SelectedDate = def.Date;
        HourBox.SelectedItem = def.Hour.ToString("D2");
        MinuteBox.SelectedIndex = 0;
    }

    /// <summary>The chosen schedule time (set only when the dialog is confirmed with a future time).</summary>
    public DateTime? Value { get; private set; }

    private void OnSchedule(object sender, RoutedEventArgs e)
    {
        if (DatePick.SelectedDate is not { } date || HourBox.SelectedItem is not string h || MinuteBox.SelectedItem is not string m)
        {
            Hint.Text = "Pick a date and time.";
            return;
        }

        DateTime at = date.Date.AddHours(int.Parse(h)).AddMinutes(int.Parse(m));
        if (at <= DateTime.Now)
        {
            Hint.Text = "Pick a time in the future.";
            return;
        }

        Value = at;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
