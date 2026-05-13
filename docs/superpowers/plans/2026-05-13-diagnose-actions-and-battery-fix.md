# Diagnose Per-Finding Actions + Battery Throttling Fix — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-finding actions to the Diagnose tab (master-detail UI) and ship the first detector that uses them — a battery-throttling detector with a Tier B reversible force-fix (DC CPU max → 100%, DC EPP → Performance, DC cooling → Active).

**Architecture:** Two coupled feature slices. Slice A extends the `Finding` record with `Id` and `Actions`, restructures the Diagnose tab XAML into a 2/3-column master-detail, and replaces the existing five global `RelayCommand` buttons with an `ActionId → handler` dispatch dictionary inside `DiagnoseViewModel`. Slice B introduces a new `IPowerService` in `WinTune.Core` that wraps `powercfg.exe` (via a unit-testable `IProcessRunner` abstraction) to read/write DC power settings, persist a one-shot pre-fix snapshot at `%APPDATA%\WinTune\battery-snapshot.json`, and expose `Capture`/`ApplyTierB`/`ApplySingle`/`RestorePrior`. The detector for battery throttling becomes the 12th check inside `DiagnoseService.InvokeDiagnosticsAsync`, and its `Finding.Actions` flow through the same registry every other detector uses — proving the architecture by being its first non-trivial consumer.

**Tech Stack:**
- .NET 10 SDK (per `global.json`), WPF (Windows.Desktop)
- CommunityToolkit.Mvvm 8.x (already in `WinTune.App`)
- Microsoft.Extensions.DependencyInjection / Hosting (already wired in `App.xaml.cs`)
- Serilog (already wired)
- xUnit + FluentAssertions (existing test project conventions — no Moq; hand-rolled stub classes)
- `powercfg.exe` (shipped in every Windows SKU; on PATH)
- `System.Text.Json` for snapshot persistence

**Spec:** `docs/superpowers/specs/2026-05-13-diagnose-actions-and-battery-fix-design.md` (`b5b0293`).

**Branch:** Work on `main` directly (small-team repo, no protected branch). Commit frequently; the plan is decomposed so each task ends in a green build.

---

## File Structure

**New files:**

| Path                                                             | Responsibility                                                          |
| ---------------------------------------------------------------- | ----------------------------------------------------------------------- |
| `src/WinTune.Core/Models/FindingAction.cs`                       | Per-finding action record (`ActionId`, `Label`, optional `Confirm`).    |
| `src/WinTune.Core/Models/FindingActionResult.cs`                 | Result returned from a handler back to the ViewModel.                   |
| `src/WinTune.Core/Models/PowerState.cs`                          | Captured DC power settings snapshot shape.                              |
| `src/WinTune.Core/Models/PowerFixResult.cs`                      | Result returned from PowerService write operations.                     |
| `src/WinTune.Core/Models/BatteryKnob.cs`                         | Enum of individual fix targets (`CpuMax`, `Epp`, `Cooling`).            |
| `src/WinTune.Core/Services/IProcessRunner.cs`                    | Interface abstracting `Process.Start` for testability.                  |
| `src/WinTune.Core/Services/ProcessRunner.cs`                     | Production impl — spawns `powercfg.exe`, captures stdout/stderr/exit.   |
| `src/WinTune.Core/Services/PowerCfgIds.cs`                       | Static constants — well-known subgroup and setting GUIDs.               |
| `src/WinTune.Core/Services/IPowerService.cs`                     | Interface — capture / apply / restore / single-knob.                    |
| `src/WinTune.Core/Services/PowerService.cs`                      | Implementation: parsing, snapshot persistence, powercfg invocation.     |
| `src/WinTune.App/Converters/NullToCollapsedConverter.cs`         | XAML converter — null → `Visibility.Collapsed`.                         |
| `src/WinTune.App/Converters/BoolToBrushConverter.cs`             | XAML converter — `true` → red brush, `false` → default foreground.      |
| `tests/WinTune.Core.Tests/TestHelpers/StubProcessRunner.cs`      | Test double for `IProcessRunner` — scripted responses keyed by argv.    |
| `tests/WinTune.Core.Tests/PowerServiceTests.cs`                  | xUnit tests for capture parsing, snapshot, apply, restore.              |
| `tests/WinTune.Core.Tests/DiagnoseServiceBatteryTests.cs`        | xUnit tests for the battery detector across `PowerState` permutations. |
| `tests/WinTune.Core.Tests/FindingActionContractTests.cs`         | Static contract: every emitted `ActionId` has a handler.                |

**Modified files:**

| Path                                                            | Change                                                                              |
| --------------------------------------------------------------- | ----------------------------------------------------------------------------------- |
| `src/WinTune.Core/Models/Finding.cs`                            | Add `Id` (first positional) and `Actions` (last positional). All callers updated.   |
| `src/WinTune.Core/Services/DiagnoseService.cs`                  | New ctor `(IPowerService)`; new `CheckBatteryThrottling`; attach `Actions` on existing 11 findings. |
| `src/WinTune.App/ViewModels/DiagnoseViewModel.cs`               | New selection + dispatch; delete 5 public `RelayCommand`s, replace with private handlers in registry; add `TabSwitchRequested` event. |
| `src/WinTune.App/App.xaml.cs`                                   | Register `IPowerService` + `IProcessRunner` in `ConfigureServices`.                 |
| `ui/MainWindow.xaml`                                            | Diagnose tab restructured to 2-column master-detail; delete WrapPanel of fix buttons; register converters as resources. |
| `tests/WinTune.Core.Tests/DiagnoseServiceTests.cs`              | Updated to new `Finding` ctor and new `DiagnoseService(IPowerService)` ctor.        |

---

## Task 1: Add `FindingAction` and `FindingActionResult` records

**Files:**
- Create: `src/WinTune.Core/Models/FindingAction.cs`
- Create: `src/WinTune.Core/Models/FindingActionResult.cs`
- Create: `tests/WinTune.Core.Tests/FindingActionTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/WinTune.Core.Tests/FindingActionTests.cs`:

```csharp
using FluentAssertions;
using WinTune.Core.Models;

namespace WinTune.Core.Tests;

public class FindingActionTests
{
    [Fact]
    public void FindingAction_carries_id_label_and_optional_confirm()
    {
        var a = new FindingAction("battery.unleash-all", "Unleash all", Confirm: null);

        a.ActionId.Should().Be("battery.unleash-all");
        a.Label.Should().Be("Unleash all");
        a.Confirm.Should().BeNull();
    }

    [Fact]
    public void FindingAction_with_confirm_text_round_trips()
    {
        var a = new FindingAction(
            "battery.restore",
            "Restore prior",
            Confirm: "Revert DC power settings to your prior values. Continue?");

        a.Confirm.Should().StartWith("Revert");
    }

    [Fact]
    public void FindingActionResult_success_has_no_errors()
    {
        var r = new FindingActionResult(Success: true, Note: "done", Errors: null);

        r.Success.Should().BeTrue();
        r.Errors.Should().BeNull();
    }

    [Fact]
    public void FindingActionResult_failure_carries_errors()
    {
        var r = new FindingActionResult(
            Success: false,
            Note: "powercfg failed",
            Errors: new[] { "exit code 1", "Access denied." });

        r.Success.Should().BeFalse();
        r.Errors.Should().HaveCount(2);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/WinTune.Core.Tests/WinTune.Core.Tests.csproj --filter FindingActionTests`
Expected: build failure — `FindingAction` and `FindingActionResult` types do not exist.

- [ ] **Step 3: Create `FindingAction.cs`**

Create `src/WinTune.Core/Models/FindingAction.cs`:

```csharp
namespace WinTune.Core.Models;

public sealed record FindingAction(
    string ActionId,
    string Label,
    string? Confirm);
```

- [ ] **Step 4: Create `FindingActionResult.cs`**

Create `src/WinTune.Core/Models/FindingActionResult.cs`:

```csharp
namespace WinTune.Core.Models;

public sealed record FindingActionResult(
    bool                     Success,
    string?                  Note,
    IReadOnlyList<string>?   Errors);
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/WinTune.Core.Tests/WinTune.Core.Tests.csproj --filter FindingActionTests`
Expected: 4/4 PASS.

- [ ] **Step 6: Commit**

```bash
git add src/WinTune.Core/Models/FindingAction.cs \
        src/WinTune.Core/Models/FindingActionResult.cs \
        tests/WinTune.Core.Tests/FindingActionTests.cs
git commit -m "feat(core): add FindingAction and FindingActionResult records"
```

---

## Task 2: Extend `Finding` with `Id` and `Actions`

**Files:**
- Modify: `src/WinTune.Core/Models/Finding.cs`
- Modify: `src/WinTune.Core/Services/DiagnoseService.cs` (every `new Finding(...)` call site)
- Modify: `tests/WinTune.Core.Tests/DiagnoseServiceTests.cs` (existing assertions on Finding properties)

This is a breaking change to a record used in 11 detector call sites. We update them all in one task — the build is broken in the middle of Step 3 by design, and green again after Step 4.

- [ ] **Step 1: Write the failing test for the new shape**

Append to `tests/WinTune.Core.Tests/FindingActionTests.cs`:

```csharp
public class FindingShapeTests
{
    [Fact]
    public void Finding_exposes_Id_and_Actions()
    {
        var f = new Finding(
            Id: "diag.demo",
            Severity: Severity.Green,
            Title: "All good",
            Detail: "Nothing to do",
            Hint: null,
            Actions: Array.Empty<FindingAction>());

        f.Id.Should().Be("diag.demo");
        f.Actions.Should().BeEmpty();
    }

    [Fact]
    public void Finding_with_actions_lists_them()
    {
        var f = new Finding(
            Id: "diag.demo",
            Severity: Severity.Red,
            Title: "Demo",
            Detail: "Detail",
            Hint: "Hint",
            Actions: new[] { new FindingAction("demo.fix", "Fix it", null) });

        f.Actions.Should().HaveCount(1);
        f.Actions[0].ActionId.Should().Be("demo.fix");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build src/WinTune.Core/WinTune.Core.csproj`
Expected: compile error — `Finding` constructor mismatch.

- [ ] **Step 3: Update `Finding` record**

Replace the contents of `src/WinTune.Core/Models/Finding.cs` with:

```csharp
namespace WinTune.Core.Models;

public enum Severity { Red, Yellow, Green }

public sealed record Finding(
    string                          Id,
    Severity                        Severity,
    string                          Title,
    string                          Detail,
    string?                         Hint,
    IReadOnlyList<FindingAction>    Actions);
```

(If the existing file has `Severity` in a separate file, leave it alone and only rewrite the `Finding` record. Use Grep on `enum Severity` to confirm.)

- [ ] **Step 4: Update every `new Finding(...)` call site in `DiagnoseService.cs`**

Open `src/WinTune.Core/Services/DiagnoseService.cs`. For each `new Finding(severity, title, detail, hint)` add `id` as the first argument and `Array.Empty<FindingAction>()` as the last. Use these `Id` values:

| Detector method            | `Id`               |
| -------------------------- | ------------------ |
| CheckDiskHealth            | `"diag.disk"`      |
| CheckFreeSpace             | `"diag.freespace"` |
| CheckCloudShellExtensions  | `"diag.shell-ext"` |
| CheckQuickAccessBloat      | `"diag.qa-bloat"`  |
| CheckSearchIndex           | `"diag.search-idx"`|
| CheckDiagTrack             | `"diag.diagtrack"` |
| CheckClassicRightClick     | `"diag.classic"`   |
| CheckPagefile              | `"diag.pagefile"`  |
| CheckStartupCount          | `"diag.startup"`   |
| CheckRamPressure           | `"diag.ram"`       |

For example, in `CheckDiskHealth` change:

