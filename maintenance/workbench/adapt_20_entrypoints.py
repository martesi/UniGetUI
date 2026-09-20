from pathlib import Path
import sys

root = Path(sys.argv[1])
ui = root / 'src/UniGetUI'


def write(path, text):
    path.write_text(text, encoding='utf-8', newline='\n')


def replace(path, old, new, count=1):
    text = path.read_text(encoding='utf-8-sig')
    if text.count(old) != count:
        raise ValueError(f'{path}: expected {count} occurrences of {old!r}; got {text.count(old)}')
    write(path, text.replace(old, new))


# Raw process arguments can contain values injected by protocol activation. Keep every
# Classic UI consumer on the shared sanitized argument source.
for relative in [
    'App.xaml.cs',
    'CLIHandler.cs',
    'CrashHandler.cs',
    'EntryPoint.cs',
    'MainWindow.xaml.cs',
    'Pages/SoftwarePages/SoftwareUpdatesPage.cs',
]:
    path = ui / relative
    text = path.read_text(encoding='utf-8-sig')
    if 'Environment.GetCommandLineArgs()' not in text:
        raise ValueError(f'{path}: expected raw process argument reads')
    write(path, text.replace('Environment.GetCommandLineArgs()', 'CoreData.GetProcessArguments()'))


# Both existing menu entries and post-operation prompts open the same native editor.
dialogs = ui / 'Pages/DialogPages/DialogHelper_Generic.cs'
text = dialogs.read_text(encoding='utf-8-sig')
start = text.index('    public static async Task ManageDesktopShortcuts(')
end = text.index('    public static async Task HandleNewDesktopShortcuts()', start)
text = text[:start] + '''    public static Task ManageDesktopShortcuts(IReadOnlyList<string>? NewShortucts = null)
        => ManageShortcuts(UniGetUI.Models.ShortcutDialogScope.Desktop, NewShortucts);

''' + text[end:]
write(dialogs, text)
operation = ui / 'Controls/OperationWidgets/OperationControl.cs'
text = operation.read_text(encoding='utf-8-sig')
start = text.index('        // Handle newly created shortcuts')
end = text.index('\n    private async Task LoadIcon()', start)
text = text[:start] + '''        // Do not surface dialogs or activate the application while it is hidden.
        // Pending shortcuts are reviewed when the user next opens the interface.
        if (!MainApp.Operations.AreThereRunningOperations() && MainApp.Instance.MainWindow.AppWindow.IsVisible)
            _ = DialogHelper.HandleNewShortcuts();
    }
''' + text[end:]
text = text.replace('if (Settings.AreSuccessNotificationsDisabled())', 'if (MainApp.Instance.MainWindow.AppWindow.IsVisible || Settings.AreSuccessNotificationsDisabled())')
text = text.replace('if (Settings.AreErrorNotificationsDisabled())', 'if (MainApp.Instance.MainWindow.AppWindow.IsVisible || Settings.AreErrorNotificationsDisabled())')
write(operation, text)
window = ui / 'MainWindow.xaml.cs'
replace(window, 'AppWindow.Show();\n            Activate();', 'AppWindow.Show();\n            Activate();\n            if (!MainApp.Operations.AreThereRunningOperations()) _ = DialogHelper.HandleNewShortcuts();')

# Use native file dialogs with an explicit initial directory; retaining the WinRT
# Downloads enum would silently ignore a custom directory chosen in settings.
pickers = root / 'src/ExternalLibraries.FilePickers'
for name in ['FolderPicker', 'FileSavePicker']:
    path = pickers / (name + '.cs')
    replace(path, 'private readonly IntPtr _windowHandle;', 'private readonly IntPtr _windowHandle;\n\n    public string? InitialDirectory { get; set; }')
