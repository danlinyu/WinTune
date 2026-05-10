# WinTune C# WPF .NET 10 Port — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the PowerShell + WPF WinTune (`WinTune.ps1` + 7 `.psm1` modules + `MainWindow.xaml`) with a self-contained .NET 10 WPF executable that ships as a single `.exe`, requires no PowerShell on the target machine, and preserves every safety guarantee called out in the existing README.

**Architecture:** Two-project solution. `WinTune.Core` is a .NET 10 class library holding all services and DTOs (no WPF references). `WinTune.App` is the WPF executable, MVVM via CommunityToolkit.Mvvm, DI via Microsoft.Extensions.DependencyInjection. Native interop via `[LibraryImport]` source-generated P/Invoke (no `DllImport`). Tests in xUnit + FluentAssertions.

**Tech Stack:**
- .NET 10 SDK (latest LTS as of 2026-05; supported through 2028-11)
- WPF (Windows.Desktop)
- CommunityToolkit.Mvvm 8.x
- Microsoft.Extensions.DependencyInjection / Microsoft.Extensions.Hosting
- Serilog (file + console sink)
- xUnit + FluentAssertions + Moq
- Microsoft.VisualBasic.FileIO (built into .NET Windows desktop) for Recycle-Bin delete
- System.Management for WMI/CIM
- System.ServiceProcess.ServiceController for service control
- System.Diagnostics.PerformanceCounter for CPU
- LibraryImport for `EmptyWorkingSet`, `SHEmptyRecycleBin`, `DnsFlushResolverCache`

**Branch:** `csharp-port` (created 2026-05-09 from `main`). PowerShell version stays on `main` until C# port reaches feature parity, then `main` flips.

**Repo layout (new):**
```
WinTune/
├── WinTune.sln                       NEW — solution file
├── src/
│   ├── WinTune.Core/                 NEW — class library
│   │   ├── WinTune.Core.csproj
│   │   ├── Models/
│   │   │   ├── PerfSnapshot.cs
│   │   │   ├── ProcessSnapshot.cs
│   │   │   ├── CleanupTarget.cs
│   │   │   ├── CleanupResult.cs
│   │   │   ├── CleanupProgress.cs
│   │   │   ├── BoostResult.cs
│   │   │   ├── StartupEntry.cs
│   │   │   ├── Finding.cs
│   │   │   ├── DiagnoseResult.cs
│   │   │   ├── DuplicateGroup.cs
│   │   │   ├── DuplicateFile.cs
│   │   │   ├── DedupeProgress.cs
│   │   │   └── RemovalResult.cs
│   │   ├── Services/
│   │   │   ├── IMonitorService.cs / MonitorService.cs
│   │   │   ├── IBoostService.cs    / BoostService.cs
│   │   │   ├── ICleanupService.cs  / CleanupService.cs
│   │   │   ├── IStartupService.cs  / StartupService.cs
│   │   │   ├── IDiagnoseService.cs / DiagnoseService.cs
│   │   │   └── IDedupService.cs    / DedupService.cs
│   │   └── NativeInterop/
│   │       ├── PsApi.cs             EmptyWorkingSet
│   │       ├── Shell32.cs           SHEmptyRecycleBin, SHFileOperation
│   │       └── DnsApi.cs            DnsFlushResolverCache
│   └── WinTune.App/                  NEW — WPF executable
│       ├── WinTune.App.csproj
│       ├── App.xaml + App.xaml.cs
│       ├── MainWindow.xaml + MainWindow.xaml.cs
│       ├── app.manifest
│       ├── Views/
│       │   ├── DashboardView.xaml + .cs
│       │   ├── CleanView.xaml      + .cs
│       │   ├── BoostView.xaml      + .cs
│       │   ├── DiagnoseView.xaml   + .cs
│       │   └── DedupeView.xaml     + .cs
│       ├── ViewModels/
│       │   ├── MainWindowViewModel.cs
│       │   ├── DashboardViewModel.cs
│       │   ├── CleanViewModel.cs
│       │   ├── BoostViewModel.cs
│       │   ├── DiagnoseViewModel.cs
│       │   └── DedupeViewModel.cs
│       ├── Converters/
│       │   ├── BytesToDisplayConverter.cs
│       │   └── SeverityToBrushConverter.cs
│       └── Resources/
│           └── Styles.xaml
├── tests/
│   ├── WinTune.Core.Tests/           NEW — xUnit
│   │   ├── WinTune.Core.Tests.csproj
│   │   ├── MonitorServiceTests.cs
│   │   ├── BoostServiceTests.cs
│   │   ├── CleanupServiceTests.cs
│   │   ├── StartupServiceTests.cs
│   │   ├── DiagnoseServiceTests.cs
│   │   ├── DedupServiceTests.cs
│   │   └── TestHelpers/
│   │       └── TempDirectory.cs
│   └── WinTune.App.Tests/            NEW — UI smoke tests via FlaUI (optional)
└── .github/workflows/dotnet.yml      NEW — alongside existing ci.yml
```

PowerShell tree (`WinTune.ps1`, `modules/`, `ui/MainWindow.xaml`, `Launch-WinTune.cmd`, `Run-Tests.ps1`, `tests/` Pester suite, `.github/workflows/ci.yml`) stays untouched on this branch — it's the reference implementation we're porting.

---

## Phase 0 — Scaffold

### Task 0.1: Verify .NET 10 SDK is on PATH

**Files:** None.

- [ ] **Step 1: Verify dotnet installed**

```powershell
dotnet --list-sdks
```

Expected: at least one line starting with `10.0.` (e.g., `10.0.203 [C:\Program Files\dotnet\sdk]`). If not, install via `winget install --id Microsoft.DotNet.SDK.10 --silent`.

- [ ] **Step 2: Set the SDK pin**

```powershell
cd C:\Users\danli\repos\WinTune
dotnet new globaljson --sdk-version 10.0.203 --roll-forward latestFeature --force
```