```csharp
return new Finding(Severity.Green, "Disk SMART OK", detail, null);
```

to:

```csharp
return new Finding(
    Id: "diag.disk",
    Severity: Severity.Green,
    Title: "Disk SMART OK",
    Detail: detail,
    Hint: null,
    Actions: Array.Empty<FindingAction>());
```

Repeat for **every** `new Finding(...)` in the file. Do not leave a single positional-only call.

- [ ] **Step 5: Update `DiagnoseServiceTests.cs`**

If any test asserts on positional Finding construction, update it. The existing three tests (in `tests/WinTune.Core.Tests/DiagnoseServiceTests.cs`) inspect properties via `.Title`, `.Severity`, `.Detail` — those still work. But the new `Finding` ctor will be triggered indirectly when `InvokeDiagnosticsAsync` runs, so the tests just keep working.

Add one new test asserting the `Id` shape:

```csharp
[Fact]
public async Task InvokeDiagnosticsAsync_findings_have_non_empty_Id()
{
    IDiagnoseService sut = new DiagnoseService();
    var findings = await sut.InvokeDiagnosticsAsync();
    findings.Should().OnlyContain(f => !string.IsNullOrEmpty(f.Id));
}
```

(Leave the `DiagnoseService()` no-arg ctor for now — Task 13 changes it.)

- [ ] **Step 6: Run all tests**

Run: `dotnet test tests/WinTune.Core.Tests/WinTune.Core.Tests.csproj`
Expected: all green, including the two new shape tests and the new `non_empty_Id` test.

- [ ] **Step 7: Commit**

```bash
git add src/WinTune.Core/Models/Finding.cs \
        src/WinTune.Core/Services/DiagnoseService.cs \
        tests/WinTune.Core.Tests/FindingActionTests.cs \
        tests/WinTune.Core.Tests/DiagnoseServiceTests.cs
git commit -m "feat(core): extend Finding with Id + Actions; populate Id on all detectors"
```

---

## Task 3: `IProcessRunner` abstraction

**Files:**
- Create: `src/WinTune.Core/Services/IProcessRunner.cs`
- Create: `src/WinTune.Core/Services/ProcessRunner.cs`
- Create: `tests/WinTune.Core.Tests/ProcessRunnerTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/WinTune.Core.Tests/ProcessRunnerTests.cs`:

```csharp
using FluentAssertions;
using WinTune.Core.Services;

namespace WinTune.Core.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_executes_real_process_and_captures_stdout()
    {
        IProcessRunner sut = new ProcessRunner();

        var r = await sut.RunAsync("cmd.exe", new[] { "/c", "echo hello" }, CancellationToken.None);

        r.ExitCode.Should().Be(0);
        r.Stdout.Trim().Should().Be("hello");
        r.Stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_captures_non_zero_exit_code()
    {
        IProcessRunner sut = new ProcessRunner();

        var r = await sut.RunAsync("cmd.exe", new[] { "/c", "exit 7" }, CancellationToken.None);

        r.ExitCode.Should().Be(7);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ProcessRunnerTests`
Expected: build failure — `IProcessRunner` does not exist.

- [ ] **Step 3: Create the interface**

Create `src/WinTune.Core/Services/IProcessRunner.cs`:

```csharp
namespace WinTune.Core.Services;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken ct);
}
```

- [ ] **Step 4: Create the implementation**

Create `src/WinTune.Core/Services/ProcessRunner.cs`:

```csharp
using System.Diagnostics;
using System.Text;

namespace WinTune.Core.Services;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {fileName}");

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);

        await p.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new ProcessResult(p.ExitCode, stdout, stderr);
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter ProcessRunnerTests`
Expected: 2/2 PASS.

- [ ] **Step 6: Commit**

```bash
git add src/WinTune.Core/Services/IProcessRunner.cs \
        src/WinTune.Core/Services/ProcessRunner.cs \
        tests/WinTune.Core.Tests/ProcessRunnerTests.cs
git commit -m "feat(core): add IProcessRunner abstraction for testable shell-out"
```

---

## Task 4: `PowerCfgIds` constants

**Files:**
- Create: `src/WinTune.Core/Services/PowerCfgIds.cs`

(No test — these are constants. They'll be exercised indirectly by PowerService tests in Task 6+.)

- [ ] **Step 1: Create the constants file**

Create `src/WinTune.Core/Services/PowerCfgIds.cs`:

```csharp
namespace WinTune.Core.Services;

/// <summary>
/// Well-known powercfg subgroup and setting GUIDs. Values are documented in
/// Microsoft's "Power Settings" reference and are stable across Windows 10/11 SKUs.
/// </summary>
public static class PowerCfgIds
{
    // Subgroup: Processor power management
    public const string SubProcessor       = "54533251-82be-4824-96c1-47b60b740d00";

    // Settings under Processor subgroup
    public const string CpuMaxState        = "bc5038f7-23e0-4960-96da-33abaf5935ec";
    public const string CpuMinState        = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    public const string Epp                = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
    public const string CoolingPolicy      = "94d3a615-a899-4ac5-ae2b-e4d8f634367f";

    // Subgroup: Energy saver settings (Win10+)
    public const string SubEnergySaver     = "de830923-a562-41af-a086-e3a2c6bad2da";
    public const string BatterySaverThresh = "e69653ca-cf7f-4f05-aa73-cb833fa90ad4";
}
```

- [ ] **Step 2: Commit**

```bash
git add src/WinTune.Core/Services/PowerCfgIds.cs
git commit -m "feat(core): add PowerCfgIds well-known GUID constants"
```

---

## Task 5: `PowerState`, `PowerFixResult`, `BatteryKnob`

**Files:**
- Create: `src/WinTune.Core/Models/PowerState.cs`
- Create: `src/WinTune.Core/Models/PowerFixResult.cs`
- Create: `src/WinTune.Core/Models/BatteryKnob.cs`
- Create: `tests/WinTune.Core.Tests/PowerStateTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/WinTune.Core.Tests/PowerStateTests.cs`:

```csharp
using FluentAssertions;
using WinTune.Core.Models;

namespace WinTune.Core.Tests;

public class PowerStateTests
{
    [Fact]
    public void PowerState_records_all_dc_fields()
    {
        var s = new PowerState(
            ActiveScheme:              Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"),
            DcCpuMaxPct:               50,
            DcCpuMinPct:               5,
            DcEpp:                     80,
            DcCoolingPolicy:           "Passive",
            BatterySaverThresholdPct:  20);

        s.DcCpuMaxPct.Should().Be(50);
        s.DcEpp.Should().Be(80);
        s.DcCoolingPolicy.Should().Be("Passive");
    }

    [Fact]
    public void PowerFixResult_failure_carries_errors()
    {
        var r = new PowerFixResult(
            Success: false,
            After:   null,
            Note:    "powercfg failed",
            Errors:  new[] { "exit 1" });

        r.Success.Should().BeFalse();
        r.Errors.Should().ContainSingle();
    }

    [Fact]
    public void BatteryKnob_enum_has_three_members()
    {
        Enum.GetValues<BatteryKnob>().Should().BeEquivalentTo(new[]
        {
            BatteryKnob.CpuMax, BatteryKnob.Epp, BatteryKnob.Cooling
        });
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter PowerStateTests`
Expected: build failure — types do not exist.

- [ ] **Step 3: Create the records and enum**

Create `src/WinTune.Core/Models/PowerState.cs`:

```csharp
namespace WinTune.Core.Models;

public sealed record PowerState(
    Guid    ActiveScheme,
    int     DcCpuMaxPct,
    int     DcCpuMinPct,
    int     DcEpp,
    string  DcCoolingPolicy,
    int     BatterySaverThresholdPct);
```

Create `src/WinTune.Core/Models/PowerFixResult.cs`:

```csharp
namespace WinTune.Core.Models;

public sealed record PowerFixResult(
    bool                     Success,
    PowerState?              After,
    string?                  Note,
    IReadOnlyList<string>?   Errors);
```

Create `src/WinTune.Core/Models/BatteryKnob.cs`:

```csharp
namespace WinTune.Core.Models;

public enum BatteryKnob
{
    CpuMax,
    Epp,
    Cooling
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter PowerStateTests`
Expected: 3/3 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WinTune.Core/Models/PowerState.cs \
        src/WinTune.Core/Models/PowerFixResult.cs \
        src/WinTune.Core/Models/BatteryKnob.cs \
        tests/WinTune.Core.Tests/PowerStateTests.cs
git commit -m "feat(core): add PowerState, PowerFixResult, BatteryKnob models"
```

---

## Task 6: `StubProcessRunner` test helper

**Files:**
- Create: `tests/WinTune.Core.Tests/TestHelpers/StubProcessRunner.cs`

This helper lets the next tasks script `powercfg` responses without hitting the real binary. One shared helper avoids per-test duplication.

- [ ] **Step 1: Create the stub**

Create `tests/WinTune.Core.Tests/TestHelpers/StubProcessRunner.cs`:

```csharp
using WinTune.Core.Services;

namespace WinTune.Core.Tests.TestHelpers;

public sealed class StubProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, ProcessResult>     _scripted = new();
    public List<(string FileName, string[] Args)>          Calls     { get; } = new();

    /// <summary>
    /// Register a scripted response. <paramref name="argsContain"/> is the substring of
    /// the joined argument list to match — first match wins. Use the setting GUID
    /// when scripting per-setting powercfg /query responses.
    /// </summary>
    public void When(string argsContain, ProcessResult result)
        => _scripted[argsContain] = result;

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        var argString = string.Join(" ", arguments);
        Calls.Add((fileName, arguments.ToArray()));

        foreach (var (needle, response) in _scripted)
            if (argString.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(response);

        return Task.FromResult(new ProcessResult(0, "", ""));
    }
}
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build tests/WinTune.Core.Tests/WinTune.Core.Tests.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add tests/WinTune.Core.Tests/TestHelpers/StubProcessRunner.cs
git commit -m "test(core): add StubProcessRunner helper for powercfg unit tests"
```

---

## Task 7: `IPowerService` interface + `PowerService` skeleton

**Files:**
- Create: `src/WinTune.Core/Services/IPowerService.cs`
- Create: `src/WinTune.Core/Services/PowerService.cs` (skeleton — Capture only)
- Create: `tests/WinTune.Core.Tests/PowerServiceTests.cs`

- [ ] **Step 1: Write the failing capture test**

Create `tests/WinTune.Core.Tests/PowerServiceTests.cs`:

```csharp
using FluentAssertions;
using WinTune.Core.Models;
using WinTune.Core.Services;
using WinTune.Core.Tests.TestHelpers;

namespace WinTune.Core.Tests;

public class PowerServiceTests
{
    private const string ActiveSchemeOutput =
        "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)";

