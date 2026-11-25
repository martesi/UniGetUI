using Devolutions.AgentSkills;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.AgentSkills;

namespace UniGetUI.Pages.SettingsPages.GeneralPages;

public sealed partial class AgentSkillsPage : Page, ISettingsPage
{
    internal static AgentSkillsUpdateChecker Updates { get; } = new();

    private readonly AgentSkillsService _skills = new();
    private readonly ComboBox _view = new() { MinWidth = 120 };
    private readonly ComboBox _type = new() { MinWidth = 100 };
    private readonly ComboBox _tool = new() { MinWidth = 135 };
    private readonly ComboBox _source = new() { MinWidth = 130 };
    private readonly TextBox _query = new() { MinWidth = 170 };
    private readonly TextBox _catalogQuery = new() { MinWidth = 170 };
    private readonly ListView _items = new() { SelectionMode = ListViewSelectionMode.None };
    private readonly ContentControl _toolbar = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly InfoBar _status = new() { IsClosable = true };
    private readonly Button _cancel = new() { Visibility = Visibility.Collapsed };
    private readonly Button _installAll = new();
    private readonly Button _updateSkills = new();
    private readonly Button _updatePlugins = new();
    private readonly Button _addSkillSource = new();
    private readonly Button _addMarketplace = new();
    private readonly StackPanel _catalog = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ToggleSwitch _publicCatalog = new();
    private readonly Grid _library = new() { RowSpacing = 8 };
    private readonly IAgentPluginService[] _providers =
    [
        new ClaudePluginService(), new CodexPluginService(), new CopilotPluginService(),
        new CopilotAppPluginService(), new CursorPluginService(),
    ];
    private readonly Dictionary<IAgentPluginService, AgentPluginCatalog> _inventories = [];
    private readonly Dictionary<(IAgentPluginService Provider, string Marketplace), AgentPluginCatalog> _browsed = [];
    private IReadOnlyList<SkillItem> _skillItems = [];
    private bool _usingPublicCatalog;
    private bool _settingFilters;
    private bool _statusPersistent;
    private bool _needsRefresh;
    private bool _refreshWhenIdle;
    private CancellationTokenSource? _work;

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

    private AgentLibraryView View => (AgentLibraryView)_view.SelectedIndex;
    private AgentLibraryType ItemType => (AgentLibraryType)_type.SelectedIndex;
    private AgentLibraryTool Tool => (AgentLibraryTool)((ComboBoxItem)_tool.SelectedItem).Tag;
    private string? Source => (_source.SelectedItem as ComboBoxItem)?.Tag as string;
    private string Query => _query.Text.Trim();

