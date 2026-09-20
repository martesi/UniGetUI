using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Models;
using UniGetUI.PackageEngine.Classes.Packages.Classes;

namespace UniGetUI.Pages.DialogPages;

public static partial class DialogHelper
{
    private static bool _shortcutDialogOpen;

    public static Task ManageStartMenuShortcuts() => ManageShortcuts(ShortcutDialogScope.StartMenu);

    public static async Task ManageShortcuts(ShortcutDialogScope scope = ShortcutDialogScope.All, IReadOnlyList<string>? desktop = null)
    {
        if (_shortcutDialogOpen) return;
        _shortcutDialogOpen = true;
        try
        {
            var model = new ManageShortcutsViewModel(desktop, scope);
            var dialog = DialogFactory.Create_AsWindow(true);
            dialog.Title = CoreTools.Translate("Manage shortcuts");
            dialog.PrimaryButtonText = CoreTools.Translate("Save");
            dialog.CloseButtonText = CoreTools.Translate("Cancel");
            var editor = new ShortcutEditor(model);
            dialog.Content = editor;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (model.StartMenuRules.Any(rule => rule.FolderIsInvalid || rule.NeedsFolder))
                {
                    args.Cancel = true;
                    editor.ShowFolderError();
                    return;
                }
                try { model.SaveChanges(); }
                catch (Exception ex)
                {
                    args.Cancel = true;
                    Logger.Error(ex);
                    editor.ShowFailure(ex.Message);
                }
            };
            await ShowDialogAsync(dialog);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            ShowDismissableBalloon(CoreTools.Translate("Failed"), CoreTools.Translate("Could not load the shortcuts"));
        }
        finally { _shortcutDialogOpen = false; }
    }

    public static async Task HandleNewShortcuts()
    {
        bool desktop = Settings.Get(Settings.K.AskToDeleteNewDesktopShortcuts) && DesktopShortcutsDatabase.GetUnknownShortcuts().Any();
        bool start = Settings.Get(Settings.K.AskAboutNewStartMenuShortcuts) && StartMenuShortcutsDatabase.GetPendingShortcuts().Any();
        if (!desktop && !start) return;
        await ManageShortcuts(desktop && start ? ShortcutDialogScope.All : desktop ? ShortcutDialogScope.Desktop : ShortcutDialogScope.StartMenu,
            desktop ? DesktopShortcutsDatabase.GetUnknownShortcuts() : null);
    }
}