    private static string QueryOutput(string settingGuid, int dcPercent)
        => $@"
Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor power management)
  Power Setting GUID: {settingGuid}  (Setting)
    Possible Setting Index: 000
    Possible Setting Friendly Name: 0
    ...
  Current AC Power Setting Index: 0x00000064
  Current DC Power Setting Index: 0x{dcPercent:X8}";

    private static StubProcessRunner BuildHealthyRunner()
    {
        var r = new StubProcessRunner();
        r.When("/getactivescheme",
            new ProcessResult(0, ActiveSchemeOutput, ""));
        r.When(PowerCfgIds.CpuMaxState,
            new ProcessResult(0, QueryOutput(PowerCfgIds.CpuMaxState, 100), ""));
        r.When(PowerCfgIds.CpuMinState,
            new ProcessResult(0, QueryOutput(PowerCfgIds.CpuMinState, 5), ""));
        r.When(PowerCfgIds.Epp,
            new ProcessResult(0, QueryOutput(PowerCfgIds.Epp, 0), ""));
        r.When(PowerCfgIds.CoolingPolicy,
            new ProcessResult(0, QueryOutput(PowerCfgIds.CoolingPolicy, 1), ""));
        r.When(PowerCfgIds.BatterySaverThresh,
            new ProcessResult(0, QueryOutput(PowerCfgIds.BatterySaverThresh, 20), ""));
        return r;
    }

    [Fact]
    public async Task CaptureCurrentAsync_parses_healthy_dc_state()
    {
        var runner = BuildHealthyRunner();
        var sut    = new PowerService(runner, snapshotPath: Path.GetTempFileName());

        var state = await sut.CaptureCurrentAsync(CancellationToken.None);

        state.ActiveScheme.Should().Be(Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"));
        state.DcCpuMaxPct.Should().Be(100);
        state.DcCpuMinPct.Should().Be(5);
        state.DcEpp.Should().Be(0);
        state.DcCoolingPolicy.Should().Be("Active");
        state.BatterySaverThresholdPct.Should().Be(20);
    }

    [Fact]
    public async Task CaptureCurrentAsync_calls_unhide_once_per_hidden_setting_before_query()
    {
        var runner = BuildHealthyRunner();
        var sut    = new PowerService(runner, snapshotPath: Path.GetTempFileName());

        await sut.CaptureCurrentAsync(CancellationToken.None);

        runner.Calls.Should().Contain(c =>
            c.Args.Contains("-ATTRIB_HIDE") && c.Args.Contains(PowerCfgIds.Epp));
        runner.Calls.Should().Contain(c =>
            c.Args.Contains("-ATTRIB_HIDE") && c.Args.Contains(PowerCfgIds.CoolingPolicy));
    }

    [Fact]
    public async Task CaptureCurrentAsync_maps_cooling_policy_zero_to_Passive()
    {
        var runner = BuildHealthyRunner();
        runner.When(PowerCfgIds.CoolingPolicy,
            new ProcessResult(0, QueryOutput(PowerCfgIds.CoolingPolicy, 0), ""));
        var sut = new PowerService(runner, snapshotPath: Path.GetTempFileName());

        var state = await sut.CaptureCurrentAsync(CancellationToken.None);

        state.DcCoolingPolicy.Should().Be("Passive");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter PowerServiceTests`
Expected: build failure — `IPowerService` / `PowerService` do not exist.

- [ ] **Step 3: Create the interface**

Create `src/WinTune.Core/Services/IPowerService.cs`:

```csharp
using WinTune.Core.Models;

namespace WinTune.Core.Services;

public interface IPowerService
{
    Task<PowerState>     CaptureCurrentAsync(CancellationToken ct);
    Task<PowerFixResult> ApplyTierBAsync   (CancellationToken ct);
    Task<PowerFixResult> ApplySingleAsync  (BatteryKnob knob, CancellationToken ct);
    Task<PowerFixResult> RestorePriorAsync (CancellationToken ct);
    bool                 SnapshotExists    { get; }
}
```

- [ ] **Step 4: Create the implementation skeleton (Capture only)**

Create `src/WinTune.Core/Services/PowerService.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using WinTune.Core.Models;

namespace WinTune.Core.Services;

public sealed class PowerService : IPowerService
{
    private readonly IProcessRunner _proc;
    private readonly string         _snapshotPath;
    private bool                    _unhidden;

    public PowerService(IProcessRunner proc, string? snapshotPath = null)
    {
        _proc         = proc;
        _snapshotPath = snapshotPath ?? DefaultSnapshotPath();
    }

    public bool SnapshotExists => File.Exists(_snapshotPath);

    public async Task<PowerState> CaptureCurrentAsync(CancellationToken ct)
    {
        await EnsureUnhiddenAsync(ct);

        var scheme = await GetActiveSchemeAsync(ct);

        var cpuMax  = await QueryDcIntAsync(PowerCfgIds.SubProcessor,   PowerCfgIds.CpuMaxState,        ct);
        var cpuMin  = await QueryDcIntAsync(PowerCfgIds.SubProcessor,   PowerCfgIds.CpuMinState,        ct);
        var epp     = await QueryDcIntAsync(PowerCfgIds.SubProcessor,   PowerCfgIds.Epp,                ct);
        var cooling = await QueryDcIntAsync(PowerCfgIds.SubProcessor,   PowerCfgIds.CoolingPolicy,      ct);
        var saver   = await QueryDcIntAsync(PowerCfgIds.SubEnergySaver, PowerCfgIds.BatterySaverThresh, ct);

        var coolingLabel = cooling switch
        {
            0 => "Passive",
            1 => "Active",
            _ => throw new InvalidOperationException($"unexpected cooling-policy index {cooling}")
        };

        return new PowerState(
            ActiveScheme:              scheme,
            DcCpuMaxPct:               cpuMax,
            DcCpuMinPct:               cpuMin,
            DcEpp:                     epp,
            DcCoolingPolicy:           coolingLabel,
            BatterySaverThresholdPct:  saver);
    }

    public Task<PowerFixResult> ApplyTierBAsync   (CancellationToken ct) => throw new NotImplementedException();
    public Task<PowerFixResult> ApplySingleAsync  (BatteryKnob knob, CancellationToken ct) => throw new NotImplementedException();
    public Task<PowerFixResult> RestorePriorAsync (CancellationToken ct) => throw new NotImplementedException();

    private async Task EnsureUnhiddenAsync(CancellationToken ct)
    {
        if (_unhidden) return;
        // EPP and cooling-policy carry ATTRIB_HIDE by default on stock Windows; unhide
        // before any /query — see spec section 5.1 for the rationale.
        await _proc.RunAsync("powercfg",
            new[] { "/attributes", PowerCfgIds.SubProcessor, PowerCfgIds.Epp,           "-ATTRIB_HIDE" }, ct);
        await _proc.RunAsync("powercfg",
            new[] { "/attributes", PowerCfgIds.SubProcessor, PowerCfgIds.CoolingPolicy, "-ATTRIB_HIDE" }, ct);
        _unhidden = true;
    }

    private async Task<Guid> GetActiveSchemeAsync(CancellationToken ct)
    {
        var r = await _proc.RunAsync("powercfg", new[] { "/getactivescheme" }, ct);
        var m = Regex.Match(r.Stdout, @"Power Scheme GUID:\s*([0-9a-fA-F\-]{36})");
        if (!m.Success) throw new InvalidOperationException("could not parse active power scheme GUID");
        return Guid.Parse(m.Groups[1].Value);
    }

    private async Task<int> QueryDcIntAsync(string subgroup, string setting, CancellationToken ct)
    {
        var r = await _proc.RunAsync("powercfg",
            new[] { "/query", "SCHEME_CURRENT", subgroup, setting }, ct);
        var m = Regex.Match(r.Stdout, @"Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)");
        if (!m.Success) throw new InvalidOperationException(
            $"could not parse DC index for setting {setting}");
        return int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static string DefaultSnapshotPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinTune");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "battery-snapshot.json");
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter PowerServiceTests`
Expected: 3/3 PASS.

- [ ] **Step 6: Commit**

```bash
git add src/WinTune.Core/Services/IPowerService.cs \
        src/WinTune.Core/Services/PowerService.cs \
        tests/WinTune.Core.Tests/PowerServiceTests.cs
git commit -m "feat(core): add IPowerService + capture parsing with attribute unhide"
```

---

## Task 8: `PowerService.ApplyTierBAsync` with snapshot

**Files:**
- Modify: `src/WinTune.Core/Services/PowerService.cs` (implement `ApplyTierBAsync`)
- Modify: `tests/WinTune.Core.Tests/PowerServiceTests.cs` (add apply tests)

- [ ] **Step 1: Write the failing tests**

Append to `tests/WinTune.Core.Tests/PowerServiceTests.cs`:

```csharp
[Fact]
public async Task ApplyTierBAsync_writes_snapshot_then_sets_three_dc_values()
{
    var runner       = BuildHealthyRunner();
    var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-{Guid.NewGuid()}.json");
    var sut          = new PowerService(runner, snapshotPath);

    var r = await sut.ApplyTierBAsync(CancellationToken.None);

    r.Success.Should().BeTrue();
    File.Exists(snapshotPath).Should().BeTrue();

    runner.Calls.Should().Contain(c =>
        c.Args.Contains("/setdcvalueindex") &&
        c.Args.Contains(PowerCfgIds.CpuMaxState) &&
        c.Args.Contains("100"));
    runner.Calls.Should().Contain(c =>
        c.Args.Contains("/setdcvalueindex") &&
        c.Args.Contains(PowerCfgIds.Epp) &&
        c.Args.Contains("0"));
    runner.Calls.Should().Contain(c =>
        c.Args.Contains("/setdcvalueindex") &&
        c.Args.Contains(PowerCfgIds.CoolingPolicy) &&
        c.Args.Contains("1"));
    runner.Calls.Should().Contain(c => c.Args.Contains("/setactive"));

    File.Delete(snapshotPath);
}

[Fact]
public async Task ApplyTierBAsync_does_not_overwrite_existing_snapshot()
{
    var runner       = BuildHealthyRunner();
    var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-{Guid.NewGuid()}.json");
    File.WriteAllText(snapshotPath, "{\"sentinel\":42}");
    var originalLen  = new FileInfo(snapshotPath).Length;
    var sut          = new PowerService(runner, snapshotPath);

    await sut.ApplyTierBAsync(CancellationToken.None);

    File.ReadAllText(snapshotPath).Should().Contain("\"sentinel\":42");
    new FileInfo(snapshotPath).Length.Should().Be(originalLen);

    File.Delete(snapshotPath);
}

[Fact]
public async Task ApplyTierBAsync_returns_failure_on_powercfg_nonzero_exit()
{
    var runner       = BuildHealthyRunner();
    runner.When("/setdcvalueindex",
        new ProcessResult(1, "", "Access denied."));
    var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-{Guid.NewGuid()}.json");
    var sut          = new PowerService(runner, snapshotPath);

    var r = await sut.ApplyTierBAsync(CancellationToken.None);

    r.Success.Should().BeFalse();
    r.Errors.Should().NotBeEmpty();

    if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
}
```

- [ ] **Step 2: Run test to verify they fail**

Run: `dotnet test --filter PowerServiceTests`
Expected: 3 new tests throw `NotImplementedException`.

- [ ] **Step 3: Implement `ApplyTierBAsync`**

In `src/WinTune.Core/Services/PowerService.cs`, replace the placeholder `ApplyTierBAsync` body with:

```csharp
public async Task<PowerFixResult> ApplyTierBAsync(CancellationToken ct)
{
    var before = await CaptureCurrentAsync(ct);

    if (!File.Exists(_snapshotPath))
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(before);
            await File.WriteAllTextAsync(_snapshotPath, json, ct);
        }
        catch (Exception ex)
        {
            return new PowerFixResult(false, null,
                "could not write snapshot — aborting fix to keep state reversible",
                new[] { ex.Message });
        }
    }

    var errors = new List<string>();
    await SetDcAsync(PowerCfgIds.CpuMaxState,   100, errors, ct);
    await SetDcAsync(PowerCfgIds.Epp,             0, errors, ct);
    await SetDcAsync(PowerCfgIds.CoolingPolicy,   1, errors, ct);
    await CommitActiveSchemeAsync(errors, ct);

    if (errors.Count > 0)
        return new PowerFixResult(false, null, "one or more powercfg writes failed", errors);

    var after = await CaptureCurrentAsync(ct);
    return new PowerFixResult(true, after, "Tier B applied", null);
}

private async Task SetDcAsync(string settingGuid, int value, List<string> errors, CancellationToken ct)
{
    var r = await _proc.RunAsync("powercfg",
        new[] { "/setdcvalueindex", "SCHEME_CURRENT", PowerCfgIds.SubProcessor, settingGuid,
                value.ToString(CultureInfo.InvariantCulture) }, ct);
    if (r.ExitCode != 0)
        errors.Add($"setdcvalueindex {settingGuid} -> {value}: exit {r.ExitCode}; {r.Stderr.Trim()}");
}

private async Task CommitActiveSchemeAsync(List<string> errors, CancellationToken ct)
{
    var r = await _proc.RunAsync("powercfg", new[] { "/setactive", "SCHEME_CURRENT" }, ct);
    if (r.ExitCode != 0)
        errors.Add($"setactive: exit {r.ExitCode}; {r.Stderr.Trim()}");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter PowerServiceTests`
Expected: 6/6 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WinTune.Core/Services/PowerService.cs \
        tests/WinTune.Core.Tests/PowerServiceTests.cs
git commit -m "feat(core): implement PowerService.ApplyTierBAsync with snapshot persistence"
```

---

## Task 9: `PowerService.RestorePriorAsync`

**Files:**
- Modify: `src/WinTune.Core/Services/PowerService.cs` (implement `RestorePriorAsync`)
- Modify: `tests/WinTune.Core.Tests/PowerServiceTests.cs` (add restore tests)

- [ ] **Step 1: Write the failing tests**

Append to `tests/WinTune.Core.Tests/PowerServiceTests.cs`:

```csharp
[Fact]
public async Task RestorePriorAsync_reads_snapshot_writes_values_back_deletes_file()
{
    var runner       = BuildHealthyRunner();
    var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-{Guid.NewGuid()}.json");
    var prior = new PowerState(
        ActiveScheme:              Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"),
        DcCpuMaxPct:               50,
        DcCpuMinPct:               5,
        DcEpp:                     80,
        DcCoolingPolicy:           "Passive",
        BatterySaverThresholdPct:  20);
    File.WriteAllText(snapshotPath, System.Text.Json.JsonSerializer.Serialize(prior));
    var sut = new PowerService(runner, snapshotPath);

    var r = await sut.RestorePriorAsync(CancellationToken.None);

    r.Success.Should().BeTrue();
    File.Exists(snapshotPath).Should().BeFalse();

    runner.Calls.Should().Contain(c =>
        c.Args.Contains("/setdcvalueindex") &&
        c.Args.Contains(PowerCfgIds.CpuMaxState) &&
        c.Args.Contains("50"));
    runner.Calls.Should().Contain(c =>
        c.Args.Contains("/setdcvalueindex") &&
        c.Args.Contains(PowerCfgIds.Epp) &&
        c.Args.Contains("80"));
    runner.Calls.Should().Contain(c =>
        c.Args.Contains("/setdcvalueindex") &&
        c.Args.Contains(PowerCfgIds.CoolingPolicy) &&
        c.Args.Contains("0"));
}

[Fact]
public async Task RestorePriorAsync_without_snapshot_returns_failure()
{
    var runner       = BuildHealthyRunner();
    var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-missing-{Guid.NewGuid()}.json");
    var sut          = new PowerService(runner, snapshotPath);

    var r = await sut.RestorePriorAsync(CancellationToken.None);

    r.Success.Should().BeFalse();
    r.Note.Should().Contain("no snapshot");
}

[Fact]
public void SnapshotExists_tracks_file_existence()
{
    var runner       = BuildHealthyRunner();
    var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-exist-{Guid.NewGuid()}.json");
    var sut          = new PowerService(runner, snapshotPath);

    sut.SnapshotExists.Should().BeFalse();

    File.WriteAllText(snapshotPath, "{}");
    sut.SnapshotExists.Should().BeTrue();

    File.Delete(snapshotPath);
    sut.SnapshotExists.Should().BeFalse();
}
```

- [ ] **Step 2: Run test to verify they fail**

Run: `dotnet test --filter PowerServiceTests`
Expected: 2 of the new tests throw `NotImplementedException`; `SnapshotExists_tracks_file_existence` already passes (the property was implemented in Task 7).

- [ ] **Step 3: Implement `RestorePriorAsync`**

In `src/WinTune.Core/Services/PowerService.cs`, replace the `RestorePriorAsync` placeholder with:

```csharp
public async Task<PowerFixResult> RestorePriorAsync(CancellationToken ct)
{
    if (!File.Exists(_snapshotPath))
        return new PowerFixResult(false, null, "no snapshot — nothing to restore", null);

    PowerState prior;
    try
    {
        var json = await File.ReadAllTextAsync(_snapshotPath, ct);
        prior = System.Text.Json.JsonSerializer.Deserialize<PowerState>(json)
            ?? throw new InvalidOperationException("snapshot deserialized to null");
    }
    catch (Exception ex)
    {
        return new PowerFixResult(false, null, "snapshot file unreadable", new[] { ex.Message });
    }

    var errors  = new List<string>();
    var cooling = prior.DcCoolingPolicy switch
    {
        "Passive" => 0,
        "Active"  => 1,
        _         => throw new InvalidOperationException(
            $"snapshot cooling-policy '{prior.DcCoolingPolicy}' is not Passive/Active")
    };

    await SetDcAsync(PowerCfgIds.CpuMaxState,   prior.DcCpuMaxPct, errors, ct);
    await SetDcAsync(PowerCfgIds.CpuMinState,   prior.DcCpuMinPct, errors, ct);
    await SetDcAsync(PowerCfgIds.Epp,           prior.DcEpp,       errors, ct);
    await SetDcAsync(PowerCfgIds.CoolingPolicy, cooling,           errors, ct);
    await CommitActiveSchemeAsync(errors, ct);

    if (errors.Count > 0)
        return new PowerFixResult(false, null, "one or more powercfg writes failed", errors);

    File.Delete(_snapshotPath);
    var after = await CaptureCurrentAsync(ct);
    return new PowerFixResult(true, after, "restored to prior DC settings", null);
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter PowerServiceTests`
Expected: 9/9 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WinTune.Core/Services/PowerService.cs \
        tests/WinTune.Core.Tests/PowerServiceTests.cs
git commit -m "feat(core): implement PowerService.RestorePriorAsync"
```

---

## Task 10: `PowerService.ApplySingleAsync` for single-knob fixes

**Files:**
- Modify: `src/WinTune.Core/Services/PowerService.cs`
- Modify: `tests/WinTune.Core.Tests/PowerServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `tests/WinTune.Core.Tests/PowerServiceTests.cs`:

```csharp
[Fact]
public async Task ApplySingleAsync_CpuMax_sets_only_that_knob()
{
    var runner       = BuildHealthyRunner();
    var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-{Guid.NewGuid()}.json");
    var sut          = new PowerService(runner, snapshotPath);

    var r = await sut.ApplySingleAsync(BatteryKnob.CpuMax, CancellationToken.None);

    r.Success.Should().BeTrue();
    runner.Calls.Should().Contain(c =>
        c.Args.Contains("/setdcvalueindex") &&
        c.Args.Contains(PowerCfgIds.CpuMaxState) &&
        c.Args.Contains("100"));
    runner.Calls.Should().NotContain(c =>
        c.Args.Contains("/setdcvalueindex") &&
        c.Args.Contains(PowerCfgIds.Epp));
    File.Delete(snapshotPath);
}

[Fact]
public async Task ApplySingleAsync_Epp_sets_only_Epp()
{
    var runner       = BuildHealthyRunner();
    var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-{Guid.NewGuid()}.json");
    var sut          = new PowerService(runner, snapshotPath);

    await sut.ApplySingleAsync(BatteryKnob.Epp, CancellationToken.None);

    runner.Calls.Should().Contain(c =>
        c.Args.Contains("/setdcvalueindex") &&
        c.Args.Contains(PowerCfgIds.Epp) &&
        c.Args.Contains("0"));
    runner.Calls.Should().NotContain(c =>
        c.Args.Contains("/setdcvalueindex") &&
        c.Args.Contains(PowerCfgIds.CpuMaxState));
    File.Delete(snapshotPath);
}

[Fact]
public async Task ApplySingleAsync_Cooling_writes_snapshot_first()
{
    var runner       = BuildHealthyRunner();
    var snapshotPath = Path.Combine(Path.GetTempPath(), $"wintune-{Guid.NewGuid()}.json");
    var sut          = new PowerService(runner, snapshotPath);

    await sut.ApplySingleAsync(BatteryKnob.Cooling, CancellationToken.None);

    File.Exists(snapshotPath).Should().BeTrue();
    File.Delete(snapshotPath);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter PowerServiceTests`
Expected: 3 new tests throw `NotImplementedException`.

- [ ] **Step 3: Implement `ApplySingleAsync`**

In `src/WinTune.Core/Services/PowerService.cs`, replace the `ApplySingleAsync` placeholder with:

```csharp
public async Task<PowerFixResult> ApplySingleAsync(BatteryKnob knob, CancellationToken ct)
{
    var before = await CaptureCurrentAsync(ct);

    if (!File.Exists(_snapshotPath))
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(before);
            await File.WriteAllTextAsync(_snapshotPath, json, ct);
        }
        catch (Exception ex)
        {
            return new PowerFixResult(false, null,
                "could not write snapshot — aborting fix to keep state reversible",
                new[] { ex.Message });
        }
    }

    var errors = new List<string>();
    switch (knob)
    {
        case BatteryKnob.CpuMax:
            await SetDcAsync(PowerCfgIds.CpuMaxState,  100, errors, ct);
            break;
        case BatteryKnob.Epp:
            await SetDcAsync(PowerCfgIds.Epp,            0, errors, ct);
            break;
        case BatteryKnob.Cooling:
            await SetDcAsync(PowerCfgIds.CoolingPolicy,  1, errors, ct);
            break;
        default:
            throw new ArgumentOutOfRangeException(nameof(knob));
    }
    await CommitActiveSchemeAsync(errors, ct);

    if (errors.Count > 0)
        return new PowerFixResult(false, null, $"powercfg write failed for {knob}", errors);

    var after = await CaptureCurrentAsync(ct);
    return new PowerFixResult(true, after, $"{knob} set", null);
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter PowerServiceTests`
Expected: 12/12 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WinTune.Core/Services/PowerService.cs \
        tests/WinTune.Core.Tests/PowerServiceTests.cs
git commit -m "feat(core): implement PowerService.ApplySingleAsync per-knob fixes"
```

---

## Task 11: `CheckBatteryThrottling` detector

**Files:**
- Modify: `src/WinTune.Core/Services/DiagnoseService.cs` (ctor, new check, call site)
- Create: `tests/WinTune.Core.Tests/DiagnoseServiceBatteryTests.cs`
- Create: `tests/WinTune.Core.Tests/TestHelpers/FakePowerService.cs`

- [ ] **Step 1: Write the `FakePowerService` helper**

Create `tests/WinTune.Core.Tests/TestHelpers/FakePowerService.cs`:

```csharp
using WinTune.Core.Models;
using WinTune.Core.Services;

namespace WinTune.Core.Tests.TestHelpers;

public sealed class FakePowerService : IPowerService
{
    public PowerState NextState { get; set; } = new(
        ActiveScheme:              Guid.Empty,
        DcCpuMaxPct:               100,
        DcCpuMinPct:               5,
        DcEpp:                     0,
        DcCoolingPolicy:           "Active",
        BatterySaverThresholdPct:  20);

    public bool SnapshotExistsValue { get; set; } = false;
    public bool SnapshotExists => SnapshotExistsValue;

    public Task<PowerState>     CaptureCurrentAsync(CancellationToken ct) => Task.FromResult(NextState);
    public Task<PowerFixResult> ApplyTierBAsync   (CancellationToken ct) =>
        Task.FromResult(new PowerFixResult(true, NextState, "applied", null));
    public Task<PowerFixResult> ApplySingleAsync  (BatteryKnob knob, CancellationToken ct) =>
        Task.FromResult(new PowerFixResult(true, NextState, $"{knob} set", null));
    public Task<PowerFixResult> RestorePriorAsync (CancellationToken ct) =>
        Task.FromResult(new PowerFixResult(true, NextState, "restored", null));
}
```

- [ ] **Step 2: Write the failing detector tests**

Create `tests/WinTune.Core.Tests/DiagnoseServiceBatteryTests.cs`:

```csharp
using FluentAssertions;
using WinTune.Core.Models;
using WinTune.Core.Services;
using WinTune.Core.Tests.TestHelpers;

namespace WinTune.Core.Tests;

public class DiagnoseServiceBatteryTests
{
    private static PowerState Healthy() => new(
        ActiveScheme:              Guid.Empty,
        DcCpuMaxPct:               100,
        DcCpuMinPct:               5,
        DcEpp:                     0,
        DcCoolingPolicy:           "Active",
        BatterySaverThresholdPct:  20);

    [Fact]
    public async Task healthy_state_emits_no_battery_finding()
    {
        var power = new FakePowerService { NextState = Healthy() };
        var sut   = new DiagnoseService(power);

        var findings = await sut.InvokeDiagnosticsAsync();

        findings.Should().NotContain(f => f.Id == "diag.battery");
    }

    [Fact]
    public async Task cpu_max_below_100_emits_red_finding_with_cpu_action()
    {
        var power = new FakePowerService { NextState = Healthy() with { DcCpuMaxPct = 50 } };
        var sut   = new DiagnoseService(power);

        var findings = await sut.InvokeDiagnosticsAsync();
        var bat      = findings.Single(f => f.Id == "diag.battery");

        bat.Severity.Should().Be(Severity.Red);
        bat.Detail.Should().Contain("CPU max");
        bat.Actions.Should().Contain(a => a.ActionId == "battery.unleash-all");
        bat.Actions.Should().Contain(a => a.ActionId == "battery.fix-cpu-max");
        bat.Actions.Should().NotContain(a => a.ActionId == "battery.fix-epp");
    }

    [Fact]
    public async Task epp_above_31_emits_red_finding_with_epp_action()
    {
        var power = new FakePowerService { NextState = Healthy() with { DcEpp = 80 } };
        var sut   = new DiagnoseService(power);

        var findings = await sut.InvokeDiagnosticsAsync();
        var bat      = findings.Single(f => f.Id == "diag.battery");

        bat.Detail.Should().Contain("EPP");
        bat.Actions.Should().Contain(a => a.ActionId == "battery.fix-epp");
    }

    [Fact]
    public async Task passive_cooling_emits_red_finding_with_cooling_action()
    {
        var power = new FakePowerService { NextState = Healthy() with { DcCoolingPolicy = "Passive" } };
        var sut   = new DiagnoseService(power);

        var findings = await sut.InvokeDiagnosticsAsync();
        var bat      = findings.Single(f => f.Id == "diag.battery");

        bat.Detail.Should().Contain("Cooling");
        bat.Actions.Should().Contain(a => a.ActionId == "battery.fix-cooling");
    }

    [Fact]
    public async Task all_three_throttled_includes_all_three_per_symptom_actions_plus_unleash()
    {
        var power = new FakePowerService
        {
            NextState = Healthy() with
            {
                DcCpuMaxPct     = 50,
                DcEpp           = 80,
                DcCoolingPolicy = "Passive"
            }
        };
        var sut = new DiagnoseService(power);

        var findings = await sut.InvokeDiagnosticsAsync();
        var bat      = findings.Single(f => f.Id == "diag.battery");

        bat.Title.Should().Contain("3 issue");
        bat.Actions.Select(a => a.ActionId).Should().Contain(new[]
        {
            "battery.unleash-all",
            "battery.fix-cpu-max",
            "battery.fix-epp",
            "battery.fix-cooling"
        });
    }

    [Fact]
    public async Task snapshot_present_adds_restore_action_even_when_healthy()
    {
        var power = new FakePowerService
        {
            NextState           = Healthy(),
            SnapshotExistsValue = true
        };
        var sut = new DiagnoseService(power);

        var findings = await sut.InvokeDiagnosticsAsync();

        var bat = findings.SingleOrDefault(f => f.Id == "diag.battery");
        bat.Should().NotBeNull();
        bat!.Actions.Should().Contain(a => a.ActionId == "battery.restore");
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter DiagnoseServiceBatteryTests`
Expected: build failure — `DiagnoseService(IPowerService)` ctor and `CheckBatteryThrottling` do not exist.

- [ ] **Step 4: Update `DiagnoseService` to accept `IPowerService` and add the check**

Open `src/WinTune.Core/Services/DiagnoseService.cs`.

(a) Replace the constructor. Find the existing:

```csharp
public DiagnoseService()
{
}
```

(or similar — there may be no explicit ctor). Replace with:

```csharp
private readonly IPowerService _power;

public DiagnoseService(IPowerService power)
{
    _power = power;
}
```

(b) Add `CheckBatteryThrottling` near the bottom of the class:

```csharp
private async Task<Finding?> CheckBatteryThrottling(CancellationToken ct)
{
    PowerState state;
    try
    {
        state = await _power.CaptureCurrentAsync(ct);
    }
    catch (Exception ex)
    {
        return new Finding(
            Id:       "diag.battery",
            Severity: Severity.Yellow,
            Title:    "Battery throttling detector unavailable",
            Detail:   $"Could not read DC power settings: {ex.Message}",
            Hint:     null,
            Actions:  Array.Empty<FindingAction>());
    }

    var symptoms = new List<string>();
    var actions  = new List<FindingAction>();

    if (state.DcCpuMaxPct < 100)
    {
        symptoms.Add($"  - CPU max on DC: {state.DcCpuMaxPct}% (should be 100%)");
        actions.Add(new FindingAction("battery.fix-cpu-max", "Fix CPU max", null));
    }
    if (state.DcEpp >= 32)
    {
        symptoms.Add($"  - EPP on DC: {state.DcEpp} (should be 0..31 / Performance)");
        actions.Add(new FindingAction("battery.fix-epp", "Fix EPP", null));
    }
    if (state.DcCoolingPolicy == "Passive")
    {
        symptoms.Add("  - Cooling policy on DC: Passive (should be Active)");
        actions.Add(new FindingAction("battery.fix-cooling", "Fix cooling", null));
    }

    var snapshotPresent = _power.SnapshotExists;
    if (symptoms.Count == 0 && !snapshotPresent)
        return null;

    if (symptoms.Count > 0)
    {
        actions.Insert(0, new FindingAction("battery.unleash-all", "Unleash all", null));
    }
    if (snapshotPresent)
    {
        actions.Add(new FindingAction(
            "battery.restore", "Restore prior settings",
            Confirm: "Revert DC power settings to your prior values. Continue?"));
    }

    var detail = symptoms.Count > 0
        ? $"Active plan: {state.ActiveScheme}\n" + string.Join("\n", symptoms)
        : "DC power settings look healthy. A snapshot from a prior fix is still on disk; use Restore to delete it.";

    return new Finding(
        Id:       "diag.battery",
        Severity: symptoms.Count > 0 ? Severity.Red : Severity.Green,
        Title:    symptoms.Count > 0
                      ? $"Battery throttling: {symptoms.Count} issue{(symptoms.Count == 1 ? "" : "s")}"
                      : "Battery throttling: healthy (snapshot present)",
        Detail:   detail,
        Hint:     symptoms.Count > 0
                      ? "Tier B fix: CPU max 100, EPP Performance, cooling Active. Reversible."
                      : null,
        Actions:  actions);
}
```

(c) In `InvokeDiagnosticsAsync`, after the call that adds the result of `CheckRamPressure`, add:

```csharp
var battery = await CheckBatteryThrottling(ct);
if (battery is not null) findings.Add(battery);
```

- [ ] **Step 5: Update `DiagnoseServiceTests.cs` to inject a fake power service**

Open `tests/WinTune.Core.Tests/DiagnoseServiceTests.cs`. Replace every `new DiagnoseService()` with `new DiagnoseService(new FakePowerService())`. Add the `using WinTune.Core.Tests.TestHelpers;` import at the top.

- [ ] **Step 6: Run all Core tests**

Run: `dotnet test tests/WinTune.Core.Tests/WinTune.Core.Tests.csproj`
Expected: all green, including the 6 new battery tests and the existing 3 DiagnoseService tests adjusted for the new ctor.

- [ ] **Step 7: Commit**

```bash
git add src/WinTune.Core/Services/DiagnoseService.cs \
        tests/WinTune.Core.Tests/DiagnoseServiceBatteryTests.cs \
        tests/WinTune.Core.Tests/DiagnoseServiceTests.cs \
        tests/WinTune.Core.Tests/TestHelpers/FakePowerService.cs
git commit -m "feat(core): CheckBatteryThrottling detector with per-symptom actions"
```

---

## Task 12: Attach `Actions` to the 11 existing detectors

**Files:**
- Modify: `src/WinTune.Core/Services/DiagnoseService.cs`
- Modify: `tests/WinTune.Core.Tests/DiagnoseServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `tests/WinTune.Core.Tests/DiagnoseServiceTests.cs`:

```csharp
[Fact]
public async Task each_detector_action_id_uses_the_detectors_finding_id_prefix_or_is_well_known()
{
    IDiagnoseService sut = new DiagnoseService(new FakePowerService());
    var findings = await sut.InvokeDiagnosticsAsync();

    // Either there are zero actions, or every action ID belongs to the known registry.
    var known = new HashSet<string>(StringComparer.Ordinal)
    {
        "qa.reset",                 "qa.open-folder-options",
        "idx.rebuild",              "idx.open-options",
        "diagtrack.disable",        "diagtrack.open-services",
        "classic.enable",           "classic.undo",
        "pagefile.open-sysdm",
        "switch-to-cleanup-tab",
        "open-task-manager",
        "boost.clear-working-sets",
        "open-reliability-monitor",
        "open-shell-ext-docs",
        "battery.unleash-all",
        "battery.fix-cpu-max",
        "battery.fix-epp",
        "battery.fix-cooling",
        "battery.restore",
    };

    foreach (var f in findings)
        foreach (var a in f.Actions)
            known.Should().Contain(a.ActionId, $"action {a.ActionId} from finding {f.Id} must be registered");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter each_detector_action_id`
Expected: PASS (the only finding with actions today is `diag.battery`, and all its IDs are in `known`). The test stays green as a guardrail — but **leave it in place**; Task 13 will rely on the same set.

(To confirm the test is strict rather than vacuously green: edit a detector locally to emit `Actions = new[] { new FindingAction("nope", "Nope", null) }`, run the test, see it fail with `"nope" not in known`, then revert the edit. Do not commit the bogus action.)

- [ ] **Step 3: Attach actions to each detector**

In `src/WinTune.Core/Services/DiagnoseService.cs`, for each detector update the `new Finding(...)` call to populate `Actions` with the appropriate set. Use the mapping below. For Green findings, keep `Actions = Array.Empty<FindingAction>()` — the user has nothing to fix.

For findings of Severity Red or Yellow only, add actions per the table below. (Detectors that emit different severities in different branches need per-branch action lists.)

| Detector              | Red / Yellow Finding `Actions`                                                                                          |
| --------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| CheckDiskHealth       | `new[] { new FindingAction("open-reliability-monitor", "Open Reliability Monitor", null) }`                             |
| CheckFreeSpace        | `new[] { new FindingAction("switch-to-cleanup-tab", "Open Cleanup tab", null) }`                                        |
| CheckCloudShellExt    | `new[] { new FindingAction("open-shell-ext-docs", "What is this?", null) }`                                             |
| CheckQuickAccessBloat | `new[] { new FindingAction("qa.reset", "Reset Quick Access", null), new FindingAction("qa.open-folder-options", "Open Folder Options", null) }` |
| CheckSearchIndex      | `new[] { new FindingAction("idx.rebuild", "Rebuild index", null), new FindingAction("idx.open-options", "Open Indexing Options", null) }`       |
| CheckDiagTrack        | `new[] { new FindingAction("diagtrack.disable", "Disable Telemetry service", "Stop and disable DiagTrack. Continue?"), new FindingAction("diagtrack.open-services", "Open services.msc", null) }` |
| CheckClassicRightClick| `new[] { new FindingAction("classic.enable", "Enable Classic right-click menu", null), new FindingAction("classic.undo", "Revert to modern menu", null) }` |
| CheckPagefile         | `new[] { new FindingAction("pagefile.open-sysdm", "Open System Properties", null) }`                                    |
| CheckStartupCount     | `new[] { new FindingAction("open-task-manager", "Open Task Manager", null) }`                                       |
| CheckRamPressure      | `new[] { new FindingAction("boost.clear-working-sets", "Clear working sets", null) }`                                   |

For example, `CheckFreeSpace`'s Yellow branch becomes:

```csharp
return new Finding(
    Id:       "diag.freespace",
    Severity: Severity.Yellow,
    Title:    "Low free space",
    Detail:   detail,
    Hint:     "Use the Cleanup tab to reclaim space.",
    Actions:  new[]
    {
        new FindingAction("switch-to-cleanup-tab", "Open Cleanup tab", null)
    });
```

Repeat for every Red/Yellow branch across all 11 detectors. Green branches keep `Actions: Array.Empty<FindingAction>()`.

- [ ] **Step 4: Run all Core tests**

Run: `dotnet test tests/WinTune.Core.Tests/WinTune.Core.Tests.csproj`
Expected: all green.

- [ ] **Step 5: Commit**

```bash
git add src/WinTune.Core/Services/DiagnoseService.cs \
        tests/WinTune.Core.Tests/DiagnoseServiceTests.cs
git commit -m "feat(core): attach per-finding Actions to all 11 existing detectors"
```

---

## Task 13: `DiagnoseViewModel` selection + action registry skeleton

**Files:**
- Modify: `src/WinTune.App/ViewModels/DiagnoseViewModel.cs`

This task introduces selection, the registry, and the dispatch command — but plugs only stub handlers for now. Tasks 14 and 15 wire the real ones.

- [ ] **Step 1: Open `src/WinTune.App/ViewModels/DiagnoseViewModel.cs` and replace the entire file**

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinTune.Core.Models;
using WinTune.Core.Services;

namespace WinTune.App.ViewModels;

public sealed partial class DiagnoseViewModel : ObservableObject
{
    private readonly IDiagnoseService _diag;
    private readonly IBoostService    _boost;
    private readonly IPowerService    _power;

    private readonly Dictionary<string, Func<CancellationToken, Task<FindingActionResult>>>
        _actionHandlers = new(StringComparer.Ordinal);

    [ObservableProperty] private string  summary               = "";
    [ObservableProperty] private bool    isRunning;
    [ObservableProperty] private bool    isActionRunning;
    [ObservableProperty] private Finding? selectedFinding;
    [ObservableProperty] private string? lastActionStatus;
    [ObservableProperty] private bool    lastActionWasError;

    public ObservableCollection<Finding> Findings { get; } = new();

    public event Action<string>? TabSwitchRequested;

    public IReadOnlyDictionary<string, Func<CancellationToken, Task<FindingActionResult>>>
        ActionHandlersForTesting => _actionHandlers;

    public DiagnoseViewModel(IDiagnoseService diag, IBoostService boost, IPowerService power)
    {
        _diag  = diag;
        _boost = boost;
        _power = power;

        RegisterHandlers();
    }

    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        IsRunning = true;
        Summary   = "Running diagnostics…";
        try
        {
            var previouslySelectedId = SelectedFinding?.Id;
            var findings             = await _diag.InvokeDiagnosticsAsync();

            Findings.Clear();
            foreach (var f in findings) Findings.Add(f);

            int red    = findings.Count(f => f.Severity == Severity.Red);
            int yellow = findings.Count(f => f.Severity == Severity.Yellow);
            int green  = findings.Count(f => f.Severity == Severity.Green);
            Summary = $"{findings.Count} findings — {red} red / {yellow} yellow / {green} green";

            if (previouslySelectedId is not null)
                SelectedFinding = Findings.FirstOrDefault(f => f.Id == previouslySelectedId);
        }
        catch (Exception ex)
        {
            Summary = $"Diagnostics failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task ExecuteFindingActionAsync(string? actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return;
        if (!_actionHandlers.TryGetValue(actionId, out var handler))
        {
            LastActionWasError = true;
            LastActionStatus   = $"No handler registered for action '{actionId}'";
            return;
        }

        var action = SelectedFinding?.Actions.FirstOrDefault(a => a.ActionId == actionId);
        if (action?.Confirm is string confirmText)
        {
            var ok = System.Windows.MessageBox.Show(
                confirmText, "Confirm",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (ok != System.Windows.MessageBoxResult.Yes) return;
        }

        IsActionRunning    = true;
        LastActionStatus   = null;
        LastActionWasError = false;
        try
        {
            var r = await handler(CancellationToken.None);
            LastActionWasError = !r.Success;
            LastActionStatus   = r.Note ?? (r.Errors is { Count: > 0 } ? string.Join("; ", r.Errors) : "");
        }
        catch (Exception ex)
        {
            LastActionWasError = true;
            LastActionStatus   = $"Action threw: {ex.Message}";
        }
        finally
        {
            IsActionRunning = false;
        }

        await RunDiagnosticsAsync();
    }

    private void RegisterHandlers()
    {
        // Stub handlers — populated by Task 14 (existing 5) and Task 15 (battery + cross-tab).
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/WinTune.App/WinTune.App.csproj`
Expected: success.

Note: the five `Fix*Btn` buttons in `ui/MainWindow.xaml:271–275` are `x:Name`-only — they have no `Command="{Binding ...}"` bindings (verified against the current XAML). Removing the five public `RelayCommand` properties from `DiagnoseViewModel` therefore breaks **no** XAML bindings; the build stays green. Task 18 deletes those button elements as part of the master-detail rewrite.

- [ ] **Step 3: Run all tests**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 4: Commit**

```bash
git add src/WinTune.App/ViewModels/DiagnoseViewModel.cs
git commit -m "refactor(app): DiagnoseViewModel selection + dispatch skeleton

Introduce SelectedFinding, ExecuteFindingActionCommand, _actionHandlers
dictionary. Old per-button RelayCommands removed (no XAML bindings to them
existed yet). Task 18 deletes the unbound buttons as part of the master-
detail rewrite."
```

---

## Task 14: Migrate existing 5 fix commands into handlers

**Files:**
- Modify: `src/WinTune.App/ViewModels/DiagnoseViewModel.cs`

- [ ] **Step 1: Implement the 5 handlers**

In `src/WinTune.App/ViewModels/DiagnoseViewModel.cs`, replace the empty `RegisterHandlers()` body with:

```csharp
private void RegisterHandlers()
{
    // ---- Quick Access ----
    _actionHandlers["qa.reset"] = async ct =>
    {
        var r = await _diag.ResetQuickAccessAsync();
        return new FindingActionResult(
            Success: true,
            Note:    $"Reset Quick Access: removed {r.FilesRemoved} files. {r.Note}",
            Errors:  null);
    };
    _actionHandlers["qa.open-folder-options"] = _ =>
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "rundll32.exe",
            Arguments       = "shell32.dll,Options_RunDLL 0",
            UseShellExecute = true
        });
        return Task.FromResult(new FindingActionResult(true, "Folder Options opened", null));
    };

    // ---- Search index ----
    _actionHandlers["idx.rebuild"] = async ct =>
    {
        var r = await _diag.StartSearchIndexRebuildAsync();
        return new FindingActionResult(r.Success, r.Note, null);
    };
    _actionHandlers["idx.open-options"] = _ =>
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "control.exe",
            Arguments       = "/name Microsoft.IndexingOptions",
            UseShellExecute = true
        });
        return Task.FromResult(new FindingActionResult(true, "Indexing Options opened", null));
    };

    // ---- Telemetry ----
    _actionHandlers["diagtrack.disable"] = async ct =>
    {
        var r = await _diag.DisableTelemetryAsync();
        return new FindingActionResult(r.Success, r.Note, null);
    };
    _actionHandlers["diagtrack.open-services"] = _ =>
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "services.msc",
            UseShellExecute = true
        });
        return Task.FromResult(new FindingActionResult(true, "services.msc opened", null));
    };

    // ---- Classic right-click ----
    _actionHandlers["classic.enable"] = async ct =>
    {
        var r = await _diag.EnableClassicRightClickAsync();
        return new FindingActionResult(r.Success, r.Note, null);
    };
    _actionHandlers["classic.undo"] = async ct =>
    {
        var r = await _diag.DisableClassicRightClickAsync();
        return new FindingActionResult(r.Success, r.Note, null);
    };

    // ---- Other one-shot opens ----
    _actionHandlers["pagefile.open-sysdm"] = _ =>
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "SystemPropertiesPerformance.exe",
            UseShellExecute = true
        });
        return Task.FromResult(new FindingActionResult(true, "System Properties opened", null));
    };
    _actionHandlers["open-reliability-monitor"] = _ =>
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "perfmon.exe",
            Arguments       = "/rel",
            UseShellExecute = true
        });
        return Task.FromResult(new FindingActionResult(true, "Reliability Monitor opened", null));
    };
    _actionHandlers["open-shell-ext-docs"] = _ =>
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "https://learn.microsoft.com/windows/win32/shell/shell-extensions",
            UseShellExecute = true
        });
        return Task.FromResult(new FindingActionResult(true, "Docs opened in browser", null));
    };

    // ---- Cross-tab nav ----
    _actionHandlers["switch-to-cleanup-tab"] = _ =>
    {
        TabSwitchRequested?.Invoke("Cleanup");
        return Task.FromResult(new FindingActionResult(true, "Switched to Cleanup tab", null));
    };
    _actionHandlers["open-task-manager"] = _ =>
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "taskmgr.exe",
            UseShellExecute = true
        });
        return Task.FromResult(new FindingActionResult(true, "Task Manager opened (click the Startup tab)", null));
    };

    // ---- Boost ----
    _actionHandlers["boost.clear-working-sets"] = async ct =>
    {
        var r = await _boost.ClearWorkingSetsAsync();
        return new FindingActionResult(
            Success: true,
            Note:    $"Cleared working sets on {r.ProcessesTrimmed} processes",
            Errors:  null);
    };

    // ---- Battery (handlers wired in Task 15) ----
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/WinTune.App/WinTune.App.csproj`
Expected: success.

- [ ] **Step 3: Run all tests**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 4: Commit**

```bash
git add src/WinTune.App/ViewModels/DiagnoseViewModel.cs
git commit -m "feat(app): wire 13 finding-action handlers (Quick Access, index, telemetry, classic, sysdm, reliability, shell-ext, tab-switch, boost)"
```

---

## Task 15: Wire battery action handlers

**Files:**
- Modify: `src/WinTune.App/ViewModels/DiagnoseViewModel.cs`

- [ ] **Step 1: Append battery handlers to `RegisterHandlers()`**

Inside `RegisterHandlers()` in `src/WinTune.App/ViewModels/DiagnoseViewModel.cs`, after the `// ---- Battery (handlers wired in Task 15) ----` marker, add:

