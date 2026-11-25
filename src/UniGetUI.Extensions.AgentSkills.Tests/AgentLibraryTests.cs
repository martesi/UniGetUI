namespace UniGetUI.Extensions.AgentSkills.Tests;

public sealed class AgentLibraryTests
{
    private static readonly AgentLibraryTool AllTools = new("All");
    private static readonly SkillSourceLocator Source = SkillSourceLocator.GitHub("owner", "skills");

    [Fact]
    public void SkillsRespectViewTypeToolTextAndSourceFilters()
    {
        SkillItem[] skills =
        [
            new("one", "Release tooling", Source, Agents: ["codex"], IsInstalled: true, HasUpdate: true),
            new("two", "Release docs", Source, Agents: ["cursor"], IsInstalled: true),
            new("three", "Release sample", Source),
            new("four", "Release sample", SkillSourceLocator.GitHub("other", "skills")),
        ];

        var codex = new AgentLibraryTool("Codex", "codex", new CodexPluginService());
        Assert.Equal("one", Assert.Single(AgentLibrary.FilterSkills(skills,
            AgentLibraryView.Updates, AgentLibraryType.All, codex, Source.InstallSource, "TOOL")).Id);
        Assert.Equal(["one", "two"], AgentLibrary.FilterSkills(skills,
            AgentLibraryView.Installed, AgentLibraryType.All, AllTools, null, "").Select(skill => skill.Id));
        Assert.Equal(["one", "two", "three"], AgentLibrary.FilterSkills(skills,
            AgentLibraryView.Discover, AgentLibraryType.Skills, AllTools, Source.InstallSource, "release").Select(skill => skill.Id));
        Assert.Empty(AgentLibrary.FilterSkills(skills, AgentLibraryView.Discover, AgentLibraryType.Plugins, AllTools, null, ""));
    }

    [Fact]
    public void InstallBatchUsesVisibleUninstalledSkillsFromOneExactSource()
    {
        var first = new SkillItem("one", "Visible", Source);
        var second = new SkillItem("two", "Visible", Source);
        var excluded = new SkillItem("three", "Other", Source);
        var installed = new SkillItem("four", "Visible", Source, IsInstalled: true);
        var sameNameDifferentSource = Source with { InstallSource = Source.InstallSource + "#other-ref" };
        var onOtherRef = new SkillItem("different", "Visible", sameNameDifferentSource);

        var visible = AgentLibrary.FilterSkills([first, second, excluded, installed],
            AgentLibraryView.Discover, AgentLibraryType.All, AllTools, null, "Visible");
        Assert.Equal([first, second], AgentLibrary.BulkInstallScope(visible));
        Assert.Empty(AgentLibrary.BulkInstallScope([first, onOtherRef]));
        Assert.Empty(AgentLibrary.BulkInstallScope([first, installed]));
    }

    [Fact]
    public void PluginUpdatesAreProviderOperationsNotDetectedSkillUpdates()
    {
        var claude = new ClaudePluginService();
        var codex = new CodexPluginService();
        var available = new AgentPluginItem("available@market", "Shown plugin", "", false, false);
        var installed = new AgentPluginItem("ready@market", "Shown plugin", "1", true, true);
        var unmanaged = installed with { Id = "managed@market", CanManage = false };
        var hidden = installed with { Id = "hidden@market", Description = "Hidden plugin" };
        AgentLibraryPlugin[] items =
        [
            new(claude, available), new(claude, installed), new(claude, unmanaged), new(claude, hidden),
            new(codex, installed),
        ];
        var visible = AgentLibrary.FilterPlugins(items, AgentLibraryView.Installed,
            AgentLibraryType.All, new("Claude Code", "claude-code", claude), "Shown");
        Assert.Equal(installed, Assert.Single(AgentLibrary.PluginUpdateScope(claude, visible)));
        Assert.Empty(AgentLibrary.PluginUpdateScope(null, visible));
        Assert.Empty(AgentLibrary.PluginUpdateScope(codex, items));
        Assert.Empty(AgentLibrary.FilterPlugins(items, AgentLibraryView.Updates, AgentLibraryType.Plugins, AllTools, ""));
        Assert.Equal(5, AgentLibrary.FilterPlugins(items, AgentLibraryView.Discover, AgentLibraryType.All, AllTools, "").Count());
    }

    [Fact]
    public async Task BatchContinuesPastFailuresAndReportsEveryAttempt()
    {
        List<(string, int)> progress = [];
        var result = await AgentLibrary.RunBatchAsync<string>(["one", "two", "three"], item => item,
            (item, _) => item == "two" ? Task.FromException(new IOException("offline")) : Task.CompletedTask,
            (name, position) => progress.Add((name, position)), CancellationToken.None);
        Assert.Equal(2, result.Completed);
        Assert.Equal("two: offline", Assert.Single(result.Failures));
        Assert.False(result.Cancelled);
        Assert.Equal([("one", 1), ("two", 2), ("three", 3)], progress);
    }

    [Fact]
    public async Task CancellationPreservesCompletedCountAndStopsBeforeNextItem()
    {
        using var cancellation = new CancellationTokenSource();
        List<string> attempted = [];
        var result = await AgentLibrary.RunBatchAsync<string>(["one", "two", "three"], item => item, (item, token) =>
        {
            attempted.Add(item);
            if (item == "two")
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        }, (_, _) => { }, cancellation.Token);

        Assert.Equal(1, result.Completed);
        Assert.Empty(result.Failures);
        Assert.True(result.Cancelled);
        Assert.Equal(["one", "two"], attempted);
    }

}
