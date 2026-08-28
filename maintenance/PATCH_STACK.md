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

The patch stack is a source reconstruction mechanism. Existing .NET tests,
Classic guard checks, and CLI E2E remain the behavioral validation gates; this
stack check proves that those gates are running against the same source tree that
was recorded for the Classic snapshot.
