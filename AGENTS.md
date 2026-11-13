# UniGetUI Classic - Agent Instructions

## Project Overview

UniGetUI Classic preserves the WinUI 3 frontend from upstream v2026.2.1 and selectively imports backend and security fixes. It is a Windows desktop app (C#/.NET 10, Windows App SDK) providing a GUI for CLI package managers (WinGet, Scoop, Chocolatey, Pip, Npm, .NET Tool, PowerShell Gallery, Cargo, Vcpkg).

Solution entry points:
- `src/UniGetUI.Windows.slnx` - Classic Windows solution; builds WinUI and shared backend projects
- `src/UniGetUI.Avalonia.slnx` - inherited experimental port; excluded from Classic releases

## Classic maintenance

- Import upstream fixes selectively. Do not merge the upstream branch into Classic.
- Review the actual diff and touched paths. Prefer shared Core, PackageEngine, and manager fixes.
- Split mixed backend/Avalonia changes. Adapt behavior to WinUI only when Classic needs it; skip Avalonia-only changes.
- Keep compatibility adapters small. Avoid duplicating PackageEngine or manager implementations.
- Prioritize security, then backend correctness, then UI features. Before a release, review upstream changes since the last reviewed tag and identify security fixes.
- Preserve imported provenance with `Upstream-Commit:` and `Upstream-PR:` trailers. Downstream patch files need a `Classic-Source-Commit:` trailer.
- Keep patch files and `maintenance/patches/series` current. Update `classic_source_commit` in `maintenance/patch-stack.json` to the source snapshot; retain that commit on a remote branch.
- Run patch replay verification from the maintenance checkout before submitting a patch-stack change. Release builds use the replayed source tree.
- Put new skipped/adapted decisions in `maintenance/PATCH_STACK.md` only when the reason is not clear from the patch. Do not maintain a second applied-change ledger.
- Preserve the separate Classic installation and update identity described in `CLASSIC.md`. Keep `BundleModernApp=false` in `src/Directory.Build.targets`.

See `maintenance/PATCH_STACK.md` for replay commands and the patch update process.

## Architecture

The codebase follows a **layered, modular structure** with ~40 projects:

- **`UniGetUI/`** - WinUI 3 entry point, XAML pages, controls, and app shell (`EntryPoint.cs`, `MainWindow.xaml`)
- **`UniGetUI.Core.*`** - Shared infrastructure: `Logger`, `Settings`, `Tools` (includes `CoreTools.Translate()`), `IconEngine`, `LanguageEngine`
- **`UniGetUI.PackageEngine.Interfaces`** - Contracts: `IPackageManager`, `IPackage`, `IManagerSource`, `IPackageDetails`
- **`UniGetUI.PackageEngine.PackageManagerClasses`** - Base implementations: `PackageManager` (abstract), `Package`, helpers (`BasePkgDetailsHelper`, `BasePkgOperationHelper`, `BaseSourceHelper`)
- **`UniGetUI.PackageEngine.Managers.*`** - Concrete manager implementations (one project per manager: `WinGet`, `Scoop`, `Chocolatey`, `Pip`, `npm`, etc.)
- **`UniGetUI.PackageEngine.Operations`** - Install/update/uninstall operation orchestration
- **`UniGetUI.Interface.*`** - Enums, telemetry, background API

## Adding a New Package Manager

Each manager extends `PackageManager` and must override three abstract methods:

```csharp
protected override IReadOnlyList<Package> FindPackages_UnSafe(string query);
protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe();
protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe();
```

Each manager also provides three helper classes (in a `Helpers/` subfolder):
- `*PkgDetailsHelper` extends `BasePkgDetailsHelper` - overrides `GetDetails_UnSafe`, `GetInstallableVersions_UnSafe`, `GetIcon_UnSafe`, etc.
- `*PkgOperationHelper` extends `BasePkgOperationHelper` - overrides `_getOperationParameters`, `_getOperationResult`
- `*SourceHelper` extends `BaseSourceHelper` - overrides `GetSources_UnSafe`, `GetAddSourceParameters`, etc.

The constructor sets `Capabilities`, `Properties`, and wires the helpers. See `src/UniGetUI.PackageEngine.Managers.Scoop/Scoop.cs` as a clean reference implementation.

## Build & Test

Run from the repository root on Windows with the SDK selected by `global.json`:

```powershell
dotnet restore src/UniGetUI.Windows.slnx
dotnet build src/UniGetUI.Windows.slnx --no-restore --verbosity minimal /p:Platform=x64
dotnet test src/UniGetUI.Windows.slnx --no-restore --verbosity q --nologo /p:Platform=x64
dotnet publish src/UniGetUI/UniGetUI.csproj /p:Configuration=Release /p:Platform=x64
```

For patch-stack changes, run from the maintenance checkout:

```powershell
pwsh ./scripts/verify-patch-stack.ps1
```

- Target framework: `net10.0-windows10.0.26100.0` (min `10.0.19041`)
- Build generates secrets via `src/UniGetUI/Services/generate-secrets.ps1` and integrity tree via `scripts/generate-integrity-tree.ps1`
- Self-contained, publish-trimmed (partial), Windows App SDK self-contained
- Tests use **xUnit** (`[Fact]`, `Assert.*`)

## Key Patterns & Conventions

### Settings
File-based settings via `Settings.Get(Settings.K.*)` / `Settings.Set(Settings.K.*, value)` and `Settings.GetValue(Settings.K.*)` / `Settings.SetValue(Settings.K.*, value)`. Setting keys are defined in the `Settings.K` enum in `SettingsEngine_Names.cs`. Boolean settings are stored as file existence; string settings as file content.

### Logging
Use `Logger.Info()`, `Logger.Warn()`, `Logger.Error()`, `Logger.Debug()`, `Logger.ImportantInfo()` from `UniGetUI.Core.Logging`. Accepts both `string` and `Exception` parameters.

### Localization
Use `CoreTools.Translate("text")` for all user-facing strings. Parameterized: `CoreTools.Translate("{0} packages found", count)`. In XAML, use the `TranslatedTextBlock` control. Translation assets live under `src/Languages/`; do not assume Tolgee-based automation exists in this repository.

### Naming
- Types, methods, properties: **PascalCase**
- Private fields: `__doubleUnderscore` or `_singleUnderscore` prefix
- Internal unsafe methods: suffix `_UnSafe` (e.g., `FindPackages_UnSafe`)
- Nullable enabled globally; `LangVersion` is `latest`
- Code style enforced in build (`EnforceCodeStyleInBuild=true`)

### Manager conventions
- `FALSE_PACKAGE_NAMES`, `FALSE_PACKAGE_IDS`, `FALSE_PACKAGE_VERSIONS` static arrays filter CLI parsing noise
- Manager initialization flows through `Initialize()` -> `_loadManagerExecutableFile()` -> `_loadManagerVersion()` -> `_performExtraLoadingSteps()`
- Operations that may fail return `OperationVeredict` (note: intentional misspelling used throughout codebase)

## Key Files

| Purpose | Path |
|---|---|
| Windows solution | `src/UniGetUI.Windows.slnx` |
| Experimental cross-platform solution | `src/UniGetUI.Avalonia.slnx` |
| Shared build props | `src/Directory.Build.props` |
| Version info | `src/SharedAssemblyInfo.cs` |
| Manager interface | `src/UniGetUI.PackageEngine.Interfaces/IPackageManager.cs` |
| Base manager class | `src/UniGetUI.PackageEngine.PackageManagerClasses/Manager/PackageManager.cs` |
| Package class | `src/UniGetUI.PackageEngine.PackageManagerClasses/Packages/Package.cs` |
| Settings engine | `src/UniGetUI.Core.Settings/SettingsEngine.cs` |
| Setting keys | `src/UniGetUI.Core.Settings/SettingsEngine_Names.cs` |
| Logger | `src/UniGetUI.Core.Logger/Logger.cs` |
| CI test workflow | `.github/workflows/dotnet-test.yml` |
