using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Core.Tools.Scheduling;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Pages.DialogPages;

public static partial class DialogHelper
{
    private sealed record AutoUpdateChoice(string Id, string Label);

    public static async Task ManageAutoUpdates()
    {
        try
        {
            var loader = InstalledPackagesLoader.Instance;
            if (loader.IsLoading) await loader.WaitForCurrentLoadAsync();
            else if (!loader.IsLoaded) await loader.ReloadPackages();
            var original = AutoUpdatesDatabase.GetDatabase().Keys.ToHashSet(StringComparer.Ordinal);
            var marked = original.ToHashSet(StringComparer.Ordinal);
            Dictionary<string, AutoUpdateChoice> choices = [];
            foreach (var package in loader.Packages)
            {
                string id = AutoUpdatesDatabase.GetIdForPackage(package);
                string label = $"{package.Name}  ({package.Id})\n{package.Manager.DisplayName}  {package.VersionString}";
                if (IgnoredUpdatesDatabase.HasUpdatesIgnored(id))
                    label += "  - " + CoreTools.Translate("Updates ignored");
                choices.TryAdd(id, new(id, label));
            }
            foreach (string id in original)
                choices.TryAdd(id, new(id, id + "  - " + CoreTools.Translate("Not installed")));
            var ordered = choices.Values.OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
            var dialog = DialogFactory.Create_AsWindow(true);
            dialog.Title = CoreTools.Translate("Manage automatic updates");
            dialog.PrimaryButtonText = CoreTools.Translate("Save");
            dialog.CloseButtonText = CoreTools.Translate("Cancel");
            StackPanel content = new() { Spacing = 8, MinWidth = 440 };
            content.Children.Add(new TextBlock
            {
                Text = CoreTools.Translate("Tick the packages that should be updated on their own when the scheduled \"Install available updates\" task runs."),
                TextWrapping = TextWrapping.Wrap,
            });
            var schedule = MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates);
            string warning = !schedule.Enabled
                ? CoreTools.Translate("Turn on \"Install available updates\" in the scheduled maintenance settings for this list to take effect.")
                : schedule.InstallTargets is ScheduleInstallTargets.AllPackages
                    ? CoreTools.Translate("Every upgradable package is currently installed automatically, so this list is not being used.")
                    : "";
            if (warning.Length > 0)
                content.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = warning });
            TextBox search = new() { PlaceholderText = CoreTools.Translate("Search for a package") };
            CheckBox onlyMarked = new() { Content = CoreTools.Translate("Only marked") };
            ListView list = new() { SelectionMode = ListViewSelectionMode.Multiple, DisplayMemberPath = nameof(AutoUpdateChoice.Label), Height = 350 };
            TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
            StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
            Button selectAll = new() { Content = CoreTools.Translate("Select all") };
            Button clear = new() { Content = CoreTools.Translate("Clear selection") };
            actions.Children.Add(selectAll);
            actions.Children.Add(clear);
            content.Children.Add(search);
            content.Children.Add(onlyMarked);
            content.Children.Add(actions);
            content.Children.Add(list);
            content.Children.Add(summary);
            dialog.Content = content;
            bool rebuilding = false;
            void Refresh()
            {
                rebuilding = true;
                var visible = ordered.Where(c =>
                    (onlyMarked.IsChecked is not true || marked.Contains(c.Id))
                    && (string.IsNullOrWhiteSpace(search.Text) || c.Label.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase))).ToList();
                list.ItemsSource = visible;
                foreach (var choice in visible.Where(c => marked.Contains(c.Id))) list.SelectedItems.Add(choice);
                rebuilding = false;
                summary.Text = CoreTools.Translate("{0} package(s) are marked", marked.Count);
            }
            list.SelectionChanged += (_, e) =>
            {
                if (rebuilding) return;
                foreach (AutoUpdateChoice choice in e.AddedItems) marked.Add(choice.Id);
                foreach (AutoUpdateChoice choice in e.RemovedItems) marked.Remove(choice.Id);
                summary.Text = CoreTools.Translate("{0} package(s) are marked", marked.Count);
            };
            search.TextChanged += (_, _) => Refresh();
            onlyMarked.Checked += (_, _) => Refresh();
            onlyMarked.Unchecked += (_, _) => Refresh();
            selectAll.Click += (_, _) => list.SelectAll();
            clear.Click += (_, _) => list.SelectedItems.Clear();
            Refresh();
            if (await ShowDialogAsync(dialog) is ContentDialogResult.Primary)
            {
                AutoUpdatesDatabase.AddRange(marked.Except(original));
                AutoUpdatesDatabase.RemoveRange(original.Except(marked));
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            ShowDismissableBalloon(CoreTools.Translate("Failed"), CoreTools.Translate("Could not load the installed packages for the automatic updates editor"));
        }
    }
}
