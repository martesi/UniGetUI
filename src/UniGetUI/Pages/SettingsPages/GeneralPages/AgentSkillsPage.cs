using Devolutions.AgentSkills;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.AgentSkills;

namespace UniGetUI.Pages.SettingsPages.GeneralPages;

public sealed class AgentSkillsPage : Page, ISettingsPage
{
    internal static AgentSkillsUpdateChecker Updates { get; } = new();

    private readonly AgentSkillsService _skills = new();
    private readonly ComboBox _mode = new() { MinWidth = 180 };
    private readonly ComboBox _host = new() { MinWidth = 150 };
    private readonly TextBox _query = new() { MinWidth = 200 };
    private readonly ListView _items = new() { SelectionMode = ListViewSelectionMode.None };
    private readonly ContentControl _toolbar = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly InfoBar _status = new() { IsClosable = true };
    private readonly ToggleSwitch _publicCatalog = new();
    private readonly Grid _library = new() { RowSpacing = 8 };
    private CancellationTokenSource? _work;

    private enum Mode
    {
        Discover,
        Installed,
        Updates,
        Sources,
        Plugins,
    }

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Agent Skills & Plugins");

    public event EventHandler? RestartRequired
    {
        add { }
        remove { }
    }

    public event EventHandler<Type>? NavigationRequested
    {
        add { }
        remove { }
    }

    private Mode CurrentMode => (Mode)_mode.SelectedIndex;
    private IAgentPluginService CurrentHost => (IAgentPluginService)_host.SelectedItem;

    public AgentSkillsPage()
    {
        var enabled = new ToggleSwitch
        {
            Header = ShortTitle,
            IsOn = Settings.Get(Settings.K.EnableAgentSkills),
        };
        AutomationProperties.SetAutomationId(enabled, "AgentSkillsEnabled");
        AutomationProperties.SetAutomationId(_mode, "AgentSkillsMode");
        enabled.Toggled += (_, _) =>
        {
            Settings.Set(Settings.K.EnableAgentSkills, enabled.IsOn);
            _library.Visibility = enabled.IsOn ? Visibility.Visible : Visibility.Collapsed;
            if (enabled.IsOn)
            {
                Refresh();
            }
            else
            {
                Updates.Stop();
                _work?.Cancel();
                _items.Items.Clear();
            }
        };

        _mode.Items.Add(CoreTools.Translate("Discover"));
        _mode.Items.Add(CoreTools.Translate("Installed"));
        _mode.Items.Add(CoreTools.Translate("Updates"));
        _mode.Items.Add(CoreTools.Translate("Sources"));
        _mode.Items.Add(CoreTools.Translate("Plugins"));
        _mode.SelectedIndex = 0;
        _mode.SelectionChanged += (_, _) => Refresh();
        _host.ItemsSource = new IAgentPluginService[]
        {
            new ClaudePluginService(),
            new CodexPluginService(),
            new CopilotPluginService(),
            new CopilotAppPluginService(),
            new CursorPluginService(),
        };
        _host.DisplayMemberPath = nameof(IAgentPluginService.Name);
        _host.SelectedIndex = 0;
        _host.SelectionChanged += (_, _) => Refresh();
        AutomationProperties.SetName(_mode, CoreTools.Translate("Library"));
        AutomationProperties.SetName(_host, CoreTools.Translate("Agent"));
        _query.PlaceholderText = CoreTools.Translate("Search");
        _query.TextChanged += (_, _) =>
        {
            string search = _query.Text.Trim();
            foreach (ListViewItem row in _items.Items)
            {
                row.Visibility = ((string)row.Tag).Contains(search, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        };
        _publicCatalog.Header = CoreTools.Translate("Enable public skills.sh discovery");
        _publicCatalog.IsOn = _skills.SkillsShDiscoveryEnabled;
        _publicCatalog.Toggled += (_, _) => Run(async token =>
        {
            _skills.SkillsShDiscoveryEnabled = _publicCatalog.IsOn;
            await LoadAsync(token);
        });
        var controls = new StackPanel { Spacing = 8 };
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        modes.Children.Add(_mode);
        modes.Children.Add(_host);
        modes.Children.Add(Button("Refresh", () => Run(token => LoadAsync(token, refresh: true))));
        modes.Children.Add(Button("Add source", AddSource));
        controls.Children.Add(modes);
        var search = new StackPanel { Spacing = 8, Orientation = Orientation.Horizontal };
        search.Children.Add(_query);
        search.Children.Add(Button("Search skills.sh", SearchCatalog));
        controls.Children.Add(search);
        controls.Children.Add(_publicCatalog);
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var notifications = new CheckBox
        {
            Content = CoreTools.Translate("Notify about skill updates"),
            IsChecked = !Settings.GetDictionaryItem<string, bool>(Settings.K.DisabledPackageManagerNotifications, "Skills"),
        };
        notifications.Checked += (_, _) => SetSkillNotifications(enabled: true);
        notifications.Unchecked += (_, _) => SetSkillNotifications(enabled: false);
        options.Children.Add(notifications);
        options.Children.Add(Button("Notion integration", () => Run(ShowNotionAsync)));
        options.Children.Add(Button("Pin Notion CLI version", () => Run(PinNotionVersionAsync)));
        controls.Children.Add(options);
        _toolbar.Content = controls;

        _library.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _library.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _library.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_status, 1);
        Grid.SetRow(_items, 2);
        _library.Children.Add(_toolbar);
        _library.Children.Add(_status);
        _library.Children.Add(_items);
        _library.Visibility = enabled.IsOn ? Visibility.Visible : Visibility.Collapsed;

        var page = new Grid { RowSpacing = 10, Padding = new Thickness(4) };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_library, 1);
        page.Children.Add(enabled);
        page.Children.Add(_library);
        Content = page;

        Loaded += (_, _) =>
        {
            if (enabled.IsOn)
                Refresh();
        };
        Unloaded += (_, _) => _work?.Cancel();
    }

