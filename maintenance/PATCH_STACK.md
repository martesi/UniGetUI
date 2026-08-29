# Classic patch stack

Stage 1 records the current Classic line as an ordered downstream patch stack over
the exact upstream base in `maintenance/upstream-base.json`.

The stack is applied with `git am --3way --keep-cr --ignore-whitespace`. The seven upstream backports retain
their upstream commit and PR trailers. The Classic patches retain the source
commit(s) from which the current Classic tree was reconstructed. The 0170 patch
is the small compatibility layer needed to preserve Classic's existing WinUI-era
anchors and regression fixtures; it does not change product behavior.

The target tree for this snapshot is recorded in
`maintenance/patch-stack.json` as `classic_source_commit`. Verification compares
the replayed Git tree with that commit, so the check covers the complete current
Classic source/build tree rather than only a selected file list.

## Commands

From the repository root:

```powershell
pwsh ./scripts/apply-patch-stack.ps1
pwsh ./scripts/verify-patch-stack.ps1
```

The apply script creates a temporary linked worktree at the requested upstream
base and leaves the current checkout untouched. Pass `-KeepWorktree` to inspect
the replay, or `-WorktreePath` to choose a worktree that the caller will retain.
The verify script removes its temporary replay worktree after comparing the
result. The keep-cr option preserves the mixed CRLF/LF blob used by the preserved
Inno Setup source. The ignore-whitespace option lets that patch match on either
platform; it does not discard added or removed source lines.

## Release source

Stage 2 makes the patch stack the source used for Classic releases. The Classic
release workflow checks out full repository history, replays the ordered patch
series into a clean worktree, and compares that worktree's Git tree with the
recorded `classic_source_commit` tree before any release mutation occurs.

Version stamping, restore, tests, WinUI publish, integrity-tree refresh, and Inno
Setup packaging then run inside that verified replay worktree. The maintenance
checkout is used only to locate patch metadata and drive reconstruction; release
binaries are not built from the checked-out `main` source tree.

Existing .NET tests, Classic guard checks, and CLI E2E remain behavioral
validation gates. Patch-stack equivalence proves that those gates and the release
pipeline can operate on the same recorded Classic source tree.