Expected: creates `global.json` at repo root pinning the major.feature version.

- [ ] **Step 3: Commit**

```powershell
git add global.json
git commit -m "chore: pin .NET 10 SDK via global.json"
```

### Task 0.2: Create solution and project skeletons

**Files:**
- Create: `WinTune.sln`
- Create: `src/WinTune.Core/WinTune.Core.csproj`
- Create: `src/WinTune.App/WinTune.App.csproj`
- Create: `tests/WinTune.Core.Tests/WinTune.Core.Tests.csproj`

- [ ] **Step 1: Create solution + projects**

```powershell
dotnet new sln -n WinTune
dotnet new classlib -n WinTune.Core -o src/WinTune.Core -f net10.0-windows
dotnet new wpf      -n WinTune.App  -o src/WinTune.App  -f net10.0-windows
dotnet new xunit    -n WinTune.Core.Tests -o tests/WinTune.Core.Tests -f net10.0-windows
dotnet sln add src/WinTune.Core/WinTune.Core.csproj
dotnet sln add src/WinTune.App/WinTune.App.csproj
dotnet sln add tests/WinTune.Core.Tests/WinTune.Core.Tests.csproj
dotnet add src/WinTune.App/WinTune.App.csproj reference src/WinTune.Core/WinTune.Core.csproj
dotnet add tests/WinTune.Core.Tests/WinTune.Core.Tests.csproj reference src/WinTune.Core/WinTune.Core.csproj
```

Delete the auto-generated `Class1.cs`, `MainWindow.xaml*`, `App.xaml*`, `UnitTest1.cs` placeholders — we'll write our own.

- [ ] **Step 2: Configure WinTune.Core.csproj**

Replace the auto-generated content with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWindowsForms>false</UseWindowsForms>
    <UseWPF>false</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="System.Management" Version="9.0.0" />
    <PackageReference Include="System.ServiceProcess.ServiceController" Version="9.0.0" />
    <PackageReference Include="System.Diagnostics.PerformanceCounter" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
  </ItemGroup>
</Project>
```

(Use `9.0.0` packages until 10.0 GA versions confirmed — the 9.0 line works on .NET 10 runtime via roll-forward.)

- [ ] **Step 3: Configure WinTune.App.csproj**

Replace auto-generated content with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <ApplicationIcon></ApplicationIcon>
    <AssemblyName>WinTune</AssemblyName>
    <RootNamespace>WinTune.App</RootNamespace>
  </PropertyGroup>

  <PropertyGroup Condition="'$(Configuration)'=='Release'">
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
    <DebugType>embedded</DebugType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.0" />
    <PackageReference Include="Serilog" Version="4.2.0" />
    <PackageReference Include="Serilog.Extensions.Hosting" Version="9.0.0" />
    <PackageReference Include="Serilog.Sinks.File" Version="6.0.0" />
    <PackageReference Include="Serilog.Sinks.Debug" Version="3.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Add app.manifest with requireAdministrator**

Create `src/WinTune.App/app.manifest`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="WinTune.app"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/> <!-- Win 10 -->
      <supportedOS Id="{1f676c76-80e1-4239-95bb-83d0f6d0da78}"/> <!-- Win 11 implicitly -->
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
</assembly>
```

- [ ] **Step 5: Configure WinTune.Core.Tests.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.0.0">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions" Version="7.0.0" />
    <PackageReference Include="Moq" Version="4.20.72" />
    <PackageReference Include="coverlet.collector" Version="6.0.2">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\WinTune.Core\WinTune.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Verify build**

```powershell
dotnet restore
dotnet build -c Debug
```

Expected: 0 warnings, 0 errors.

- [ ] **Step 7: Verify tests run (empty suite)**

```powershell
dotnet test
```

Expected: "Passed: 0" (no tests yet, but the runner finds the project).

- [ ] **Step 8: Add .gitignore entries**

Append to `.gitignore`:

```
# .NET build output
bin/
obj/
*.user
.vs/
publish/
artifacts/
TestResults/
*.coverage
*.nupkg
```

- [ ] **Step 9: Commit**

```powershell
git add .
git commit -m "feat(csharp): scaffold .NET 10 WPF solution with Core + App + Tests projects"
```

---

## Phase 1 — Models + MonitorService + BoostService

### Task 1.1: Create the model DTOs

**Files:** Create one file per model under `src/WinTune.Core/Models/`. All records use init-only properties.

- [ ] **Step 1: PerfSnapshot.cs**

```csharp
namespace WinTune.Core.Models;

public sealed record PerfSnapshot(
    int CpuPct,
    double RamUsedGB,
    double RamTotalGB,
    double RamPct,
    double DiskUsedGB,
    double DiskFreeGB,
    double DiskTotalGB,
    double DiskPct,
    DateTime Timestamp);
```

- [ ] **Step 2: ProcessSnapshot.cs**

```csharp
namespace WinTune.Core.Models;

public sealed record ProcessSnapshot(
    string Name,
    int Id,
    double RamMB,
    int Threads,
    string StartTime);  // "HH:mm:ss" or "-"
```

- [ ] **Step 3: CleanupTarget.cs**

```csharp
namespace WinTune.Core.Models;

public enum CleanupTarget
{
    UserTemp,
    SystemTemp,
    Prefetch,
    WindowsErrorReports,
    WindowsUpdate,
    EdgeCache,
    ChromeCache,
    FirefoxCache,
    RecycleBin,
    DnsCache
}
```

- [ ] **Step 4: CleanupResult.cs and CleanupProgress.cs**

```csharp
// CleanupResult.cs
namespace WinTune.Core.Models;

public sealed record CleanupResult(
    CleanupTarget Target,
    int FilesRemoved,
    long BytesFreed,
    IReadOnlyList<string> Errors,
    bool Skipped,
    string? SkipReason);

// CleanupProgress.cs
namespace WinTune.Core.Models;

public sealed record CleanupProgress(
    string Phase,    // "Start" | "TargetDone"
    int Index,
    int Total,
    CleanupTarget Target,
    int FilesRemoved = 0,
    long BytesFreed = 0,
    bool Skipped = false,
    int ErrorCount = 0);
```