    private static void SetSkillNotifications(bool enabled) =>
        Settings.SetDictionaryItem(Settings.K.DisabledPackageManagerNotifications, "Skills", !enabled);

    private async Task ShowNotionAsync(CancellationToken token)
    {
        var status = await _skills.GetNotionStatusAsync();
        token.ThrowIfCancellationRequested();
        var panel = new StackPanel { Spacing = 8 };
        string message = status.State switch
        {
            NotionCliState.NotInstalled => CoreTools.Translate("Install the Notion CLI to use Notion skill sources."),
            NotionCliState.SignedIn => CoreTools.Translate("Signed in to {0}", status.WorkspaceName ?? "Notion"),
            NotionCliState.SignedOut => CoreTools.Translate("Not signed in to Notion"),
            _ => status.Message ?? CoreTools.Translate("Could not check Notion sign-in"),
        };
        panel.Children.Add(Label(message));
        if (status.CliVersion is { } version)
            panel.Children.Add(Label(CoreTools.Translate("Version: {0}", version)));
        if (status.State == NotionCliState.NotInstalled)
        {
            string pin = NotionCliTool.PinnedVersion is { } pinned ? $" --version {pinned}" : "";
            panel.Children.Add(Label($"winget install --id Notion.ntn --exact{pin}", selectable: true));
        }

        var dialog = Dialog("Notion integration", panel);
        if (status.State == NotionCliState.SignedIn)
            dialog.PrimaryButtonText = CoreTools.Translate("Sign out");
        else if (status.State == NotionCliState.SignedOut)
            dialog.PrimaryButtonText = CoreTools.Translate("Sign in");
        else
            dialog.PrimaryButtonText = "";

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        token.ThrowIfCancellationRequested();
        if (status.State == NotionCliState.SignedIn)
        {
            await _skills.SignOutOfNotionAsync();
            return;
        }

        var signIn = await _skills.BeginNotionSignInAsync(token);
        var prompt = new StackPanel { Spacing = 8 };
        prompt.Children.Add(Label(CoreTools.Translate("Check that Notion shows the same code before approving the sign-in.")));
        prompt.Children.Add(Label(signIn.VerificationCode, selectable: true));
        var signInDialog = Dialog("Open Notion in your browser", prompt);
        if (await signInDialog.ShowAsync() == ContentDialogResult.Primary)
        {
            token.ThrowIfCancellationRequested();
            if (!await Windows.System.Launcher.LaunchUriAsync(signIn.Url))
                throw new InvalidOperationException(CoreTools.Translate("Could not open the Notion sign-in page."));
            await _skills.CompleteNotionSignInAsync(token);
        }
        else
        {
            _work?.Cancel();
        }
    }