```csharp
_actionHandlers["battery.unleash-all"] = async ct =>
{
    var r = await _power.ApplyTierBAsync(ct);
    return new FindingActionResult(r.Success, r.Note, r.Errors);
};
_actionHandlers["battery.fix-cpu-max"] = async ct =>
{
    var r = await _power.ApplySingleAsync(BatteryKnob.CpuMax, ct);
    return new FindingActionResult(r.Success, r.Note, r.Errors);
};
_actionHandlers["battery.fix-epp"] = async ct =>
{
    var r = await _power.ApplySingleAsync(BatteryKnob.Epp, ct);
    return new FindingActionResult(r.Success, r.Note, r.Errors);
};
_actionHandlers["battery.fix-cooling"] = async ct =>
{
    var r = await _power.ApplySingleAsync(BatteryKnob.Cooling, ct);
    return new FindingActionResult(r.Success, r.Note, r.Errors);
};
_actionHandlers["battery.restore"] = async ct =>
{
    var r = await _power.RestorePriorAsync(ct);
    return new FindingActionResult(r.Success, r.Note, r.Errors);
};
```

- [ ] **Step 2: Build**

Run: `dotnet build src/WinTune.App/WinTune.App.csproj`
Expected: success.

- [ ] **Step 3: Run all tests**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 4: Commit**

