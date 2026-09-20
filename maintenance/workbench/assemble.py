"""Temporary source workbench; never part of the Classic release patch tree."""
from pathlib import Path
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

CONTROL = Path(__file__).resolve().parents[2]
SOURCE = Path(sys.argv[1]).resolve()
UPSTREAM = '5e8b14e102780e05a72dd79afc9e20f584da8d12'
BASE = '68411409d001b9f34cfb896af1543a1d7067ca5e'
CLASSIC = 'b5efda4c9918b5585c08ec419ed57b7c3fd8a11f'


def git(*args, cwd=SOURCE):
    return subprocess.check_output(['git', '-C', str(cwd), *args])


def run(*args, cwd=SOURCE):
    subprocess.run(args, cwd=cwd, check=True)


def tree(ref):
    entries = {}
    for entry in git('ls-tree', '-r', '-z', ref).split(b'\0'):
        if entry:
            meta, path = entry.split(b'\t', 1)
            mode, kind, sha = meta.decode().split()
            if kind == 'blob':
                entries[path.decode()] = (mode, sha)
    return entries


def replace(path, old, new, count=1):
    target = SOURCE / path
    content = target.read_text(encoding='utf-8-sig')
    actual = content.count(old)
    if actual != count:
        raise ValueError(f'{path}: expected {count} occurrences of {old!r}, got {actual}')
    target.write_text(content.replace(old, new), encoding='utf-8', newline='\n')


run('git', 'fetch', '--no-tags', 'https://github.com/Devolutions/UniGetUI.git', UPSTREAM, cwd=CONTROL)
run('git', 'worktree', 'add', '--detach', str(SOURCE), CLASSIC, cwd=CONTROL)
run('git', 'config', 'core.autocrlf', 'false')
run('git', 'config', 'user.name', 'Classic backport workbench')
run('git', 'config', 'user.email', '96113508+martesi@users.noreply.github.com')
base = tree(BASE)
classic = tree(CLASSIC)
upstream = tree(UPSTREAM)
retained = {
    'src/UniGetUI.Windows.slnx',
    'src/Directory.Build.targets',
    'src/UniGetUI.Tests/UniGetUI.Tests.csproj',
    'src/UniGetUI.Tests/ModernAppLauncherTests.cs',
    'src/UniGetUI.Tests/AutoUpdaterTests.cs',
}


def shared(path):
    return path.startswith('src/') and not path.startswith('src/UniGetUI/') and path not in retained


# Remove retired shared implementations, but never upstream's deleted WinUI frontend.
for path in base.keys() - upstream.keys():
    if shared(path) and (SOURCE / path).is_file():
        (SOURCE / path).unlink()

# Merge only actual downstream edits. The current upstream implementation resolves
# overlaps except product identity; clean downstream-only additions stay intact.
with tempfile.TemporaryDirectory() as temporary:
    temp = Path(temporary)
    for path, (mode, sha) in upstream.items():
        if not shared(path):
            continue
        content = git('cat-file', 'blob', sha)
        target = SOURCE / path
        if path in base and path in classic and classic[path][1] != base[path][1] and classic[path][1] != sha and b'\0' not in content:
            ours = git('cat-file', 'blob', classic[path][1])
            ancestor = git('cat-file', 'blob', base[path][1])
            for name, data in [('ours', ours), ('base', ancestor), ('theirs', content)]:
                (temp / name).write_bytes(data)
            result = subprocess.run(['git', 'merge-file', '-p', str(temp / 'ours'), str(temp / 'base'), str(temp / 'theirs')], capture_output=True)
            if result.returncode not in range(0, 128):
                raise RuntimeError(result.stderr.decode(errors='replace'))
            merged = result.stdout
            if result.returncode:
                if path == 'src/UniGetUI.Core.Data/CoreData.cs':
                    merged = re.sub(rb'^<<<<<<<[^\n]*\n(.*?)^=======\n.*?^>>>>>>>[^\n]*\n', rb'\1', merged, flags=re.M | re.S)
                elif path == 'src/UniGetUI.Core.Tools/Tools.cs':
                    merged = re.sub(rb'^<<<<<<<[^\n]*\n.*?^=======\n(.*?)^>>>>>>>[^\n]*\n', rb'\1', merged, flags=re.M | re.S)
                else:
                    merged = content
            content = merged
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content)
        if mode == '100755':
            target.chmod(0o755)

# New source tools and deployment scripts are used by upstream project references.
for path, (mode, sha) in upstream.items():
    if path.startswith('scripts/') and (path not in base or upstream[path] != base[path]):
        if path in classic and path in base and classic[path] != base[path]:
            continue
        target = SOURCE / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(git('cat-file', 'blob', sha))
        if mode == '100755':
            target.chmod(0o755)

