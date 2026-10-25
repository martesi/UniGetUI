using System.Diagnostics;
using System.Reflection;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.NpmManager;
using UniGetUI.PackageEngine.Managers.PipManager;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Tests;

[CollectionDefinition(nameof(OperationOrchestrationTestCollection), DisableParallelization = true)]
public sealed class OperationOrchestrationTestCollection;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class PackageOperationsTests
{
    [Fact]
    public void RetryModesMutateInstallOptionsAndMetadata()
    {
        var package = CreatePackage();
        var options = new InstallOptions();
        var operation = new InspectableInstallPackageOperation(package, options);

        operation.Retry(AbstractOperation.RetryMode.Retry_AsAdmin);
        operation.Retry(AbstractOperation.RetryMode.Retry_Interactive);
        operation.Retry(AbstractOperation.RetryMode.Retry_SkipIntegrity);

        Assert.True(options.RunAsAdministrator);
        Assert.True(options.InteractiveInstallation);
        Assert.True(options.SkipHashCheck);
        Assert.Contains("Retried package operation", operation.Metadata.OperationInformation);
        Assert.Contains(package.Id, operation.Metadata.OperationInformation);
        Assert.Throws<InvalidOperationException>(() => operation.Retry("InvalidRetryMode"));
    }

    [Fact]
    public void InstallOperationBuildsPrerequisitesKillListAndPreCommand()
    {
        var package = CreatePackage();
        var options = new InstallOptions
        {
            PreInstallCommand = "echo before install",
            AbortOnPreInstallFail = false,
        };
        options.KillBeforeOperation.Add("proc-one");
        options.KillBeforeOperation.Add("proc-two");
        using var prerequisite = new StubOperation();
        using var operation = new InspectableInstallPackageOperation(package, options, req: prerequisite);

        var preOperations = GetInnerOperations(operation, "PreOperations");

        Assert.Collection(
            preOperations,
            inner =>
            {
                Assert.Same(prerequisite, inner.Operation);
                Assert.True(inner.MustSucceed);
            },
            inner =>
            {
                Assert.IsType<KillProcessOperation>(inner.Operation);
                Assert.False(inner.MustSucceed);
            },
            inner =>
            {
                Assert.IsType<KillProcessOperation>(inner.Operation);
                Assert.False(inner.MustSucceed);
            },
            inner =>
            {
                var preCommand = Assert.IsType<PrePostOperation>(inner.Operation);
                Assert.False(inner.MustSucceed);
                Assert.Contains("echo before install", preCommand.Metadata.Status);
            }
        );
    }

    [Fact]
    public async Task UpdateOperationBuildsPostOperationsForCommandAndPreviousVersions()
    {
        var manager = CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        var olderInstalledVersion = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .Build();
        var newerInstalledVersion = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("3.1.0")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(olderInstalledVersion);
        await InstalledPackagesLoader.Instance.AddForeign(newerInstalledVersion);
        var options = new InstallOptions
        {
            PostUpdateCommand = "echo after update",
            UninstallPreviousVersionsOnUpdate = true,
        };

        using var operation = new UpdatePackageOperation(package, options);
        var postOperations = GetInnerOperations(operation, "PostOperations");

        Assert.Collection(
            postOperations,
            inner =>
            {
                var postCommand = Assert.IsType<PrePostOperation>(inner.Operation);
                Assert.False(inner.MustSucceed);
                Assert.Contains("echo after update", postCommand.Metadata.Status);
            },
            inner =>
            {
                var uninstall = Assert.IsType<UninstallPackageOperation>(inner.Operation);
                Assert.False(inner.MustSucceed);
                Assert.Equal("2.0.0", uninstall.Package.VersionString);
            }
        );
        Assert.Contains("1.0.0 -> 3.0.0", operation.Metadata.OperationInformation);
    }