    public AgentSkillsPage()
    {
        InitializeComponent();
        ExtensionEnabled.Header = ShortTitle;
        ExtensionEnabled.IsOn = Settings.Get(Settings.K.EnableAgentSkills);
        ExtensionEnabled.Toggled += (_, _) =>
        {
            Settings.Set(Settings.K.EnableAgentSkills, ExtensionEnabled.IsOn);
            _library.Visibility = ExtensionEnabled.IsOn ? Visibility.Visible : Visibility.Collapsed;
            if (ExtensionEnabled.IsOn)
            {
                Refresh();
            }
            else
            {
                _refreshWhenIdle = false;
                Updates.Stop();
                _work?.Cancel();
                _skillItems = [];
                _inventories.Clear();
                _browsed.Clear();
                _items.Items.Clear();
            }
        };

        PrepareCombo(_view, "Library", "AgentSkillsMode", ["Discover", "Installed", "Updates", "Sources"]);
        PrepareCombo(_type, "Type", "AgentSkillsTypeFilter", ["All types", "Skills", "Plugins"]);
        PrepareCombo(_tool, "Tool", "AgentSkillsToolFilter", []);
        PrepareCombo(_source, "Source", "AgentSkillsSourceFilter", []);
        AddTool(new AgentLibraryTool(CoreTools.Translate("All tools")));
        foreach (var provider in _providers)
            AddTool(new AgentLibraryTool(provider.Name, ProviderAgentId(provider), provider));
        _tool.SelectedIndex = 0;
        _view.SelectionChanged += (_, _) => Refresh();
        _type.SelectionChanged += (_, _) => Refresh();
        _tool.SelectionChanged += (_, _) => Refresh();
        _source.SelectionChanged += (_, _) => Render();
        _query.PlaceholderText = CoreTools.Translate("Filter items");
        _query.TextChanged += (_, _) => Render();
        _catalogQuery.PlaceholderText = CoreTools.Translate("Public catalog query");
        _publicCatalog.Header = CoreTools.Translate("Enable public skills.sh discovery");
        _publicCatalog.IsOn = _skills.SkillsShDiscoveryEnabled;
        _publicCatalog.Toggled += (_, _) => Run(async token =>
        {
            _skills.SkillsShDiscoveryEnabled = _publicCatalog.IsOn;
            await LoadAsync(token);
        });

        var controls = new StackPanel { Spacing = 8 };
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        filters.Children.Add(_view);
        filters.Children.Add(_type);
        filters.Children.Add(_tool);
        filters.Children.Add(_source);
        controls.Children.Add(filters);
        var search = new StackPanel { Spacing = 8, Orientation = Orientation.Horizontal };
        search.Children.Add(_query);
        search.Children.Add(Button("Refresh", () => Run(token => LoadAsync(token, refresh: true))));
        ConfigureButton(_addSkillSource, "Add skill source", AddSkillSource);
        ConfigureButton(_addMarketplace, "Add marketplace", AddMarketplace);
        search.Children.Add(_addSkillSource);
        search.Children.Add(_addMarketplace);
        controls.Children.Add(search);
        _catalog.Children.Add(_catalogQuery);
        _catalog.Children.Add(Button("Search skills.sh", SearchCatalog));
        controls.Children.Add(_catalog);
        var bulk = new StackPanel { Spacing = 8, Orientation = Orientation.Horizontal };
        ConfigureButton(_installAll, "Install all", BulkInstall);
        ConfigureButton(_updateSkills, "Update all skills", BulkUpdateSkills);
        ConfigureButton(_updatePlugins, "Update installed plugins", BulkUpdatePlugins);
        AutomationProperties.SetAutomationId(_installAll, "AgentSkillsInstallAll");
        AutomationProperties.SetAutomationId(_updateSkills, "AgentSkillsUpdateAll");
        AutomationProperties.SetAutomationId(_updatePlugins, "AgentPluginsUpdateAll");
        bulk.Children.Add(_installAll);
        bulk.Children.Add(_updateSkills);
        bulk.Children.Add(_updatePlugins);
        controls.Children.Add(bulk);
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
        var activity = new StackPanel { Spacing = 6 };
        activity.Children.Add(_status);
        ConfigureButton(_cancel, "Cancel operation", () => _work?.Cancel());
        AutomationProperties.SetAutomationId(_cancel, "AgentSkillsCancelOperation");
        activity.Children.Add(_cancel);
        Grid.SetRow(activity, 1);
        Grid.SetRow(_items, 2);
        _library.Children.Add(_toolbar);
        _library.Children.Add(activity);
        _library.Children.Add(_items);
        _library.Visibility = ExtensionEnabled.IsOn ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(_library, 1);
        PageRoot.Children.Add(_library);

        Loaded += (_, _) =>
        {
            if (ExtensionEnabled.IsOn)
                Refresh();
        };
        Unloaded += (_, _) =>
        {
            _refreshWhenIdle = false;
            _work?.Cancel();
        };
    }

    private static void PrepareCombo(ComboBox combo, string label, string automationId, string[] items)
    {
        combo.Header = CoreTools.Translate(label);
        AutomationProperties.SetAutomationId(combo, automationId);
        foreach (string value in items)
            combo.Items.Add(CoreTools.Translate(value));
        if (items.Length > 0)
            combo.SelectedIndex = 0;
    }

    private IEnumerable<ComboBoxItem> ToolOptions => _tool.Items.Cast<ComboBoxItem>();

    private void AddTool(AgentLibraryTool tool) =>
        _tool.Items.Add(new ComboBoxItem { Content = tool.Name, Tag = tool });

    private static string? ProviderAgentId(IAgentPluginService provider) => provider switch
    {
        ClaudePluginService => "claude-code",
        CodexPluginService => "codex",
        CopilotPluginService => "github-copilot",
        CursorPluginService => "cursor",
        _ => null,
    };

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

    private static void ConfigureButton(Button button, string title, Action onClick)
    {
        button.Content = CoreTools.Translate(title);
        button.Click += (_, _) => onClick();
    }

    private static Button Button(string title, Action onClick)
    {
        var button = new Button();
        ConfigureButton(button, title, onClick);
        return button;
    }

