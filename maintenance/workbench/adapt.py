"""Generate the WinUI adaptation on the isolated source branch, not in main."""
from pathlib import Path
import os
import shutil
import subprocess
import sys

ROOT = Path(sys.argv[1]).resolve()
UPSTREAM = sys.argv[2]
UI = 'src/UniGetUI/'


def read(path):
    return (ROOT / path).read_text(encoding='utf-8-sig')


def write(path, text):
    target = ROOT / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text, encoding='utf-8', newline='\n')


def replace(path, old, new, count=1):
    text = read(path)
    actual = text.count(old)
    if actual != count:
        raise ValueError(f'{path}: expected {count} occurrences of {old!r}, got {actual}')
    write(path, text.replace(old, new))


def upstream(path):
    if folder := os.environ.get('PARITY_UPSTREAM_DIR'):
        return (Path(folder) / path).read_text(encoding='utf-8-sig')
    return subprocess.check_output(['git', '-C', str(ROOT), 'show', f'{UPSTREAM}:{path}']).decode('utf-8-sig')


def add_usings(path, *names):
    text = read(path)
    write(path, ''.join(f'using {name};\n' for name in names if f'using {name};' not in text) + text)


# Keep upstream's tested scheduling state machine. Only dispatch and backup UI seams differ.
text = upstream('src/UniGetUI.Avalonia/Infrastructure/MaintenanceScheduler.cs')
text = text.replace('using Avalonia.Threading;', 'using Microsoft.UI.Dispatching;')
text = text.replace('using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;', 'using UniGetUI.Interface.SoftwarePages;')
text = text.replace('namespace UniGetUI.Avalonia.Infrastructure;', 'namespace UniGetUI.Services;')
text = text.replace('DispatcherTimer?', 'DispatcherQueueTimer?')
text = text.replace('_timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickInterval };', '_timer = MainApp.Dispatcher.CreateTimer();\n        _timer.Interval = TickInterval;')
text = text.replace('Dispatcher.UIThread.Post(', 'MainApp.Dispatcher.TryEnqueue(')
text = text.replace('BackupViewModel.DoLocalBackupStatic()', 'InstalledPackagesPage.BackupPackages_LOCAL()')
text = text.replace('BackupViewModel.DoCloudBackupStatic()', 'InstalledPackagesPage.BackupPackages_CLOUD()')
write(UI + 'Services/MaintenanceScheduler.cs', text)

app = UI + 'App.xaml.cs'
add_usings(app, 'UniGetUI.Services', 'UniGetUI.PackageEngine.Classes.Packages.Classes')
replace(app, 'await Task.WhenAll(iniTasks);', 'await Task.WhenAll(iniTasks);\n                AutoUpdatesMigration.RunOnce(PEInterface.Managers);\n                MaintenanceScheduler.Start();')

installed = UI + 'Pages/SoftwarePages/InstalledPackagesPage.cs'
add_usings(installed, 'UniGetUI.Core.Tools.Scheduling')
text = read(installed)
start = text.index('            if (!HasDoneBackup)')
end = text.index('            var infoBar', start)
text = text[:start] + '''            if (!HasDoneBackup)
            {
                HasDoneBackup = true;
                foreach (var kind in new[] { MaintenanceTaskKind.LocalBackup, MaintenanceTaskKind.CloudBackup })
                    if (MaintenanceScheduler.ShouldRunAtAppStart(kind))
                        _ = MaintenanceScheduler.RunAsync(kind);
            }

''' + text[end:]
start = text.index('        public static async Task BackupPackages_CLOUD()')
end = text.index('        private static void LaunchUpdate', start)
text = text[:start] + '''        public static async Task<bool> BackupPackages_CLOUD()
        {
            try
            {
                await CoreTools.WaitForInternetConnection();
                string backupContents = await GenerateBackupContents();
                var backupService = new GitHubBackupService(new GitHubAuthService());
                await backupService.UploadPackageBundle(backupContents);
                Logger.ImportantInfo("Cloud backup succeeded");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("An error occurred while performing a CLOUD backup");
                Logger.Error(ex);
                return false;
            }
        }

        public static async Task<bool> BackupPackages_LOCAL()
        {
            try
            {
                string backupContents = await GenerateBackupContents();
                string filePath = await LocalBackupManager.SaveBackupAsync(backupContents);
                LocalBackupManager.ApplyRetentionLimit();
                Logger.ImportantInfo("Backup saved to " + filePath);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("An error occurred while performing a LOCAL backup");
                Logger.Error(ex);
                return false;
            }
        }

''' + text[end:]
write(installed, text)
replace(installed, 'Logger.Debug("Starting package backup");', '''var loader = InstalledPackagesLoader.Instance;
            if (!loader.IsLoaded || loader.IsLoading || loader.LastLoadReportedFailures || !loader.Packages.Any())
                throw new InvalidOperationException("Refusing to back up an incomplete or empty installed-package list");
            Logger.Debug("Starting package backup");''')

