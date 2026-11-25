namespace UniGetUI.Extensions.AgentSkills.Tests;

internal sealed class WindowsPowerShellFactAttribute : FactAttribute
{
    public WindowsPowerShellFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Uses the Windows PowerShell host.";
    }
}