    private void Refresh()
    {
        if (_settingFilters)
            return;
        if (_work is not null)
        {
            _refreshWhenIdle = true;
            return;
        }
        Run(token => LoadAsync(token));
    }

    private async void Run(Func<CancellationToken, Task> action)
    {
        if (_work is not null || _library.Visibility != Visibility.Visible)
            return;

        using var work = new CancellationTokenSource();
        _work = work;
        _toolbar.IsEnabled = _items.IsEnabled = false;
        _cancel.Visibility = Visibility.Visible;
        _statusPersistent = false;
        ShowStatus(CoreTools.Translate("Loading…"));
        try
        {
            await action(work.Token);
            if (!_statusPersistent)
                _status.IsOpen = false;
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            ShowStatus(CoreTools.Translate("Operation cancelled."));
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _work = null;
            _cancel.Visibility = Visibility.Collapsed;
            _toolbar.IsEnabled = _items.IsEnabled = true;
            Render();
            if (_refreshWhenIdle)
            {
                _refreshWhenIdle = false;
                Refresh();
            }
        }
    }

    private void ShowStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational, bool keep = false)
    {
        _statusPersistent = keep;
        _status.Severity = severity;
        _status.Message = message;
        _status.IsOpen = true;
    }

    private async Task LoadAsync(CancellationToken token, bool refresh = false)
    {
        _needsRefresh = true;
        _usingPublicCatalog = false;
        List<string> problems = [];
        IProgress<string> activity = new Progress<string>(problems.Add);
        try
        {
            _skillItems = View switch
            {
                AgentLibraryView.Discover when ItemType != AgentLibraryType.Plugins => await Task.Run(
                    () => ReconcileDiscover(
                        _skills.Search("", activity, token, refreshSources: refresh, scope: SkillDiscoveryScope.Sources),
                        _skills.ListInstalled(token)),
                    token),
                AgentLibraryView.Installed when ItemType != AgentLibraryType.Plugins => await Task.Run(
                    () => _skills.ListInstalled(token).Select(skill => skill with { IsInstalled = true }).ToArray(), token),
                AgentLibraryView.Updates when ItemType != AgentLibraryType.Plugins => await Task.Run(
                    () => _skills.CheckUpdates(progress: activity, token: token, refresh: refresh)
                        .Select(skill => skill with { IsInstalled = true, HasUpdate = true }).ToArray(), token),
                _ => [],
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _skillItems = [];
            problems.Add(ex.Message);
        }

        if (ItemType != AgentLibraryType.Plugins && View != AgentLibraryView.Sources)
            await PopulateKnownToolsAsync(token);
        if (ItemType != AgentLibraryType.Skills && View != AgentLibraryView.Updates)
        {
            IEnumerable<IAgentPluginService> hosts = Tool.IsAll ? _providers : Tool.Provider is { } provider ? [provider] : [];
            foreach (var host in hosts)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (Tool.IsAll && ProviderAgentId(host) is { } agentId
                        && host is not CursorPluginService
                        && await Task.Run(() => AgentHostLocator.Find(AgentExecutableName(agentId)), token) is null)
                        continue;
                    if (refresh || !_inventories.ContainsKey(host))
                        _inventories[host] = await host.GetInstalledCatalogAsync(token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _inventories.Remove(host);
                    foreach (var key in _browsed.Keys.Where(key => ReferenceEquals(key.Provider, host)).ToArray())
                        _browsed.Remove(key);
                    problems.Add($"{host.Name}: {ex.Message}");
                }
            }
        }

        token.ThrowIfCancellationRequested();
        _needsRefresh = false;
        UpdateSourcesFilter();
        if (problems.Count > 0)
            ShowStatus(string.Join(Environment.NewLine, problems), InfoBarSeverity.Warning, keep: true);
    }

    private static string AgentExecutableName(string agentId) => agentId switch
    {
        "claude-code" => "claude",
        "github-copilot" => "copilot",
        _ => agentId,
    };

    private async Task PopulateKnownToolsAsync(CancellationToken token)
    {
        var installedTargets = _skillItems.SelectMany(skill => skill.Agents ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        installedTargets.UnionWith(AgentSkillsService.TargetAgents);
        var agents = await Task.Run(_skills.GetSupportedAgents, token);
        var listed = ToolOptions.Select(item => ((AgentLibraryTool)item.Tag).AgentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in agents.Where(agent => agent.IsDetected || installedTargets.Contains(agent.Id)))
        {
            if (listed.Add(agent.Id))
                AddTool(new AgentLibraryTool(agent.DisplayName, agent.Id));
        }
    }

    private static SkillItem[] ReconcileDiscover(IEnumerable<SkillItem> catalog, IEnumerable<SkillItem> installed)
    {
        var installedBySkill = installed.ToLookup(skill => (skill.Id, skill.Source?.InstallSource));
        return catalog.Select(skill =>
        {
            var matching = installedBySkill[(skill.Id, skill.Source?.InstallSource)];
            return matching.Any()
                ? skill with { IsInstalled = true, Agents = matching.SelectMany(item => item.Agents ?? []).Distinct().ToArray() }
                : skill;
        }).ToArray();
    }

    private void UpdateSourcesFilter(string? select = null)
    {
        string? previous = select ?? Source;
        _settingFilters = true;
        try
        {
            _source.Items.Clear();
            _source.Items.Add(new ComboBoxItem { Content = CoreTools.Translate("All sources") });
            foreach (var source in _skillItems.Select(skill => skill.Source).OfType<SkillSourceLocator>()
                .DistinctBy(source => source.InstallSource).OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase))
            {
                _source.Items.Add(new ComboBoxItem { Content = source.Name, Tag = source.InstallSource });
            }
            _source.SelectedItem = _source.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == previous)
                ?? _source.Items[0];
        }
        finally
        {
            _settingFilters = false;
        }
    }

    private void SearchCatalog() => Run(async token =>
    {
        string query = _catalogQuery.Text.Trim();
        if (!_skills.SkillsShDiscoveryEnabled || query.Length < 2)
            throw new InvalidOperationException(CoreTools.Translate("Enable skills.sh discovery and enter at least two characters to search."));
        _needsRefresh = true;
        _skillItems = await Task.Run(() => ReconcileDiscover(
            _skills.Search(query, cancellationToken: token, scope: SkillDiscoveryScope.PublicCatalog),
            _skills.ListInstalled(token)), token);
        _usingPublicCatalog = true;
        _needsRefresh = false;
        UpdateSourcesFilter();
    });

    private IEnumerable<SkillItem> VisibleSkills() => AgentLibrary.FilterSkills(
        _skillItems, View, ItemType, Tool, Source, Query);

    private IEnumerable<AgentLibraryPlugin> VisiblePlugins() => AgentLibrary.FilterPlugins(
        _inventories.SelectMany(entry => entry.Value.Plugins.Where(plugin => plugin.IsInstalled)
                .Select(plugin => new AgentLibraryPlugin(entry.Key, plugin)))
            .Concat(_browsed.SelectMany(entry => entry.Value.Plugins
                .Select(plugin => new AgentLibraryPlugin(entry.Key.Provider, plugin))))
            .DistinctBy(entry => (entry.Provider, entry.Plugin.Id)),
        View, ItemType, Tool, Query);

    private void Render()
    {
        if (_settingFilters || _view.SelectedIndex < 0 || _tool.SelectedItem is null
            || _library.Visibility != Visibility.Visible)
            return;

        _items.Items.Clear();
        _source.Visibility = View != AgentLibraryView.Sources && ItemType != AgentLibraryType.Plugins
            ? Visibility.Visible : Visibility.Collapsed;
        _publicCatalog.Visibility = View == AgentLibraryView.Discover && ItemType != AgentLibraryType.Plugins
            ? Visibility.Visible : Visibility.Collapsed;
        _catalog.Visibility = _publicCatalog.Visibility == Visibility.Visible && _publicCatalog.IsOn
            ? Visibility.Visible : Visibility.Collapsed;
        _addSkillSource.Visibility = View is AgentLibraryView.Discover or AgentLibraryView.Sources
            && ItemType != AgentLibraryType.Plugins ? Visibility.Visible : Visibility.Collapsed;
        _addMarketplace.Visibility = View is AgentLibraryView.Discover or AgentLibraryView.Sources
            && ItemType != AgentLibraryType.Skills && Tool.Provider is not null ? Visibility.Visible : Visibility.Collapsed;

        var skills = VisibleSkills().ToArray();
        var plugins = VisiblePlugins().ToArray();
        var install = View == AgentLibraryView.Discover ? AgentLibrary.BulkInstallScope(skills) : [];
        var updateSkills = View == AgentLibraryView.Updates ? skills : [];
        var updatePlugins = View == AgentLibraryView.Installed ? AgentLibrary.PluginUpdateScope(Tool.Provider, plugins) : [];
        SetBulkButton(_installAll, CoreTools.Translate("Install all {0}", install.Length), install.Length);
        SetBulkButton(_updateSkills, CoreTools.Translate("Update all {0} skills", updateSkills.Length), updateSkills.Length);
        SetBulkButton(_updatePlugins, CoreTools.Translate("Update {0} installed plugins", updatePlugins.Length), updatePlugins.Length);

        if (View == AgentLibraryView.Sources)
        {
            RenderSources();
            if (Tool.Provider is { } provider && ItemType != AgentLibraryType.Skills)
                Row(provider.Name, provider.Guidance);
        }
        else
        {
            IEnumerable<(string Name, Action Render)> rows = skills.Select(skill => (skill.Name, (Action)(() => ShowSkill(skill))))
                .Concat(plugins.Select(entry => (entry.Plugin.Name, (Action)(() => ShowPlugin(entry)))));
            foreach (var row in rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase))
                row.Render();
            if (View == AgentLibraryView.Discover && Tool.Provider is { } provider && ItemType != AgentLibraryType.Skills)
            {
                if (_inventories.TryGetValue(provider, out var inventory))
                {
                    foreach (var marketplace in inventory.Marketplaces)
                    {
                        if (AgentLibrary.MatchesQuery(Query, marketplace.Name, marketplace.Source))
                            Row(marketplace.Name, $"{provider.Name} · {marketplace.Source}", Button("Browse", () => BrowseMarketplace(provider, marketplace)));
                    }
                }
                Row(provider.Name, provider.Guidance, Button("Install by identifier", () => InstallByIdentifier(provider)));
            }
        }

        if (View == AgentLibraryView.Updates && ItemType != AgentLibraryType.Skills)
            Row(CoreTools.Translate("Plugin updates"), CoreTools.Translate("Plugins do not report detected updates here. In Installed, select a tool to run its plugin update action when supported."));
        if (_items.Items.Count == 0 && _work is null)
            Row(CoreTools.Translate("No matching items"), CoreTools.Translate("Try another source, type, or tool; or clear the filter."));
    }

    private void SetBulkButton(Button button, string label, int count)
    {
        button.Content = label;
        button.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        button.IsEnabled = !_needsRefresh && _work is null;
    }

    private void ShowSkill(SkillItem skill)
    {
        string description = $"{CoreTools.Translate("Skill")} · {skill.Provenance}\n{skill.Description}";
        SkillAction action = View switch
        {
            AgentLibraryView.Installed => SkillAction.Remove,
            AgentLibraryView.Updates => SkillAction.Update,
            _ => SkillAction.Install,
        };
        if (View == AgentLibraryView.Discover && !skill.CanInstallFromDiscover)
        {
            Row(skill.Name, $"{description}\n{skill.DiscoverStatus}");
            return;
        }

        Row(skill.Name, description, Button(action.ToString(), () => Run(async token =>
        {
            IReadOnlyList<string>? targets = null;
            if (action == SkillAction.Install)
            {
                if (await SelectInstallTargetsAsync([skill], token) is not { Count: > 0 } selected)
                    return;
                targets = selected;
            }
            else if (!await ConfirmAsync(action.ToString(), $"{skill.Name}\n{description}"))
                return;

            token.ThrowIfCancellationRequested();
            _needsRefresh = true;
            await _skills.PerformAsync(skill, action, cancellationToken: token, targetAgents: targets);
            await ReloadAfterMutationAsync(token, action == SkillAction.Update);
        })));
    }

    private async Task<IReadOnlyList<string>?> SelectInstallTargetsAsync(IReadOnlyList<SkillItem> skills, CancellationToken token)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(Label(skills[0].Source?.InstallSource ?? skills[0].SourceName));
        if (skills.Count == 1)
        {
            SkillPreview preview = await _skills.PreviewAsync(skills[0], token);
            if (preview.ContainsScripts || preview.ContainsExecutables)
                content.Children.Add(Label(CoreTools.Translate("This skill contains scripts or executable files.")));
            if (preview.AllowedTools.Count > 0)
                content.Children.Add(Label(CoreTools.Translate("Requests tool access: {0}", string.Join(", ", preview.AllowedTools))));
            content.Children.Add(Label(preview.Markdown, selectable: true));
            if (preview.IsMarkdownTruncated)
                content.Children.Add(Label(CoreTools.Translate("Preview truncated.")));
            foreach (string file in preview.Files)
                content.Children.Add(Label(file));
        }
        else
        {
            content.Children.Add(Label(CoreTools.Translate("Install these {0} skills from this source? Only the visible uninstalled skills are included.", skills.Count)));
            foreach (var skill in skills)
                content.Children.Add(Label($"{skill.Name} · {skill.Id}"));
        }

        var agents = (await Task.Run(_skills.GetSupportedAgents, token))
            .Where(agent => agent.GlobalSkillsDirectory is not null)
            .OrderByDescending(agent => agent.IsDetected)
            .Select(agent => (agent.Id, Choice: new CheckBox
            {
                Content = agent.DisplayName,
                IsChecked = AgentSkillsService.TargetAgents.Contains(agent.Id),
            })).ToArray();
        content.Children.Add(Label(CoreTools.Translate("Choose which agents should receive these skills.")));
        foreach (var agent in agents)
            content.Children.Add(agent.Choice);
        var remember = new CheckBox { Content = CoreTools.Translate("Remember these agents as my default") };
        content.Children.Add(remember);

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
        token.ThrowIfCancellationRequested();
        var selected = agents.Where(agent => agent.Choice.IsChecked is true).Select(agent => agent.Id).ToArray();
        if (remember.IsChecked is true)
            AgentSkillsService.TargetAgents = selected;
        return selected;
    }

    private void BulkInstall() => Run(async token =>
    {
        var skills = AgentLibrary.BulkInstallScope(VisibleSkills());
        if (View != AgentLibraryView.Discover || _needsRefresh || skills.Length == 0)
            return;
        if (await SelectInstallTargetsAsync(skills, token) is not { Count: > 0 } targets)
            return;

        await PerformBatchAsync(skills, skill => skill.Name,
            (skill, ct) => _skills.PerformAsync(skill, SkillAction.Install, cancellationToken: ct, targetAgents: targets),
            token);
    });

    private void BulkUpdateSkills() => Run(async token =>
    {
        var skills = VisibleSkills().ToArray();
        if (View != AgentLibraryView.Updates || _needsRefresh || skills.Length == 0)
            return;
        string details = CoreTools.Translate("Update these {0} skills for their installed agents? Only updates shown by the current filters are included.", skills.Length)
            + Environment.NewLine + string.Join(Environment.NewLine, skills.Select(skill => $"{skill.Name} ({skill.SourceName})"));
        if (!await ConfirmAsync("Update all skills", details))
            return;
        await PerformBatchAsync(skills, skill => skill.Name,
            (skill, ct) => _skills.PerformAsync(skill, SkillAction.Update, cancellationToken: ct), token);
    });

    private void BulkUpdatePlugins() => Run(async token =>
    {
        var provider = Tool.Provider;
        var plugins = AgentLibrary.PluginUpdateScope(provider, VisiblePlugins());
        if (View != AgentLibraryView.Installed || _needsRefresh || provider is null || plugins.Length == 0)
            return;
        string details = CoreTools.Translate("Run Update for these {0} installed plugins in {1}? Only plugins shown by the current filters are included. This does not mean newer versions were detected.", plugins.Length, provider.Name)
            + Environment.NewLine + string.Join(Environment.NewLine, plugins.Select(plugin => plugin.Id));
        if (!await ConfirmAsync("Update installed plugins", details))
            return;
        _inventories.Remove(provider);
        await PerformBatchAsync(plugins, plugin => plugin.Id,
            async (plugin, ct) =>
            {
                var output = await provider.PerformAsync(plugin.Id, AgentPluginAction.Update, ct);
                Logger.Info($"{plugin.Id}: {output}");
            }, token, provider.RestartNotice);
    });

    private async Task PerformBatchAsync<T>(IReadOnlyList<T> items, Func<T, string> identity,
        Func<T, CancellationToken, Task> perform, CancellationToken token, string? notice = null)
    {
        token.ThrowIfCancellationRequested();
        _needsRefresh = true;
        var result = await AgentLibrary.RunBatchAsync(items, identity, perform,
            (name, position) => ShowStatus(CoreTools.Translate("{0} ({1}/{2})", name, position, items.Count)), token);
        string message = CoreTools.Translate("{0} completed; {1} failed.", result.Completed, result.Failures.Count);
        bool refreshFailed = false;
        if (result.Cancelled)
            message += Environment.NewLine + CoreTools.Translate("Cancelled. Refresh to see the latest state before retrying.");
        else
        {
            try
            {
                await ReloadAfterMutationAsync(token, refresh: true);
                if (_statusPersistent && _status.Severity == InfoBarSeverity.Warning)
                    message += Environment.NewLine + _status.Message;
            }
            catch (Exception ex)
            {
                refreshFailed = true;
                message += Environment.NewLine + CoreTools.Translate("Refresh could not finish: {0}", ex.Message);
            }
        }

        if (result.Failures.Count > 0)
            message += Environment.NewLine + string.Join(Environment.NewLine, result.Failures);
        if (!string.IsNullOrWhiteSpace(notice) && result.Completed > 0)
            message += Environment.NewLine + notice;
        ShowStatus(message, result.Failures.Count > 0 || result.Cancelled || refreshFailed
            ? InfoBarSeverity.Warning : InfoBarSeverity.Success, keep: true);
    }

    private Task ReloadAfterMutationAsync(CancellationToken token, bool refresh = false)
    {
        if (_usingPublicCatalog)
        {
            var installed = _skills.ListInstalled(token);
            _skillItems = ReconcileDiscover(_skillItems, installed);
            _needsRefresh = false;
            return Task.CompletedTask;
        }
        return LoadAsync(token, refresh);
    }

    private void AddSkillSource() => Run(async token =>
    {
        string? source = await PromptAsync("Add skill source", "Repository, website index, or Notion database");
        if (source is null)
            return;
        var added = await _skills.AddSourceAsync(source, token: token);
        SelectView(AgentLibraryView.Discover);
        await LoadAsync(token);
        UpdateSourcesFilter(added.Source.InstallSource);
    });

    private void AddMarketplace() => Run(async token =>
    {
        if (Tool.Provider is not { } host)
            return;
        string? marketplace = await PromptAsync("Add marketplace", host.Guidance);
        if (marketplace is null)
            return;
        token.ThrowIfCancellationRequested();
        string output = await host.AddMarketplaceAsync(marketplace, token);
        _inventories.Remove(host);
        await LoadAsync(token);
        ShowStatus(output, keep: true);
    });

    private void RenderSources()
    {
        if (ItemType != AgentLibraryType.Plugins)
        {
            foreach (var source in AgentSkillsService.GetConfiguredSources().Where(source =>
                AgentLibrary.MatchesQuery(Query, source.Name, source.InstallSource)))
            {
                // Configured skill sources are shared across all skill-capable tools.
                if (!Tool.IsAll && Tool.AgentId is null)
                    continue;
                Row(source.Name, $"{CoreTools.Translate("Skill source")} · {source.InstallSource}",
                    Button("Browse", () => Run(async token =>
                    {
                        SelectView(AgentLibraryView.Discover);
                        await LoadAsync(token);
                        UpdateSourcesFilter(source.InstallSource);
                    })),
                    Button("Remove", () => Run(async token =>
                    {
                        if (!await ConfirmAsync("Remove source", source.InstallSource))
                            return;
                        token.ThrowIfCancellationRequested();
                        _skills.RemoveSource(source);
                        await LoadAsync(token);
                    })));
            }
        }
        if (ItemType == AgentLibraryType.Skills)
            return;
        foreach (var (provider, inventory) in _inventories)
        {
            if (!Tool.IsAll && !ReferenceEquals(Tool.Provider, provider))
                continue;
            foreach (var marketplace in inventory.Marketplaces.Where(marketplace =>
                AgentLibrary.MatchesQuery(Query, marketplace.Name, marketplace.Source, provider.Name)))
            {
                var actions = new List<Button> { Button("Browse", () => BrowseMarketplace(provider, marketplace)) };
                if (marketplace.CanRefresh)
                    actions.Add(Button("Refresh", () => Run(async token =>
                    {
                        string output = await provider.UpdateMarketplaceAsync(marketplace.Name, token);
                        _inventories.Remove(provider);
                        _browsed.Remove((provider, marketplace.Name));
                        await LoadAsync(token);
                        ShowStatus(output, keep: true);
                    })));
                if (marketplace.CanManage)
                    actions.Add(Button("Remove", () => Run(async token =>
                    {
                        if (!await ConfirmAsync("Remove marketplace", $"{provider.RemovalWarning}\n{marketplace.Source}"))
                            return;
                        token.ThrowIfCancellationRequested();
                        string output = await provider.RemoveMarketplaceAsync(marketplace.Name, token);
                        _inventories.Remove(provider);
                        _browsed.Remove((provider, marketplace.Name));
                        await LoadAsync(token);
                        ShowStatus(output, keep: true);
                    })));
                Row(marketplace.Name, $"{provider.Name} · {marketplace.Source}", [.. actions]);
            }
        }
    }

    private void BrowseMarketplace(IAgentPluginService provider, AgentPluginMarketplace marketplace) => Run(async token =>
    {
        var catalog = await provider.GetMarketplaceCatalogAsync(marketplace.Name, token);
        token.ThrowIfCancellationRequested();
        _browsed[(provider, marketplace.Name)] = catalog;
        SelectView(AgentLibraryView.Discover, provider);
        await LoadAsync(token);
    });

    private void SelectView(AgentLibraryView view, IAgentPluginService? provider = null)
    {
        _settingFilters = true;
        try
        {
            _view.SelectedIndex = (int)view;
            if (provider is not null)
                _tool.SelectedItem = ToolOptions.First(item => ReferenceEquals(((AgentLibraryTool)item.Tag).Provider, provider));
        }
        finally
        {
            _settingFilters = false;
        }
    }

    private void ShowPlugin(AgentLibraryPlugin entry)
    {
        var (host, plugin) = entry;
        List<Button> actions = [];
        if (plugin.ShowInstall)
            actions.Add(PluginButton(host, plugin, AgentPluginAction.Install));
        if (plugin.IsInstalled)
        {
            if (host.SupportsUpdate && plugin.CanManage)
                actions.Add(PluginButton(host, plugin, AgentPluginAction.Update));
            if (host.SupportsToggle && plugin.CanToggle)
                actions.Add(PluginButton(host, plugin, plugin.Enabled ? AgentPluginAction.Disable : AgentPluginAction.Enable));
            if (plugin.CanManage)
                actions.Add(PluginButton(host, plugin, AgentPluginAction.Uninstall));
        }
        Row(plugin.Name, $"{CoreTools.Translate("Plugin")} · {host.Name} · {plugin.Id} · {plugin.Status} · {plugin.Version}\n{plugin.Description}\n{plugin.Notice}",
            [.. actions]);
    }

    private void InstallByIdentifier(IAgentPluginService host) => Run(async token =>
    {
        string? input = await PromptAsync("Install by identifier", host.Guidance);
        if (input is not null)
            await ApplyPluginActionAsync(host, input, AgentPluginAction.Install, token);
    });

    private Button PluginButton(IAgentPluginService host, AgentPluginItem plugin, AgentPluginAction action) =>
        Button(action.ToString(), () => Run(async token =>
        {
            if (await ConfirmAsync(action.ToString(), $"{host.Name}\n{plugin.Id}\n{plugin.Description}\n{plugin.Notice}"))
                await ApplyPluginActionAsync(host, plugin.Id, action, token);
        }));

    private async Task ApplyPluginActionAsync(IAgentPluginService host, string id, AgentPluginAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _needsRefresh = true;
        _inventories.Remove(host);
        string output = await host.PerformAsync(id, action, token);
        await LoadAsync(token);
        ShowStatus($"{output}\n{host.RestartNotice}", InfoBarSeverity.Success, keep: true);
    }

    private void Row(string title, string details, params Button[] actions)
    {
        var content = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 8) };
        content.Children.Add(Label(title));
        content.Children.Add(Label(details, selectable: true));
        if (actions.Length > 0)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var action in actions)
            {
                action.IsEnabled = !_needsRefresh;
                buttons.Children.Add(action);
            }
            content.Children.Add(buttons);
        }

        _items.Items.Add(new ListViewItem { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch });
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
        await Dialog(title, new ScrollViewer { Content = Label(details, selectable: true), MaxHeight = 450 }).ShowAsync() == ContentDialogResult.Primary;

    private async Task<string?> PromptAsync(string title, string description)
    {
        var input = new TextBox { Header = CoreTools.Translate(description) };
        var dialog = Dialog(title, input);
        dialog.IsPrimaryButtonEnabled = false;
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }
}
