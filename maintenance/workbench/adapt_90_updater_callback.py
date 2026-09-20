from pathlib import Path
import sys

path = Path(sys.argv[1]) / 'src/UniGetUI/AutoUpdater.cs'
text = path.read_text(encoding='utf-8-sig')
old = ', LogUpdateWarn)'
if text.count(old) != 8:
    raise ValueError(f'Expected eight updater callbacks, got {text.count(old)}')
path.write_text(text.replace(old, ', message => LogUpdateWarn(message))'), encoding='utf-8', newline='\n')
