# Classic backport policy

## Rules

1. Never merge `upstream` wholesale into `main`.
2. Review upstream changes by touched paths and actual diff, not by PR/commit title alone.
3. Prefer backend-only changes under `UniGetUI.Core.*`, `UniGetUI.PackageEngine.*`, and manager projects.
4. For mixed Avalonia/backend changes, split the backend portion and manually adapt WinUI only when the behavior matters to Classic.
5. Record every imported upstream change in `maintenance/backports.yml`.
6. Preserve upstream commit identity in commit messages with `Upstream-Commit:` and `Upstream-PR:` trailers.
7. Keep Classic-only compatibility code thin. If compatibility changes begin duplicating PackageEngine or manager implementations, stop and reconsider the boundary.
8. Security fixes outrank feature parity. Backend correctness fixes outrank new UI features.
9. Avalonia-only appearance, navigation, cross-platform, and NativeAOT changes are non-applicable unless they expose a shared backend defect.
10. Before every Classic release, review all upstream commits since the last reviewed upstream tag and explicitly classify security-sensitive changes.

## Backport status values

- `candidate`: expected to apply substantially as-is.
- `partial`: split backend/shared changes from upstream UI changes.
- `inspect-classic`: upstream fix is UI-specific; first verify Classic has the same defect.
- `included`: present in Classic.
- `skipped`: intentionally not applicable; retain the reason.

## Commit message convention

```text
backport: fix WinGet phantom updates

Upstream-Commit: 26c93b859babdc831b2d8462048d3e5f85d3da67
Upstream-PR: Devolutions/UniGetUI#5199
```

The goal is reproducibility: a maintainer should be able to answer exactly which upstream fixes Classic contains without reconstructing Git archaeology.