- [ ] **Step 5: BoostResult.cs**

```csharp
namespace WinTune.Core.Models;

public sealed record WorkingSetResult(
    int ProcessesTrimmed,
    int ProcessesSkipped,
    long BytesFreedEstimate);

public sealed record ExplorerRestartResult(int ProcessesRestarted);

public sealed record DnsFlushResult(bool Success, string? Error);
```

- [ ] **Step 6: StartupEntry.cs**

```csharp
namespace WinTune.Core.Models;

public sealed record StartupEntry(
    string Name,
    string Command,
    string Location,    // friendly path
    string User,        // "AllUsers" or username
    StartupSource Source);

public enum StartupSource { Wmi, Registry, StartupFolder }
```

- [ ] **Step 7: Finding.cs and DiagnoseResult.cs**

```csharp
// Finding.cs
namespace WinTune.Core.Models;

public enum Severity { Red, Yellow, Green }

public sealed record Finding(
    Severity Severity,
    string Title,
    string Detail,
    string? Hint);

// DiagnoseResult.cs
namespace WinTune.Core.Models;

public sealed record DiagnoseResult(
    bool Success,
    string Note,
    int FilesRemoved = 0,
    IReadOnlyList<string>? Errors = null);
```

- [ ] **Step 8: DuplicateGroup.cs, DuplicateFile.cs, DedupeProgress.cs, RemovalResult.cs**

```csharp
// DuplicateFile.cs
namespace WinTune.Core.Models;

public sealed record DuplicateFile(string FullPath, long SizeBytes, DateTime LastWriteTime);

// DuplicateGroup.cs
namespace WinTune.Core.Models;

public sealed record DuplicateGroup(
    int GroupId,
    string Hash,
    long SizeBytes,
    long WastedBytes,
    IReadOnlyList<DuplicateFile> Files);

// DedupeProgress.cs
namespace WinTune.Core.Models;

public sealed record DedupeProgress(
    string Phase,        // "Enumerate" | "Hash" | "Group" | "Done"
    int FilesScanned,
    int FilesHashed,
    int GroupsFound,
    long WastedBytes);

// RemovalResult.cs
namespace WinTune.Core.Models;

public sealed record RemovalResult(
    int Deleted,
    long BytesFreed,
    IReadOnlyList<string> Errors,
    bool Permanent);
```

- [ ] **Step 9: Commit**

```powershell
git add src/WinTune.Core/Models/
git commit -m "feat(core): add Model DTOs for all services"
```

### Task 1.2: NativeInterop scaffolding

**Files:** Create `src/WinTune.Core/NativeInterop/` files.

- [ ] **Step 1: PsApi.cs**

```csharp
using System.Runtime.InteropServices;

namespace WinTune.Core.NativeInterop;

internal static partial class PsApi
{
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyWorkingSet(IntPtr hProcess);
}
```

- [ ] **Step 2: Shell32.cs**

```csharp
using System.Runtime.InteropServices;

namespace WinTune.Core.NativeInterop;

internal static partial class Shell32
{
    [Flags]
    internal enum SHERB : uint
    {
        NoConfirmation = 0x00000001,
        NoProgressUI   = 0x00000002,
        NoSound        = 0x00000004
    }

    [LibraryImport("shell32.dll", SetLastError = true)]
    internal static partial int SHEmptyRecycleBinW(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPWStr)] string? pszRootPath,
        SHERB dwFlags);
}
```

- [ ] **Step 3: DnsApi.cs**

```csharp
using System.Runtime.InteropServices;

namespace WinTune.Core.NativeInterop;

internal static partial class DnsApi
{
    [LibraryImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DnsFlushResolverCache();
}
```

- [ ] **Step 4: Commit**

```powershell
git add src/WinTune.Core/NativeInterop/
git commit -m "feat(core): add LibraryImport bindings for psapi, shell32, dnsapi"
```

### Task 1.3: MonitorService — TDD

**Files:**
- Create: `src/WinTune.Core/Services/IMonitorService.cs`
- Create: `src/WinTune.Core/Services/MonitorService.cs`
- Create: `tests/WinTune.Core.Tests/MonitorServiceTests.cs`

- [ ] **Step 1: Define the interface**

```csharp
// IMonitorService.cs
using WinTune.Core.Models;

namespace WinTune.Core.Services;

public interface IMonitorService
{
    Task<PerfSnapshot> GetPerfSnapshotAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProcessSnapshot>> GetTopProcessesAsync(int count = 10, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the failing tests**

```csharp
// MonitorServiceTests.cs
using FluentAssertions;
using WinTune.Core.Services;
using Xunit;

namespace WinTune.Core.Tests;