replace(pickers / 'FolderPicker.cs', 'FOS.FOS_PICKFOLDERS | FOS.FOS_FORCEFILESYSTEM);', 'FOS.FOS_PICKFOLDERS | FOS.FOS_FORCEFILESYSTEM, initialDirectory: InitialDirectory);')
replace(pickers / 'FileSavePicker.cs', 'typeFilters, defaultName);', 'typeFilters, defaultName, InitialDirectory);')
helper = pickers / 'Classes/Helper.cs'
replace(helper, 'List<string>? typeFilters = null)', 'List<string>? typeFilters = null, string? initialDirectory = null)')
replace(helper, 'string name = ""\n', 'string name = "",\n        string? initialDirectory = null\n')
replace(helper, 'dialog.SetOptions(fos);', 'dialog.SetOptions(fos);\n            SetInitialDirectory(dialog, initialDirectory);', 2)
replace(helper, 'return path.Contains(fileExtension) ? path : path + fileExtension;', 'return path.EndsWith(fileExtension, StringComparison.OrdinalIgnoreCase) ? path : path + fileExtension;')
replace(helper, 'internal static class Helper\n{', '''internal static class Helper
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    private static void SetInitialDirectory(IFileDialog dialog, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory)) return;
        Guid iid = typeof(IShellItem).GUID;
        IShellItem? folder = null;
        try
        {
            if (SHCreateItemFromParsingName(directory, IntPtr.Zero, ref iid, out folder) >= 0)
                dialog.SetFolder(folder);
        }
        finally
        {
            if (folder is not null) Marshal.ReleaseComObject(folder);
        }
    }
''')
operations = ui / 'AppOperationHelper.cs'
text = operations.read_text(encoding='utf-8-sig')
start = text.index('                FileSavePicker savePicker = new();')
end = text.index('                DialogHelper.HideLoadingDialog(loadingId);', start)
text = text[:start] + '''                string name = await package.GetInstallerFileName() ?? "installer.bin";
                string extension = Path.GetExtension(name);
                if (string.IsNullOrWhiteSpace(extension)) { extension = ".bin"; name += extension; }
                List<string> filters = [extension, ".exe", ".msi", ".zip", ".msix", ".appx", ".tar", ".tgz", ".nupkg"];
                var savePicker = new ExternalLibraries.Pickers.FileSavePicker(Instance.MainWindow.GetWindowHandle())
                {
                    InitialDirectory = InstallerDownloadLocation.ResolveStartDirectory(),
                };
                DialogHelper.HideLoadingDialog(loadingId);
                string filePath = savePicker.Show(filters.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), name);
''' + text[end:]
text = text.replace('if (file is not null)\n                {\n                    var op = new DownloadOperation(package, file.Path);', 'if (!string.IsNullOrEmpty(filePath))\n                {\n                    var op = new DownloadOperation(package, filePath);')
text = text.replace('new ExternalLibraries.Pickers.FolderPicker(hWnd);', 'new ExternalLibraries.Pickers.FolderPicker(hWnd) { InitialDirectory = InstallerDownloadLocation.ResolveStartDirectory() };')
text = text.replace('await Task.Run(picker.Show)', 'picker.Show()').replace('await Task.Run(a.Show)', 'a.Show()')
write(operations, text)

manager = ui / 'Pages/SettingsPages/ManagersPages/PackageManager.xaml.cs'
replace(manager, 'ExtraControls.Children.Add(WinGet_DownloadFullManifest);', '''ExtraControls.Children.Add(WinGet_DownloadFullManifest);
                NumberBox stuckThreshold = new() { Minimum = 1, Maximum = 1000, Value = int.TryParse(Settings.GetValue(Settings.K.WinGetStuckUpgradeThreshold), out int threshold) && threshold > 0 ? threshold : 3, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
                stuckThreshold.ValueChanged += (_, _) =>
                {
                    if (double.IsFinite(stuckThreshold.Value)) Settings.SetValue(Settings.K.WinGetStuckUpgradeThreshold, ((int)stuckThreshold.Value).ToString(System.Globalization.CultureInfo.InvariantCulture));
                };
                ExtraControls.Children.Add(new SettingsCard
                {
                    Header = CoreTools.Translate("Stop offering an update that never applies after this many attempts"),
                    Description = CoreTools.Translate("When WinGet keeps offering an update that reports success without ever changing the installed version, stop offering it after this many attempts, until a newer version is available (default: 3)"),
                    Content = stuckThreshold,
                });''')
replace(manager, 'SettingName = Settings.K.EnableScoopCleanup,\n                    Text = "Enable Scoop cleanup on launch",', 'SettingName = Settings.K.EnableScoopCleanupCache,\n                    Text = "Clear Scoop download cache on launch",')
replace(manager, 'ExtraControls.Children.Add(Scoop_CleanupOnStart);', '''ExtraControls.Children.Add(Scoop_CleanupOnStart);
                ExtraControls.Children.Add(new CheckboxCard
                {
                    SettingName = Settings.K.EnableScoopCleanupApps,
                    Text = "Clean up older Scoop app versions on launch",
                });''')