    private async Task PinNotionVersionAsync(CancellationToken token)
    {
        var input = new TextBox
        {
            Header = CoreTools.Translate("Version to install. Leave empty to allow updates."),
            Text = NotionCliTool.PinnedVersion ?? "",
        };
        var dialog = Dialog("Pin Notion CLI version", input);
        dialog.PrimaryButtonText = CoreTools.Translate("Save");
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled =
            string.IsNullOrWhiteSpace(input.Text) || NotionCliTool.IsValidVersion(input.Text.Trim());
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        token.ThrowIfCancellationRequested();
        if (!NotionCliTool.TrySetPinnedVersion(input.Text))
            throw new InvalidOperationException(CoreTools.Translate("Enter a valid Notion CLI version."));
        _skills.RefreshNotionSources();
    }

    private static Button Button(string title, Action onClick)
    {
        var button = new Button { Content = CoreTools.Translate(title) };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void Refresh() => Run(token => LoadAsync(token));

    private async void Run(Func<CancellationToken, Task> action)
    {
        if (_work is not null || _library.Visibility != Visibility.Visible)
            return;

        using var work = new CancellationTokenSource();
        _work = work;
        _toolbar.IsEnabled = _items.IsEnabled = false;
        ShowStatus(CoreTools.Translate("Loading…"));
        try
        {
            await action(work.Token);
            _status.IsOpen = false;
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            _status.IsOpen = false;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _work = null;
            _toolbar.IsEnabled = _items.IsEnabled = true;
        }
    }

    private void ShowStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _status.Severity = severity;
        _status.Message = message;
        _status.IsOpen = true;
    }

    private async Task LoadAsync(CancellationToken token, bool refresh = false)
    {
        _host.Visibility = CurrentMode is Mode.Plugins ? Visibility.Visible : Visibility.Collapsed;
        _items.Items.Clear();
        switch (CurrentMode)
        {
            case Mode.Discover:
                ShowSkills(await Task.Run(
                    () => _skills.Search("", cancellationToken: token, scope: SkillDiscoveryScope.Sources, refreshSources: refresh), token),
                    SkillAction.Install);
                break;
            case Mode.Installed:
                ShowSkills(await Task.Run(() => _skills.ListInstalled(token), token), SkillAction.Remove);
                break;
            case Mode.Updates:
                ShowSkills(await Task.Run(() => _skills.CheckUpdates(token: token, refresh: refresh), token), SkillAction.Update);
                break;
            case Mode.Sources:
                foreach (var source in AgentSkillsService.GetConfiguredSources())
                {
                    Row(source.Name, source.InstallSource,
                        Button("Browse", () => Run(async ct =>
                        {
                            var skills = await Task.Run(() => _skills.ListSource(source, token: ct), ct);
                            _items.Items.Clear();
                            ShowSkills(skills, SkillAction.Install);
                        })),
                        Button("Remove", () => Run(async ct =>
                        {
                            if (!await ConfirmAsync("Remove source", source.InstallSource))
                                return;
                            ct.ThrowIfCancellationRequested();
                            _skills.RemoveSource(source);
                            await LoadAsync(ct);
                        })));
                }
                break;
            case Mode.Plugins:
                await LoadPluginsAsync(token);
                break;
        }
        token.ThrowIfCancellationRequested();
    }