```bash
git add src/WinTune.App/ViewModels/DiagnoseViewModel.cs
git commit -m "feat(app): wire 5 battery action handlers (unleash, per-knob fixes, restore)"
```

---

## Task 16: `FindingActionContractTests` — registry covers every emitted action ID

**Files:**
- Create: `tests/WinTune.Core.Tests/FindingActionContractTests.cs`
- Modify: `tests/WinTune.Core.Tests/WinTune.Core.Tests.csproj` (add ProjectReference to `WinTune.App.csproj` if not already present)

This test lives in the Core test project but references `WinTune.App` to read the registry. If that creates a circular reference (Core depends on nothing, App depends on Core, tests depend on Core only), add a `ProjectReference` from the test project to `WinTune.App`.

- [ ] **Step 1: Check existing test-project references**

Run: `Grep ProjectReference tests/WinTune.Core.Tests/WinTune.Core.Tests.csproj`
Expected: at least a reference to `WinTune.Core`. If `WinTune.App` is not there yet, add it in Step 2.

- [ ] **Step 2: Add ProjectReference to `WinTune.App`**

In `tests/WinTune.Core.Tests/WinTune.Core.Tests.csproj`, inside the `<ItemGroup>` that holds `ProjectReference` elements, add:

```xml
<ProjectReference Include="..\..\src\WinTune.App\WinTune.App.csproj" />
```