    // UninstallPreviousVersionsOnUpdate is the one destructive consumer of the per-manager
    // version comparison: it queues every installed copy it considers superseded for removal.
    // On a SemVer registry the pre-release IS superseded by its stable release and must be
    // cleaned up, which the shared numeric comparison could never see.
    [Fact]
    public async Task UpdateOperationQueuesASupersededPreReleaseForUninstallOnSemVerManagers()
    {
        var manager = new Npm();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("contoso-tool")
            .WithVersion("2.0.0-rc1")
            .WithNewVersion("2.0.0")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("contoso-tool")
                .WithVersion("2.0.0-rc1")
                .Build()
        );

        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { UninstallPreviousVersionsOnUpdate = true }
        );

        var inner = Assert.Single(GetInnerOperations(operation, "PostOperations"));
        var uninstall = Assert.IsType<UninstallPackageOperation>(inner.Operation);
        Assert.Equal("2.0.0-rc1", uninstall.Package.VersionString);
    }

    // The mirror image, and the reason this had to be per-manager: on PyPI a bare trailing
    // dash-number is an implicit POST-release, so "1.0.0-1" is NEWER than "1.0.0" and must
    // never be queued for removal when updating onto it.
    [Fact]
    public async Task UpdateOperationLeavesANewerPostReleaseInstalledOnPip()
    {
        var manager = new Pip();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("contoso-tool")
            .WithVersion("0.9.0")
            .WithNewVersion("1.0.0")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("contoso-tool")
                .WithVersion("1.0.0-1")
                .Build()
        );

        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { UninstallPreviousVersionsOnUpdate = true }
        );

        Assert.Empty(GetInnerOperations(operation, "PostOperations"));
    }

    // Pins pre-existing behaviour rather than endorsing it: when an installed version cannot be
    // parsed at all, it is still treated as superseded and queued for removal. Left as-is
    // deliberately - changing what a destructive operation does to unparseable input belongs in
    // its own change, not in a version-comparison fix.
    [Fact]
    public async Task UpdateOperationStillQueuesAnUnparseableInstalledVersionForUninstall()
    {
        var manager = CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Tool")
                .WithVersion("10c8e557")
                .Build()
        );

        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { UninstallPreviousVersionsOnUpdate = true }
        );

        var inner = Assert.Single(GetInnerOperations(operation, "PostOperations"));
        var uninstall = Assert.IsType<UninstallPackageOperation>(inner.Operation);
        Assert.Equal("10c8e557", uninstall.Package.VersionString);
    }

    [Fact]
    public void UninstallOperationBuildsPreAndPostCommandsForUninstallPath()
    {
        var package = CreatePackage();
        var options = new InstallOptions
        {
            PreUninstallCommand = "echo before uninstall",
            AbortOnPreUninstallFail = false,
            PostUninstallCommand = "echo after uninstall",
        };
        using var operation = new UninstallPackageOperation(package, options);

        var preOperations = GetInnerOperations(operation, "PreOperations");
        var postOperations = GetInnerOperations(operation, "PostOperations");

        var preOperation = Assert.Single(preOperations);
        var preCommand = Assert.IsType<PrePostOperation>(preOperation.Operation);
        Assert.False(preOperation.MustSucceed);
        Assert.Contains("echo before uninstall", preCommand.Metadata.Status);

        var postOperation = Assert.Single(postOperations);
        var postCommand = Assert.IsType<PrePostOperation>(postOperation.Operation);
        Assert.False(postOperation.MustSucceed);
        Assert.Contains("echo after uninstall", postCommand.Metadata.Status);
    }

    [Fact]
    public void InstallOperationPrepareProcessStartInfoUsesManagerCommandLineAndSetsBadges()
    {
        var manager = new PackageManagerBuilder()
            .ConfigureManager(manager =>
            {
                manager.ExecutablePath = "C:\\tools\\pkgmgr.exe";
                manager.ExecutableArguments = "--cli";
            })
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (package, _, operation) =>
                [
                    operation.ToString().ToLowerInvariant(),
                    package.Id,
                ])
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithOptions(new OverridenInstallationOptions(scope: PackageScope.Machine))
            .Build();
        var options = new InstallOptions
        {
            InstallationScope = PackageScope.User,
            InteractiveInstallation = true,
            SkipHashCheck = true,
        };
        using var operation = new InspectableInstallPackageOperation(package, options);
        AbstractOperation.BadgeCollection? badges = null;
        operation.BadgesChanged += (_, updatedBadges) => badges = updatedBadges;

        var startInfo = operation.PrepareProcessStartInfoForTests();

        Assert.Equal("C:\\tools\\pkgmgr.exe", startInfo.FileName);
        Assert.Equal("--cli install Contoso.Test", startInfo.Arguments.Trim());
        Assert.Equal(PackageTag.OnQueue, package.Tag);
        Assert.NotNull(badges);
        Assert.Equal(CoreTools.IsAdministrator(), badges!.AsAdministrator);
        Assert.True(badges.Interactive);
        Assert.True(badges.SkipHashCheck);
        Assert.Equal(PackageScope.Machine, badges.Scope);
    }

    private static TestPackageManager CreateSlowProcessManager(bool serializesOperations)
    {
        return new PackageManagerBuilder()
            .WithName($"SlowManager{Guid.NewGuid():N}")
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.SerializesOperations = serializesOperations;
                return capabilities;
            })
            .ConfigureManager(manager =>
            {
                manager.ExecutablePath = Path.Join(Environment.SystemDirectory, "cmd.exe");
                manager.ExecutableArguments = "/c";
            })
            .ConfigureOperation(helper =>
            {
                helper.ParametersFactory = (_, _, _) => ["ping", "-n", "3", "127.0.0.1"];
                helper.ResultFactory = (_, _, _, _) => OperationVeredict.Success;
            })
            .Build();
    }

    private static bool HasLine(AbstractOperation operation, string prefix) =>
        operation.GetOutput().Any(line => line.Item1.StartsWith(prefix, StringComparison.Ordinal));

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
            await Task.Delay(25, timeout.Token);
    }

    private static Func<bool> ReportsWaitingOnTheGate(AbstractOperation operation)
    {
        bool reported = false;
        operation.LogLineAdded += (_, line) =>
        {
            if (line.Item1.StartsWith("Waiting for another", StringComparison.Ordinal))
                reported = true;
        };
        return () => reported;
    }

    [Fact]
    public async Task OperationsOfASerializingManagerNeverRunTheirProcessesConcurrently()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateSlowProcessManager(serializesOperations: true);
        InitializeLoaders();
        using var first = new InstallPackageOperation(
            new PackageBuilder().WithManager(manager).WithId("first").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );
        using var second = new InstallPackageOperation(
            new PackageBuilder().WithManager(manager).WithId("second").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );
        var secondWaited = ReportsWaitingOnTheGate(second);
        bool? firstFinishedWhenSecondStarted = null;
        second.LogLineAdded += (_, line) =>
        {
            if (line.Item1.StartsWith("Executing process", StringComparison.Ordinal))
                firstFinishedWhenSecondStarted = HasLine(first, "Process return value");
        };

        Task firstRun = first.MainThread();
        await WaitForConditionAsync(() => HasLine(first, "Start Time"));
        Task secondRun = second.MainThread();
        await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Succeeded, first.Status);
        Assert.Equal(OperationStatus.Succeeded, second.Status);
        Assert.True(firstFinishedWhenSecondStarted);
        Assert.True(secondWaited());
    }

    [Fact]
    public async Task OperationsOfANonSerializingManagerRunTheirProcessesConcurrently()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateSlowProcessManager(serializesOperations: false);
        InitializeLoaders();
        using var first = new InstallPackageOperation(
            new PackageBuilder().WithManager(manager).WithId("first").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );
        using var second = new InstallPackageOperation(
            new PackageBuilder().WithManager(manager).WithId("second").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );
        var secondWaited = ReportsWaitingOnTheGate(second);
        bool? firstFinishedWhenSecondStarted = null;
        second.LogLineAdded += (_, line) =>
        {
            if (line.Item1.StartsWith("Executing process", StringComparison.Ordinal))
                firstFinishedWhenSecondStarted = HasLine(first, "Process return value");
        };

        Task firstRun = first.MainThread();
        await WaitForConditionAsync(() => HasLine(first, "Start Time"));
        Task secondRun = second.MainThread();
        await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(firstFinishedWhenSecondStarted);
        Assert.False(secondWaited());
    }

    [Fact]
    public async Task CancelingAnOperationWaitingOnASerializingManagerLeavesTheGateUsable()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateSlowProcessManager(serializesOperations: true);
        InitializeLoaders();
        using var first = new InstallPackageOperation(
            new PackageBuilder().WithManager(manager).WithId("first").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );
        using var waiting = new InstallPackageOperation(
            new PackageBuilder().WithManager(manager).WithId("waiting").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );
        using var third = new InstallPackageOperation(
            new PackageBuilder().WithManager(manager).WithId("third").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );
        var waitingReported = ReportsWaitingOnTheGate(waiting);
        var thirdWaited = ReportsWaitingOnTheGate(third);

        Task firstRun = first.MainThread();
        await WaitForConditionAsync(() => HasLine(first, "Start Time"));
        Task waitingRun = waiting.MainThread();
        await WaitForConditionAsync(waitingReported);
        waiting.Cancel();
        await waitingRun.WaitAsync(TimeSpan.FromSeconds(10));
        await firstRun.WaitAsync(TimeSpan.FromSeconds(30));
        await third.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Canceled, waiting.Status);
        Assert.False(HasLine(waiting, "Executing process"));
        Assert.DoesNotContain(
            waiting.GetOutput(),
            line => line.Item1.Contains("CanceledException", StringComparison.Ordinal)
        );
        Assert.Equal(OperationStatus.Succeeded, first.Status);
        Assert.Equal(OperationStatus.Succeeded, third.Status);
        Assert.False(thirdWaited());
    }

    [Fact]
    public async Task AnOperationWaitingOnASerializingManagerDoesNotHoldAParallelSlot()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var serialized = CreateSlowProcessManager(serializesOperations: true);
        var other = CreateSlowProcessManager(serializesOperations: false);
        InitializeLoaders();
        using var first = new InstallPackageOperation(
            new PackageBuilder().WithManager(serialized).WithId("first").Build(),
            new InstallOptions()
        );
        using var waiting = new InstallPackageOperation(
            new PackageBuilder().WithManager(serialized).WithId("waiting").Build(),
            new InstallOptions()
        );
        using var unrelated = new InstallPackageOperation(
            new PackageBuilder().WithManager(other).WithId("unrelated").Build(),
            new InstallOptions()
        );
        var waitingReported = ReportsWaitingOnTheGate(waiting);
        bool? firstFinishedWhenUnrelatedStarted = null;
        unrelated.LogLineAdded += (_, line) =>
        {
            if (line.Item1.StartsWith("Executing process", StringComparison.Ordinal))
                firstFinishedWhenUnrelatedStarted = HasLine(first, "Process return value");
        };

        AbstractOperation.OperationQueue.Clear();
        int previousMax = AbstractOperation.MAX_OPERATIONS;
        AbstractOperation.MAX_OPERATIONS = 2;
        try
        {
            Task firstRun = first.MainThread();
            await WaitForConditionAsync(() => HasLine(first, "Start Time"));
            Task waitingRun = waiting.MainThread();
            await WaitForConditionAsync(waitingReported);
            Task unrelatedRun = unrelated.MainThread();
            await Task.WhenAll(firstRun, waitingRun, unrelatedRun).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            AbstractOperation.MAX_OPERATIONS = previousMax;
            AbstractOperation.OperationQueue.Clear();
        }

        Assert.Equal(OperationStatus.Succeeded, first.Status);
        Assert.Equal(OperationStatus.Succeeded, waiting.Status);
        Assert.Equal(OperationStatus.Succeeded, unrelated.Status);
        Assert.False(firstFinishedWhenUnrelatedStarted);
    }

    private static TestPackageManager CreateFailingEchoManager(params string[] echoed)
    {
        return new PackageManagerBuilder()
            .WithName($"FailingManager{Guid.NewGuid():N}")
            .ConfigureManager(manager =>
            {
                manager.ExecutablePath = Path.Join(Environment.SystemDirectory, "cmd.exe");
                manager.ExecutableArguments = "/c";
            })
            .ConfigureOperation(helper =>
            {
                helper.ParametersFactory = (_, _, _) => ["echo", .. echoed];
                helper.ResultFactory = (_, _, _, _) => OperationVeredict.Failure;
            })
            .Build();
    }

    [Fact]
    public async Task AFailureCausedByAFileInUseTellsTheUserToCloseTheProgramUsingIt()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateFailingEchoManager("ERROR:", "[WinError", "32]", "file", "busy");
        InitializeLoaders();
        using var operation = new UpdatePackageOperation(
            new PackageBuilder().WithManager(manager).WithId("botocore").WithName("botocore").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Equal(
            "A file of botocore is in use by another program. Close any program that may be using it, then try again",
            operation.Metadata.FailureMessage
        );
    }

    [Fact]
    public async Task AFailureWithoutAFileInUseKeepsTheGenericFailureMessage()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateFailingEchoManager("ERROR:", "[WinError", "5]", "denied");
        InitializeLoaders();
        using var operation = new UpdatePackageOperation(
            new PackageBuilder().WithManager(manager).WithId("botocore").WithName("botocore").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.DoesNotContain("in use by another program", operation.Metadata.FailureMessage);
    }

    [Fact]
    public async Task InstallOperationSuccessfulRunSetsPackageTagAndAddsInstalledCopy()
    {
        var package = CreatePackage();
        InitializeLoaders();
        using var operation = new SimulatedInstallPackageOperation(
            package,
            new InstallOptions(),
            OperationVeredict.Success
        );

        await operation.MainThread();
        await WaitForAsync(() => InstalledPackagesLoader.Instance.GetEquivalentPackage(package) is not null);

        Assert.Equal(PackageTag.AlreadyInstalled, package.Tag);
        Assert.NotNull(InstalledPackagesLoader.Instance.GetEquivalentPackage(package));
    }

    [Fact]
    public async Task InstallOperationSuccessfulRunPrefersAuthoritativeInstalledVersion()
    {
        TestPackageManager? manager = null;
        Package? installedPackage = null;
        manager = new PackageManagerBuilder()
            .WithInstalledPackages(_ => [Assert.IsType<Package>(installedPackage)])
            .Build();
        var searchResult = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithVersion("3.0.3")
            .Build();
        installedPackage = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithVersion("2.1.4")
            .Build();
        InitializeLoaders();
        using var operation = new SimulatedInstallPackageOperation(
            searchResult,
            new InstallOptions { Version = "2.1.4" },
            OperationVeredict.Success
        );

        await operation.MainThread();
        await WaitForAsync(() =>
            InstalledPackagesLoader.Instance.GetEquivalentPackages(searchResult)
                .Any(package => package.VersionString == "2.1.4")
        );

        Assert.DoesNotContain(
            InstalledPackagesLoader.Instance.GetEquivalentPackages(searchResult),
            package => package.VersionString == "3.0.3"
        );
    }

    [Fact]
    public async Task UpdateOperationSuccessfulRunPrefersAuthoritativeInstalledVersion()
    {
        TestPackageManager? manager = null;
        Package? installedPackage = null;
        manager = new PackageManagerBuilder()
            .WithInstalledPackages(_ => [Assert.IsType<Package>(installedPackage)])
            .Build();
        var upgradablePackage = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithVersion("2.1.4")
            .WithNewVersion("3.0.0")
            .Build();
        installedPackage = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithVersion("3.0.3")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(upgradablePackage);
        using var operation = new SimulatedUpdatePackageOperation(
            upgradablePackage,
            new InstallOptions(),
            OperationVeredict.Success
        );

        await operation.MainThread();
        await WaitForAsync(() =>
            InstalledPackagesLoader.Instance.GetEquivalentPackages(upgradablePackage)
                .Any(package => package.VersionString == "3.0.3")
        );

        Assert.DoesNotContain(
            InstalledPackagesLoader.Instance.GetEquivalentPackages(upgradablePackage),
            package => package.VersionString == "3.0.0"
        );
    }

    [Fact]
    public async Task UpdateOperationSuccessfulRunPrefersRequestedVersionWhenSnapshotLags()
    {
        TestPackageManager? manager = null;
        Package? installedPackage = null;
        manager = new PackageManagerBuilder()
            .WithInstalledPackages(_ => [Assert.IsType<Package>(installedPackage)])
            .Build();
        var installedBeforeUpdate = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithVersion("2.1.4")
            .Build();
        installedPackage = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithVersion("2.1.4")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(installedBeforeUpdate);
        using var operation = new SimulatedUpdatePackageOperation(
            installedBeforeUpdate,
            new InstallOptions { Version = "3.0.3" },
            OperationVeredict.Success
        );

        await operation.MainThread();
        await WaitForAsync(() =>
            InstalledPackagesLoader.Instance.GetEquivalentPackages(installedBeforeUpdate)
                .Any(package => package.VersionString == "3.0.3")
        );

        Assert.DoesNotContain(
            InstalledPackagesLoader.Instance.GetEquivalentPackages(installedBeforeUpdate),
            package => package.VersionString == "2.1.4"
        );
    }

    [Fact]
    public void UsernameRedactionAppliesToDisplayOutputButNeverToResultParsingOutput()
    {
        // Regression: result parsing must see raw output; only display (GetOutput) may redact.
        var username = Environment.UserName;
        if (string.IsNullOrEmpty(username))
            return;

        using var operation = new LoggingStubOperation();
        var rawLine = $"Error: the operation was canceled by C:\\Users\\{username}\\app";
        operation.EmitLine(rawLine, AbstractOperation.LineType.Information);

        bool previous = Logger.RedactUsername;
        Logger.RedactUsername = true;
        try
        {
            var parsingOutput = operation.RawOutputForTests();
            var displayOutput = operation.GetOutput();

            Assert.Contains(parsingOutput, l => l.Item1 == rawLine);
            Assert.DoesNotContain(displayOutput, l => l.Item1.Contains(username));
            Assert.Contains(displayOutput, l => l.Item1.Contains("****"));
        }
        finally
        {
            Logger.RedactUsername = previous;
        }
    }

    private static IReadOnlyList<AbstractOperation.InnerOperation> GetInnerOperations(
        AbstractOperation operation,
        string fieldName
    )
    {
        var field = typeof(AbstractOperation).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        return Assert.IsAssignableFrom<IReadOnlyList<AbstractOperation.InnerOperation>>(
            field?.GetValue(operation)
        );
    }

    private static IPackage CreatePackage()
    {
        var manager = CreateManager();
        return new PackageBuilder().WithManager(manager).Build();
    }

    private static IPackageManager CreateManager()
    {
        return new PackageManagerBuilder()
            .ConfigureManager(manager =>
            {
                manager.ExecutablePath = "C:\\test-tools\\manager.exe";
                manager.ExecutableArguments = "--test";
            })
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (package, _, operation) =>
                [
                    operation.ToString().ToLowerInvariant(),
                    package.Id,
                ])
            .Build();
    }

    private static void InitializeLoaders()
    {
        _ = new DiscoverablePackagesLoader([]);
        _ = new UpgradablePackagesLoader([]);
        _ = new InstalledPackagesLoader([]);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (condition())
                return;

            await Task.Delay(25);
        }
    }

    private class InspectableInstallPackageOperation : InstallPackageOperation
    {
        public InspectableInstallPackageOperation(
            IPackage package,
            InstallOptions options,
            bool ignoreParallelInstalls = true,
            AbstractOperation? req = null
        )
            : base(package, options, ignoreParallelInstalls, req) { }

        public ProcessStartInfo PrepareProcessStartInfoForTests()
        {
            InitializeProcessStartInfoDefaults();
            PrepareProcessStartInfo();
            return process.StartInfo;
        }

        private void InitializeProcessStartInfoDefaults()
        {
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            process.StartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
            process.StartInfo.StandardInputEncoding = System.Text.Encoding.UTF8;
            process.StartInfo.WorkingDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile
            );
            process.StartInfo.FileName = "lol";
            process.StartInfo.Arguments = "lol";
        }
    }

    private sealed class SimulatedInstallPackageOperation : InspectableInstallPackageOperation
    {
        private readonly OperationVeredict _veredict;

        public SimulatedInstallPackageOperation(
            IPackage package,
            InstallOptions options,
            OperationVeredict veredict
        )
            : base(package, options)
        {
            _veredict = veredict;
        }

        protected override Task<OperationVeredict> PerformOperation()
        {
            return Task.FromResult(_veredict);
        }
    }

    private sealed class SimulatedUpdatePackageOperation : UpdatePackageOperation
    {
        private readonly OperationVeredict _veredict;

        public SimulatedUpdatePackageOperation(
            IPackage package,
            InstallOptions options,
            OperationVeredict veredict
        )
            : base(package, options)
        {
            _veredict = veredict;
        }

        protected override Task<OperationVeredict> PerformOperation()
        {
            return Task.FromResult(_veredict);
        }
    }

    private sealed class LoggingStubOperation : AbstractOperation
    {
        public LoggingStubOperation()
            : base(queue_enabled: false) { }

        public void EmitLine(string line, LineType type) => Line(line, type);

        public IReadOnlyList<(string, LineType)> RawOutputForTests() => GetRawOutput();

        protected override void ApplyRetryAction(string retryMode) { }

        protected override Task<OperationVeredict> PerformOperation()
            => Task.FromResult(OperationVeredict.Success);

        public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("about:blank"));
    }

    private sealed class StubOperation : AbstractOperation
    {
        public StubOperation()
            : base(queue_enabled: false)
        {
            Metadata.Status = "Stub status";
            Metadata.Title = "Stub title";
            Metadata.OperationInformation = "Stub info";
            Metadata.SuccessTitle = "Stub success";
            Metadata.SuccessMessage = "Stub success";
            Metadata.FailureTitle = "Stub failure";
            Metadata.FailureMessage = "Stub failure";
        }

        protected override void ApplyRetryAction(string retryMode) { }

        protected override Task<OperationVeredict> PerformOperation()
        {
            return Task.FromResult(OperationVeredict.Success);
        }

        public override Task<Uri> GetOperationIcon()
        {
            return Task.FromResult(new Uri("about:blank"));
        }
    }
    [Fact]
    public async Task UnmetPreconditionIsReportedWithoutTheInternalErrorStackTrace()
    {
        using var operation = new ThrowingStubOperation(
            new OperationPreconditionException("Run UniGetUI as a regular user and try again.")
        );

        await operation.MainThread();

        Assert.Equal(OperationStatus.Failed, operation.Status);
        var lines = operation.GetOutput().Select(line => line.Item1).ToList();
        Assert.Contains("Run UniGetUI as a regular user and try again.", lines);
        Assert.DoesNotContain("An internal error occurred:", lines);
        Assert.DoesNotContain(lines, line => line.Contains("OperationPreconditionException"));
    }

    [Fact]
    public async Task AnUnexpectedExceptionStillReportsItsStackTrace()
    {
        using var operation = new ThrowingStubOperation(
            new InvalidOperationException("some unexpected failure")
        );

        await operation.MainThread();

        Assert.Equal(OperationStatus.Failed, operation.Status);
        var lines = operation.GetOutput().Select(line => line.Item1).ToList();
        Assert.Contains(lines, line => line.Contains("InvalidOperationException"));
        Assert.Contains(lines, line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal));
    }

    private sealed class ThrowingStubOperation : AbstractOperation
    {
        private readonly Exception _exception;

        public ThrowingStubOperation(Exception exception)
            : base(queue_enabled: false)
        {
            _exception = exception;
            Metadata.Status = "Throwing stub status";
            Metadata.Title = "Throwing stub title";
            Metadata.OperationInformation = "Throwing stub info";
            Metadata.SuccessTitle = "Throwing stub success";
            Metadata.SuccessMessage = "Throwing stub success";
            Metadata.FailureTitle = "Throwing stub failure";
            Metadata.FailureMessage = "Throwing stub failure";
        }

        protected override void ApplyRetryAction(string retryMode) { }

        protected override Task<OperationVeredict> PerformOperation() => throw _exception;

        public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("about:blank"));
    }

}