    private void SearchCatalog() => Run(async token =>
    {
        string query = _query.Text.Trim();
        if (!_skills.SkillsShDiscoveryEnabled || query.Length < 2)
            throw new InvalidOperationException(CoreTools.Translate("Enable skills.sh discovery and enter at least two characters to search."));
        var results = await Task.Run(() => _skills.Search(query, cancellationToken: token, scope: SkillDiscoveryScope.PublicCatalog), token);
        _items.Items.Clear();
        ShowSkills(results, SkillAction.Install);
    });

    private void ShowSkills(IEnumerable<SkillItem> skills, SkillAction action)
    {
        foreach (var skill in skills)
        {
            string description = $"{skill.Provenance}\n{skill.Description}";
            if (action == SkillAction.Install && !skill.CanInstallFromDiscover)
            {
                Row(skill.Name, $"{description}\n{skill.DiscoverStatus}");
                continue;
            }

            Row(skill.Name, description, Button(action.ToString(), () => Run(async token =>
            {
                IReadOnlyList<string>? agents = null;
                if (action == SkillAction.Install)
                {
                    if (await SelectInstallTargetsAsync(skill, token) is not { Count: > 0 } selected)
                        return;
                    agents = selected;
                }
                else if (!await ConfirmAsync(action.ToString(), $"{skill.Name}\n{description}"))
                {
                    return;
                }
                token.ThrowIfCancellationRequested();
                await _skills.PerformAsync(skill, action, cancellationToken: token, targetAgents: agents);
                await LoadAsync(token, refresh: action == SkillAction.Update);
            })));
        }
    }

    private async Task<IReadOnlyList<string>?> SelectInstallTargetsAsync(SkillItem skill, CancellationToken token)
    {
        SkillPreview preview = await _skills.PreviewAsync(skill, token);
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(Label(skill.Source?.InstallSource ?? skill.SourceName));
        if (preview.ContainsScripts || preview.ContainsExecutables)
            content.Children.Add(Label(CoreTools.Translate("This skill contains scripts or executable files.")));
        content.Children.Add(Label(preview.Markdown, selectable: true));
        if (preview.IsMarkdownTruncated)
            content.Children.Add(Label(CoreTools.Translate("Preview truncated.")));
        foreach (string file in preview.Files)
            content.Children.Add(Label(file));

        var agents = (await Task.Run(_skills.GetSupportedAgents, token))
            .Where(agent => agent.GlobalSkillsDirectory is not null)
            .OrderByDescending(agent => agent.IsDetected)
            .Select(agent => (agent.Id, Choice: new CheckBox
            {
                Content = agent.DisplayName,
                IsChecked = AgentSkillsService.TargetAgents.Contains(agent.Id),
            })).ToArray();
        content.Children.Add(Label(CoreTools.Translate("Choose which agents should receive this skill.")));
        foreach (var agent in agents)
            content.Children.Add(agent.Choice);

        var dialog = Dialog("Install", new ScrollViewer { Content = content, MaxHeight = 450 });
        dialog.IsPrimaryButtonEnabled = agents.Any(agent => agent.Choice.IsChecked is true);
        foreach (var agent in agents)
        {
            agent.Choice.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
            agent.Choice.Unchecked += (_, _) =>
                dialog.IsPrimaryButtonEnabled = agents.Any(entry => entry.Choice.IsChecked is true);
        }

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        var selected = agents.Where(agent => agent.Choice.IsChecked is true).Select(agent => agent.Id).ToArray();
        AgentSkillsService.TargetAgents = selected;
        return selected;
    }

    private void AddSource() => Run(async token =>
    {
        if (CurrentMode == Mode.Plugins)
        {
            var host = CurrentHost;
            string? marketplace = await PromptAsync("Add marketplace", host.Guidance);
            if (marketplace is null)
                return;
            token.ThrowIfCancellationRequested();
            await host.AddMarketplaceAsync(marketplace, token);
        }
        else
        {
            string? source = await PromptAsync("Add skill source", "Repository, website index, or Notion database");
            if (source is null)
                return;
            token.ThrowIfCancellationRequested();
            await _skills.AddSourceAsync(source, token: token);
        }

        await LoadAsync(token);
    });