If this causes a build error because `WinTune.App` is a WPF executable rather than a library (likely — its csproj sets `<OutputType>WinExe</OutputType>`), do this instead: extract the registry into a `public static IReadOnlySet<string> KnownActionIds` collection on `DiagnoseViewModel` and use *reflection* from the test to enumerate it.

Cleanest path: expose a static set on `DiagnoseViewModel`:

```csharp
public static readonly IReadOnlySet<string> KnownActionIds = new HashSet<string>(StringComparer.Ordinal)
{
    "qa.reset", "qa.open-folder-options",
    "idx.rebuild", "idx.open-options",
    "diagtrack.disable", "diagtrack.open-services",
    "classic.enable", "classic.undo",
    "pagefile.open-sysdm",
    "open-reliability-monitor",
    "open-shell-ext-docs",
    "switch-to-cleanup-tab", "open-task-manager",
    "boost.clear-working-sets",
    "battery.unleash-all", "battery.fix-cpu-max",
    "battery.fix-epp", "battery.fix-cooling",
    "battery.restore",
};
```

Add this static field near the top of `DiagnoseViewModel`. Modify `RegisterHandlers` to add a runtime assertion:

```csharp
private void RegisterHandlers()
{
    // ... all handlers from Task 14 + 15 ...
    System.Diagnostics.Debug.Assert(
        _actionHandlers.Keys.ToHashSet().SetEquals(KnownActionIds),
        "DiagnoseViewModel handler registry must match KnownActionIds exactly");
}
```

