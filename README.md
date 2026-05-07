# WinTune

A small, single-window Windows 11 performance toolkit. Monitor live system load,
clean accumulated junk, and free memory — all from one place. Pure PowerShell +
WPF, no installer, no compiled binary.

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

### Three tabs

**Dashboard** — live CPU, RAM, and disk-C: bars (refresh every 2 s) plus a top-10
process list sorted by working-set RAM.

**Clean** — pick what to clear, click Run:
- User Temp (`%TEMP%`)
- System Temp (`C:\Windows\Temp`)
- Windows Prefetch
- Windows Error Reports (`WER\Report{Archive,Queue}`)
- Windows Update download cache (stops `wuauserv` + `bits` briefly, then restarts them)
- Edge / Chrome / Firefox caches (skipped if the browser is running — close it first)
- Empty Recycle Bin
- Flush DNS resolver cache

**Boost**
- Free RAM — calls Win32 `EmptyWorkingSet` on every process. Reduces working-set
  pressure without killing anything.
- Restart Explorer — fastest fix when the taskbar / file dialogs feel sluggish.
- Flush DNS Cache — `Clear-DnsClientCache`.
- Read-only listing of every startup program (WMI + registry Run keys + Startup
  folders), with a button that opens **Task Manager → Startup** if you want to
  disable any of them yourself.

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

1. Clone or download this repo:
   ```powershell
   git clone https://github.com/danlinyu/WinTune.git
   ```
2. Double-click **`Launch-WinTune.cmd`**.
3. Approve the UAC prompt (admin rights are needed to clear System Temp,
   Windows Update cache, and Prefetch).

That's it — no install step, no dependencies beyond the Windows PowerShell 5.1
that ships with every Windows 10 / 11 install.

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
- Windows PowerShell 5.1 (the launcher pins this rather than pwsh 7 — WPF on
  pwsh 7 is currently flaky)

## Project layout

```
WinTune/
├── WinTune.ps1            entry point — builds the WPF window and wires events
├── Launch-WinTune.cmd     double-clickable wrapper
├── modules/
│   ├── Monitor.psm1       Get-PerfSnapshot, Get-TopProcesses
│   ├── Cleanup.psm1       Invoke-Cleanup, Get-CleanupTargets, Format-Bytes
│   ├── Boost.psm1         Clear-WorkingSets, Restart-Explorer, Clear-DNSCacheSafe
│   └── Startup.psm1       Get-StartupApps, Open-StartupTaskManager
├── ui/MainWindow.xaml     WPF layout (loaded at runtime by WinTune.ps1)
├── README.md
└── LICENSE                MIT
```

The four modules are pure functions — easy to dot-source and use from any
PowerShell prompt:

```powershell
Import-Module .\modules\Monitor.psm1
Get-PerfSnapshot
Get-TopProcesses -Count 5
```

## License

MIT — see [LICENSE](LICENSE).
