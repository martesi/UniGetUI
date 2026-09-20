"""Create the final maintenance-only branch after source and native UI validation."""
from pathlib import Path
import json
import os
import shutil
import subprocess
import sys
import zipfile

control = Path(__file__).resolve().parents[2]
source = Path(sys.argv[1]).resolve()
branch = 'classic-backport-feature-parity'
baseline = 'b5efda4c9918b5585c08ec419ed57b7c3fd8a11f'
upstream = '5e8b14e102780e05a72dd79afc9e20f584da8d12'
output = control / 'parity-artifacts'
worktree = control.parent / 'parity-patches'


def git(*args, cwd=control):
    return subprocess.check_output(['git', '-C', str(cwd), *args])


def run(*args, cwd=control):
    subprocess.run(args, cwd=cwd, check=True)


head = git('rev-parse', 'HEAD', cwd=source).decode().strip()
if git('status', '--porcelain', cwd=source):
    raise RuntimeError('Refusing to package a source tree with uncommitted changes')
run('git', 'fetch', 'origin', 'main')
remote = subprocess.run(['git', '-C', str(control), 'ls-remote', '--exit-code', '--heads', 'origin', branch], capture_output=True)
if remote.returncode == 0:
    run('git', 'fetch', 'origin', branch)
    parent = remote.stdout.decode().split()[0]
else:
    parent = git('rev-parse', 'origin/main').decode().strip()
run('git', 'worktree', 'add', '--detach', str(worktree), parent)
run('git', 'config', 'user.name', 'Classic backport workbench', cwd=worktree)
run('git', 'config', 'user.email', '96113508+martesi@users.noreply.github.com', cwd=worktree)

# Retain the original reviewed stack, including PR #16's three additions.
shutil.copytree(control / 'maintenance/patches', worktree / 'maintenance/patches', dirs_exist_ok=True)
series = (control / 'maintenance/patches/series').read_text().splitlines()
commits = git('rev-list', '--reverse', baseline + '..' + head, cwd=source).decode().splitlines()
for index, commit in enumerate(commits, start=30):
    name = f'backports/{index:03d}0-feature-parity-' + ('engine' if index == 30 else 'winui') + '.patch'
    data = git('format-patch', '-1', '--stdout', '--binary', commit, cwd=source)
    (worktree / 'maintenance/patches' / name).write_bytes(data)
    series.append(name)
(worktree / 'maintenance/patches/series').write_text('\n'.join(series) + '\n', encoding='utf-8', newline='\n')
metadata = json.loads((control / 'maintenance/patch-stack.json').read_text())
metadata['classic_source_commit'] = head
(worktree / 'maintenance/patch-stack.json').write_text(json.dumps(metadata, indent=2) + '\n', encoding='utf-8', newline='\n')

# Preserve the historic audit, but explicitly supersede its UI exclusions.
ledger = (control / 'maintenance/backports.yml').read_text()
ledger += '\nfeature_parity:\n'
ledger += f'  upstream_commit: {upstream}\n  classic_source_commit: {head}\n'
ledger += '  supersedes: "Earlier partial/skipped frontend notes above; see FEATURE_PARITY.md for native equivalents and platform-specific exclusions."\n'
ledger += '  report: maintenance/FEATURE_PARITY.md\n'
ledger += f'  validation_run: https://github.com/martesi/UniGetUI/actions/runs/{os.environ["GITHUB_RUN_ID"]}\n'
(worktree / 'maintenance/backports.yml').write_text(ledger, encoding='utf-8', newline='\n')
report = control / 'maintenance/workbench/FEATURE_PARITY.md'
if not report.exists():
    raise RuntimeError('Feature coverage report is required before creating the final branch')
text = report.read_text().replace('@SOURCE_COMMIT@', head).replace('@RUN_ID@', os.environ['GITHUB_RUN_ID'])
(worktree / 'maintenance/FEATURE_PARITY.md').write_text(text, encoding='utf-8', newline='\n')

# Replay all patches, not merely the two newest patches, and compare the whole tree.
with (output / 'patch-replay.log').open('w', encoding='utf-8') as log:
    result = subprocess.run(['pwsh', '-NoProfile', '-File', str(worktree / 'scripts/verify-patch-stack.ps1'), '-RepositoryRoot', str(worktree)], stdout=log, stderr=subprocess.STDOUT)
if result.returncode:
    raise RuntimeError('Full patch replay failed; see patch-replay.log')
changed = git('diff', '--name-only', cwd=worktree).decode().splitlines()
untracked = git('ls-files', '--others', '--exclude-standard', cwd=worktree).decode().splitlines()
for path in changed + untracked:
    if not path.startswith('maintenance/') or path.startswith('maintenance/workbench/'):
        raise RuntimeError('Final branch contains a non-maintenance change: ' + path)
run('git', 'add', 'maintenance', cwd=worktree)
run('git', 'commit', '-m', 'backport: current engine and complete native Classic feature surface\n\nUpstream-Commit: ' + upstream + '\nClassic-Source-Commit: ' + head, cwd=worktree)
run('git', 'push', 'origin', 'HEAD:refs/heads/' + branch, cwd=worktree)
record = {'branch': branch, 'commit': git('rev-parse', 'HEAD', cwd=worktree).decode().strip(), 'source_commit': head, 'source_tree': git('rev-parse', 'HEAD^{tree}', cwd=source).decode().strip(), 'upstream_commit': upstream, 'validation_run': os.environ['GITHUB_RUN_ID']}
(output / 'patch-branch.json').write_text(json.dumps(record, indent=2))
with zipfile.ZipFile(output / 'final-patch-stack.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in (worktree / 'maintenance').rglob('*'):
        if path.is_file():
            archive.write(path, path.relative_to(worktree))
print(json.dumps(record, indent=2))
