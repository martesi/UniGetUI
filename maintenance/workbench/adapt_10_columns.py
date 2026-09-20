from pathlib import Path
import os
import subprocess
import sys

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
        raise ValueError(f'{path}: expected {count} occurrences of {old!r}, got {text.count(old)}')
    write(path, text.replace(old, new))


original = upstream('src/UniGetUI.Avalonia/Models/PackageCollections.cs')
fields = original[original.index('    private const string UnknownInstallerHost'):original.index('    private static Bitmap? GetCachedIcon')]
methods = original[original.index('    private int _installerHostLoadStarted;'):original.index('    /// <summary>\n    /// For upgradable WinGet packages')]
methods = methods.replace('_page.InstallerHostColumnVisible', 'Settings.Get(Settings.K.ShowInstallerHostColumn)').replace('_page.DownloadSizeColumnVisible', 'Settings.Get(Settings.K.ShowDownloadSizeColumn)')
methods = methods.replace('await Dispatcher.UIThread.InvokeAsync(', '_page.DispatcherQueue.TryEnqueue(').replace('Dispatcher.UIThread.CheckAccess()', '_page.DispatcherQueue.HasThreadAccess').replace('Dispatcher.UIThread.Post(', '_page.DispatcherQueue.TryEnqueue(')
write(ui / 'Controls/PackageWrapper.Columns.cs', '''using System.ComponentModel;
using Microsoft.UI.Xaml;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Managers.PipManager;
using UniGetUI.PackageEngine.Managers.ScoopManager;
using UniGetUI.PackageEngine.Managers.WingetManager;

namespace UniGetUI.PackageEngine.PackageClasses;

public partial class PackageWrapper
{
    public string InstallerHostText { get; private set; } = "";
    public string? InstallerHostTooltip { get; private set; }
    public string DownloadSizeText { get; private set; } = "";
    public long DownloadSizeBytes { get; private set; }
    public string? InstalledVersionTooltip => InstalledVersionNotice.BuildTooltip(Package) ?? Package.VersionString;
    public GridLength InstallerHostWidth => new(Settings.Get(Settings.K.ShowInstallerHostColumn) ? 140 : 0);
    public GridLength DownloadSizeWidth => new(Settings.Get(Settings.K.ShowDownloadSizeColumn) ? 100 : 0);
    private readonly CancellationTokenSource _lifetimeCts = new();

    public void RefreshColumns()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallerHostWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadSizeWidth)));
    }

''' + fields + methods + '\n}\n')
wrapper = ui / 'Controls/PackageWrapper.cs'
replace(wrapper, 'Package.PropertyChanged -= Package_PropertyChanged;', 'Package.PropertyChanged -= Package_PropertyChanged;\n            _lifetimeCts.Cancel();')
container = ui / 'Controls/PackageItemContainer.cs'
replace(container, 'public IPackage? Package { get; set; }', '''public IPackage? Package { get; set; }

        public PackageItemContainer()
        {
            Loaded += (_, _) => LoadRowValues();
        }

        private void LoadRowValues()
        {
            if (!IsLoaded || _wrapper is null) return;
            _wrapper.EnsureInstallerHostLoaded();
            _wrapper.EnsureDownloadSizeLoaded();
        }''')
replace(container, '_wrapper?.PropertyChanged += Wrapper_PropertyChanged;', '_wrapper?.PropertyChanged += Wrapper_PropertyChanged;\n                LoadRowValues();')
replace(container, 'if (e.PropertyName == nameof(PackageWrapper.IsChecked))', '''if (e.PropertyName is nameof(PackageWrapper.InstallerHostWidth) or nameof(PackageWrapper.DownloadSizeWidth))
                LoadRowValues();
            if (e.PropertyName == nameof(PackageWrapper.IsChecked))''')
page = ui / 'Pages/SoftwarePages/AbstractPackagesPage.xaml.cs'
replace(page, 'public void OnEnter()\n        {', '''public GridLength InstallerHostWidth => new(Settings.Get(Settings.K.ShowInstallerHostColumn) ? 140 : 0);
        public GridLength DownloadSizeWidth => new(Settings.Get(Settings.K.ShowDownloadSizeColumn) ? 100 : 0);
        public string InstallerHostHeaderText => CoreTools.Translate("Installer host");
        public string DownloadSizeHeaderText => CoreTools.Translate("Download size");

        public void OnEnter()
        {
            Bindings.Update();
            foreach (var wrapper in FilteredPackages) wrapper.RefreshColumns();''')
xaml = ui / 'Pages/SoftwarePages/AbstractPackagesPage.xaml'
replace(xaml, '<ColumnDefinition Width="*" MaxWidth="175" />', '''<ColumnDefinition Width="*" MaxWidth="175" />
            <ColumnDefinition Width="{x:Bind InstallerHostWidth, Mode=OneWay}" />
            <ColumnDefinition Width="{x:Bind DownloadSizeWidth, Mode=OneWay}" />''')
text = xaml.read_text()
end = text.index('        </Grid>\n      </widgets:PackageItemContainer>')
text = text[:end] + '''          <TextBlock Grid.Column="6" VerticalAlignment="Center" FontSize="13"
            Text="{x:Bind InstallerHostText, Mode=OneWay}" TextTrimming="CharacterEllipsis"
            ToolTipService.ToolTip="{x:Bind InstallerHostTooltip, Mode=OneWay}" />
          <TextBlock Grid.Column="7" VerticalAlignment="Center" FontSize="13"
            Text="{x:Bind DownloadSizeText, Mode=OneWay}" TextTrimming="CharacterEllipsis" />
''' + text[end:]
text = text.replace('ToolTipService.ToolTip="{x:Bind Package.VersionString}"', 'ToolTipService.ToolTip="{x:Bind InstalledVersionTooltip, Mode=OneWay}"')
text = text.replace('<ColumnDefinition Width="11" />', '''<ColumnDefinition Width="11" />
              <ColumnDefinition Width="{x:Bind InstallerHostWidth, Mode=OneWay}" />
              <ColumnDefinition Width="{x:Bind DownloadSizeWidth, Mode=OneWay}" />''', 1)
anchor = '          </Grid>\n\n          <Toolkit:SwitchPresenter'
if text.count(anchor) != 1:
    raise ValueError('Package table header anchor changed')
text = text.replace(anchor, '''            <TextBlock Grid.Column="13" Text="{x:Bind InstallerHostHeaderText}" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
            <TextBlock Grid.Column="14" Text="{x:Bind DownloadSizeHeaderText}" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
          </Grid>

          <Toolkit:SwitchPresenter''')
write(xaml, text)
