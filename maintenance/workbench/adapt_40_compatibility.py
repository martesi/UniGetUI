from pathlib import Path
import sys

root = Path(sys.argv[1])
ui = root / 'src/UniGetUI'


def replace(path, old, new, count=1):
    text = path.read_text(encoding='utf-8-sig')
    if text.count(old) != count:
        raise ValueError(f'{path}: expected {count} occurrences of {old!r}, got {text.count(old)}')
    path.write_text(text.replace(old, new), encoding='utf-8', newline='\n')


replace(ui / 'CLIHandler.cs', 'SharedPreUiCommandDispatcher.WinUiExitCodes', 'SharedPreUiCommandDispatcher.WindowsCliExitCodes', 9)
replace(ui / 'EntryPoint.cs', 'SharedPreUiCommandDispatcher.WinUiExitCodes', 'SharedPreUiCommandDispatcher.WindowsCliExitCodes')
settings = root / 'src/UniGetUI.Core.Settings/SettingsEngine_Names.cs'
replace(settings, '        EnableUniGetUIBeta,', '        EnableUniGetUIBeta,\n        DisableClassicMode,\n        UseClassicMode,')
replace(settings, '            K.EnableUniGetUIBeta => "EnableUniGetUIBeta",', '            K.EnableUniGetUIBeta => "EnableUniGetUIBeta",\n            K.DisableClassicMode => "DisableClassicMode",\n            K.UseClassicMode => "UseClassicMode",')
updates = ui / 'Pages/SoftwarePages/SoftwareUpdatesPage.cs'
replace(updates, 'await MainApp.Operations.Update(targets);', 'foreach (var package in targets) await MainApp.Operations.Update(package);')
replace(updates, 'await MainApp.Operations.Update(upgradablePackages);', 'foreach (var package in upgradablePackages) await MainApp.Operations.Update(package);')
