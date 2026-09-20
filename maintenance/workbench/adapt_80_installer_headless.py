from pathlib import Path
import sys

root = Path(sys.argv[1])


def replace(path, old, new, count=1):
    text = path.read_text(encoding='utf-8-sig')
    if text.count(old) != count:
        raise ValueError(f'{path}: expected {count} occurrences of {old!r}, got {text.count(old)}')
    path.write_text(text.replace(old, new), encoding='utf-8', newline='\n')


installer = root / 'UniGetUI.iss'
replace(installer, '[Setup]\n', '#ifndef InstallerCompression\n#define InstallerCompression "lzma"\n#endif\n\n[Setup]\n')
replace(installer, 'Compression=lzma', 'Compression={#InstallerCompression}')
replace(installer, "if not CmdLineParamExists('/NoVCRedist') then", "if not (CmdLineParamExists('/NoVCRedist') or CmdLineParamExists('/MSStore')) then")
replace(installer, "if not CmdLineParamExists('/NoEdgeWebView') then", "if not (CmdLineParamExists('/NoEdgeWebView') or CmdLineParamExists('/MSStore')) then")
replace(installer, "Check: not CmdLineParamExists('/NoAutoStart');", "Check: not (CmdLineParamExists('/NoAutoStart') or CmdLineParamExists('/MSStore'));")
replace(installer, "Check: CmdLineParamExists('/NoRunOnStartup') or PreserveAutostartDisabled;", "Check: CmdLineParamExists('/NoRunOnStartup') or CmdLineParamExists('/MSStore') or PreserveAutostartDisabled;")
replace(root / 'src/UniGetUI/WinUiHeadlessHost.cs', '''                MainApp.LoadGSudoAsync()
            );''', '''                MainApp.LoadGSudoAsync()
            );
            UniGetUI.Services.MaintenanceScheduler.StartHeadless();''')
