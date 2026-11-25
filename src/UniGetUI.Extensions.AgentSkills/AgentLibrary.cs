namespace UniGetUI.Extensions.AgentSkills;

internal enum AgentLibraryView { Discover, Installed, Updates, Sources }
internal enum AgentLibraryType { All, Skills, Plugins }

internal sealed record AgentLibraryTool(string Name, string? AgentId = null, IAgentPluginService? Provider = null)
{
    internal bool IsAll => AgentId is null && Provider is null;
}

internal sealed record AgentLibraryPlugin(IAgentPluginService Provider, AgentPluginItem Plugin);

internal static class AgentLibrary
{
    internal static bool MatchesQuery(string query, params string[] fields) =>
        query.Length == 0 || fields.Any(field => field.Contains(query, StringComparison.OrdinalIgnoreCase));

    internal static IEnumerable<SkillItem> FilterSkills(
        IEnumerable<SkillItem> skills, AgentLibraryView view, AgentLibraryType type,
        AgentLibraryTool tool, string? source, string query)
    {
        if (type == AgentLibraryType.Plugins || view == AgentLibraryView.Sources)
            return [];
        return skills.Where(skill =>
            (view != AgentLibraryView.Installed || skill.IsInstalled)
            && (view != AgentLibraryView.Updates || skill.HasUpdate)
            && (tool.IsAll || tool.AgentId is { } agentId
                && skill.Agents?.Contains(agentId, StringComparer.OrdinalIgnoreCase) == true)
            && (source is null || skill.Source?.InstallSource == source)
            && MatchesQuery(query, skill.Name, skill.Id, skill.Description, skill.SourceName));
    }

    internal static IEnumerable<AgentLibraryPlugin> FilterPlugins(
        IEnumerable<AgentLibraryPlugin> plugins, AgentLibraryView view, AgentLibraryType type,
        AgentLibraryTool tool, string query)
    {
        if (type == AgentLibraryType.Skills || view is AgentLibraryView.Updates or AgentLibraryView.Sources)
            return [];
        return plugins.Where(entry =>
            (view != AgentLibraryView.Installed || entry.Plugin.IsInstalled)
            && (tool.IsAll || ReferenceEquals(tool.Provider, entry.Provider))
            && MatchesQuery(query, entry.Plugin.Name, entry.Plugin.Id, entry.Plugin.Description, entry.Plugin.Marketplace));
    }

    internal static SkillItem[] BulkInstallScope(IEnumerable<SkillItem> visibleSkills)
    {
        var items = visibleSkills.Where(skill => skill.CanInstallFromDiscover && skill.Source is not null).ToArray();
        return items.Length > 1 && items.All(skill => skill.Source?.InstallSource == items[0].Source?.InstallSource)
            ? items : [];
    }

    internal static AgentPluginItem[] PluginUpdateScope(
        IAgentPluginService? selectedProvider, IEnumerable<AgentLibraryPlugin> visiblePlugins) =>
        selectedProvider is { SupportsUpdate: true }
            ? visiblePlugins.Where(entry => ReferenceEquals(entry.Provider, selectedProvider)
                && entry.Plugin is { IsInstalled: true, CanManage: true }).Select(entry => entry.Plugin).ToArray()
            : [];

    internal static async Task<AgentLibraryBatchResult> RunBatchAsync<T>(
        IReadOnlyList<T> items, Func<T, string> identity, Func<T, CancellationToken, Task> perform,
        Action<string, int> onProgress, CancellationToken token)
    {
        int completed = 0;
        List<string> failures = [];
        foreach (var item in items)
        {
            if (token.IsCancellationRequested)
                return new(completed, failures, true);

            string name = identity(item);
            onProgress(name, completed + failures.Count + 1);
            try
            {
                await perform(item, token);
                completed++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new(completed, failures, true);
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
            }
        }

        return new(completed, failures, token.IsCancellationRequested);
    }
}

internal sealed record AgentLibraryBatchResult(int Completed, IReadOnlyList<string> Failures, bool Cancelled);