internal sealed class ShortcutEditor : Grid
{
    private readonly ManageShortcutsViewModel _model;
    private readonly Pivot _tabs = new();
    private readonly InfoBar _error = new() { IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly StackPanel _desktop = new() { Spacing = 8 };
    private readonly StackPanel _folders = new() { Spacing = 8 };
    private readonly StackPanel _tracked = new() { Spacing = 8 };

    public ShortcutEditor(ManageShortcutsViewModel model)
    {
        _model = model;
        MinWidth = 520;
        MaxWidth = 850;
        Height = 530;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(_error);
        Children.Add(_tabs);
        SetRow(_tabs, 1);
        if (model.ShowDesktopTab) AddTab("Desktop shortcuts", _desktop);
        if (model.ShowStartMenuTabs)
        {
            AddTab("Start Menu folders", _folders);
            AddTab("Other Start Menu shortcuts", _tracked);
        }
        RefreshDesktop();
        RefreshFolders();
        RefreshTracked();
    }

    private void AddTab(string title, Panel content) => _tabs.Items.Add(new PivotItem
    {
        Header = CoreTools.Translate(title),
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
    });

    public void ShowFolderError()
    {
        _tabs.SelectedIndex = _model.ShowDesktopTab ? 1 : 0;
        ShowFailure(CoreTools.Translate("Choose a valid folder for the shortcuts selected to move."));
    }

    public void ShowFailure(string message)
    {
        _error.Message = message;
        _error.IsOpen = true;
    }

    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    private static Button Action(string title, Action clicked)
    {
        Button button = new() { Content = CoreTools.Translate(title) };
        button.Click += (_, _) => clicked();
        return button;
    }

    private static CheckBox Check(string title, bool selected, Action<bool> changed)
    {
        CheckBox check = new() { Content = CoreTools.Translate(title), IsChecked = selected };
        check.Checked += (_, _) => changed(true);
        check.Unchecked += (_, _) => changed(false);
        return check;
    }

    private void RefreshDesktop()
    {
        _desktop.Children.Clear();
        _desktop.Children.Add(Check("Automatically remove all desktop shortcuts", _model.AutoDelete, value => _model.AutoDelete = value));
        _desktop.Children.Add(Text(CoreTools.Translate("Mark shortcuts to delete now and whenever an upgrade creates them again.")));
        foreach (var entry in _model.Entries.ToArray())
        {
            StackPanel row = new() { Spacing = 4 };
            row.Children.Add(Text(entry.Name + "\n" + entry.Path));
            StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(Check("Delete", entry.IsDeletable, value => entry.IsDeletable = value));
            actions.Children.Add(Action("Open location", entry.Open));
            actions.Children.Add(Action("Stop tracking", () => { entry.Remove(); RefreshDesktop(); }));
            row.Children.Add(actions);
            _desktop.Children.Add(row);
        }
        if (_model.Entries.Count == 0) _desktop.Children.Add(Text(CoreTools.Translate("No shortcuts to review")));
    }

    private void RefreshTracked()
    {
        _tracked.Children.Clear();
        _tracked.Children.Add(Check(_model.ShowAllStartMenuShortcutsLabel, _model.ShowAllStartMenuShortcuts, value =>
        {
            _model.ShowAllStartMenuShortcuts = value;
            RefreshTracked();
        }));
        foreach (var entry in _model.StartMenuEntries.ToArray())
        {
            StackPanel row = new() { Spacing = 4 };
            row.Children.Add(Text(entry.Name + "\n" + entry.Location));
            StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(Check("Delete", entry.IsDeletable, value => entry.IsDeletable = value));
            actions.Children.Add(Action("Open location", entry.Open));
            if (entry.CanStopTracking)
                actions.Children.Add(Action("Stop tracking", () => { entry.Remove(); RefreshTracked(); }));
            row.Children.Add(actions);
            _tracked.Children.Add(row);
        }
        if (!_model.HasStartMenuEntries) _tracked.Children.Add(Text(_model.StartMenuEmptyStateText));
    }

    private void RefreshFolders()
    {
        _folders.Children.Clear();
        _folders.Children.Add(Check("Ask to organize Start Menu shortcuts created during an install or upgrade.", _model.AskAboutStartMenuShortcuts, value => _model.AskAboutStartMenuShortcuts = value));
        foreach (var rule in _model.StartMenuRules.ToArray())
        {
            StackPanel fields = new() { Spacing = 8 };
            ComboBox folder = new() { Header = CoreTools.Translate("Folder"), ItemsSource = rule.FolderOptions, SelectedItem = rule.SelectedFolderOption, HorizontalAlignment = HorizontalAlignment.Stretch };
            TextBox newFolder = new() { Header = CoreTools.Translate("New folder"), Text = rule.NewFolderName, Visibility = rule.IsCreatingFolder ? Visibility.Visible : Visibility.Collapsed };
            newFolder.TextChanged += (_, _) => rule.NewFolderName = newFolder.Text;
            folder.SelectionChanged += (_, _) =>
            {
                if (folder.SelectedItem is string value) rule.SelectedFolderOption = value;
                newFolder.Visibility = rule.IsCreatingFolder ? Visibility.Visible : Visibility.Collapsed;
            };
            fields.Children.Add(folder);
            fields.Children.Add(newFolder);
            foreach (var candidate in rule.Candidates)
            {
                fields.Children.Add(Text(candidate.Name + "\n" + candidate.Location));
                StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
                CheckBox move = new() { Content = CoreTools.Translate("Move"), IsChecked = candidate.IsMoveSelected };
                CheckBox delete = new() { Content = CoreTools.Translate("Delete"), IsChecked = candidate.IsDeleteSelected };
                AutomationProperties.SetName(move, candidate.MoveAutomationName);
                AutomationProperties.SetName(delete, candidate.DeleteAutomationName);
                move.Checked += (_, _) => { candidate.IsMoveSelected = true; delete.IsChecked = false; };
                move.Unchecked += (_, _) => candidate.IsMoveSelected = false;
                delete.Checked += (_, _) => { candidate.IsDeleteSelected = true; move.IsChecked = false; };
                delete.Unchecked += (_, _) => candidate.IsDeleteSelected = false;
                actions.Children.Add(move);
                actions.Children.Add(delete);
                actions.Children.Add(Action("Open location", candidate.Open));
                fields.Children.Add(actions);
                TextBox rename = new() { Header = CoreTools.Translate("Rename (optional)"), Text = candidate.NewName, PlaceholderText = candidate.Name };
                rename.TextChanged += (_, _) => candidate.NewName = rename.Text;
                fields.Children.Add(rename);
            }
            fields.Children.Add(Action("Forget folder rule", () => { rule.Remove(); RefreshFolders(); RefreshTracked(); }));
            _folders.Children.Add(new Expander { Header = rule.DisplayName, Content = fields, IsExpanded = rule.PendingShortcuts.Count > 0, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        if (!_model.HasStartMenuRules) _folders.Children.Add(Text(CoreTools.Translate("No Start Menu shortcut has been handled by UniGetUI yet. Shortcuts show up here once an install creates them, or once you give a package a folder.")));
    }
}