public class MonitorServiceTests
{
    [Fact]
    public async Task GetPerfSnapshotAsync_returns_plausible_values()
    {
        IMonitorService sut = new MonitorService();

        var snap = await sut.GetPerfSnapshotAsync();

        snap.CpuPct.Should().BeInRange(0, 100);
        snap.RamPct.Should().BeInRange(0, 100);
        snap.DiskPct.Should().BeInRange(0, 100);
        snap.RamTotalGB.Should().BeGreaterThan(0);
        snap.DiskTotalGB.Should().BeGreaterThan(0);
        snap.Timestamp.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GetTopProcessesAsync_returns_at_most_count_entries_sorted_desc_by_ram()
    {
        IMonitorService sut = new MonitorService();

        var top = await sut.GetTopProcessesAsync(5);

        top.Should().NotBeEmpty();
        top.Count.Should().BeLessThanOrEqualTo(5);
        top.Should().BeInDescendingOrder(p => p.RamMB);
        top.Should().OnlyContain(p => p.Id > 0);
        top.Should().OnlyContain(p => !string.IsNullOrEmpty(p.Name));
    }

    [Fact]
    public async Task GetTopProcessesAsync_respects_cancellation()
    {
        IMonitorService sut = new MonitorService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sut.GetTopProcessesAsync(10, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
```

- [ ] **Step 3: Run tests and verify failure**

```powershell
dotnet test --filter "FullyQualifiedName~MonitorServiceTests"
```

Expected: FAIL — MonitorService does not exist.

- [ ] **Step 4: Implement MonitorService**

```csharp
// MonitorService.cs
using System.Diagnostics;
using System.IO;
using System.Management;
using WinTune.Core.Models;

namespace WinTune.Core.Services;

public sealed class MonitorService : IMonitorService
{
    private static readonly Lazy<PerformanceCounter> CpuCounter = new(() =>
    {
        var pc = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
        pc.NextValue();              // first call always returns 0
        Thread.Sleep(100);
        return pc;
    });

    public Task<PerfSnapshot> GetPerfSnapshotAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            int cpuPct = ReadCpuPct();
            (double ramUsed, double ramTotal, double ramPct) = ReadMemory();
            (double diskUsed, double diskFree, double diskTotal, double diskPct) = ReadDisk();
            return new PerfSnapshot(
                cpuPct, ramUsed, ramTotal, ramPct,
                diskUsed, diskFree, diskTotal, diskPct,
                DateTime.Now);
        }, ct);

    public Task<IReadOnlyList<ProcessSnapshot>> GetTopProcessesAsync(int count = 10, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<ProcessSnapshot>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var snapshots = new List<ProcessSnapshot>();
            foreach (var p in Process.GetProcesses())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long ws = p.WorkingSet64;
                    if (ws <= 0) continue;
                    string startStr = "-";
                    try { startStr = p.StartTime.ToString("HH:mm:ss"); } catch { }
                    snapshots.Add(new ProcessSnapshot(
                        Name: p.ProcessName,
                        Id: p.Id,
                        RamMB: Math.Round(ws / 1024.0 / 1024.0, 1),
                        Threads: p.Threads.Count,
                        StartTime: startStr));
                }
                catch { /* protected/system processes throw on access */ }
                finally { p.Dispose(); }
            }
            return snapshots
                .OrderByDescending(p => p.RamMB)
                .Take(count)
                .ToList();
        }, ct);

    private static int ReadCpuPct()
    {
        try
        {
            float v = CpuCounter.Value.NextValue();
            return (int)Math.Round(v, MidpointRounding.AwayFromZero);
        }
        catch
        {
            // WMI fallback
            using var mc = new ManagementClass("Win32_Processor");
            using var moc = mc.GetInstances();
            int sum = 0, n = 0;
            foreach (ManagementObject mo in moc)
            {
                using (mo)
                {
                    if (mo["LoadPercentage"] is ushort load) { sum += load; n++; }
                }
            }
            return n == 0 ? 0 : sum / n;
        }
    }

    private static (double used, double total, double pct) ReadMemory()
    {
        using var search = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
        foreach (ManagementObject mo in search.Get())
        {
            using (mo)
            {
                ulong totalKB = (ulong)mo["TotalVisibleMemorySize"];
                ulong freeKB  = (ulong)mo["FreePhysicalMemory"];
                double totalGB = totalKB / 1024.0 / 1024.0;
                double usedGB  = (totalKB - freeKB) / 1024.0 / 1024.0;
                double pct     = totalKB == 0 ? 0 : Math.Round((totalKB - freeKB) * 100.0 / totalKB, 1);
                return (Math.Round(usedGB, 2), Math.Round(totalGB, 2), pct);
            }
        }
        return (0, 0, 0);
    }

    private static (double used, double free, double total, double pct) ReadDisk()
    {
        var sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var di = new DriveInfo(sysDrive);
        if (!di.IsReady) return (0, 0, 0, 0);
        double totalGB = di.TotalSize / 1024.0 / 1024.0 / 1024.0;
        double freeGB  = di.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
        double usedGB  = totalGB - freeGB;
        double pct = totalGB == 0 ? 0 : Math.Round(usedGB * 100.0 / totalGB, 1);
        return (Math.Round(usedGB, 2), Math.Round(freeGB, 2), Math.Round(totalGB, 2), pct);
    }
}
```

- [ ] **Step 5: Run tests and verify pass**

```powershell
dotnet test --filter "FullyQualifiedName~MonitorServiceTests"
```

Expected: PASS — all 3 tests.

- [ ] **Step 6: Commit**

```powershell
git add src/WinTune.Core/Services/IMonitorService.cs src/WinTune.Core/Services/MonitorService.cs tests/WinTune.Core.Tests/MonitorServiceTests.cs
git commit -m "feat(core): port MonitorService with CPU/RAM/Disk snapshot + top-N processes"
```

### Task 1.4: BoostService — TDD

**Files:**
- Create: `src/WinTune.Core/Services/IBoostService.cs`
- Create: `src/WinTune.Core/Services/BoostService.cs`
- Create: `tests/WinTune.Core.Tests/BoostServiceTests.cs`

- [ ] **Step 1: Define the interface**

```csharp
// IBoostService.cs
using WinTune.Core.Models;

namespace WinTune.Core.Services;

public interface IBoostService
{
    Task<WorkingSetResult> ClearWorkingSetsAsync(CancellationToken ct = default);
    Task<ExplorerRestartResult> RestartExplorerAsync(CancellationToken ct = default);
    Task<DnsFlushResult> FlushDnsCacheAsync(CancellationToken ct = default);
}
```

- [ ] **Step 2: Tests**

```csharp
// BoostServiceTests.cs
using FluentAssertions;
using WinTune.Core.Services;
using Xunit;

namespace WinTune.Core.Tests;

public class BoostServiceTests
{
    [Fact]
    public async Task ClearWorkingSetsAsync_trims_at_least_some_processes_and_does_not_throw()
    {
        IBoostService sut = new BoostService();

        var result = await sut.ClearWorkingSetsAsync();

        result.ProcessesTrimmed.Should().BeGreaterThan(0);
        // We don't assert BytesFreedEstimate strictly because OS reschedules pages quickly
    }

