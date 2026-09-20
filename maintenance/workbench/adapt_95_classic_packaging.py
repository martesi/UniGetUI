from pathlib import Path
import sys

root = Path(sys.argv[1])


def replace(path, old, new, count=1):
    text = path.read_text(encoding='utf-8-sig')
    if text.count(old) != count:
        raise ValueError(f'{path}: expected {count} occurrences of {old!r}, got {text.count(old)}')
    path.write_text(text.replace(old, new), encoding='utf-8', newline='\n')


build = root / 'scripts/build.ps1'
replace(build, 'Join-Path $SrcDir "UniGetUI.Avalonia" "UniGetUI.Avalonia.csproj"', 'Join-Path $SrcDir "UniGetUI" "UniGetUI.csproj"')
replace(build, 'Join-Path $SrcDir "UniGetUI.Avalonia" "bin"', 'Join-Path $SrcDir "UniGetUI" "bin"')
replace(build, 'dotnet publish Avalonia failed', 'dotnet publish Classic WinUI failed')
replace(build, '"UniGetUI.$Platform.zip"', '"UniGetUI.Classic.$Platform.zip"')
replace(build, '"UniGetUI.Installer.$Platform"', '"UniGetUI.Classic.Installer.$Platform"')
installer = root / 'UniGetUI.iss'
replace(installer, 'function InitializeSetup: Boolean;\nbegin', '''function ShouldSuppressRunOnStartup: Boolean;
begin
  Result := CmdLineParamExists('/NoRunOnStartup') or
    CmdLineParamExists('/MSStore') or PreserveAutostartDisabled;
end;

function InitializeSetup: Boolean;
begin''')
replace(installer, "Check: CmdLineParamExists('/NoRunOnStartup') or CmdLineParamExists('/MSStore') or PreserveAutostartDisabled;", 'Check: ShouldSuppressRunOnStartup;')