updates = UI + 'Pages/SoftwarePages/SoftwareUpdatesPage.cs'
add_usings(updates, 'UniGetUI.Services', 'UniGetUI.Core.Tools.Scheduling', 'UniGetUI.PackageEngine.Classes.Packages.Classes', 'UniGetUI.PackageEngine.Operations')
text = read(updates)
text = text.replace('if (package.Tag is not PackageTag.OnQueue and not PackageTag.BeingProcessed)', 'if (package.Tag is not PackageTag.OnQueue and not PackageTag.BeingProcessed\n                        && !PackageOperation.HasPendingOperation(package, OperationType.Update))')
start = text.index('                else if (Settings.Get(Settings.K.AutomaticallyUpdatePackages))')
end = text.index('            }\n            catch (Exception ex)', start)
text = text[:start] + '''                else if (MaintenanceScheduler.IsAutoInstallDue())
                {
                    MaintenanceScheduler.MarkAutoInstallHandled();
                    bool markedOnly = MaintenanceScheduleStore.GetInstallTargets() is ScheduleInstallTargets.MarkedPackagesOnly;
                    var targets = upgradablePackages.Where(p => !markedOnly || AutoUpdatesDatabase.IsAutoUpdated(p)).ToList();
                    var skipped = upgradablePackages.Except(targets).ToList();
                    if (targets.Count > 0)
                    {
                        await MainApp.Operations.Update(targets);
                        await ShowUpgradingPackagesNotification(targets);
                    }
                    if (skipped.Count > 0) await ShowAvailableUpdatesNotification(skipped);
                }
                else if (Environment.GetCommandLineArgs().Contains("--updateapps"))
                {
                    await MainApp.Operations.Update(upgradablePackages);
                    await ShowUpgradingPackagesNotification(upgradablePackages);
                }
                else
                {
                    await ShowAvailableUpdatesNotification(upgradablePackages);
                }
''' + text[end:]
write(updates, text)

# Keep manual operation prefill behavior identical; native clipboard is the only UI dependency.
text = upstream('src/UniGetUI.Avalonia/Infrastructure/ManualInstallHelper.cs')
text = text.replace('using Avalonia.Input.Platform;\n', '').replace('using UniGetUI.Avalonia.Views;\n', '')
text = text.replace('namespace UniGetUI.Avalonia.Infrastructure;', 'namespace UniGetUI.Services;')
text = text.replace('private static async Task CopyToClipboardAsync(string text)\n    {\n        if (MainWindow.Instance?.Clipboard is { } clipboard)\n            await clipboard.SetTextAsync(text);\n    }', 'private static Task CopyToClipboardAsync(string text)\n    {\n        ExternalLibraries.Clipboard.WindowsClipboard.SetText(text);\n        return Task.CompletedTask;\n    }')
write(UI + 'Services/ManualInstallHelper.cs', text)

page = UI + 'Pages/SoftwarePages/AbstractPackagesPage.xaml.cs'
add_usings(page, 'UniGetUI.Services')
replace(page, 'var menu = GenerateContextMenu();', '''var menu = GenerateContextMenu();
            if (PAGE_ROLE is OperationType.Install or OperationType.Update or OperationType.Uninstall)
            {
                BetterMenuItem manual = new()
                {
                    Text = CoreTools.Translate(PAGE_ROLE is OperationType.Install ? "Manual install" : PAGE_ROLE is OperationType.Update ? "Manual update" : "Manual uninstall"),
                    IconName = IconType.Console,
                };
                manual.Click += async (_, _) => await ManualInstallHelper.LaunchManualAsync(SelectedItem, PAGE_ROLE);
                menu.Items.Add(manual);
            }''')
