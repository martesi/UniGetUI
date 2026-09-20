using System.Globalization;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UniGetUI.Core.Tools;
using UniGetUI.Core.Tools.Scheduling;
using UniGetUI.Pages.DialogPages;
using UniGetUI.Services;

namespace UniGetUI.Pages.SettingsPages.GeneralPages;

public sealed partial class Scheduler : Page, ISettingsPage
{
    private readonly Dictionary<MaintenanceTaskKind, TextBlock> _statuses = [];
    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Scheduled maintenance");
    public event EventHandler? RestartRequired { add { } remove { } }
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    public Scheduler()
    {
        StackPanel content = new() { Spacing = 8, Padding = new Thickness(4, 16, 12, 16), MaxWidth = 900 };
        Content = new ScrollViewer { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        foreach (var kind in MaintenanceTasks.All)
            content.Children.Add(BuildTask(kind));
        Button manage = new() { Content = CoreTools.Translate("Manage automatic updates"), Margin = new Thickness(0, 8, 0, 0) };
        manage.Click += async (_, _) => await DialogHelper.ManageAutoUpdates();
        content.Children.Add(manage);
        Loaded += (_, _) =>
        {
            MaintenanceScheduler.TaskFinished += OnTaskFinished;
            RefreshStatuses();
        };
        Unloaded += (_, _) => MaintenanceScheduler.TaskFinished -= OnTaskFinished;
    }

    private Expander BuildTask(MaintenanceTaskKind kind)
    {
        var stored = MaintenanceScheduleStore.Get(kind);
        string title = kind switch
        {
            MaintenanceTaskKind.CheckForUpdates => "Check for package updates",
            MaintenanceTaskKind.InstallUpdates => "Install available updates",
            MaintenanceTaskKind.LocalBackup => "Local package backup",
            _ => "Cloud package backup",
        };
        StackPanel fields = new() { Spacing = 8 };
        ToggleSwitch enabled = new() { IsOn = stored.Enabled, Header = CoreTools.Translate("Enabled") };
        fields.Children.Add(enabled);
        var frequencies = MaintenanceTasks.GetSupportedFrequencies(kind);
        ComboBox frequency = new() { MinWidth = 240, ItemsSource = frequencies.Select(FrequencyLabel).ToList(), SelectedIndex = frequencies.ToList().IndexOf(stored.Frequency) };
        fields.Children.Add(Card("Frequency", frequency));
        NumberBox interval = new() { Value = stored.IntervalSeconds / 60d, Minimum = 1, Maximum = int.MaxValue / 60, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 140 };
        var intervalCard = Card("Interval (minutes)", interval);
        fields.Children.Add(intervalCard);
        TimePicker time = new() { Time = TimeSpan.FromMinutes(stored.StartMinutes) };
        var timeCard = Card("Start time", time);
        fields.Children.Add(timeCard);
        StackPanel days = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        Dictionary<DayOfWeek, CheckBox> dayChecks = [];
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            CheckBox check = new() { Content = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day), IsChecked = stored.HasDay(day) };
            dayChecks[day] = check;
            days.Children.Add(check);
        }
        ScrollViewer daysScroll = new() { Content = days, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var daysCard = Card("On selected days", daysScroll);
        fields.Children.Add(daysCard);
        NumberBox grace = new() { Value = stored.GraceMinutes, Minimum = -1, Maximum = int.MaxValue, MinWidth = 140, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var graceCard = Card("Catch-up window (minutes; -1 means unlimited)", grace);
        fields.Children.Add(graceCard);
        ComboBox targets = new()
        {
            ItemsSource = new[] { CoreTools.Translate("All available updates"), CoreTools.Translate("Only packages marked for automatic updates") },
            SelectedIndex = (int)stored.InstallTargets,
            MinWidth = 240,
        };
        if (kind is MaintenanceTaskKind.InstallUpdates)
            fields.Children.Add(Card("Packages to update", targets));
        TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        _statuses[kind] = status;
        fields.Children.Add(status);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button save = new() { Content = CoreTools.Translate("Save") };
        Button run = new() { Content = CoreTools.Translate("Run now"), IsEnabled = stored.Enabled };
        actions.Children.Add(save);
        actions.Children.Add(run);
        fields.Children.Add(actions);
        void UpdateVisibility()
        {
            if (frequency.SelectedIndex < 0) return;
            var selected = frequencies[frequency.SelectedIndex];
            intervalCard.Visibility = selected is ScheduleFrequency.Interval ? Visibility.Visible : Visibility.Collapsed;
            bool timed = ScheduleEvaluator.IsTimeBased(selected);
            timeCard.Visibility = graceCard.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
            daysCard.Visibility = selected is ScheduleFrequency.Weekly ? Visibility.Visible : Visibility.Collapsed;
        }
        frequency.SelectionChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();
        save.Click += (_, _) =>
        {
            if (frequency.SelectedIndex < 0 || !double.IsFinite(interval.Value) || !double.IsFinite(grace.Value))
            {
                status.Text = CoreTools.Translate("Enter a valid number");
                return;
            }
            var schedule = MaintenanceScheduleStore.Get(kind);
            schedule.Enabled = enabled.IsOn;
            schedule.Frequency = frequencies[frequency.SelectedIndex];
            schedule.IntervalSeconds = checked((int)Math.Round(interval.Value * 60));
            schedule.StartMinutes = (int)time.Time.TotalMinutes;
            schedule.GraceMinutes = checked((int)Math.Round(grace.Value));
            schedule.InstallTargets = (ScheduleInstallTargets)Math.Max(0, targets.SelectedIndex);
            foreach (var (day, check) in dayChecks)
                schedule.SetDay(day, check.IsChecked is true);
            if (schedule.Frequency is ScheduleFrequency.Weekly && schedule.Days == 0)
            {
                status.Text = CoreTools.Translate("No days selected");
                return;
            }
            MaintenanceScheduleStore.Set(kind, schedule);
            run.IsEnabled = schedule.Enabled;
            RefreshStatuses();
        };
        run.Click += async (_, _) =>
        {
            run.IsEnabled = false;
            try { await MaintenanceScheduler.RunAsync(kind); }
            finally { run.IsEnabled = MaintenanceScheduleStore.IsEnabled(kind); RefreshStatuses(); }
        };
        return new Expander { Header = CoreTools.Translate(title), Content = fields, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private static SettingsCard Card(string title, object content) => new()
    {
        Header = new TextBlock { Text = CoreTools.Translate(title), TextWrapping = TextWrapping.Wrap },
        Content = content,
        HorizontalContentAlignment = HorizontalAlignment.Right,
    };

    private static string FrequencyLabel(ScheduleFrequency value) => CoreTools.Translate(value switch
    {
        ScheduleFrequency.AtAppStart => "When UniGetUI starts",
        ScheduleFrequency.AfterEveryUpdateCheck => "After every update check",
        ScheduleFrequency.Interval => "On a fixed interval",
        ScheduleFrequency.Daily => "Every day",
        _ => "On selected days",
    });

    private void OnTaskFinished(object? sender, MaintenanceTaskKind kind) => RefreshStatuses();

    private void RefreshStatuses()
    {
        foreach (var (kind, label) in _statuses)
        {
            var schedule = MaintenanceScheduleStore.Get(kind);
            var last = MaintenanceScheduleStore.GetLastRun(kind);
            var next = schedule.Enabled ? ScheduleEvaluator.GetNextOccurrence(schedule, last, DateTime.Now) : null;
            label.Text = CoreTools.Translate("Last run: {0}", last?.ToLocalTime().ToString("g") ?? CoreTools.Translate("Never"))
                + "\n" + CoreTools.Translate("Next run: {0}", next?.ToString("g") ?? FrequencyLabel(schedule.Frequency));
            if (MaintenanceScheduleStore.GetLastFailure(kind) is { } failed)
                label.Text += "\n" + CoreTools.Translate("Last failure: {0}", failed.ToLocalTime().ToString("g"));
        }
    }
}
