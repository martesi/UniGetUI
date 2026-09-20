from pathlib import Path
import sys

root = Path(sys.argv[1])
ui = root / 'src/UniGetUI'


def replace(path, old, new, count=1):
    text = path.read_text(encoding='utf-8-sig')
    if text.count(old) != count:
        raise ValueError(f'{path}: expected {count} occurrences of {old!r}, got {text.count(old)}')
    path.write_text(text.replace(old, new), encoding='utf-8', newline='\n')


updater = ui / 'AutoUpdater.cs'
replace(updater, 'await Task.Delay(TimeSpan.FromMinutes(updateSucceeded ? 60 : 10));', 'await Task.Delay(UpdaterDownloadEngine.DefaultAutomaticUpdateCheckInterval);')
replace(updater, '                if (\n                    File.Exists(InstallerPath)', '''                UpdaterDownloadIdentity identity = new(updateCandidate.VersionName, updateCandidate.InstallerHash, updateCandidate.InstallerDownloadUrl);
                string failureStatePath = UpdaterDownloadEngine.GetFailureStatePath(InstallerPath);
                if (
                    File.Exists(InstallerPath)''')
replace(updater, '                    LogUpdateInfo($"A cached valid installer was found, launching update process...");', '''                    UpdaterDownloadEngine.ClearFailureState(failureStatePath, LogUpdateWarn);
                    LogUpdateInfo($"A cached valid installer was found, launching update process...");''')
replace(updater, '                File.Delete(InstallerPath);', '''                if (!ManualCheck && !Verbose && UpdaterDownloadEngine.IsFailureBackoffActive(
                    failureStatePath, identity, DateTime.UtcNow, out TimeSpan remaining,
                    out UpdaterDownloadFailureState? failure, LogUpdateWarn))
                {
                    LogUpdateWarn($"Installer {identity.Version} download deferred after {failure?.FailureClass}: {remaining:g} remaining.");
                    MarkAttemptFinished("installer download skipped by failure backoff");
                    return true;
                }''')
replace(updater, '''                await DownloadInstaller(
                    updateCandidate.InstallerDownloadUrl,
                    InstallerPath,
                    updaterOverrides
                );''', '''                string downloadedInstallerPath;
                try
                {
                    downloadedInstallerPath = await DownloadInstaller(
                        updateCandidate.InstallerDownloadUrl, InstallerPath, updaterOverrides, identity);
                }
                catch
                {
                    UpdaterDownloadEngine.RecordFailure(failureStatePath, identity, "download", DateTime.UtcNow, LogUpdateWarn);
                    throw;
                }''')
replace(updater, '''                    await CheckInstallerHash(
                        InstallerPath,
                        updateCandidate.InstallerHash,
                        updaterOverrides
                    ) && CheckInstallerSignerThumbprint(InstallerPath, updaterOverrides)''', '''                    await CheckInstallerHash(
                        downloadedInstallerPath,
                        updateCandidate.InstallerHash,
                        updaterOverrides
                    ) && CheckInstallerSignerThumbprint(downloadedInstallerPath, updaterOverrides)''')
replace(updater, '                    LogUpdateInfo("The downloaded installer is valid, launching update process...");', '''                    UpdaterDownloadEngine.PromotePartialDownload(InstallerPath, LogUpdateWarn);
                    UpdaterDownloadEngine.ClearFailureState(failureStatePath, LogUpdateWarn);
                    LogUpdateInfo("The downloaded installer is valid, launching update process...");''')
replace(updater, '''                ShowMessage_ThreadSafe(
                    CoreTools.Translate("The installer authenticity could not be verified."),''', '''                UpdaterDownloadEngine.RecordFailure(failureStatePath, identity, "validation", DateTime.UtcNow, LogUpdateWarn);
                UpdaterDownloadEngine.DeletePartialDownload(InstallerPath, LogUpdateWarn);
                ShowMessage_ThreadSafe(
                    CoreTools.Translate("The installer authenticity could not be verified."),''')
replace(updater, '''    private static async Task DownloadInstaller(
        string downloadUrl,
        string installerLocation,
        UpdaterOverrides updaterOverrides
''', '''    private static async Task<string> DownloadInstaller(
        string downloadUrl,
        string installerLocation,
        UpdaterOverrides updaterOverrides,
        UpdaterDownloadIdentity identity
''')
replace(updater, '''            HttpResponseMessage result = await client.GetAsync(downloadUrl);
            result.EnsureSuccessStatusCode();
            using FileStream fs = new(installerLocation, FileMode.OpenOrCreate);
            await result.Content.CopyToAsync(fs);
        }
        LogUpdateDebug("The download has finished successfully");''', '''            var result = await UpdaterDownloadEngine.DownloadInstallerPartAsync(client, identity, installerLocation, LogUpdateWarn);
            LogUpdateDebug("The download has finished successfully");
            return result.PartialPath;
        }''')
replace(updater, '"/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NoVCRedist /NoEdgeWebView /NoWinGet",', '''UniGetUI.Shared.AutoUpdaterInstallerArguments.ForWindows(
                        CoreData.IsPortable, CoreData.UniGetUIExecutableDirectory,
                        UniGetUI.Shared.AutoUpdaterInstallerArguments.DetectInstallScope(CoreData.UniGetUIExecutableDirectory)),''')
replace(root / 'src/Shared/AutoUpdater.InstallerArguments.cs', '889610CC-4337-4BDB-AC3B-4F21806C0BDE', 'E385AFF5-90A4-4296-8702-EC129F9DC40B')

# Capture the user's startup preference before uninstall/reinstall changes keys.
installer = root / 'UniGetUI.iss'
replace(installer, '  RegisterPackageBundle: Boolean;', '''  RegisterPackageBundle: Boolean;
  PreserveAutostartDisabled: Boolean;

function IsAutostartDisabledByUser: Boolean;
var
  Data: AnsiString;
begin
  Result := False;
  if RegQueryBinaryValue(HKEY_CURRENT_USER,
       'Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run',
       'UniGetUIClassic', Data) then
    Result := (Length(Data) >= 1) and ((Ord(Data[1]) and 1) = 1);
end;
''')
replace(installer, 'function InitializeSetup: Boolean;\nbegin', 'function InitializeSetup: Boolean;\nbegin\n  PreserveAutostartDisabled := IsAutostartDisabledByUser;')
replace(installer, "Check: CmdLineParamExists('/NoRunOnStartup');", "Check: CmdLineParamExists('/NoRunOnStartup') or PreserveAutostartDisabled;")
replace(installer, 'Name: "{autodesktop}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExeName}"; Tasks: regularinstall\\desktopicon', 'Name: "{autodesktop}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExeName}"; Tasks: regularinstall\\desktopicon; Check: not CmdLineParamExists(\'/NoDesktopShortcut\')')

# Normalize relative and case-insensitive startup bundles using the shared parser.
window = ui / 'MainWindow.xaml.cs'
replace(window, 'public void ProcessCommandLineParameters()\n        {', '''public void ProcessCommandLineParameters()
        {
            var normalized = UniGetUI.Shared.StartupBundleArguments.Normalize(ParametersToProcess.ToArray(), Environment.CurrentDirectory);
            ParametersToProcess.Clear();
            foreach (string argument in normalized) ParametersToProcess.Enqueue(argument);''')
for extension in ['.ubundle', '.json', '.xml', '.yaml']:
    replace(window, f'param.EndsWith("{extension}")', f'param.EndsWith("{extension}", StringComparison.OrdinalIgnoreCase)')
