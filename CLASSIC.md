# UniGetUI Classic

This branch preserves the WinUI frontend that shipped in UniGetUI v2026.2.1 while selectively backporting package-engine, security, and backend fixes from upstream.

## Branch model

- `upstream`: moving mirror of Devolutions/UniGetUI.
- `main`: Classic line, rooted at upstream `v2026.2.1` commit `68411409d001b9f34cfb896af1543a1d7067ca5e`.
- Import upstream fixes selectively; do not merge `upstream` wholesale into `main`.

## Maintenance model

Classic releases use an ordered patch stack over the exact upstream base in `maintenance/upstream-base.json`. The release workflow replays `maintenance/patches/series` and verifies that the complete source tree matches `classic_source_commit` in `maintenance/patch-stack.json` before building.

Read `AGENTS.md` for maintenance rules and `maintenance/PATCH_STACK.md` for commands, patch updates, and decisions.

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

## Installed app identity

Classic is a separate installed application and is intended to coexist with upstream UniGetUI rather than replace its installation.

- Display name: `UniGetUI Classic`.
- Publisher: `Martes`.
- Inno Setup AppId: `{E385AFF5-90A4-4296-8702-EC129F9DC40B}`.
- Default install directory: `Program Files\UniGetUI Classic`.
- Startup registry value: `UniGetUIClassic`.
- Writable local data directory: `%LOCALAPPDATA%\UniGetUIClassic`.
- Runtime app identifier: `Martes.UniGetUIClassic`.
- Main-window identifier: `Martes.UniGetUIClassic.MainInterface`.

Classic does not automatically move or copy upstream UniGetUI's writable data. An explicit import can be added later if needed without making the two applications share live state.

The existing `unigetui://` protocol and `.ubundle`/`UniGetUI.PackageBundle` identifiers are intentionally retained rather than renamed. They are shared compatibility surfaces, not isolated product identifiers. Classic registers them only when they are currently unclaimed, so installing Classic beside upstream does not replace upstream's active handler. On uninstall, Classic removes a shared handler only when its command still points into the Classic installation directory; if another installation has claimed the handler in the meantime, Classic leaves it untouched.

The Classic installer also avoids broad image-name `taskkill` operations and does not run the old `--migrate-wingetui-to-unigetui` post-install migration, because either behavior could affect an upstream installation running alongside Classic.

## Auto-update policy

Classic uses its own GitHub Releases channel:

- Manifest: `https://github.com/martesi/UniGetUI/releases/latest/download/productinfo.json`
- Product key: `martesi.UniGetUI.Classic`
- Transport: HTTPS only.
- Installer integrity: SHA-256 hash from `productinfo.json` is mandatory.
- Authenticode signer validation: intentionally disabled while Classic releases are unsigned.

The release workflow generates `productinfo.json` and `checksums.txt` from the exact installer artifacts before creating the GitHub Release. The inherited Devolutions updater registry namespace is also separated to `HKLM\Software\martesi\UniGetUIClassic`.

Classic versions are numeric four-part versions. The first three components identify the preserved upstream UI/source base and the fourth component is the Classic release revision, for example `2026.2.1.1`.

## Backports

Imported fixes carry upstream commit and PR trailers in Git history and patch files. The ordered patch series defines the changes used for releases. Skipped or adapted backport decisions are recorded in `maintenance/PATCH_STACK.md`.
