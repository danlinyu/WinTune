# WinTune

A small, single-window Windows 11 performance toolkit. Monitor live system load,
clean accumulated junk, and free memory — all from one place. Ships as a
self-contained C# / WPF / .NET 10 executable; original PowerShell + WPF
script preserved as a reference implementation.

## Why this exists

Windows laptops slow down over time even when nothing is "wrong" — Explorer drags,
file dialogs hang, RAM stays pinned long after closing apps. The usual culprits are:

- **Working-set memory bloat** — apps hold RAM after their windows close.
- **Temp file accumulation** — `%TEMP%`, `Windows\Temp`, Prefetch, Windows Error
  Reports, Windows Update download cache.
- **Browser cache bloat** — Edge, Chrome, Firefox happily eat 5 – 20 GB.
- **Startup app overload** — every install adds another auto-launcher.
- **Stale DNS cache** — slow / dead lookups linger.

WinTune addresses each of these with one click.

## What it does (and what it deliberately doesn't)

### Six tabs

**Dashboard** — live CPU, RAM, and disk-C: bars (refresh every 2 s) plus a top-10
process list sorted by working-set RAM.

**Optimize** — adaptive system overview for the whole machine:
- Profiles CPU count, RAM tier, all fixed drives, GPU adapter memory, and live
  drive I/O pressure.
- Flags RAM pressure, low free space on any fixed drive, busy/saturated drive I/O,
  and high CPU load with clear next actions.
- Optional **Adaptive smoothing** only performs reversible working-set trimming
  after sustained RAM pressure and a cooldown.
- Per-drive **Optimize** buttons call Windows' own media-aware drive optimizer
  (`defrag.exe /O`), so SSDs get SSD-appropriate maintenance and HDDs get
  defragmentation when Windows decides it is appropriate.
- **Resource Monitor** button opens Windows' built-in disk/process view when a
  drive is busy.

**Clean** — pick what to clear, click Run:
- User Temp (`%TEMP%`)
- System Temp (`%SystemRoot%\Temp`)
- Windows Prefetch
- Windows Error Reports (`WER\Report{Archive,Queue}`)
- Windows Update download cache (stops `wuauserv` + `bits` briefly, then restarts
  them — restart is in a `finally` block so the services come back even if the
  delete fails)
- Edge / Chrome / Firefox caches (skipped if the browser is running — close it first)
- Empty Recycle Bin
- Flush DNS resolver cache

Reparse points (junctions, symlinks) inside any cleanup target are skipped — never
followed — so a stray junction can't redirect the recursive delete somewhere
unexpected.

**Boost**
- Free RAM — calls Win32 `EmptyWorkingSet` on every process. Reduces working-set
  pressure without killing anything.
- Restart Explorer — fastest fix when the taskbar / file dialogs feel sluggish.
- Flush DNS Cache — `Clear-DnsClientCache`.
- Read-only listing of every startup program (WMI + registry Run keys + Startup
  folders), with a button that opens **Task Manager → Startup** if you want to
  disable any of them yourself.

**Diagnose** — find what's making Explorer slow. Each finding is colour-coded
(Red = act, Yellow = worth a look, Green = OK) with a one-line hint. Checks
include disk health, free-space pressure, multiple cloud sync shell extensions,
Quick Access bloat, Search index emptiness, DiagTrack telemetry, the Win11
right-click overlay, pagefile placement, startup-app count, and RAM pressure.

Backed by safe one-click fixes:
- **Reset Quick Access** — clears stale `Recent` and `AutomaticDestinations`
  shortcuts, a common cause of Explorer hangs on dead network paths.
- **Disable Telemetry** — stops + disables `DiagTrack` *and* sets the
  `AllowTelemetry` Group Policy value so Windows Update cumulative updates
  don't silently re-enable it.
