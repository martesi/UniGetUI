using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.AgentSkills;
using UniGetUI.Interface.Enums;

namespace UniGetUI.Pages.SettingsPages.GeneralPages;

internal sealed class AgentSkillsUpdateChecker
{
    private readonly AgentSkillsService _skills = new();
    private CancellationTokenSource? _work;
    private string _notified = "";

    public async Task CheckAsync()
    {
        if (_work is not null || !Settings.Get(Settings.K.EnableAgentSkills))
            return;

        using var work = new CancellationTokenSource();
        _work = work;
        try
        {
            var updates = await Task.Run(() => _skills.CheckUpdates(token: work.Token), work.Token);
            if (work.IsCancellationRequested || !Settings.Get(Settings.K.EnableAgentSkills))
                return;

            string signature = string.Join(
                '|',
                updates.Select(skill => $"{skill.SourceName}:{skill.Id}:{skill.NewVersion}").Order(StringComparer.Ordinal)
            );
            if (updates.Count > 0
                && signature != _notified
                && !Settings.AreUpdatesNotificationsDisabled()
                && !Settings.GetDictionaryItem<string, bool>(Settings.K.DisabledPackageManagerNotifications, "Skills"))
            {
                var notification = new AppNotificationBuilder()
                    .SetTag("ClassicAgentSkillsUpdates")
                    .AddText(CoreTools.Translate("Agent Skills"))
                    .AddText(updates.Count == 1
                        ? CoreTools.Translate("1 skill update")
                        : CoreTools.Translate("{0} skill updates", updates.Count))
                    .AddArgument("action", NotificationArguments.ShowOnAgentSkillsTab)
                    .BuildNotification();
                AppNotificationManager.Default.Show(notification);
            }

            _notified = signature;
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
        }
        finally
        {
            _work = null;
        }
    }

    public void Stop()
    {
        _work?.Cancel();
        _notified = "";
    }
}