    private async Task LoadPluginsAsync(CancellationToken token)
    {
        var host = CurrentHost;
        AgentPluginCatalog catalog = await host.GetCatalogAsync(token);
        foreach (var marketplace in catalog.Marketplaces)
        {
            List<Button> actions = [];
            if (marketplace.CanRefresh)
                actions.Add(Button("Refresh", () => Run(async ct =>
                {
                    await host.UpdateMarketplaceAsync(marketplace.Name, ct);
                    await LoadAsync(ct);
                })));
            if (marketplace.CanManage)
                actions.Add(Button("Remove", () => Run(async ct =>
                {
                    if (!await ConfirmAsync("Remove marketplace", marketplace.Source))
                        return;
                    ct.ThrowIfCancellationRequested();
                    await host.RemoveMarketplaceAsync(marketplace.Name, ct);
                    await LoadAsync(ct);
                })));
            Row(marketplace.Name, marketplace.Source, [.. actions]);
        }

        foreach (var plugin in catalog.Plugins)
        {
            List<Button> actions = [];
            if (plugin.ShowInstall)
                actions.Add(PluginButton(host, plugin, AgentPluginAction.Install));
            if (plugin.IsInstalled && plugin.CanManage)
            {
                if (host.SupportsUpdate)
                    actions.Add(PluginButton(host, plugin, AgentPluginAction.Update));
                if (host.SupportsToggle && plugin.CanToggle)
                    actions.Add(PluginButton(host, plugin, plugin.Enabled ? AgentPluginAction.Disable : AgentPluginAction.Enable));
                actions.Add(PluginButton(host, plugin, AgentPluginAction.Uninstall));
            }
            Row(plugin.Id, $"{plugin.Status} · {plugin.Version}\n{plugin.Description}\n{plugin.Notice}", [.. actions]);
        }

        Row(host.Name, host.Guidance, Button("Install by identifier", () => Run(async ct =>
        {
            string? input = await PromptAsync("Install by identifier", host.Guidance);
            if (input is null)
                return;
            ct.ThrowIfCancellationRequested();
            await host.PerformAsync(input, AgentPluginAction.Install, ct);
            await LoadAsync(ct);
        })));
    }

    private Button PluginButton(IAgentPluginService host, AgentPluginItem plugin, AgentPluginAction action) =>
        Button(action.ToString(), () => Run(async token =>
        {
            if (!await ConfirmAsync(action.ToString(), $"{host.Name}\n{plugin.Id}\n{plugin.Description}\n{plugin.Notice}"))
                return;
            token.ThrowIfCancellationRequested();
            await host.PerformAsync(plugin.Id, action, token);
            await LoadAsync(token);
        }));

    private void Row(string title, string details, params Button[] actions)
    {
        var content = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 8) };
        content.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(Label(details, selectable: true));
        if (actions.Length > 0)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var action in actions)
                buttons.Children.Add(action);
            content.Children.Add(buttons);
        }

        var item = new ListViewItem
        {
            Content = content,
            Tag = $"{title} {details}",
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        if (CurrentMode != Mode.Discover && !((string)item.Tag).Contains(_query.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            item.Visibility = Visibility.Collapsed;
        _items.Items.Add(item);
    }

    private static TextBlock Label(string text, bool selectable = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = selectable,
    };

    private ContentDialog Dialog(string title, object content) => new()
    {
        XamlRoot = XamlRoot,
        Title = CoreTools.Translate(title),
        Content = content,
        PrimaryButtonText = CoreTools.Translate(title),
        CloseButtonText = CoreTools.Translate("Cancel"),
    };

    private async Task<bool> ConfirmAsync(string title, string details) =>
        await Dialog(title, Label(details, selectable: true)).ShowAsync() == ContentDialogResult.Primary;

    private async Task<string?> PromptAsync(string title, string description)
    {
        var input = new TextBox { Header = CoreTools.Translate(description) };
        var dialog = Dialog(title, input);
        dialog.IsPrimaryButtonEnabled = false;
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }
}