- **Apply Classic Right-Click** — restores the Win10-style instant context
  menu (Win11's "Show more options" overlay is the slow path).
- **Rebuild Search Index** — kicks Windows Search to re-index from scratch.
- **Undo Classic Right-Click** — reverts the above.

**Dedupe** — find and remove duplicate files in user folders.
- Two-pass scan: group by exact size first, then SHA1-hash only size-matches.
- Skips reparse points (OneDrive on-demand files won't trigger a download) and
  system files.
- Auto-select helpers: keep oldest / keep newest / keep shortest path.
- Safety stop: refuses to delete every file in a duplicate group — you must
  un-tick at least one row per group to keep a copy.
- Deletes go to the Recycle Bin so you can recover.

### Out of scope on purpose

WinTune **does not**:

- Edit the registry beyond reading Run keys.
- Permanently disable services.
- Touch drivers.
- Auto-disable startup apps (too easy to break a workflow you forgot you needed).
- Clear the standby memory list (the API is undocumented and crashes on some Win11
  builds).

If you want the heavier stuff, use Sysinternals' RAMMap or Autoruns by hand.

## Install & run

WinTune ships as a self-contained C# WPF executable. The original PowerShell +
WPF script is preserved in this repo as a reference implementation.

### Self-contained .exe (recommended)

Single self-contained `WinTune.exe`, no PowerShell or .NET runtime needed on the
target machine.

Download the latest prebuilt executable here:

**[Download WinTune.exe](https://github.com/danlinyu/WinTune/releases/latest/download/WinTune.exe)**

If you want to build it yourself, install the .NET 10 SDK, clone the repo, and
run the publish command from the repo root. The `src\WinTune.App\WinTune.App.csproj`
path only exists after cloning and `cd`-ing into `WinTune`:

```powershell
git clone https://github.com/danlinyu/WinTune.git
cd WinTune
dotnet publish .\src\WinTune.App\WinTune.App.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
.\publish\win-x64\WinTune.exe
```

UAC will prompt automatically (the app manifest requests `requireAdministrator`).

If you see `MSB1009: Project file does not exist`, you are not in the cloned
repo root. Run `cd WinTune` first and confirm that `WinTune.sln` is in the
current directory.

### PowerShell version (reference implementation)

The original Windows PowerShell 5.1 + WPF version is still here for reference.
WPF on PowerShell 5.1 (STA mode) is required:

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\WinTune.ps1
```

Self-elevation is handled inside the script (UAC prompt fires automatically).

### How safe is it?

Every cleanup target is a well-known, OS-managed cache. Microsoft's own *Disk
Cleanup* utility removes the same paths. The Boost tab uses only documented APIs
(`EmptyWorkingSet`, `Clear-DnsClientCache`) and supported commands (Stop / Start
of `explorer.exe`). Nothing is irreversible — Windows regenerates these caches
on demand.

The browser-cache cleaners refuse to run while the browser is open. That's
intentional: clearing a cache out from under a live process can corrupt the
browser profile.

## Tested on

- Windows 11 Pro for Workstations, build 22631
- C# build: .NET 10 SDK, single-file self-contained `win-x64`
- PowerShell reference build: Windows PowerShell 5.1 STA (WPF on pwsh 7 is flaky)

## Project layout

```
WinTune/
├── WinTune.sln            .NET 10 solution (Core + App + Tests)
├── src/WinTune.Core/      class library — services and DTOs (no WPF refs)
├── src/WinTune.App/       WPF executable, MVVM via CommunityToolkit.Mvvm
├── tests/                 xUnit + FluentAssertions
├── .github/workflows/     CI — dotnet.yml (C#) + ci.yml (PowerShell)
│
├── WinTune.ps1            PowerShell reference — builds WPF window directly
├── modules/
│   ├── Monitor.psm1       Get-PerfSnapshot, Get-TopProcesses
│   ├── Cleanup.psm1       Invoke-Cleanup, Get-CleanupTargets, Format-Bytes,
│   │                      Get-LastCleanupLog
│   ├── Boost.psm1         Clear-WorkingSets, Restart-Explorer, Clear-DNSCacheSafe
│   ├── Startup.psm1       Get-StartupApps, Open-StartupTaskManager
│   ├── Diagnose.psm1      Invoke-Diagnostics + Reset-QuickAccess, Disable-Telemetry,
│   │                      Enable-/Disable-ClassicRightClick, Start-SearchIndexRebuild
│   └── Dedup.psm1         Find-Duplicates, Remove-DuplicateFiles, Get-DefaultScanRoots
├── ui/MainWindow.xaml     WPF layout (loaded at runtime by WinTune.ps1)
├── README.md
└── LICENSE                MIT
```

The six modules are pure functions — easy to dot-source and use from any
PowerShell prompt:

```powershell
Import-Module .\modules\Monitor.psm1
Get-PerfSnapshot
Get-TopProcesses -Count 5

Import-Module .\modules\Diagnose.psm1
Invoke-Diagnostics | Format-Table Severity, Title, Detail

Import-Module .\modules\Dedup.psm1
Find-Duplicates -Paths $HOME\Downloads -MinSizeBytes 10MB
```

## Development

Run the test gate (PSScriptAnalyzer + Pester) locally:

```powershell
# Once
Install-Module Pester           -MinimumVersion 5.5.0 -Force -SkipPublisherCheck -Scope CurrentUser
Install-Module PSScriptAnalyzer -Force -Scope CurrentUser

# Each commit
.\Run-Tests.ps1
```

GitHub Actions runs the same script on every push and PR
([`.github/workflows/ci.yml`](.github/workflows/ci.yml)).

## License

MIT — see [LICENSE](LICENSE).
