from pathlib import Path
import sys

root = Path(sys.argv[1])
ui = root / 'src/UniGetUI'


def replace(path, old, new, count=1):
    text = path.read_text(encoding='utf-8-sig')
    if text.count(old) != count:
        raise ValueError(f'{path}: expected {count} occurrences of {old!r}, got {text.count(old)}')
    path.write_text(text.replace(old, new), encoding='utf-8', newline='\n')


replace(ui / 'Pages/SoftwarePages/AbstractPackagesPage.xaml.cs', '            FilteredPackages.Query = "";\n', '')
# Sanitize protocol launches before any privileged CLI action, just as upstream does.
replace(ui / 'EntryPoint.cs', '                if (ShouldPrepareCliConsole(args))', '''                args = SharedPreUiCommandDispatcher.IgnoreArgumentsInjectedIntoProtocolLaunch(args);
                if (ShouldPrepareCliConsole(args))''')
