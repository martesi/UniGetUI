using UniGetUI.Shared;

namespace UniGetUI.Tests;

public class ProtocolLaunchTests
{
    [Theory]
    [InlineData("unigetui://showDiscover")]
    [InlineData("UNIGETUI://showDiscover")]
    public void ProtocolLaunchDropsInjectedCommandLineSwitches(string uri)
    {
        string[] arguments = [uri, "--set-setting-value", "ParallelOperationCount", "100"];

        Assert.Equal([uri], SharedPreUiCommandDispatcher.IgnoreArgumentsInjectedIntoProtocolLaunch(arguments));
    }

    [Fact]
    public void OrdinaryCommandLineKeepsItsArguments()
    {
        string[] arguments = ["--export-settings", "settings.json"];

        Assert.Same(arguments, SharedPreUiCommandDispatcher.IgnoreArgumentsInjectedIntoProtocolLaunch(arguments));
    }
}
