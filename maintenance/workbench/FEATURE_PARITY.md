# Classic feature backport: September 2026

## Source and review boundaries

- Upstream target: `5e8b14e102780e05a72dd79afc9e20f584da8d12` (includes v2026.3.0 and subsequent fixes through the pinned commit).
- Existing Classic source retained underneath the backport: `b5efda4c9918b5585c08ec419ed57b7c3fd8a11f`.
- Reconstructed Classic source: `@SOURCE_COMMIT@`.
- Source build, test, native-window checks, and complete patch replay: https://github.com/martesi/UniGetUI/actions/runs/@RUN_ID@.
- The final PR is a patch-stack update. Product source changes are inside patches; the temporary source-construction workbench is not part of the final PR.

This supersedes the old ledger's statements that newer frontend controls were excluded. The current shared engine and regression tests are imported together, while Classic's WinUI frontend receives native adapters. Previous CSV export, WinGet catalog refresh, and installed-package update actions from PR #16 are included.

## User-visible coverage

| Feature | Classic entry point / implementation |
| --- | --- |
| Scheduled update checks, installations, local backups and cloud backups | Settings > Scheduled maintenance; `GeneralPages/Scheduler.cs` and `Services/MaintenanceScheduler.cs` |
| Startup, after-check, interval, daily and selected-weekday schedules; catch-up windows and run status | Per-task controls in Scheduled maintenance; shared schedule evaluator/store |
| Automatic updates limited to selected packages | Settings > Manage automatic updates; package installation options; native editor supports search, marked-only filtering and selection persistence |
| Start Menu shortcut organization | Operation preferences > Manage Start Menu shortcuts; per-package folders, move/delete/rename, future-operation rules, review of unknown shortcuts and elevated deletion |
| Desktop shortcut management | Existing shortcut entry points use the shared shortcut editor/model; post-operation review is deferred while the window is hidden |
| Structured operation history | Operation history page: search, status/kind filters, logs, export, rerun, revert, retry variants and confirmed removal/clear |
| Manual install/update/uninstall | Package context menus and install-options command preview open a terminal with the command prefilled, not executed |
| Independent operation argument fields | Installation options explain independence and provide explicit copy-to-update/uninstall controls |
| Configurable ignored version depth | Installation options expose minor/patch/revision component selection |
| Pending process name preservation | An uncommitted process name in the stop-processes field is included when saving options |
| Installer download naming | Operation preferences: publisher filename, name/version, ID/version and publisher-name/version modes |
| Default installer download directory | Operation preferences; native save and folder dialogs start in the configured directory |
| Installer-host and download-size columns | User interface preferences; optional package-list columns reuse upstream lazy loading, bounded caches, confidence rules and retry behavior |
| Unverified installed versions | Version tooltips expose the shared installed-version notice |
| Local backup retention | Backup preferences; configurable count, safe timestamped-file cleanup after successful backup, zero for unlimited |
| Searchable settings | Settings homepage searches translated labels, English labels and setting keys; results navigate and bring the matching control into view |
| Navigation modes | User interface preferences: adaptive, docked-open and sliding-overlay modes |
| Windows system font | User interface preferences; opt-in reads the Windows message font while default mode retains native WinUI typography |
| Empty-state artwork and visibility | Native package pages use upstream artwork; visibility setting does not remove explanatory text |
| Small-window package actions | Native command bar moves excess actions into its overflow flyout |
| Per-page sorting persistence | Package pages restore their saved sort field and direction |
| Tray-close search clearing | All native package-page query backups and the active search box are cleared |
| Operation-card interaction | Whole-card activation opens operation details/logs; embedded buttons keep their own action |
| Operation-panel sizing | Automatic height is capped at three rows while preserving manual expansion |
| Live log behavior | Progress lines collapse; manual scroll-up is respected; existing lines follow theme changes |
| Reset confirmation | Settings reset requires explicit confirmation with Cancel as the default |
| WinGet stuck-upgrade threshold | Manager preferences expose the threshold rather than only using its backend default |
| Separate Scoop cleanup choices | Manager preferences independently control cache and old application cleanup |
| Agent broker | Administrator preferences expose the existing shared broker-backed operation path |
| Relative/case-insensitive bundle arguments | Native startup uses the shared bundle-argument normalizer |
| Resumable self-updater downloads | Native updater uses the shared partial-download engine, validation before promotion and failure backoff; manual checks can retry immediately |
| Update installation choices | Updater preserves portable directory/task and installation scope; installer preserves disabled autostart and does not recreate desktop shortcuts during self-update |
| Notification behavior | Native success/error notifications avoid duplicating visible-window feedback; automatic update notifications describe the actual selected targets |

## Shared engine and compatibility

The shared-source import includes current manager implementations, settings validation, scheduling, automatic-update membership/migration, shortcut databases, operation history, installer naming/location, local-backup management, updater transfer logic and associated regression tests. It also includes the current WinGet/Pinget, PowerShell, NuGet V3, Cargo, Scoop, Chocolatey, npm and pip fixes. The PowerShell operation shim and shared native assets are deployed with Classic.

Classic retains its downstream identity: WinUI-only startup, disabled Avalonia bundling, separate application/data/startup identifiers, the Classic Inno AppId and install directory, fork-owned update manifest, hash validation, and shared-protocol ownership protections. The patched SQLitePCLRaw bundle override remains in place. Historical Classic settings keys remain readable, without exposing a frontend switch.

## Platform and framework boundaries

Avalonia source-level rendering work, Avalonia NativeAOT packaging, macOS/Linux integration and removal of the WinUI frontend are not transplanted as Windows UI technology changes. Classic keeps its existing native implementation where the upstream change merely makes Avalonia behave like WinUI (for example native snap layouts, selectable links, keyboard handling and native window controls). Shared cross-platform code remains synchronized but Classic's distributed frontend is still Windows/WinUI.

The source workbench is a testing aid, not a new build dependency. Normal Classic builds and releases continue to replay the maintained patch series from the established upstream base.

## Verification scope

The linked validation run records the exact source commit, complete Windows solution build and automated tests, native UI Automation screenshots/tree dumps, and full patch replay equality. Native-window checks cover startup, settings navigation, the scheduler, persisted scheduling settings and the automatic-update editor. These checks do not substitute for live installation/uninstallation tests against every third-party manager, signed/unsigned installer variant or elevation configuration. No package installation is performed by the native-window smoke test.