# A per-user directory must not silently rejoin the upstream application's live data.
replace('src/UniGetUI.Core.Data/CoreData.cs', 'TEST_PerUserDataDirectoryOverride ?? Path.Join(GetLocalDataRoot(), "UniGetUI")', 'TEST_PerUserDataDirectoryOverride ?? Path.Join(GetLocalDataRoot(), "UniGetUIClassic")')
project = SOURCE / 'src/UniGetUI.PackageEngine.Managers.WinGet/UniGetUI.PackageEngine.Managers.WinGet.csproj'
text = project.read_text()
if 'SQLitePCLRaw.bundle_e_sqlite3' not in text:
    text = text.replace('<PackageReference Include="Devolutions.Pinget.Core"', '<PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" Version="2.1.13" />\n        <PackageReference Include="Devolutions.Pinget.Core"')
project.write_text(text, encoding='utf-8', newline='\n')

# Keep native test seams while compiling newly shared command/startup helpers.
replace('src/UniGetUI.Tests/UniGetUI.Tests.csproj', '<Compile Include="..\\Shared\\SharedPreUiCommandDispatcher.cs" Link="Shared\\SharedPreUiCommandDispatcher.cs" />', '<Compile Include="..\\Shared\\SharedPreUiCommandDispatcher.cs" Link="Shared\\SharedPreUiCommandDispatcher.cs" />\n        <Compile Include="..\\Shared\\StartupBundleArguments.cs" Link="Shared\\StartupBundleArguments.cs" />\n        <Compile Include="..\\Shared\\AutoUpdater.InstallerArguments.cs" Link="Shared\\AutoUpdater.InstallerArguments.cs" />')
replace('src/UniGetUI/UniGetUI.csproj', '<Compile Include="..\\Shared\\SharedPreUiCommandDispatcher.cs" Link="Shared\\SharedPreUiCommandDispatcher.cs" />', '<Compile Include="..\\Shared\\SharedPreUiCommandDispatcher.cs" Link="Shared\\SharedPreUiCommandDispatcher.cs" />\n        <Compile Include="..\\Shared\\StartupBundleArguments.cs" Link="Shared\\StartupBundleArguments.cs" />\n        <Compile Include="..\\Shared\\AutoUpdater.InstallerArguments.cs" Link="Shared\\AutoUpdater.InstallerArguments.cs" />')

run('git', 'add', '-A')
run('git', 'commit', '-m', 'backport: synchronize shared engine, managers, security and regression tests\n\nUpstream-Commit: ' + UPSTREAM + '\nClassic-Source-Commit: ' + CLASSIC)

# A separate adapter script owns WinUI behavior, without contaminating source with
# the temporary generation harness or changing the final PR to direct-source work.
adapter = CONTROL / 'maintenance/workbench/adapt.py'
if adapter.exists():
    run(sys.executable, str(adapter), str(SOURCE), UPSTREAM)
overlay = CONTROL / 'maintenance/workbench/overlay'
if overlay.exists():
    shutil.copytree(overlay, SOURCE, dirs_exist_ok=True)
if git('status', '--porcelain'):
    run('git', 'add', '-A')
    run('git', 'commit', '-m', 'backport: adapt current package-management features to Classic WinUI\n\nUpstream-Commit: ' + UPSTREAM + '\nClassic-Source-Commit: ' + CLASSIC)

output = CONTROL / 'parity-artifacts'
output.mkdir(exist_ok=True)
(output / 'source.json').write_text(json.dumps({'source_commit': git('rev-parse', 'HEAD').decode().strip(), 'source_tree': git('rev-parse', 'HEAD^{tree}').decode().strip(), 'upstream_commit': UPSTREAM, 'previous_source_commit': CLASSIC}, indent=2))
(output / 'source.diff').write_bytes(git('diff', '--binary', CLASSIC, 'HEAD'))
(output / 'changes.txt').write_bytes(git('diff', '--stat', CLASSIC, 'HEAD'))
(output / 'engine-and-ui.patch').write_bytes(git('format-patch', '--stdout', '--binary', CLASSIC + '..HEAD'))
(output / 'upstream-commits.txt').write_bytes(git('log', '--reverse', '--format=%H %s', BASE + '..' + UPSTREAM))
run('git', 'push', 'origin', 'HEAD:refs/heads/classic-feature-parity-source-' + os.environ['GITHUB_RUN_ID'])
print('Prepared source:', git('rev-parse', 'HEAD').decode().strip())