- [ ] **Step 3: Write the contract test**

Create `tests/WinTune.Core.Tests/FindingActionContractTests.cs`:

```csharp
using FluentAssertions;
using WinTune.App.ViewModels;
using WinTune.Core.Models;
using WinTune.Core.Services;
using WinTune.Core.Tests.TestHelpers;

namespace WinTune.Core.Tests;

public class FindingActionContractTests
{
    [Fact]
    public async Task every_emitted_ActionId_resolves_in_registry()
    {
        var power = new FakePowerService
        {
            // Force one of every kind: all three throttled, snapshot present.
            NextState = new PowerState(
                ActiveScheme:              Guid.Empty,
                DcCpuMaxPct:               50,
                DcCpuMinPct:               5,
                DcEpp:                     80,
                DcCoolingPolicy:           "Passive",
                BatterySaverThresholdPct:  20),
            SnapshotExistsValue = true
        };
        var diag = new DiagnoseService(power);

        var findings = await diag.InvokeDiagnosticsAsync();

        var emittedIds = findings.SelectMany(f => f.Actions).Select(a => a.ActionId).ToHashSet();
        emittedIds.IsSubsetOf(DiagnoseViewModel.KnownActionIds).Should().BeTrue(
            "every emitted ActionId must be in DiagnoseViewModel.KnownActionIds — " +
            "missing: " + string.Join(", ", emittedIds.Except(DiagnoseViewModel.KnownActionIds)));
    }
}
```

- [ ] **Step 4: Run the contract test**

Run: `dotnet test --filter FindingActionContractTests`
Expected: 1/1 PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/WinTune.Core.Tests/FindingActionContractTests.cs \
        tests/WinTune.Core.Tests/WinTune.Core.Tests.csproj \
        src/WinTune.App/ViewModels/DiagnoseViewModel.cs
git commit -m "test(core): contract test — every emitted ActionId is in handler registry"
```

---

## Task 17: `NullToCollapsedConverter` + `BoolToBrushConverter`

**Files:**
- Create: `src/WinTune.App/Converters/NullToCollapsedConverter.cs`
- Create: `src/WinTune.App/Converters/BoolToBrushConverter.cs`

- [ ] **Step 1: Create `NullToCollapsedConverter`**

Create `src/WinTune.App/Converters/NullToCollapsedConverter.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinTune.App.Converters;

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || (value is string s && string.IsNullOrEmpty(s))
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 2: Create `BoolToBrushConverter`**

Create `src/WinTune.App/Converters/BoolToBrushConverter.cs`:

```csharp
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace WinTune.App.Converters;

public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush  { get; set; } = new SolidColorBrush(Color.FromRgb(0xB0, 0x10, 0x10));
    public Brush FalseBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/WinTune.App/WinTune.App.csproj`
Expected: success.

- [ ] **Step 4: Commit**

```bash
git add src/WinTune.App/Converters/
git commit -m "feat(app): add NullToCollapsedConverter and BoolToBrushConverter"
```

---

## Task 18: Diagnose tab master-detail XAML

**Files:**
- Modify: `ui/MainWindow.xaml`

- [ ] **Step 1: Locate the Diagnose tab content**

Run: `Grep "DiagFindingsGrid" ui/MainWindow.xaml`
Expected: one hit, around line `261`. The TabItem starts a few lines earlier (~`241`).

- [ ] **Step 2: Register converters as window resources**

Near the top of `ui/MainWindow.xaml`, inside `<Window.Resources>` (create it if not present), add:

```xml
<conv:NullToCollapsedConverter x:Key="NullToCollapsed"/>
<conv:BoolToBrushConverter     x:Key="BoolToBrush"/>
```

And ensure the `xmlns:conv` declaration exists on the `<Window>` root:

```xml
xmlns:conv="clr-namespace:WinTune.App.Converters"
```

- [ ] **Step 3: Replace the Diagnose tab content**

Binding pattern is pinned. Facts confirmed against the current `ui/MainWindow.xaml`:
- The `Window`'s `DataContext` is set in `App.xaml.cs:55` to `MainWindowViewModel` — there is no `DataContext="{Binding ...}"` in the XAML.
- `MainWindowViewModel` exposes the Diagnose view-model as the property `Diagnose` (not `DiagnoseViewModel`) — see `src/WinTune.App/ViewModels/MainWindowViewModel.cs:10`.
- The current Diagnose tab's content has no `DataContext` set at all; the existing `x:Name`'d buttons are unbound.

So the rewrite must set `DataContext="{Binding Diagnose}"` on the tab's root Grid, name that Grid `x:Name="DiagRoot"`, and reach back to it via `ElementName=DiagRoot` from inside `DataTemplate` scopes (which break the inherited DataContext chain).

Replace the contents of `<TabItem Header="  Diagnose  ">` (lines `241–278` in the current XAML) with:

```xml
<TabItem Header="  Diagnose  ">
    <Grid x:Name="DiagRoot" Margin="14" DataContext="{Binding Diagnose}">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="Find what is making your laptop slow"
                   Style="{StaticResource HeaderText}"/>

        <StackPanel Grid.Row="1" Orientation="Horizontal" Margin="0,0,0,10">
            <Button Content="Run Diagnostics" Width="180"
                    Background="#FF2D6CDF" Foreground="White" FontWeight="SemiBold"
                    Command="{Binding RunDiagnosticsCommand}"/>
            <TextBlock Text="{Binding Summary}" Margin="20,0,0,0"
                       VerticalAlignment="Center" FontWeight="SemiBold"/>
        </StackPanel>

        <Grid Grid.Row="2">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="2*"/>
                <ColumnDefinition Width="3*"/>
            </Grid.ColumnDefinitions>

            <DataGrid x:Name="DiagFindingsGrid" Grid.Column="0"
                      ItemsSource="{Binding Findings}"
                      SelectedItem="{Binding SelectedFinding, Mode=TwoWay}"
                      AutoGenerateColumns="False" IsReadOnly="True"
                      HeadersVisibility="Column"
                      GridLinesVisibility="Horizontal"
                      RowBackground="White" AlternatingRowBackground="#FFF2F2F7"
                      BorderBrush="#FFD0D0DC" BorderThickness="1"
                      CanUserSortColumns="False" RowHeight="28">
                <DataGrid.Columns>
                    <DataGridTextColumn Header="Sev"   Binding="{Binding Severity}" Width="60"/>
                    <DataGridTextColumn Header="Title" Binding="{Binding Title}"    Width="*"/>
                </DataGrid.Columns>
            </DataGrid>

            <Border Grid.Column="1" Padding="16" BorderThickness="1,0,0,0" BorderBrush="#DDD">
                <ContentControl Content="{Binding SelectedFinding}">
                    <ContentControl.ContentTemplate>
                        <DataTemplate>
                            <StackPanel>
                                <TextBlock Text="{Binding Title}" FontSize="16" FontWeight="SemiBold"/>
                                <TextBlock Text="{Binding Detail}" TextWrapping="Wrap" Margin="0,8,0,0"/>
                                <TextBlock Text="{Binding Hint}" FontStyle="Italic" Foreground="#666"
                                           TextWrapping="Wrap" Margin="0,8,0,12"
                                           Visibility="{Binding Hint, Converter={StaticResource NullToCollapsed}}"/>
                                <ItemsControl ItemsSource="{Binding Actions}">
                                    <ItemsControl.ItemsPanel>
                                        <ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate>
                                    </ItemsControl.ItemsPanel>
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate>
                                            <Button Content="{Binding Label}"
                                                    Margin="0,0,8,8" Padding="10,4"
                                                    Command="{Binding DataContext.ExecuteFindingActionCommand,
                                                              ElementName=DiagRoot}"
                                                    CommandParameter="{Binding ActionId}"/>
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>
                                <TextBlock Margin="0,12,0,0" TextWrapping="Wrap"
                                    Text="{Binding DataContext.LastActionStatus,
                                           ElementName=DiagRoot}"
                                    Foreground="{Binding DataContext.LastActionWasError,
                                                 ElementName=DiagRoot,
                                                 Converter={StaticResource BoolToBrush}}"/>
                            </StackPanel>
                        </DataTemplate>
                    </ContentControl.ContentTemplate>
                </ContentControl>
            </Border>
        </Grid>
    </Grid>
</TabItem>
```