    [Fact]
    public async Task FlushDnsCacheAsync_returns_success_true()
    {
        IBoostService sut = new BoostService();

        var result = await sut.FlushDnsCacheAsync();

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    // RestartExplorerAsync is destructive (kills explorer.exe); not run in CI
    // Local-only smoke test marked with explicit Trait.
    [Fact(Skip = "destructive: kills explorer.exe — run manually")]
    public async Task RestartExplorerAsync_smoke()
    {
        IBoostService sut = new BoostService();
        var result = await sut.RestartExplorerAsync();
        result.ProcessesRestarted.Should().BeGreaterThanOrEqualTo(0);
    }
}
```

- [ ] **Step 3: Run tests, verify failure**

```powershell
dotnet test --filter "FullyQualifiedName~BoostServiceTests"
```

Expected: FAIL.

- [ ] **Step 4: Implement BoostService**

```csharp
// BoostService.cs
using System.Diagnostics;
using WinTune.Core.Models;
using WinTune.Core.NativeInterop;

namespace WinTune.Core.Services;

public sealed class BoostService : IBoostService
{
    public Task<WorkingSetResult> ClearWorkingSetsAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            int trimmed = 0, skipped = 0;
            long beforeTotal = 0, afterTotal = 0;

            foreach (var p in Process.GetProcesses())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    beforeTotal += p.WorkingSet64;
                    if (PsApi.EmptyWorkingSet(p.Handle)) trimmed++;
                    else skipped++;
                }
                catch { skipped++; }
                finally { p.Dispose(); }
            }

            Thread.Sleep(600);

            foreach (var p in Process.GetProcesses())
            {
                try { afterTotal += p.WorkingSet64; }
                catch { }
                finally { p.Dispose(); }
            }

            long freed = Math.Max(0, beforeTotal - afterTotal);
            return new WorkingSetResult(trimmed, skipped, freed);
        }, ct);

    public Task<ExplorerRestartResult> RestartExplorerAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            int killed = 0;
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); killed++; } catch { }
                finally { p.Dispose(); }
            }
            Thread.Sleep(1000);
            if (Process.GetProcessesByName("explorer").Length == 0)
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            }
            return new ExplorerRestartResult(killed);
        }, ct);

    public Task<DnsFlushResult> FlushDnsCacheAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                bool ok = DnsApi.DnsFlushResolverCache();
                return ok
                    ? new DnsFlushResult(true, null)
                    : new DnsFlushResult(false, "DnsFlushResolverCache returned false");
            }
            catch (Exception ex)
            {
                return new DnsFlushResult(false, ex.Message);
            }
        }, ct);
}
```

- [ ] **Step 5: Run tests, verify pass**

```powershell
dotnet test --filter "FullyQualifiedName~BoostServiceTests"
```

Expected: PASS (2 ran, 1 skipped).

- [ ] **Step 6: Commit**

```powershell
git add src/WinTune.Core/Services/IBoostService.cs src/WinTune.Core/Services/BoostService.cs tests/WinTune.Core.Tests/BoostServiceTests.cs
git commit -m "feat(core): port BoostService with EmptyWorkingSet P/Invoke and DNS flush"
```

---

## Phase 2 — CleanupService + StartupService

### Task 2.1: TempDirectory test helper

**Files:** Create `tests/WinTune.Core.Tests/TestHelpers/TempDirectory.cs`.

- [ ] **Step 1: Implementation**

```csharp
namespace WinTune.Core.Tests.TestHelpers;

public sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wintune-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string CreateFile(string name, byte[] content)
    {
        var full = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string CreateFile(string name, string content) => CreateFile(name, System.Text.Encoding.UTF8.GetBytes(content));

    public void CreateJunction(string linkName, string targetDir)
    {
        var linkPath = System.IO.Path.Combine(Path, linkName);
        Directory.CreateSymbolicLink(linkPath, targetDir);   // .NET 6+; junction-equivalent
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
```

- [ ] **Step 2: Commit**

```powershell
git add tests/WinTune.Core.Tests/TestHelpers/
git commit -m "test: add TempDirectory helper for filesystem tests"
```

### Task 2.2: CleanupService — TDD with safety invariants

**Files:**
- Create: `src/WinTune.Core/Services/ICleanupService.cs`
- Create: `src/WinTune.Core/Services/CleanupService.cs`
- Create: `tests/WinTune.Core.Tests/CleanupServiceTests.cs`

> NOTE TO IMPLEMENTER: Cleanup is the highest-risk service. The PowerShell version (`modules/Cleanup.psm1`) has commits explicitly documenting bugs that cost real work — read it before writing C# (`git show 711fa7a`, `2ffbc2a`, `4698564`). The C# port MUST preserve:
> 1. **Reparse-point skip during recursion** — never follow junctions/symlinks. (See `git show 2ffbc2a`.)
> 2. **`finally` block service restart** — `wuauserv` and `bits` MUST come back even if delete throws. (See `git show 711fa7a`.)
> 3. **Browser-running guard** — refuse to clear cache if browser is open.
> 4. **Path constants from env vars, not hardcoded `C:\Windows`** — (See `git show 4698564`.)

- [ ] **Step 1: Interface**

```csharp
// ICleanupService.cs
using WinTune.Core.Models;

namespace WinTune.Core.Services;

public interface ICleanupService
{
    IReadOnlyList<CleanupTarget> GetAvailableTargets();

    Task<IReadOnlyList<CleanupResult>> InvokeCleanupAsync(
        IReadOnlyCollection<CleanupTarget> targets,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken ct = default);

    string? GetLastCleanupLogPath();
    string FormatBytes(long bytes);
}
```

- [ ] **Step 2: Tests for safety invariants** — write enough tests that a regression in the four bugs above is caught:

```csharp
// CleanupServiceTests.cs
using FluentAssertions;
using WinTune.Core.Models;
using WinTune.Core.Services;
using WinTune.Core.Tests.TestHelpers;
using Xunit;

namespace WinTune.Core.Tests;

public class CleanupServiceTests
{
    [Fact]
    public void Reparse_points_inside_target_are_NOT_followed()
    {
        // Arrange: create a temp dir, make it look like a "cleanup target"
        // by overriding the target's path resolver. Inside it, place a junction
        // pointing to ANOTHER temp dir that contains a sentinel file. After
        // running cleanup on the target, the sentinel must still exist.
        using var sentinelDir = new TempDirectory();
        var sentinelPath = sentinelDir.CreateFile("sentinel.txt", "DO NOT DELETE");
        using var targetDir = new TempDirectory();
        targetDir.CreateFile("trash.txt", "ok to delete");
        targetDir.CreateJunction("link-to-sentinel", sentinelDir.Path);

        // Act: drive the recursive deleter against targetDir.Path directly,
        // bypassing the env-var lookup. This requires CleanupService to expose
        // an internal `RemoveDirectoryContentsSafe(string path)` method.
        var sut = new CleanupService();
        var (filesRemoved, bytesFreed, errors) = sut.RemoveDirectoryContentsSafeForTest(targetDir.Path);

        // Assert: sentinel survived
        File.Exists(sentinelPath).Should().BeTrue("reparse-point junction must NOT be followed during recursive delete");
        filesRemoved.Should().BeGreaterThan(0);
    }

    [Fact]
    public void FormatBytes_is_human_readable()
    {
        var sut = new CleanupService();
        sut.FormatBytes(0).Should().Be("0 B");
        sut.FormatBytes(1023).Should().Be("1023 B");
        sut.FormatBytes(1024).Should().Be("1.0 KB");
        sut.FormatBytes(1024L * 1024).Should().Be("1.0 MB");
        sut.FormatBytes(1024L * 1024 * 1024).Should().Be("1.0 GB");
    }

    [Fact]
    public async Task InvokeCleanupAsync_emits_progress_for_each_target()
    {
        var sut = new CleanupService();
        var progressEvents = new List<CleanupProgress>();
        var progress = new Progress<CleanupProgress>(p => progressEvents.Add(p));

        // UserTemp is the safest real target to test against
        var results = await sut.InvokeCleanupAsync(
            new[] { CleanupTarget.UserTemp },
            progress);

        results.Should().HaveCount(1);
        progressEvents.Should().ContainSingle(p => p.Phase == "Start");
        progressEvents.Should().ContainSingle(p => p.Phase == "TargetDone");
    }
}
```

(Test for finally-block service restart requires admin + an integration setting; document but skip in unit suite — covered by manual smoke or an integration test fixture.)

- [ ] **Step 3: Verify failure**

```powershell
dotnet test --filter "FullyQualifiedName~CleanupServiceTests"
```

Expected: FAIL — type does not exist.

- [ ] **Step 4: Implement CleanupService**

> Implementation guidance follows the spec from the Explore mappers. Key safety methods:
>
> - `RemoveDirectoryContentsSafe(string path, CancellationToken ct)` — enumerates `DirectoryInfo.EnumerateFileSystemInfos(...)`, for each entry checks `(info.Attributes & FileAttributes.ReparsePoint) != 0` and skips if true, otherwise recurses (for dirs) or deletes (for files). Catches `IOException` / `UnauthorizedAccessException` per entry into an errors list, never rethrows.
> - `WindowsUpdate` target wraps the delete in `try { StopServices(["wuauserv","bits"]); DeleteContents(...); } finally { StartServices(["wuauserv","bits"]); }`.
> - `EdgeCache` / `ChromeCache` / `FirefoxCache` check `Process.GetProcessesByName("msedge"|"chrome"|"firefox").Length > 0` and short-circuit with `Skipped = true, SkipReason = "Browser is running"`.
> - `RecycleBin` calls `Shell32.SHEmptyRecycleBinW(IntPtr.Zero, null, NoConfirmation|NoProgressUI|NoSound)`.
> - `DnsCache` calls `DnsApi.DnsFlushResolverCache()`.
> - All paths derived from `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)`, `Environment.GetEnvironmentVariable("TEMP")`, `Environment.GetEnvironmentVariable("SystemRoot")`, etc. NEVER hardcoded `C:\Windows` etc.
>
> `RemoveDirectoryContentsSafeForTest(string path)` is an `internal` method exposed via `[InternalsVisibleTo("WinTune.Core.Tests")]` in `WinTune.Core.csproj`.
>
> Log file: `%LOCALAPPDATA%\WinTune\logs\cleanup-yyyyMMdd-HHmmss.log`. Service writes Serilog-style entries on errors; `GetLastCleanupLogPath()` returns the most recent file.

(The full implementation is ~250 lines. Implementer reads `modules/Cleanup.psm1` line-by-line and ports each helper.)

- [ ] **Step 5: Verify tests pass**

```powershell
dotnet test --filter "FullyQualifiedName~CleanupServiceTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/WinTune.Core/Services/ICleanupService.cs src/WinTune.Core/Services/CleanupService.cs tests/WinTune.Core.Tests/CleanupServiceTests.cs
git commit -m "feat(core): port CleanupService with reparse-point skip + finally-block service restart"
```

### Task 2.3: StartupService — TDD

**Files:**
- Create: `src/WinTune.Core/Services/IStartupService.cs`
- Create: `src/WinTune.Core/Services/StartupService.cs`
- Create: `tests/WinTune.Core.Tests/StartupServiceTests.cs`

- [ ] **Step 1: Interface**

```csharp
public interface IStartupService
{
    Task<IReadOnlyList<StartupEntry>> GetStartupAppsAsync(CancellationToken ct = default);
    void OpenStartupTaskManager();
}
```

- [ ] **Step 2: Tests** — assert non-empty list on a real Windows box, distinct entries, every entry has Name+Location.

- [ ] **Step 3: Implementation** — three sources merged + `.DistinctBy(e => (e.User, e.Name))`:
  1. WMI `Win32_StartupCommand` via `ManagementObjectSearcher`.
  2. Registry HKLM + HKCU `Run` and `RunOnce` keys via `Microsoft.Win32.RegistryKey`.
  3. Common + per-user Startup folder via `Environment.GetFolderPath(SpecialFolder.Startup)` and `.CommonStartup`.

- [ ] **Step 4: Verify pass + commit**

```powershell
dotnet test --filter "FullyQualifiedName~StartupServiceTests"
git add src/WinTune.Core/Services/IStartupService.cs src/WinTune.Core/Services/StartupService.cs tests/WinTune.Core.Tests/StartupServiceTests.cs
git commit -m "feat(core): port StartupService (WMI + registry + Startup folders)"
```

---

## Phase 3 — DiagnoseService + DedupService

### Task 3.1: DiagnoseService — 11 checks + 5 fixes

**Files:** standard service trio.

The 11 checks (from `Diagnose.psm1`):
1. Disk health — WMI `MSStorageDriver_FailurePredictStatus` if available, else free-space heuristic via `DriveInfo`.
2. Free-space pressure — `DriveInfo.AvailableFreeSpace / TotalSize < 0.10`.
3. Multiple cloud sync shell extensions — enumerate `explorer.exe` modules; flag if ≥2 of OneDrive/Dropbox/GoogleDrive/Box/iCloud DLLs loaded.
4. Quick Access bloat — count files in `%APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations` > 50.
5. Search index empty — directory size of `%PROGRAMDATA%\Microsoft\Search\Data\Applications\Windows\Projects\SystemIndex\Indexer\CiFiles` < 10 MB.
6. DiagTrack telemetry — `ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == "DiagTrack")?.Status == Running`.
7. Win11 right-click overlay enabled — registry presence of `HKCU\Software\Classes\CLSID\{86ca1aa0-...}` is *missing* (default Win11) → flag yellow.
8. Pagefile placement — `Win32_PageFileSetting`; flag if on system drive AND another fixed drive has more free space.
9. Startup app count — WMI `Win32_StartupCommand`; flag if > 15.
10. RAM pressure — `(used/total) > 0.85`.
11. Disk free percentage per drive — already covered by #2; per-drive list.

The 5 fixes:
- ResetQuickAccess — `File.Delete` recursively in Recent + AutomaticDestinations + CustomDestinations.
- DisableTelemetry — `ServiceController.Stop("DiagTrack")` + `WaitForStatus(Stopped)` + `ChangeServiceConfig` to Disabled (PInvoke or use sc.exe via Process.Start) + `RegistryKey.SetValue("HKLM\Software\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0)`.
- EnableClassicRightClick — create `HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32` with default value `""`.
- DisableClassicRightClick — delete that key.
- RebuildSearchIndex — `ServiceController.Stop("WSearch")` + delete index data dir + restart service.

Each check returns a `Finding`; each fix returns a `DiagnoseResult`.

Tasks 3.1.1 through 3.1.16 follow the pattern:
1. Add interface method
2. Add test (fixture-mocked where possible; live-only for the rest with `[Trait("Category","Live")]`)
3. Run, verify failure
4. Implement
5. Run, verify pass
6. Commit per group of 3-4 checks (`feat(core): port DiagnoseService checks 1-3`, etc.)

### Task 3.2: DedupService — two-pass

**Files:** standard service trio.

- [ ] **Step 1: Interface**

```csharp
public interface IDedupService
{
    Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(
        IReadOnlyCollection<string> roots,
        long minSizeBytes = 1L * 1024 * 1024,
        bool includeHidden = false,
        IReadOnlySet<string>? excludeExtensions = null,
        IProgress<DedupeProgress>? progress = null,
        CancellationToken ct = default);

    Task<RemovalResult> RemoveDuplicateFilesAsync(
        IReadOnlyCollection<string> paths,
        bool permanent = false,
        CancellationToken ct = default);

    IReadOnlyList<string> GetDefaultScanRoots();
}
```

- [ ] **Step 2: Tests** — temp dir with 3 identical files + 1 unique → 1 group of 3, 0 unique. Reparse-point file skipped. Files below `minSizeBytes` skipped.

- [ ] **Step 3: Implementation** — two-pass:
  1. Enumerate via `DirectoryInfo.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })`. Filter by `.Length >= minSizeBytes`, `(Attributes & (ReparsePoint|Offline|System)) == 0`, extension not in excludes. Group by `Length`.
  2. For each size-group with ≥2 files, hash with `SHA1.HashData(File.OpenRead(path))` (chunked 64KB read). Group by hash. Yield groups with ≥2.
  
  `RemoveDuplicateFilesAsync(paths, permanent=false)`: use `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)`. Permanent path uses `File.Delete`.

- [ ] **Step 4: Pass + commit.**

---

## Phase 4 — WPF UI (MVVM)

> **Approach:** Port `ui/MainWindow.xaml` largely unchanged. Replace inline event handlers with `{Binding Command}`. Wire each tab's content to a tab-specific ViewModel via DataContext. Use `CommunityToolkit.Mvvm` for `[ObservableProperty]` and `[RelayCommand]`. Inject services via DI.

### Task 4.1: App.xaml + DI bootstrap

**Files:**
- Create: `src/WinTune.App/App.xaml`
- Create: `src/WinTune.App/App.xaml.cs`

- [ ] **Step 1: App.xaml**

```xml
<Application x:Class="WinTune.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnMainWindowClose">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Resources/Styles.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 2: App.xaml.cs**

```csharp
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using WinTune.App.ViewModels;
using WinTune.Core.Services;

namespace WinTune.App;

public partial class App : Application
{
    public static IHost? Host { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinTune", "logs", "wintune-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMonitorService, MonitorService>();
                services.AddSingleton<IBoostService, BoostService>();
                services.AddSingleton<ICleanupService, CleanupService>();
                services.AddSingleton<IStartupService, StartupService>();
                services.AddSingleton<IDiagnoseService, DiagnoseService>();
                services.AddSingleton<IDedupService, DedupService>();
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<DashboardViewModel>();
                services.AddSingleton<CleanViewModel>();
                services.AddSingleton<BoostViewModel>();
                services.AddSingleton<DiagnoseViewModel>();
                services.AddSingleton<DedupeViewModel>();
                services.AddTransient<MainWindow>();
            })
            .Build();

        var window = Host.Services.GetRequiredService<MainWindow>();
        window.DataContext = Host.Services.GetRequiredService<MainWindowViewModel>();
        window.Show();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Host?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
```

- [ ] **Step 3: Commit**

### Task 4.2: ViewModels per tab

For each ViewModel, the pattern is:

```csharp
public partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IMonitorService _monitor;
    private readonly DispatcherTimer _timer;

    [ObservableProperty] private int cpuPct;
    [ObservableProperty] private int ramPct;
    [ObservableProperty] private int diskPct;
    [ObservableProperty] private string ramDisplay = "";
    [ObservableProperty] private string diskDisplay = "";

    public ObservableCollection<ProcessSnapshot> TopProcesses { get; } = new();

    public DashboardViewModel(IMonitorService monitor)
    {
        _monitor = monitor;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var snap = await _monitor.GetPerfSnapshotAsync();
            CpuPct = snap.CpuPct;
            RamPct = (int)Math.Round(snap.RamPct);
            DiskPct = (int)Math.Round(snap.DiskPct);
            RamDisplay = $"{snap.RamUsedGB:F1} / {snap.RamTotalGB:F1} GB";
            DiskDisplay = $"{snap.DiskUsedGB:F1} / {snap.DiskTotalGB:F1} GB";

            var top = await _monitor.GetTopProcessesAsync(10);
            TopProcesses.Clear();
            foreach (var p in top) TopProcesses.Add(p);
        }
        catch { /* don't crash dashboard on transient failure */ }
    }

    public void Dispose() => _timer.Stop();
}
```

CleanViewModel, BoostViewModel, DiagnoseViewModel, DedupeViewModel follow the same pattern with `[RelayCommand]` methods that bind to buttons. Cancellation is per-VM `CancellationTokenSource`, cancelled in `Dispose()`.

(Tasks 4.2.1–4.2.5 are one VM per task, ~30-50 lines each.)

### Task 4.3: MainWindow.xaml port

Migrate `ui/MainWindow.xaml` directly. Changes:
- Set `x:Class="WinTune.App.MainWindow"` on root Window.
- Each tab's content moves into a UserControl (`Views/DashboardView.xaml`, etc.) with its own DataContext.
- Replace any `x:Name`-based code-behind manipulation with `{Binding}` to ViewModel properties.
- Buttons: replace `Click=...` (none in current XAML — good) with `Command="{Binding RunCleanupCommand}"`.

Each Button → Command mapping is documented in `docs/superpowers/plans/2026-05-09-csharp-wpf-port-bindings.md` (generated by the explorer agent and committed alongside the plan).

### Task 4.4: Manual smoke test

- [ ] Run from VS or `dotnet run --project src/WinTune.App` — UAC prompt fires, window opens, dashboard updates every 2s, every tab loads without exception.

---

## Phase 5 — Publish + CI

### Task 5.1: Single-file publish

- [ ] **Step 1: Publish locally**

```powershell
dotnet publish src/WinTune.App/WinTune.App.csproj -c Release -r win-x64 -o publish/win-x64
```

Expected: `publish/win-x64/WinTune.exe` ~30-50 MB, no other files (or just the .pdb).

- [ ] **Step 2: Smoke-test the exe**

Copy `WinTune.exe` to a clean directory (no .NET runtime adjacent), double-click. Expected: UAC prompt → window opens.

- [ ] **Step 3: Document**

Add to `README.md`:

```markdown
## Build the C# version

