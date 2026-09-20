using System.Globalization;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Widgets;
using UniGetUI.Pages.DialogPages;
using UniGetUI.Pages.SettingsPages.GeneralPages;

namespace UniGetUI.Pages.SettingsPages;

internal static class FeatureSettings
{
    public static void Attach(Page page, ScrollViewer scroller, Action<Type> navigate)
    {
        if (scroller.Content is not Panel content) return;
        StackPanel added = new() { Spacing = 8, Margin = new Thickness(0, 16, 0, 16) };
        content.Children.Insert(0, added);
        if (page is SettingsHomepage or Updates or Backup or Operations)
        {
            added.Children.Add(ActionCard("Scheduled maintenance", "Configure", () => navigate(typeof(Scheduler))));
            if (page is not Backup)
                added.Children.Add(ActionCard("Manage automatic updates", "Manage", async () => await DialogHelper.ManageAutoUpdates()));
        }
        if (page is SettingsHomepage)
            SettingsSearch.Attach(added, navigate);
        if (page is Operations)
        {
            added.Children.Add(new CheckboxCard
            {
                SettingName = Settings.K.ExpandEnvVarsWithPercentSyntax,
                Text = "Expand environment variables written as %VAR% (instead of <VAR>)",
                WarningText = "Package substitutions %PACKAGE% and %NAME% remain available in either mode.",
            });
            ComboboxCard naming = new() { SettingName = Settings.K.InstallerFileNameScheme, Text = "Installer filenames" };
            naming.AddItem("Publisher filename", InstallerFileNaming.PublisherNameValue);
            naming.AddItem("Package name and version", InstallerFileNaming.NameAndVersionValue);
            naming.AddItem("Package ID and version", InstallerFileNaming.IdAndVersionValue);
            naming.AddItem("Publisher filename and version", InstallerFileNaming.PublisherNameAndVersionValue);
            naming.ShowAddedItems();
            added.Children.Add(naming);
            AddDownloadDirectory(added);
            added.Children.Add(new CheckboxCard
            {
                SettingName = Settings.K.AskAboutNewStartMenuShortcuts,
                Text = "Ask to organize Start Menu shortcuts created during an install or upgrade.",
            });
            added.Children.Add(ActionCard("Manage Start Menu shortcuts", "Manage", async () => await DialogHelper.ManageStartMenuShortcuts()));
        }
        if (page is Backup)
        {
            NumberBox limit = new() { Minimum = 0, Maximum = int.MaxValue, Value = LocalBackupManager.GetRetentionLimit(), MinWidth = 150, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            limit.ValueChanged += (_, _) =>
            {
                if (double.IsFinite(limit.Value))
                    Settings.SetValue(Settings.K.MaxLocalBackupCount, ((int)limit.Value).ToString(CultureInfo.InvariantCulture));
            };
            added.Children.Add(new SettingsCard
            {
                Header = CoreTools.Translate("Maximum local backups to keep"),
                Description = CoreTools.Translate("0 means unlimited. Only timestamped backups created by UniGetUI are removed, after a new backup succeeds."),
                Content = limit,
            });
        }
        if (page is Interface_P)
        {
            ComboboxCard navigation = new() { SettingName = Settings.K.NavMenuMode, Text = "Navigation menu:" };
            navigation.AddItem("Automatic", "auto");
            navigation.AddItem("Docked open", "docked");
            navigation.AddItem("Sliding overlay", "overlay");
            navigation.ShowAddedItems();
            navigation.ValueChanged += (_, _) => ApplyNavigationMode(MainApp.Instance.MainWindow.NavigationPage.NavView);
            added.Children.Add(navigation);
            added.Children.Add(new CheckboxCard { SettingName = Settings.K.ShowInstallerHostColumn, Text = "Show the installer host column" });
            added.Children.Add(new CheckboxCard { SettingName = Settings.K.ShowDownloadSizeColumn, Text = "Show the installer download size column" });
            added.Children.Add(new CheckboxCard { SettingName = Settings.K.DisablePackageIllustrations, Text = "Show illustrations on empty package lists" });
        }
        if (page is Administrator)
        {
            added.Children.Add(new CheckboxCard
            {
                SettingName = Settings.K.UseAgentBroker,
                Text = "Delegate package operations to the Devolutions Agent broker",
                WarningText = "When enabled, supported package operations use the Devolutions Agent service. Operations will fail unless the agent is installed and running.",
            });
        }
        if (added.Children.Count == 0) content.Children.Remove(added);
    }

    public static void ApplyNavigationMode(NavigationView navigation)
    {
        string mode = Settings.GetValue(Settings.K.NavMenuMode);
        navigation.PaneDisplayMode = mode switch
        {
            "docked" => NavigationViewPaneDisplayMode.Left,
            "overlay" => NavigationViewPaneDisplayMode.LeftMinimal,
            _ => NavigationViewPaneDisplayMode.Auto,
        };
        navigation.IsPaneToggleButtonVisible = true;
        if (mode is "docked") navigation.IsPaneOpen = true;
        else if (mode is "overlay") navigation.IsPaneOpen = false;
    }

    private static SettingsCard ActionCard(string text, string action, Action clicked)
    {
        Button button = new() { Content = CoreTools.Translate(action) };
        button.Click += (_, _) => clicked();
        return new SettingsCard { Header = CoreTools.Translate(text), Content = button };
    }

    private static void AddDownloadDirectory(Panel content)
    {
        TextBlock current = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        void Refresh() => current.Text = InstallerDownloadLocation.GetCustomDirectory() ?? CoreTools.Translate("System default");
        Refresh();
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button choose = new() { Content = CoreTools.Translate("Browse") };
        Button reset = new() { Content = CoreTools.Translate("Reset") };
        actions.Children.Add(choose);
        actions.Children.Add(reset);
        choose.Click += (_, _) =>
        {
            try
            {
                var picker = new ExternalLibraries.Pickers.FolderPicker(MainApp.Instance.MainWindow.GetWindowHandle());
                string path = picker.Show();
                if (path.Length > 0) InstallerDownloadLocation.SetCustomDirectory(path);
                Refresh();
            }
            catch (Exception ex) { Logger.Error(ex); }
        };
        reset.Click += (_, _) => { InstallerDownloadLocation.SetCustomDirectory(null); Refresh(); };
        content.Children.Add(new SettingsCard
        {
            Header = CoreTools.Translate("Default installer download directory"),
            Description = current,
            Content = actions,
        });
    }
}
