from pathlib import Path
import json
import os
import subprocess
import sys
import xml.etree.ElementTree as ET

root = Path(sys.argv[1])
ref = sys.argv[2]
ui = root / 'src/UniGetUI'


def upstream(path):
    if folder := os.environ.get('PARITY_UPSTREAM_DIR'):
        return (Path(folder) / path).read_text(encoding='utf-8-sig')
    return subprocess.check_output(['git', '-C', str(root), 'show', f'{ref}:{path}']).decode('utf-8-sig')


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding='utf-8', newline='\n')


def replace(path, old, new, count=1):
    text = path.read_text(encoding='utf-8-sig')
    if text.count(old) != count:
        raise ValueError(f'{path}: expected {count} occurrences of {old!r}; got {text.count(old)}')
    write(path, text.replace(old, new))


# These helpers were backported earlier into a different location. A textual
# three-way merge cannot recognize the duplicate methods; use the unified source.
path = 'src/UniGetUI.PackageEngine.Managers.Generic.NuGet/BaseNuGet.cs'
write(root / path, upstream(path))

write(ui / 'Models/ManageShortcutsViewModel.cs', upstream('src/UniGetUI.Avalonia/ViewModels/DialogPages/ManageShortcutsViewModel.cs').replace('namespace UniGetUI.Avalonia.ViewModels;', 'namespace UniGetUI.Models;'))
write(ui / 'Services/OperationHistoryActionService.cs', upstream('src/UniGetUI.Avalonia/Infrastructure/OperationHistoryActionService.cs').replace('namespace UniGetUI.Avalonia.Infrastructure;', 'namespace UniGetUI.Services;').replace('AvaloniaOperationRegistry.Add(', 'MainApp.Operations.Add('))
project = ui / 'UniGetUI.csproj'
replace(project, '<PackageReference Include="Devolutions.Pinget.Cli.Rust" Version="0.9.0"', '<PackageReference Include="Devolutions.Pinget.Cli.Rust" Version="0.12.0"')
replace(project, '<PackageReference Include="CommunityToolkit.Common"', '<PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />\n        <PackageReference Include="CommunityToolkit.Common"')

admin = ui / 'Pages/SettingsPages/GeneralPages/Administrator.xaml.cs'
replace(admin, 'this.InitializeComponent();', 'this.InitializeComponent();\n            FeatureSettings.Attach(this, Scroller, _ => { });')
main = ui / 'Pages/MainView.xaml.cs'
replace(main, 'InitializeComponent();', 'InitializeComponent();\n            UniGetUI.Pages.SettingsPages.FeatureSettings.ApplyNavigationMode(NavView);')

# Index the actual native cards rather than maintaining a second settings model.
entries = set()
for path in (ui / 'Pages/SettingsPages/GeneralPages').glob('*.xaml'):
    tree = ET.parse(path)
    page = tree.getroot().attrib.get('{http://schemas.microsoft.com/winfx/2006/xaml}Class')
    if not page:
        continue
    for element in tree.iter():
        label = element.attrib.get('Text') or element.attrib.get('CheckboxText')
        key = element.attrib.get('SettingName') or element.attrib.get('DictName')
        if label and key and not label.startswith('{'):
            entries.add((page, label, key))
for page, label, key in [
    ('GeneralPages.Scheduler', 'Scheduled maintenance', 'MaintenanceSchedules'),
    ('GeneralPages.Scheduler', 'Manage automatic updates', 'AutoUpdates'),
    ('GeneralPages.Operations', 'Default installer download directory', 'DefaultInstallerDownloadDirectory'),
    ('GeneralPages.Operations', 'Installer filenames', 'InstallerFileNameScheme'),
    ('GeneralPages.Operations', 'Manage Start Menu shortcuts', 'AskAboutNewStartMenuShortcuts'),
    ('GeneralPages.Operations', 'Expand environment variables written as %VAR% (instead of <VAR>)', 'ExpandEnvVarsWithPercentSyntax'),
    ('GeneralPages.Backup', 'Maximum local backups to keep', 'MaxLocalBackupCount'),
    ('GeneralPages.Interface_P', 'Navigation menu:', 'NavMenuMode'),
    ('GeneralPages.Interface_P', 'Show the installer host column', 'ShowInstallerHostColumn'),
    ('GeneralPages.Interface_P', 'Show the installer download size column', 'ShowDownloadSizeColumn'),
    ('GeneralPages.Administrator', 'Delegate package operations to the Devolutions Agent broker', 'UseAgentBroker'),
]:
    entries.add(('UniGetUI.Pages.SettingsPages.' + page, label, key))
lines = '\n'.join('        new(typeof(' + page + '), ' + json.dumps(label, ensure_ascii=True) + ', ' + json.dumps(key) + '),' for page, label, key in sorted(entries))
write(ui / 'Pages/SettingsPages/SettingsSearch.Index.cs', 'namespace UniGetUI.Pages.SettingsPages;\n\ninternal static partial class SettingsSearch\n{\n    private static readonly Entry[] Entries =\n    [\n' + lines + '\n    ];\n}\n')
base = ui / 'Pages/SettingsPages/SettingsBasePage.xaml.cs'
replace(base, 'SettingsTitle.Text = page.ShortTitle;', 'SettingsTitle.Text = page.ShortTitle;\n            if (e.Content is Page nativePage) SettingsSearch.Highlight(nativePage);')

# The shared engine now persists structured history, including failed operations.
operation = ui / 'Controls/OperationWidgets/OperationControl.cs'
text = operation.read_text(encoding='utf-8-sig')
start = text.index('        // Generate process output')
end = text.index('        // Handle UAC for batches', start)
write(operation, text[:start] + text[end:])
