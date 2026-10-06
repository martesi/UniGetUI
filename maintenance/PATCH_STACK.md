# Classic patch stack

Classic releases replay `maintenance/patches/series` over the exact upstream
base in `maintenance/upstream-base.json`. The expected source tree is
`classic_source_commit` in `maintenance/patch-stack.json`. Verification compares
the complete Git trees.

Patch files retain `Upstream-Commit:` and `Upstream-PR:` provenance for imports,
or `Classic-Source-Commit:` for downstream changes. The series records applied
changes; the decisions below explain exceptions. Maintenance rules are in
`AGENTS.md`; product identity and update policy are in `CLASSIC.md`.

## Replay commands

Run from the maintenance checkout, which contains the metadata and replay
scripts:

```powershell
pwsh ./scripts/apply-patch-stack.ps1
pwsh ./scripts/verify-patch-stack.ps1
```

The apply script creates a linked worktree and uses
`git am --3way --keep-cr --ignore-whitespace`. Pass `-KeepWorktree` to retain
a temporary replay, or `-WorktreePath` to choose a retained worktree.
Verification removes its temporary worktree.

The replayed source snapshot does not contain the maintenance checkout's
patch series or replay scripts. Run restore, build, and tests in the replay
worktree; run replay commands in the maintenance checkout.

## Update a patch

1. Replay the current series into a retained worktree.
2. Make and commit the source change there. Retain the source commit on a remote
   branch so full-history CI and release checkouts can fetch it.
3. Export the commit with `git format-patch`. Preserve upstream trailers for an
   import; otherwise add `Classic-Source-Commit: <source commit SHA>` to the patch
   message. Append the patch path to `maintenance/patches/series`.
4. Update `classic_source_commit` in `maintenance/patch-stack.json`. Mirror
   changed source files in the maintenance checkout so its tests and agents see
   the same change.
5. Run `pwsh ./scripts/verify-patch-stack.ps1` and the checks required for the
   changed source.

The final `classic/1110-classic-maintenance-docs.patch` consolidates maintenance
rules, preserves the decisions below, and removes the unused ledger and its
existence checks. Earlier patches are retained for provenance.

## Release source

The Classic release workflow fetches full history, replays the series, and
verifies source-tree equivalence before version stamping. Restore, tests,
WinUI publish, integrity-tree generation, and Inno Setup packaging then run
inside that replay worktree.

The Classic guard checks product constraints. Existing .NET and CLI E2E checks
validate behavior. Replay equivalence checks reconstruction, not behavior.

## Backport decisions

These notes preserve decisions from the former ledger. PR numbers below refer
to `Devolutions/UniGetUI`; they are historical import decisions, not a current
feature checklist. Add a note only when a patch does not explain an exception.

| Change | Classic decision |
|---|---|
| #5311, icon cache | Import URL lookup/cache fixes. Omit Avalonia bitmap caching because WinUI stores resolved icon URLs. |
| #5296, version segments | Map extra-segment comparison to Classic's legacy `IsUpdateMinor` API rather than importing the newer skip-level API. |
| #5322, Cargo dependencies | Import Cargo home detection and dependency commands; adapt dependency install checks to WinUI. Omit the Avalonia dialog rewrite. |
| #5244, Pinget exit status | Recognize no-applicable-upgrade output even with exit code zero; preserve retry and phantom-update guards. |
| #5173, install location | Adapt explicit per-package WinGet update location to Classic's inline location logic. |
| #5167, stuck upgrades | Import backend detection and settings. Omit the Avalonia settings control. |
| #4998 and #5199, phantom updates | Retry once without forced scope/architecture, then report failure. Suppress phantom updates only after that retry, using #5167 tracking. |
| #5114, PowerShell scope | Map upstream Avalonia scope behavior to the Classic WinUI scope selector. |
| #5077, reinstall orchestration | Skip: Classic already chains one install operation after the uninstall prerequisite and does not launch uninstall separately. Neither reported defect applies. |
| #4959, frontend retirement | Remove the obsolete frontend selector while preserving WinUI and legacy helpers for compatibility. |
| Classic issue #5, SQLite | Pin SQLitePCLRaw to 2.1.13 for GHSA-2m69-gcr7-jv3q. Remove the override when upstream or Pinget stops resolving vulnerable 2.1.11. |