Requires .NET 10 SDK.

```powershell
dotnet publish src/WinTune.App/WinTune.App.csproj -c Release -r win-x64 -o publish/win-x64
```

The output `WinTune.exe` is a single self-contained file — no .NET runtime needed on the target machine.
```

- [ ] **Step 4: Commit**

### Task 5.2: GitHub Actions

**Files:** Create `.github/workflows/dotnet.yml`.

```yaml
name: dotnet

on:
  push:
    branches: [main, csharp-port]
  pull_request:
    branches: [main]

jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet restore
      - run: dotnet build -c Release --no-restore
      - run: dotnet test -c Release --no-build --logger "trx;LogFileName=test-results.trx"
      - run: dotnet publish src/WinTune.App/WinTune.App.csproj -c Release -r win-x64 -o publish/win-x64
      - uses: actions/upload-artifact@v4
        with:
          name: WinTune-win-x64
          path: publish/win-x64/WinTune.exe
```

- [ ] **Commit + push** — verify CI green.

---

## Self-review checklist

- [x] Every Phase 0/1 step has complete, runnable code or an exact command.
- [x] Tests precede implementation in every TDD task.
- [x] Phases 2/3/4 use detailed-pattern + brief-task style because the patterns repeat — implementer reads the corresponding `.psm1` to fill in details, with explicit invariants called out.
- [x] No "TBD" / "implement later" placeholders in P0/P1.
- [x] File paths are absolute or repo-relative.
- [x] Safety invariants (reparse-point skip, finally-block service restart, browser guard) are tested where unit-testable.
- [x] Commits are frequent and atomic — one per task.
- [x] Branch strategy stated (`csharp-port` until parity, then flip).

---

## Execution mode

**Subagent-Driven** (recommended) — fresh subagent per task, parent reviews each commit before dispatching the next. Use `superpowers:subagent-driven-development` skill.

Multi-session: this plan covers ~1-2 weeks of work. The plan file is the durable handoff between sessions. Each session starts by reading this plan and the most recent git log to figure out where to resume.