This deletes the old `WrapPanel` of five unbound `Fix*Btn` buttons (lines `270–276` of the current XAML) along with the redundant "Safe one-click fixes" header (`267–268`) and the unused `DiagRunBtn` + `DiagSummaryLbl` named elements (`255–258`).

- [ ] **Step 4: Verify the bindings resolve**

Why these bindings work:
- The outer `<Grid x:Name="DiagRoot" DataContext="{Binding Diagnose}">` rebinds the local context from `MainWindowViewModel` to `DiagnoseViewModel`. The intermediate `{Binding Diagnose}` is evaluated against the Window-level `MainWindowViewModel`, which exposes a `Diagnose` property.
- `{Binding Findings}`, `{Binding SelectedFinding, Mode=TwoWay}`, `{Binding Summary}`, `{Binding RunDiagnosticsCommand}` all resolve against `DiagnoseViewModel` directly because they are inside the `DiagRoot` Grid's scope.
- The two `DataTemplate` blocks (`ContentControl.ContentTemplate` and `ItemsControl.ItemTemplate`) break the inherited DataContext chain — inside them the implicit context becomes the templated item (a `Finding`, then a `FindingAction`). `ElementName=DiagRoot` reaches back to the named root Grid whose `DataContext` is `DiagnoseViewModel`, and `DataContext.ExecuteFindingActionCommand` then resolves cleanly.

No runtime fiddling required; if the binding chain is wrong the immediate window will throw `BindingExpression path error` to the Output window on first render.

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 6: Run all tests**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add ui/MainWindow.xaml
git commit -m "feat(ui): Diagnose tab master-detail layout with per-finding action panel"
```

---

## Task 19: Register `IPowerService` and `IProcessRunner` in DI

**Files:**
- Modify: `src/WinTune.App/App.xaml.cs`

- [ ] **Step 1: Add the registrations**

Open `src/WinTune.App/App.xaml.cs`. Inside the `ConfigureServices` lambda (currently lines `35–52`), after `services.AddSingleton<IDedupHashCache, DedupHashCache>();` add:

```csharp
services.AddSingleton<IProcessRunner, ProcessRunner>();
services.AddSingleton<IPowerService,  PowerService>();
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 3: Run all tests**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 4: Commit**

```bash
git add src/WinTune.App/App.xaml.cs
git commit -m "feat(app): register IProcessRunner and IPowerService in DI container"
```

---

## Task 20: Wire `TabSwitchRequested` to `MainWindowViewModel`

**Files:**
- Modify: `src/WinTune.App/ViewModels/MainWindowViewModel.cs`
- Modify: `ui/MainWindow.xaml` (bind `TabControl.SelectedIndex`)

Facts verified against the current code:
- `MainWindowViewModel` has no tab-selection property today — none of `SelectedTabIndex`, `SelectedTab`, `CurrentTab` exist. Add `SelectedTabIndex` as a new `[ObservableProperty]`.
- The XAML `<TabControl>` at `ui/MainWindow.xaml:41` has no `SelectedIndex` binding — add one.
- Tab order from the current XAML (lines 44, 98, 141, 177, 241): `Dashboard=0`, `Clean=1`, `Boost=2`, `Dedupe=3`, `Diagnose=4`. There is **no** Startup tab.

Only one `TabSwitchRequested` key is used in the action set: `"Cleanup"` (raised by the `switch-to-cleanup-tab` handler in Task 14). The handler for `CheckStartupCount` opens Task Manager directly (Task 14, `open-task-manager`), so no Startup case is needed here.

- [ ] **Step 1: Add `SelectedTabIndex` to `MainWindowViewModel`**

In `src/WinTune.App/ViewModels/MainWindowViewModel.cs`, add the property and a constructor subscription. Replace the file contents with:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinTune.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    public DashboardViewModel Dashboard { get; }
    public CleanViewModel     Clean     { get; }
    public BoostViewModel     Boost     { get; }
    public DiagnoseViewModel  Diagnose  { get; }
    public DedupeViewModel    Dedupe    { get; }

    [ObservableProperty] private string statusBar       = "Ready. Run as Administrator for full cleanup access.";
    [ObservableProperty] private int    selectedTabIndex;

    public MainWindowViewModel(
        DashboardViewModel dashboard,
        CleanViewModel     clean,
        BoostViewModel     boost,
        DiagnoseViewModel  diagnose,
        DedupeViewModel    dedupe)
    {
        Dashboard = dashboard;
        Clean     = clean;
        Boost     = boost;
        Diagnose  = diagnose;
        Dedupe    = dedupe;

        Diagnose.TabSwitchRequested += OnTabSwitchRequested;
    }

    private void OnTabSwitchRequested(string tabKey)
    {
        SelectedTabIndex = tabKey switch
        {
            "Cleanup" => 1,
            _         => SelectedTabIndex
        };
    }

    public void Dispose()
    {
        Diagnose.TabSwitchRequested -= OnTabSwitchRequested;
        Dashboard.Dispose();
        Clean.Dispose();
        Dedupe.Dispose();
    }
}
```

- [ ] **Step 2: Bind `TabControl.SelectedIndex` in XAML**

In `ui/MainWindow.xaml:41`, change:

```xml
<TabControl Margin="10" Background="Transparent" BorderThickness="0">
```

to:

```xml
<TabControl Margin="10" Background="Transparent" BorderThickness="0"
            SelectedIndex="{Binding SelectedTabIndex, Mode=TwoWay}">
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 4: Run all tests**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 5: Commit**

```bash
git add src/WinTune.App/ViewModels/MainWindowViewModel.cs ui/MainWindow.xaml
git commit -m "feat(app): SelectedTabIndex + TabSwitchRequested subscription for cross-tab nav"
```

---

## Task 21: Manual smoke test on the lagging laptop

**Files:** none — instructions only.

This is the verification gate. The unit tests cover parsing, snapshot semantics, registry coverage, and detector logic — but only the real laptop tells us whether typing lag actually goes away.

- [ ] **Step 1: Build a fresh self-contained exe**

Run:
```
dotnet publish src/WinTune.App/WinTune.App.csproj -c Release -r win-x64 -o publish/win-x64
```
Expected: `publish/win-x64/WinTune.exe` exists, ~60 MB.

- [ ] **Step 2: Set the scene**

1. Unplug AC. Confirm typing lag reproduces in your usual workload (browser + IDE).
2. From an elevated PowerShell, run `powercfg /query SCHEME_CURRENT SUB_PROCESSOR` and copy the output to a scratch file — independent record of pre-fix DC settings, used to sanity-check the snapshot.

- [ ] **Step 3: Run the app, click through the Diagnose tab**

1. Launch `publish/win-x64/WinTune.exe` (will prompt for elevation; accept).
2. Diagnose tab → Run.
3. Confirm: a `Battery throttling: N issue(s)` Red finding appears. Click it. The right-hand panel shows Detail, Hint, and 4–5 action buttons.

- [ ] **Step 4: Apply the fix**

1. Click **Unleash all**. Watch the status line — expect `Tier B applied` in default colour.
2. Verify `%APPDATA%\WinTune\battery-snapshot.json` exists; open it; values should match the powercfg query from Step 2.
3. Resume typing. Confirm lag is gone.

- [ ] **Step 5: Verify reversibility**

1. Click the now-visible **Restore prior settings** action (it should appear since `SnapshotExists` is true). Accept the confirmation dialog.
2. Status should read `restored to prior DC settings`. The snapshot file should be gone.
3. Run `powercfg /query SCHEME_CURRENT SUB_PROCESSOR` again — DC indexes should match the pre-fix copy from Step 2.

- [ ] **Step 6: Verify per-symptom actions**

1. Manually re-set one DC value to a throttle-y number, e.g. `powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR <CpuMax-GUID> 50; powercfg /setactive SCHEME_CURRENT`.
2. Re-run Diagnose. A `Battery throttling: 1 issue` Red finding should appear. Click `Fix CPU max only`. Status should read `CpuMax set`; the finding should disappear on the auto-rerun.

- [ ] **Step 7: Document the result**

If everything passed, you're done. If a step failed:
- Capture the relevant `%LOCALAPPDATA%\WinTune\logs\wintune-<date>.log` lines.
- Capture the failing `powercfg /query` output.
- File the discrepancy as an issue or jump back to the offending task.

No commit for this task — it's a verification, not a code change. If you needed to tweak code to fix smoke failures, commit those fixes against the originating task's commit history.

---

## Self-Review

**1. Spec coverage** — every requirement from the spec maps to a task:

- Spec §4 (data model: `FindingAction`, `FindingActionResult`, `Finding.Id`, `Finding.Actions`, `PowerState`, `PowerFixResult`, `BatteryKnob`) → Tasks 1, 2, 5.
- Spec §5.1 (`IPowerService` + powercfg GUIDs + unhide + snapshot rules) → Tasks 4, 7, 8, 9, 10.
- Spec §5.2 (`CheckBatteryThrottling` detector + attach actions to existing 11 findings) → Tasks 11, 12.
- Spec §6 (ViewModel selection, action registry, dispatch command) → Tasks 13, 14, 15.
- Spec §7 (master-detail XAML, converters) → Tasks 17, 18.
- Spec §8 (error handling) → Tasks 8, 9, 10, 11, 13 (try/catch surfaces).
- Spec §9 (testing) → Tasks 1, 5, 7–11, 16; manual smoke → Task 21.
- Spec §10 (file list) → matches the File Structure table above.
- Spec §11 (risks: ATTRIB_HIDE, cooling map, locale, snapshot location) → handled in Tasks 7 (unhide), 7+9 (cooling map throws on unknown), implicit in parser (GUID-keyed regex), Task 8 (`%APPDATA%`).
- DI registration → Task 19.
- Cross-tab nav (`TabSwitchRequested`) → Task 20.

No gaps detected.

**2. Placeholder scan** — searched the document for "TBD", "TODO", "implement later", "add appropriate", "similar to". The only `TBD` is in the document header (spec link → implementation plan, which is *this* document, so the spec's TBD is now resolved). No placeholder steps. Every code step contains complete code.

**3. Type consistency check:**
- `Finding` ctor signature is consistent across Tasks 2, 11, 12.
- `FindingActionResult` is used by every handler (Tasks 13, 14, 15) and by `ExecuteFindingActionAsync` (Task 13) — fields match.
- `PowerState` shape matches between PowerService Tasks 7–10 and detector Task 11 and snapshot JSON in Task 8/9.
- `BatteryKnob` enum members `CpuMax / Epp / Cooling` match between model (Task 5), service (Task 10), and handler IDs in Task 15.
- `IPowerService` method signatures are identical across Tasks 7, 8, 9, 10, 11, 15.
- ActionIds: every ID emitted by `DiagnoseService.CheckBatteryThrottling` and the action table in Task 12 also appears in `_actionHandlers` (Tasks 14, 15) and in `KnownActionIds` (Task 16). The contract test in Task 16 enforces this at build time.

No drift detected.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-05-13-diagnose-actions-and-battery-fix.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

**Which approach?**
