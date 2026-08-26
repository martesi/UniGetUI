# UniGetUI Classic

This branch preserves the WinUI frontend that shipped in UniGetUI v2026.2.1 while selectively backporting package-engine, security, and backend fixes from upstream.

## Branch model

- `upstream`: moving mirror of Devolutions/UniGetUI.
- `main`: Classic line, rooted at upstream `v2026.2.1` commit `68411409d001b9f34cfb896af1543a1d7067ca5e`.
- `main` is not intended to merge `upstream` wholesale.

## Maintenance model

Classic follows a Betterbird/ungoogled-chromium style downstream model:

1. Keep the WinUI presentation layer stable.
2. Track an exact upstream base revision.
3. Review upstream releases by changed path, not commit title.
4. Backport package-engine/Core/security fixes selectively.
5. Split mixed UI/backend commits when only the backend portion applies.
6. Keep permanent downstream changes small, explicit, and documented.
7. Do not carry an old package-management engine merely to preserve the UI.

## Compatibility boundary

Classic owns:

- WinUI presentation and WinUI-specific orchestration.
- Small compatibility adapters required by upstream API changes.
- Classic release/update identity.

Upstream should continue to own, whenever practical:

- Core libraries.
- PackageEngine APIs and models.
- WinGet, Scoop, Chocolatey, PowerShell, pip, npm, Bun, .NET Tool, and other manager implementations.
- Security fixes and package-operation behavior.

## Avalonia policy

The upstream `v2026.2.1` WinUI project can bundle the Avalonia app during publish. Classic overrides `BundleModernApp=false` in `src/Directory.Build.targets`, so Classic builds remain WinUI-only without rewriting the upstream project file.

## Auto-update policy

Do not publish a Classic release using the upstream Devolutions update feed. The v2026.2.1 updater still points at Devolutions product metadata and trusts Devolutions signing certificates. Before the first Classic binary release, either disable self-update or provide a Classic-owned update feed and signing policy.

Until that is done, releases should be considered development builds only.

## Backports

See `maintenance/backports.yml` for the initial triage ledger and `maintenance/PATCH_POLICY.md` for the rules used when importing upstream fixes.
