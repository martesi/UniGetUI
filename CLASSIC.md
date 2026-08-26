# UniGetUI Classic

This branch preserves the WinUI frontend that shipped in UniGetUI v2026.2.1 while selectively backporting package-engine, security, and backend fixes from upstream.

## Branch model

- `upstream`: moving mirror of Devolutions/UniGetUI.
- `main`: Classic line, rooted at upstream `v2026.2.1` commit `68411409d001b9f34cfb896af1543a1d7067ca5e`.
- `main` is not intended to merge `upstream` wholesale during the bootstrap phase.

## Maintenance model

Classic is bootstrapping with selective backports, with the target architecture being a Betterbird/ungoogled-chromium style reproducible downstream patch stack over a newer upstream base.

1. Keep the WinUI presentation layer stable.
2. Track an exact upstream base revision.
3. Review upstream releases by changed path, not commit title.
4. Backport package-engine/Core/security fixes selectively while the Classic compatibility boundary is established.
5. Split mixed UI/backend commits when only the backend portion applies.
6. Keep permanent downstream changes small, explicit, and documented.
7. Move toward rebuilding Classic as `current upstream + ordered Classic patch set` once WinUI can be carried cleanly against the current PackageEngine boundary.
8. Do not carry an old package-management engine merely to preserve the UI.

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

Classic uses its own GitHub Releases channel:

- Manifest: `https://github.com/martesi/UniGetUI/releases/latest/download/productinfo.json`
- Product key: `martesi.UniGetUI.Classic`
- Transport: HTTPS only.
- Installer integrity: SHA-256 hash from `productinfo.json` is mandatory.
- Authenticode signer validation: intentionally disabled while Classic releases are unsigned.

The release workflow generates `productinfo.json` and `checksums.txt` from the exact installer artifacts before creating the GitHub Release. The inherited Devolutions updater registry namespace is also separated to `HKLM\Software\martesi\UniGetUIClassic`.

Classic versions are numeric four-part versions. During the bootstrap phase the first three components identify the preserved upstream UI/source base and the fourth component is the Classic release revision, for example `2026.2.1.1`.

## Backports

See `maintenance/backports.yml` for the triage ledger and `maintenance/PATCH_POLICY.md` for the rules used when importing upstream fixes.
