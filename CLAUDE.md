# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project at a glance

WinTune is a single-window Windows 11 performance toolkit (live monitor, junk cleanup, RAM trim, diagnostics with safe fixes, duplicate-file removal). It exists in two coexisting forms:

1. **Primary**: C# / WPF / .NET 10 app — `src/WinTune.App` + `src/WinTune.Core`. Ships as a single-file, self-contained `WinTune.exe` (`win-x64`).
2. **Reference implementation**: original Windows PowerShell 5.1 + WPF script — `WinTune.ps1`, `modules/`, `ui/MainWindow.xaml`. Kept buildable and CI-tested. The README's dot-source surface (`Get-CleanupTargets`, `Find-Duplicates`, etc.) is part of the public contract — renaming legacy module functions breaks callers.

Both forms self-elevate to Administrator; the C# app does so via `app.manifest` (`requireAdministrator`), the PowerShell script via `Start-Process -Verb RunAs`.

## Common commands

.NET (primary):

```powershell
# Build + test
dotnet build -c Release
dotnet test  -c Release

# Run one test class / method
dotnet test --filter "FullyQualifiedName~DiagnoseServiceBatteryTests"
dotnet test --filter "FullyQualifiedName~DedupServiceTests.Foo"

# Publish single-file self-contained exe (matches CI artifact)
dotnet publish src/WinTune.App/WinTune.App.csproj -c Release -r win-x64 -o publish/win-x64

# Run dev build (UAC prompt fires)
dotnet run --project src/WinTune.App
```

PowerShell reference (still gated by CI):

```powershell
# Run the legacy WPF app — STA required, pwsh 7 is flaky for WPF
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\WinTune.ps1

# Full PowerShell test gate (PSScriptAnalyzer + Pester 5)
.\Run-Tests.ps1
.\Run-Tests.ps1 -NoAnalyze    # Pester only
.\Run-Tests.ps1 -NoPester     # PSSA only

# One-time test deps
Install-Module Pester           -MinimumVersion 5.5.0 -Force -SkipPublisherCheck -Scope CurrentUser
Install-Module PSScriptAnalyzer -Force -Scope CurrentUser
```

SDK is pinned in `global.json` (`10.0.203`, `latestFeature`). Both projects enable `TreatWarningsAsErrors=true` and `AnalysisLevel=latest-recommended` — warnings break the build, fix at source rather than suppress. CI: `.github/workflows/dotnet.yml` (build → test → publish exe artifact) and `.github/workflows/ci.yml` (PSSA + Pester for the legacy script).

## Architecture

### C# port — two-project split

`WinTune.Core` is a plain class library — **no WPF references**. Everything UI-agnostic lives here so it can be unit-tested without a dispatcher:

- `Services/` — one interface + one implementation per tab feature (`IMonitorService`, `IBoostService`, `ICleanupService`, `IStartupService`, `IDiagnoseService`, `IDedupService`, `IPowerService`) plus shared helpers (`IProcessRunner`, `IDedupHashCache`).
- `Models/` — immutable DTOs returned by services (`PerfSnapshot`, `CleanupResult`, `Finding`, `DuplicateGroup`, `PowerState`, etc.).
- `NativeInterop/` — P/Invoke shims (`Kernel32`, `PsApi.EmptyWorkingSet`, `Shell32.SHFileOperation` for Recycle Bin, `DnsApi`). Add new P/Invoke here, not inline.
- `InternalsVisibleTo` exposes internals to `WinTune.Core.Tests` only.
- `IProcessRunner` exists so services that shell out (most notably `PowerService` calling `powercfg.exe`) are mockable. Real I/O lives in `ProcessRunner`; tests use `Moq`.

`WinTune.App` is the WPF executable, MVVM via `CommunityToolkit.Mvvm`:

- `App.xaml.cs` wires the `Microsoft.Extensions.Hosting` container — every service and every view-model is registered there. Services are singletons, `MainWindow` is transient.
- One view-model per tab in `ViewModels/` (`DashboardViewModel`, `CleanViewModel`, `BoostViewModel`, `DiagnoseViewModel`, `DedupeViewModel`) plus `MainWindowViewModel` for cross-tab nav. Tab switching is done by setting `MainWindowViewModel.SelectedTabIndex` or raising `TabSwitchRequested` — do not poke the `TabControl` directly.
- `Converters/` holds the value-converters referenced from XAML (`BytesToDisplayConverter`, `SeverityToBrushConverter`, `BoolToBrushConverter`, `NullToCollapsedConverter`). All XAML bindings should route formatting through these — don't pre-format in view-models.
- Logging: Serilog to `%LocalAppData%\WinTune\logs\wintune-YYYYMMDD.log` (daily rolling, 7-day retention). Construct loggers via DI, not `Log.Logger` directly inside services.

### Threading model

Services that do real work (cleanup scan, dedup hashing, diagnostics CIM queries, RAM trim) are `async` and accept a `CancellationToken`. View-models await them on the UI thread; never `.Wait()` or `.Result` on those tasks — it will deadlock the WPF dispatcher. The legacy PowerShell version uses a dispatcher-timer poll loop (`modules/Async.psm1`) for the same reason.

### `PowerService` subgroup gotcha

`PowerService.SetDcAsync` takes an **explicit `subgroupGuid` argument** (commit `a06b07e`). Earlier code inferred the subgroup from the setting GUID and silently wrote to the wrong subgroup when GUIDs collided. Always pass the subgroup explicitly; the `PowerCfgIds` constants distinguish `SubProcessor` from `SubEnergySaver`.

### Legacy PowerShell modules

`modules/*.psm1` mirror the Core services (`Monitor`, `Cleanup`, `Boost`, `Startup`, `Diagnose`, `Dedup`, plus `Async` for the dispatcher-timer wrapper). They are pure functions you can dot-source from any PowerShell prompt — preserve that property when editing. Cleanup recursive deletes deliberately skip reparse points (junctions/symlinks) — keep that guard; it prevents a stray junction from redirecting the delete outside the cleanup target.

### Specs and plans

Implementation plans / design specs live in `docs/superpowers/plans/` and `docs/superpowers/specs/`. Filenames are date-prefixed. Check there before re-deriving design intent for the Diagnose-actions / battery / Power-tier work.

## Testing notes

- xUnit + FluentAssertions + Moq, `TestHelpers/` holds fakes (e.g. fake `IProcessRunner`).
- `PowerServiceTests` and `DiagnoseServiceBatteryTests` exercise the powercfg surface via mocked `IProcessRunner` — don't make those tests hit real `powercfg.exe`.
- `FindingActionContractTests` enforces the contract between `Finding.Action` IDs and the `FindingAction` handlers exposed to the Diagnose UI. Adding a new diagnostic action: add the ID, the handler, and the contract-test entry together or the build fails.
- Pester tests in `tests/*.Tests.ps1` cover the legacy modules; PSSA settings (`PSScriptAnalyzerSettings.psd1`) intentionally suppress several rules — the reasons are documented inline in that file, read them before adding a new suppression.

## Out of scope by policy

The README's "what it deliberately doesn't" list is binding: no registry edits beyond reading Run keys, no permanent service disabling, no driver touching, no auto-disabling of startup apps, no standby-memory clearing. New features that cross these lines should be discussed before implementation.
