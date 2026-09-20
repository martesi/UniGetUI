using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Operations.History;
using UniGetUI.Services;

namespace UniGetUI.Interface.Pages.LogPage;

public sealed partial class OperationHistoryPage : Page
{
    private readonly TextBox _search = new() { PlaceholderText = CoreTools.Translate("Search operation history"), MinWidth = 220 };
    private readonly ComboBox _status = new() { ItemsSource = new[] { CoreTools.Translate("All statuses"), CoreTools.Translate("Succeeded"), CoreTools.Translate("Failed"), CoreTools.Translate("Canceled") }, SelectedIndex = 0 };
    private readonly ComboBox _kind = new() { ItemsSource = new[] { CoreTools.Translate("All operations"), CoreTools.Translate("Install"), CoreTools.Translate("Update"), CoreTools.Translate("Uninstall"), CoreTools.Translate("Download") }, SelectedIndex = 0 };
    private readonly ListView _records = new() { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), MinHeight = 120 };
    private readonly CommandBar _actions = new() { IsDynamicOverflowEnabled = true, DefaultLabelPosition = CommandBarDefaultLabelPosition.Right };
    private OperationHistoryRecord? _selected;
    private IReadOnlyList<OperationHistoryRecord> _visible = [];

    public OperationHistoryPage()
    {
        Grid layout = new() { RowSpacing = 8, Padding = new Thickness(16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid filters = new() { ColumnSpacing = 8 };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filters.Children.Add(_search);
        filters.Children.Add(_status);
        filters.Children.Add(_kind);
        Grid.SetColumn(_status, 1);
        Grid.SetColumn(_kind, 2);
        layout.Children.Add(filters);
        layout.Children.Add(_records);
        Grid.SetRow(_records, 1);
        layout.Children.Add(_actions);
        Grid.SetRow(_actions, 2);
        ScrollViewer.SetHorizontalScrollBarVisibility(_output, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_output, ScrollBarVisibility.Auto);
        layout.Children.Add(_output);
        Grid.SetRow(_output, 3);
        Content = layout;
        _search.TextChanged += (_, _) => Refresh();
        _status.SelectionChanged += (_, _) => Refresh();
        _kind.SelectionChanged += (_, _) => Refresh();
        _records.SelectionChanged += (_, _) =>
        {
            _selected = (_records.SelectedItem as ListViewItem)?.Tag as OperationHistoryRecord;
            RenderSelection();
        };
        Loaded += (_, _) => { OperationHistoryStore.Changed += OnChanged; Refresh(); };
        Unloaded += (_, _) => OperationHistoryStore.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        string[] statuses = ["", "succeeded", "failed", "canceled"];
        string[] kinds = ["", "install-package", "update-package", "uninstall-package", "download-package"];
        string query = _search.Text.Trim();
        _visible = OperationHistoryStore.GetAll().Where(record =>
            (_status.SelectedIndex <= 0 || record.Status == statuses[_status.SelectedIndex])
            && (_kind.SelectedIndex <= 0 || record.Kind == kinds[_kind.SelectedIndex])
            && (query.Length == 0 || $"{record.PackageName} {record.PackageId} {record.ManagerName} {record.SourceName} {record.FailureSummary}".Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        string? selection = _selected?.Id;
        _records.Items.Clear();
        foreach (var record in _visible)
        {
            string when = DateTimeOffset.TryParse(record.TimestampUtc, out var parsed) ? parsed.ToLocalTime().ToString("g") : record.TimestampUtc;
            string title = record.Kind == OperationHistoryRecord.KindLegacyLog ? CoreTools.Translate("Imported operation log") : $"{record.PackageName} ({record.PackageId})";
            string detail = $"{when}   {record.ManagerName}   {record.Kind}   {record.Status}\n{record.VersionBefore} -> {record.VersionAfter}";
            if (record.FailureSummary.Length > 0) detail += "\n" + record.FailureSummary;
            StackPanel row = new() { Spacing = 4, Padding = new Thickness(8) };
            row.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap });
            ListViewItem item = new() { Content = row, Tag = record, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            _records.Items.Add(item);
            if (record.Id == selection) _records.SelectedItem = item;
        }
        RenderSelection();
    }

    private void RenderSelection()
    {
        _actions.PrimaryCommands.Clear();
        _actions.SecondaryCommands.Clear();
        _output.Text = _selected is { } record ? FormatLog(record) : CoreTools.Translate("Select an operation to view its log");
        if (_selected is { } selected)
        {
            AddAction("Re-run", Symbol.Play, () => OperationHistoryActionService.ReRunAsync(selected), OperationHistoryActionService.CanReRun(selected));
            AddAction("Revert", Symbol.Undo, () => OperationHistoryActionService.RevertAsync(selected), OperationHistoryActionService.CanRevert(selected));
            AddAction("Copy log", Symbol.Copy, () => { ExternalLibraries.Clipboard.WindowsClipboard.SetText(FormatLog(selected)); return Task.CompletedTask; });
            AddAction("Export log", Symbol.Save, () => ExportAsync(FormatLog(selected)));
            var retries = OperationHistoryActionService.GetRetryModes(selected);
            if (retries.AsAdmin) AddAction("Retry as administrator", Symbol.Permissions, () => OperationHistoryActionService.RetryAsync(selected, "admin"), secondary: true);
            if (retries.Interactive) AddAction("Retry interactively", Symbol.RepeatAll, () => OperationHistoryActionService.RetryAsync(selected, "interactive"), secondary: true);
            if (retries.SkipHash) AddAction("Retry skipping integrity checks", Symbol.Important, () => OperationHistoryActionService.RetryAsync(selected, "skip-hash"), secondary: true);
            AddAction("Remove", Symbol.Delete, async () =>
            {
                if (await ConfirmationDialog.ShowAsync(CoreTools.Translate("Remove this history entry?"))) OperationHistoryStore.Remove(selected.Id);
            }, secondary: true);
        }
        AddAction("Export all", Symbol.Save, () => ExportAsync(string.Join("\n\n", _visible.Select(FormatLog))), secondary: true);
        AddAction("Clear history", Symbol.Delete, async () =>
        {
            if (await ConfirmationDialog.ShowAsync(CoreTools.Translate("Clear all operation history? This cannot be undone."))) OperationHistoryStore.Clear();
        }, secondary: true);
    }

    private void AddAction(string label, Symbol icon, Func<Task> run, bool enabled = true, bool secondary = false)
    {
        AppBarButton button = new() { Label = CoreTools.Translate(label), Icon = new SymbolIcon(icon), IsEnabled = enabled };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await run(); }
            catch (Exception ex) { Logger.Error(ex); _output.Text = ex.Message; }
            finally { button.IsEnabled = enabled; }
        };
        (secondary ? _actions.SecondaryCommands : _actions.PrimaryCommands).Add(button);
    }

    private static string FormatLog(OperationHistoryRecord record) =>
        $"{record.PackageName} ({record.PackageId})\n{record.TimestampUtc} | {record.ManagerName} | {record.Kind} | {record.Status} | {record.ExitCode}\n{record.VersionBefore} -> {record.VersionAfter}\n\n"
        + string.Join('\n', record.Output.Select(line => line.Text));

    private static async Task ExportAsync(string text)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "UniGetUI-Classic-operation-history" };
        picker.FileTypeChoices.Add(CoreTools.Translate("Text file"), new List<string> { ".txt" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainApp.Instance.MainWindow.GetWindowHandle());
        if (await picker.PickSaveFileAsync() is { } file) await Windows.Storage.FileIO.WriteTextAsync(file, text);
    }
}
