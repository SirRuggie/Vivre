using System.Windows;
using Vivre.Core.Updates;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>Small modal: pick a future date + time for a scheduled install. Returns the chosen
/// <see cref="Value"/> when <c>ShowDialog()</c> is true.</summary>
public partial class ScheduleWindow : FluentWindow
{
    /// <param name="action">What's being scheduled ("install" or "reboot") — tunes the title + blurb.</param>
    /// <param name="targetNames">Captured targets for count and local-host reboot disclosure.</param>
    public ScheduleWindow(string action = "install", IEnumerable<string?>? targetNames = null)
    {
        InitializeComponent();

        string?[] targets = targetNames?.ToArray() ?? [];
        bool reboot = string.Equals(action, "reboot", StringComparison.OrdinalIgnoreCase);
        Title = reboot ? "Schedule reboot" : "Schedule install";
        Bar.Title = Title;
        Intro.Text = reboot
            ? "Pick when the reboot should run on the selected machine(s). A one-time task runs as "
              + "SYSTEM at that time and force-restarts the box — any unsaved work on it is lost."
            : "Pick when the install should run on the selected machine(s). A one-time task runs as "
              + "SYSTEM at that time; reboot is reported, not forced.";

        ScheduleButton.Content = reboot ? $"Schedule forced reboot ({targets.Length})" : $"Schedule install ({targets.Length})";
        ScheduleButton.IsDefault = !reboot;
        Intro.Text = $"Targets: {targets.Length} selected machine(s).\n\n" + Intro.Text;

        // DISCLOSURE ONLY, and only on the reboot branch (a scheduled INSTALL never restarts this box —
        // "reboot is reported, not forced"). Every scheduled machine is still scheduled exactly as before.
        if (reboot && LocalHostRebootWarning.WarningOrNull(targets) is { } localWarning)
        {
            Intro.Text += "\n\n" + localWarning;
        }

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
        if (ScheduleTimeError(at, DateTime.Now, TimeZoneInfo.Local) is { } error)
        {
            Hint.Text = error;
            return;
        }
        Value = at;
        DialogResult = true;
        Close();
    }

    internal static string? ScheduleTimeError(DateTime at, DateTime now, TimeZoneInfo zone) =>
        at <= now ? "Pick a time in the future."
        : zone.IsInvalidTime(at) ? "That local time does not exist because the clock moves forward. Choose another time."
        : null;

    internal static string DescribeTimeZone(DateTime at, TimeZoneInfo zone)
    {
        var offset = zone.GetUtcOffset(at);
        string sign = offset < TimeSpan.Zero ? "−" : "+";
        return $"Vivre host time: {zone.Id} (UTC{sign}{offset.Duration().Hours:00}:{offset.Duration().Minutes:00}). All targets run at the same instant."
            + (zone.IsAmbiguousTime(at) ? " This time occurs twice; the displayed offset is used." : string.Empty);
    }

    private void OnTimeChanged(object? sender, RoutedEventArgs e)
    {
        if (TimeZoneSummary is null) return;
        DateTime at = (DatePick.SelectedDate ?? DateTime.Today).Date;
        if (HourBox.SelectedItem is string h) at = at.AddHours(int.Parse(h));
        if (MinuteBox.SelectedItem is string m) at = at.AddMinutes(int.Parse(m));
        var zone = TimeZoneInfo.Local;
        TimeZoneSummary.Text = DescribeTimeZone(at, zone);
        Hint.Text = zone.IsInvalidTime(at) ? "That local time does not exist because the clock moves forward." : string.Empty;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