replace(page, 'Loaded += (_, _) => ChangeFilteringPaneLayout();\n            UpdateSortingMenu();', '''Loaded += (_, _) => ChangeFilteringPaneLayout();
            int savedSorter = Settings.GetDictionaryItem<string, int>(Settings.K.PackageListSortFieldIndex, PAGE_NAME);
            if (savedSorter > 0 && Enum.IsDefined(typeof(ObservablePackageCollection.Sorter), savedSorter))
                FilteredPackages.SetSorter((ObservablePackageCollection.Sorter)savedSorter);
            FilteredPackages.Descending = Settings.GetDictionaryItem<string, bool>(Settings.K.PackageListSortDescending, PAGE_NAME);
            UpdateSortingMenu();''')
replace(page, 'FilteredPackages.Sort();\n            UpdateSortingMenu();', '''FilteredPackages.Sort();
            Settings.SetDictionaryItem(Settings.K.PackageListSortFieldIndex, PAGE_NAME, (int)FilteredPackages.CurrentSorter);
            Settings.SetDictionaryItem(Settings.K.PackageListSortDescending, PAGE_NAME, FilteredPackages.Descending);
            UpdateSortingMenu();''', 2)
replace(UI + 'Pages/SoftwarePages/AbstractPackagesPage.xaml', 'IsDynamicOverflowEnabled="False"', 'IsDynamicOverflowEnabled="True"')
replace(UI + 'Pages/SoftwarePages/AbstractPackagesPage.xaml', 'OverflowButtonVisibility="Collapsed"', 'OverflowButtonVisibility="Auto"')

options = UI + 'Pages/DialogPages/InstallOptions_Package.xaml.cs'
add_usings(options, 'UniGetUI.PackageEngine.Classes.Packages.Classes')
replace(options, 'AutoUpdatePackageCheckbox.IsChecked = Options.AutoUpdatePackage;', 'AutoUpdatePackageCheckbox.IsChecked = AutoUpdatesDatabase.IsAutoUpdated(Package);')
replace(options, 'SkipMinorUpdatesCheckbox.IsChecked = Options.SkipMinorUpdates;', 'SkipMinorUpdatesCheckbox.IsChecked = Options.SkipMinorUpdates;\n            SkipMinorUpdatesLevel.Value = Math.Clamp(Options.SkipMinorUpdatesLevel, 2, 4);\n            SkipMinorUpdatesLevel.IsEnabled = SkipMinorUpdatesCheckbox.IsChecked ?? false;')
replace(options, 'options.AutoUpdatePackage = AutoUpdatePackageCheckbox.IsChecked ?? false;', '// Automatic-update membership is saved separately, never while generating a command preview.')
replace(options, 'options.SkipMinorUpdates = SkipMinorUpdatesCheckbox?.IsChecked ?? false;', 'options.SkipMinorUpdates = SkipMinorUpdatesCheckbox?.IsChecked ?? false;\n            options.SkipMinorUpdatesLevel = double.IsFinite(SkipMinorUpdatesLevel.Value) ? Math.Clamp((int)SkipMinorUpdatesLevel.Value, 2, 4) : 2;')
replace(options, 'foreach (var p in ProcessesToKill)\n                options.KillBeforeOperation.Add(p.Name);', '''foreach (var p in ProcessesToKill)
                options.KillBeforeOperation.Add(p.Name);
            string pendingProcess = KillProcessesBox.Text?.Trim() ?? "";
            if (pendingProcess.Length > 0 && !options.KillBeforeOperation.Contains(pendingProcess, StringComparer.OrdinalIgnoreCase))
                options.KillBeforeOperation.Add(pendingProcess);''')
replace(options, 'if (updateDetachedOptions)\n            {', '''if (updateDetachedOptions)
            {
                string id = AutoUpdatesDatabase.GetIdForPackage(Package);
                if (AutoUpdatePackageCheckbox.IsChecked is true) AutoUpdatesDatabase.Add(id);
                else if (AutoUpdatesDatabase.IsAutoUpdated(id)) AutoUpdatesDatabase.Remove(id);''')
replace(UI + 'Pages/DialogPages/InstallOptions_Package.xaml', '<CheckBox Name="SkipMinorUpdatesCheckbox">', '<CheckBox Name="SkipMinorUpdatesCheckbox" Checked="SkipMinorUpdatesCheckbox_Changed" Unchecked="SkipMinorUpdatesCheckbox_Changed">')
replace(options, '        private void CloseButton_Click(object sender, RoutedEventArgs e)', '''        private void SkipMinorUpdatesCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            if (SkipMinorUpdatesLevel is not null)
                SkipMinorUpdatesLevel.IsEnabled = SkipMinorUpdatesCheckbox.IsChecked ?? false;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)''')
