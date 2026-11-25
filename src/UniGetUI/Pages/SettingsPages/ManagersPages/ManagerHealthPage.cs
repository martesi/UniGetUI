using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.Pages.DialogPages;

namespace UniGetUI.Pages.SettingsPages;

public sealed partial class ManagerHealthPage : Page, ISettingsPage
{
    private readonly TextBox filter = new();
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel rows = new() { Spacing = 8 };
    private readonly Button check = new() { Content = CoreTools.Translate("Check again") };
    private readonly InfoBar feedback = new() { IsClosable = true };
    private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
    private bool active;
    private bool busy;
    private int refreshPending;

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Manager Health");

    public event EventHandler<Type>? NavigationRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? RestartRequired
    {
        add { }
        remove { }
    }

    public ManagerHealthPage()
    {
        filter.PlaceholderText = CoreTools.Translate("Filter managers");
        AutomationProperties.SetName(filter, filter.PlaceholderText);
        AutomationProperties.SetAutomationId(filter, "ManagerHealthFilter");
        AutomationProperties.SetAutomationId(check, "ManagerHealthCheckAgain");
        filter.TextChanged += (_, _) => Refresh();
        check.Click += async (_, _) => await RunAsync(CheckAgainAsync);
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Padding = new Thickness(4, 0, 12, 12),
                Spacing = 12,
                Children = { summary, check, filter, feedback, rows },
            },
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        active = true;
        ManagerHealthStore.HealthChanged += HealthChanged;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        active = false;
        ManagerHealthStore.HealthChanged -= HealthChanged;
        base.OnNavigatedFrom(e);
    }

    private void HealthChanged(object? sender, string manager)
    {
        if (Interlocked.Exchange(ref refreshPending, 1) == 1)
            return;

        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref refreshPending, 0);
            if (active && !busy)
                Refresh();
        }))
            Interlocked.Exchange(ref refreshPending, 0);
    }

    private void Refresh()
    {
        var reports = PEInterface.Managers
            .Select(ManagerHealthEvaluator.Evaluate)
            .OrderByDescending(report => report.NeedsAttention)
            .ThenBy(report => report.State is ManagerHealthState.Disabled)
            .ThenBy(report => report.Manager.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        int attention = reports.Count(report => report.NeedsAttention);
        summary.Text = attention == 0
            ? CoreTools.Translate("No package manager needs attention.")
            : CoreTools.Translate("{0} package manager(s) need attention.", attention);
        string query = filter.Text.Trim();
        rows.Children.Clear();
        foreach (var report in reports)
        {
            string state = ManagerHealthEvaluator.StateName(report.State);
            string description = ManagerHealthEvaluator.Describe(report);
            if (query.Length != 0 && !report.Manager.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !state.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            StackPanel details = new() { Spacing = 8 };
            details.Children.Add(Line(description));
            foreach (var kind in Enum.GetValues<ManagerListingKind>())
            {
                var listing = report.Record[kind];
                string lastSuccess = listing.LastSuccessUtc is { } success
                    ? CoreTools.Translate("Last success: {0}", success.ToLocalTime().ToString("g"))
                    : CoreTools.Translate("Last success: never");
                string status = ManagerHealthEvaluator.ListingName(kind) + ": " + lastSuccess;
                if (listing.LastFailureUtc is { } failure)
                    status += "\n" + CoreTools.Translate("Last failure: {0}", failure.ToLocalTime().ToString("g"))
                        + (listing.LastFailureReason is { } reason ? $" ({reason})" : "");
                if (listing.RecentDurationsMs.Count > 0)
                    status += "\n" + CoreTools.Translate("Average duration: {0} s",
                        (listing.RecentDurationsMs.Average() / 1000d).ToString("0.0"));
                details.Children.Add(Line(status));
            }

            foreach (var action in report.Manager.GetMaintenanceActions())
            {
                var button = new Button { Content = action.Title, IsEnabled = !busy };
                ToolTipService.SetToolTip(button, action.Description);
                button.Click += async (_, _) => await ConfirmAndRunAsync(report.Manager, action);
                details.Children.Add(button);
            }

            var row = new Expander
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Header = $"{report.Manager.DisplayName} · {state}",
                Content = details,
                IsExpanded = expanded.Contains(report.Manager.Id),
            };
            row.Expanding += (_, _) => expanded.Add(report.Manager.Id);
            row.Collapsed += (_, _) => expanded.Remove(report.Manager.Id);
            rows.Children.Add(row);
        }

        if (rows.Children.Count == 0 && query.Length > 0)
            rows.Children.Add(Line(CoreTools.Translate("No matching managers.")));
    }

    private static TextBlock Line(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
    };

    private static Task CheckAgainAsync() => Task.WhenAll(
        InstalledPackagesLoader.Instance.ReloadPackages(),
        UpgradablePackagesLoader.Instance.ReloadPackages()
    );

    private async Task ConfirmAndRunAsync(IPackageManager manager, ManagerMaintenanceAction action)
    {
        if (busy)
            return;

        await RunAsync(async () =>
        {
            if (action.RequiresConfirmation)
            {
                var dialog = DialogHelper.DialogFactory.Create();
                dialog.Title = CoreTools.Translate("Run \"{0}\" on {1}?", action.Title, manager.DisplayName);
                dialog.Content = action.Description;
                dialog.PrimaryButtonText = CoreTools.Translate("Run");
                dialog.CloseButtonText = CoreTools.Translate("Cancel");
                dialog.DefaultButton = ContentDialogButton.Close;
                if (await DialogHelper.ShowDialogAsync(dialog) is not ContentDialogResult.Primary)
                    return;
            }

            await manager.RunAsync(action);
            feedback.Title = CoreTools.Translate("{0} completed", action.Title);
            feedback.Severity = InfoBarSeverity.Success;
            feedback.IsOpen = true;
            await CheckAgainAsync();
        });
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (busy)
            return;

        busy = true;
        check.IsEnabled = false;
        feedback.IsOpen = false;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            feedback.Title = ex.Message;
            feedback.Severity = InfoBarSeverity.Error;
            feedback.IsOpen = true;
        }
        finally
        {
            busy = false;
            check.IsEnabled = true;
            if (active)
                Refresh();
        }
    }
}