replace(UI + 'Pages/DialogPages/InstallOptions_Package.xaml', '<CheckBox Name="AutoUpdatePackageCheckbox">', '''<NumberBox Name="SkipMinorUpdatesLevel" Minimum="2" Maximum="4" SmallChange="1"
                        Value="2" SpinButtonPlacementMode="Compact" Width="110"
                        IsEnabled="False"
                        ToolTipService.ToolTip="Ignore changes from this version component onward (2 = minor, 3 = patch, 4 = revision)" />
                      <CheckBox Name="AutoUpdatePackageCheckbox">''')

# Ensure the native frontend deploys the same operation shim and utilities as its shared engine.
shared_assets = ROOT / 'src/SharedAssets/Assets'
if shared_assets.exists():
    shutil.copytree(shared_assets, ROOT / UI / 'Assets', dirs_exist_ok=True)
project = UI + 'UniGetUI.csproj'
replace(project, '<Content Update="Assets\\Utilities\\uninstall_scoop.cmd">', '''<Content Include="Assets\\Utilities\\unigetui_ps_operation.ps1">
            <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
            <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
        </Content>
        <Content Update="Assets\\Utilities\\uninstall_scoop.cmd">''')

# Settings integration is separate from generated XAML, keeping existing cards and navigation.
for name in ['SettingsHomepage', 'Operations', 'Updates', 'Backup', 'Interface_P', 'Experimental']:
    path = UI + f'Pages/SettingsPages/GeneralPages/{name}.xaml.cs'
    text = read(path)
    text = text.replace('public event EventHandler<Type>? NavigationRequested\n        {\n            add { }\n            remove { }\n        }', 'public event EventHandler<Type>? NavigationRequested;')
    text = text.replace('this.InitializeComponent();', 'this.InitializeComponent();\n            FeatureSettings.Attach(this, Scroller, page => NavigationRequested?.Invoke(this, page));', 1)
    write(path, text)

# Checkbox initialization must never persist before XAML has assigned ForceInversion.
checkbox = UI + 'Controls/SettingsWidgets/CheckboxCard.cs'
replace(checkbox, 'protected bool IS_INVERTED;', 'protected bool IS_INVERTED;\n        protected bool SkipPersistChanges;')
replace(checkbox, '_checkbox.IsOn = Settings.Get(setting_name) ^ IS_INVERTED ^ ForceInversion;', 'SkipPersistChanges = true;\n                _checkbox.IsOn = Settings.Get(setting_name) ^ IS_INVERTED ^ ForceInversion;\n                SkipPersistChanges = false;')
replace(checkbox, 'public bool ForceInversion { get; set; }', '''private bool _forceInversion;
        public bool ForceInversion
        {
            get => _forceInversion;
            set
            {
                if (_forceInversion == value) return;
                _forceInversion = value;
                SkipPersistChanges = true;
                _checkbox.IsOn = !_checkbox.IsOn;
                _textblock.Opacity = _checkbox.IsOn ? 1 : 0.7;
                SkipPersistChanges = false;
            }
        }''')
replace(checkbox, 'Settings.Set(setting_name, _checkbox.IsOn ^ IS_INVERTED ^ ForceInversion);', 'if (SkipPersistChanges || setting_name is Settings.K.Unset) return;\n            Settings.Set(setting_name, _checkbox.IsOn ^ IS_INVERTED ^ ForceInversion);')
replace(checkbox, 'if (_disableStateChangedEvent)\n                return;', 'if (_disableStateChangedEvent || SkipPersistChanges || _dictName is Settings.K.Unset || _keyName.Length == 0)\n                return;')
text = read(checkbox)
old = '''                    _checkbox.IsOn =
                        Settings.GetDictionaryItem<string, bool>(_dictName, _keyName)'''
text = text.replace(old, '''                    SkipPersistChanges = true;
                    _checkbox.IsOn =
                        Settings.GetDictionaryItem<string, bool>(_dictName, _keyName)''')
text = text.replace('                    _textblock.Opacity = _checkbox.IsOn ? 1 : 0.7;', '                    SkipPersistChanges = false;\n                    _textblock.Opacity = _checkbox.IsOn ? 1 : 0.7;')
write(checkbox, text)

# Additional narrowly scoped adaptations can be tested independently without rewriting this file.
for script in sorted(Path(__file__).parent.glob('adapt_*.py')):
    subprocess.run([sys.executable, str(script), str(ROOT), UPSTREAM], check=True)
