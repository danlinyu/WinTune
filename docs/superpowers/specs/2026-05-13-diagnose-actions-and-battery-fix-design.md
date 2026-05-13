# Diagnose Per-Finding Actions + Battery Throttling Fix — Design

> Status: draft, awaiting user review.
> Author: Claude Code session, 2026-05-13.
> Repo: `danlinyu/WinTune`, branch `main` at `84d8502`.
> Implementation plan: `docs/superpowers/plans/2026-05-13-diagnose-actions-and-battery-fix.md`.

## 1. Problem

Two adjacent gaps in the current Diagnose tab:

1. **Battery throttling.** Danli's laptop is sluggish on battery — typing lag, foreground app stutter — even with no obvious resource pressure. No detector in `DiagnoseService.cs` examines power-plan DC settings, and no service exposes a write API for them. The user wants WinTune to detect the cause and force-fix it.
2. **Diagnose findings have no per-item actions.** `ui/MainWindow.xaml:241–278` renders the 11 findings in a flat `DataGrid` and offers five global fix buttons in a `WrapPanel`. Clicking a finding does nothing; the buttons apply to the system, not to the selected finding. The user expects each finding to expose its own remediation.

These features are coupled: battery throttling becomes the first detector to emit a `Finding` whose remediations live on the finding itself.

## 2. Goals

- A `Finding` carries an ordered `Actions` list. The Diagnose tab is master-detail: list of findings on the left, details + action buttons on the right.
- The five existing global fix buttons disappear from the XAML. Their behaviour is preserved by re-attaching it to the relevant findings' `Actions`.
- A new `IPowerService` reads current DC (battery-side) power settings and writes Tier B fixes: DC CPU max → 100, DC EPP → Performance (0), DC cooling policy → Active.
- A new detector `CheckBatteryThrottling` emits a Red Finding when any Tier B knob is in a throttle-y state, with `Actions = [Unleash all, Fix CPU max, Fix EPP, Fix cooling, (Restore prior — when snapshot exists)]`.
- Tier B is reversible. Before applying, the pre-fix DC state is snapshotted to `%APPDATA%\WinTune\battery-snapshot.json`. Restore reads it, writes values back, deletes the snapshot.

## 3. Non-goals

- No power-plan switching (Tier D rejected). The fix mutates DC values of the *active* plan only.
- No CPU minimum-state change, no Battery Saver auto-threshold change (Tier C rejected for v1).
- No background polling. Battery detector runs on the normal Diagnose-run path with the other detectors.
- No undervolting, no XTU, no MSR pokes.
- No EcoQoS / Power-Throttling reg key — Tier B alone solves the typing-lag class.
- No multi-snapshot history. One snapshot file; Apply skips writing if it exists; Restore deletes after success.

## 4. Data model

```csharp
// src/WinTune.Core/Models/Finding.cs — extended
public sealed record Finding(
    string                          Id,        // NEW: stable key, e.g. "diag.battery"
    Severity                        Severity,
    string                          Title,
    string                          Detail,
    string?                         Hint,
    IReadOnlyList<FindingAction>    Actions);  // NEW: 0..N

// src/WinTune.Core/Models/FindingAction.cs — new
public sealed record FindingAction(
    string  ActionId,        // e.g. "battery.unleash-all", "qa.reset"
    string  Label,
    string? Confirm);        // optional confirm dialog text; null = no confirm

// src/WinTune.Core/Models/FindingActionResult.cs — new
public sealed record FindingActionResult(
    bool                       Success,
    string?                    Note,
    IReadOnlyList<string>?     Errors);

// src/WinTune.Core/Models/PowerState.cs — new
public sealed record PowerState(
    Guid    ActiveScheme,
    int     DcCpuMaxPct,            // 0..100
    int     DcCpuMinPct,            // 0..100
    int     DcEpp,                  // 0..100, 0 = Performance, 100 = BetterBattery
    string  DcCoolingPolicy,        // "Active" | "Passive"
    int     BatterySaverThresholdPct);

// src/WinTune.Core/Models/PowerFixResult.cs — new
public sealed record PowerFixResult(
    bool                       Success,
    PowerState?                After,
    string?                    Note,
    IReadOnlyList<string>?     Errors);

public enum BatteryKnob { CpuMax, Epp, Cooling }
```

**Why `ActionId` is a string, not an enum:** `WinTune.Core` defines `Finding`; concrete action implementations need `BoostService`, registry writes, tab-switch events, etc. — all of which live in `WinTune.App`. A string ID lets the Core-side detector declare the action without Core taking an App reference. The App-side `DiagnoseViewModel` owns the `ActionId → handler` registry.

## 5. Services

### 5.1 `IPowerService` (new — `src/WinTune.Core/Services/`)

```csharp
public interface IPowerService
{
    Task<PowerState>     CaptureCurrentAsync(CancellationToken ct);
    Task<PowerFixResult> ApplyTierBAsync   (CancellationToken ct);
    Task<PowerFixResult> ApplySingleAsync  (BatteryKnob knob, CancellationToken ct);
    Task<PowerFixResult> RestorePriorAsync (CancellationToken ct);
    bool                 SnapshotExists    { get; }
}
```

**powercfg GUIDs** (well-known, defined as constants in a `PowerCfgIds` static class):

| Setting          | Subgroup GUID                          | Setting GUID                            | Tier B value |
| ---------------- | -------------------------------------- | --------------------------------------- | ------------ |
| CPU max state    | `54533251-82be-4824-96c1-47b60b740d00` | `bc5038f7-23e0-4960-96da-33abaf5935ec`  | 100          |
| CPU min state    | `54533251-...`                         | `893dee8e-2bef-41e0-89c6-b55d0929964c`  | (read only)  |
| EPP              | `54533251-...`                         | `36687f9e-e3a5-4dbf-b1dc-15eb381c6863`  | 0            |
| Cooling policy   | `54533251-...`                         | `94d3a615-a899-4ac5-ae2b-e4d8f634367f`  | 1 (Active)   |
| Saver threshold  | `de830923-a562-41af-a086-e3a2c6bad2da` | `e69653ca-cf7f-4f05-aa73-cb833fa90ad4`  | (read only)  |

Capture flow: `powercfg /getactivescheme` → parse GUID; for each setting GUID, `powercfg /query SCHEME_CURRENT SUB_PROCESSOR <setting>` → parse the `Current DC Power Setting Index` hex line. Decimal value of the parsed hex is the integer 0..100.

Apply flow: `powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR <setting> <value>` for each of the three Tier B knobs; then `powercfg /setactive SCHEME_CURRENT` to commit.

EPP and Cooling carry `ATTRIB_HIDE` on a fresh Windows install — `powercfg /query` omits them and `/setdcvalueindex` reports success while changing nothing. This is a documented Windows behaviour, not a tooling bug; the supported workflow is `powercfg /attributes <subgroup> <setting> -ATTRIB_HIDE` to clear the attribute before reading or writing the value. PowerService runs the unhide call **unconditionally** at startup, once per process — not as a retry, but as the required first step against any setting whose `Current DC` index isn't visible from `/query`. The unhide is idempotent and persistent across reboots, so it's a one-time cost for the user.

Reading values: `powercfg /query SCHEME_CURRENT SUB_PROCESSOR <setting>` → parser extracts the `Current DC Power Setting Index: 0x...` line, decodes the hex to int. The cooling-policy setting returns `0` (Passive) or `1` (Active); the `DcCoolingPolicy` string property maps from that integer, and any unexpected value throws — there is no silent fallback for non-{0,1} values, since seeing one means a real bug the user should hear about.

Snapshot rules (`%APPDATA%\WinTune\battery-snapshot.json`):
- Apply: write snapshot **only if file does not exist**. Repeat Apply calls preserve the original prior state.
- Restore: load snapshot, write each value back via `setdcvalueindex`, `setactive`, then delete the snapshot file.
- Restore with no snapshot file → `Success=false, Note="no snapshot — nothing to restore"`.
- Snapshot write failure aborts the Apply path; do not mutate DC state when prior state cannot be recorded.

powercfg invocation lives behind `IProcessRunner` (new, also Core) so tests can inject golden output. Production impl is a thin `Process.Start` wrapper.

### 5.2 `DiagnoseService` delta

```csharp
public DiagnoseService(
    ILogger<DiagnoseService> log,
    IBoostService            boost,
    IPowerService            power)   // NEW dependency
```

- New private method `CheckBatteryThrottling()` — calls `power.CaptureCurrentAsync`. Throttle predicates, each exposed as a named static method for testability:
  - `DcCpuMaxPct < 100` — CPU max state caps below full.
  - `DcEpp >= 32` — EPP at or above `BetterPerformance` (Windows preset values: 0=Performance, 32=BetterPerformance, 64=Balanced, 80=BetterBattery, 96=BatterySaver). Healthy band is the Performance range `0..31`; everything else is a throttle.
  - `DcCoolingPolicy == "Passive"`.
  
  If any predicate is true, emit `Finding`:
  - `Id = "diag.battery"`
  - `Severity = Red`
  - `Title = $"Battery throttling: {issueCount} issue(s)"`
  - `Detail` = multi-line list of each detected symptom with current vs. target
  - `Hint = "Tier B fix: CPU max 100, EPP Performance, cooling Active. Reversible."`
  - `Actions` = `[unleash-all, fix-cpu-max, fix-epp, fix-cooling]` filtered to symptoms present, plus `restore-prior` iff `power.SnapshotExists`.
- All 11 existing detectors get `Id` populated and `Actions` populated where there is a natural fix:

| Detector              | Id                 | Actions                                                      |
| --------------------- | ------------------ | ------------------------------------------------------------ |
| CheckDiskHealth       | `diag.disk`        | `[open-reliability-monitor]` (Red only)                      |
| CheckFreeSpace        | `diag.freespace`   | `[switch-to-cleanup-tab]`                                    |
| CheckCloudShellExt    | `diag.shell-ext`   | `[open-shell-ext-docs]`                                      |
| CheckQuickAccessBloat | `diag.qa-bloat`    | `[qa.reset, qa.open-folder-options]`                         |
| CheckSearchIndex      | `diag.search-idx`  | `[idx.rebuild, idx.open-options]`                            |
| CheckDiagTrack        | `diag.diagtrack`   | `[diagtrack.disable, diagtrack.open-services]`               |
| CheckClassicRightClick| `diag.classic`     | `[classic.enable, classic.undo]`                             |
| CheckPagefile         | `diag.pagefile`    | `[pagefile.open-sysdm]`                                      |
| CheckStartupCount     | `diag.startup`     | `[open-task-manager]` (Win11 has no separate startup deeplink and WinTune has no Startup tab; Task Manager's Startup tab is the canonical user-facing surface) |
| CheckRamPressure      | `diag.ram`         | `[boost.clear-working-sets]`                                 |
| CheckBatteryThrottling| `diag.battery`     | `[battery.unleash-all, battery.fix-cpu-max, battery.fix-epp, battery.fix-cooling, battery.restore]` |

`InvokeDiagnosticsAsync` adds `CheckBatteryThrottling` after `CheckRamPressure`.

## 6. ViewModel

`src/WinTune.App/ViewModels/DiagnoseViewModel.cs`:

- New props: `Finding? SelectedFinding`, `string? LastActionStatus`, `bool LastActionWasError`, `bool IsActionRunning`.
- New command: `IAsyncRelayCommand<string> ExecuteFindingActionCommand`.
- New private field: `Dictionary<string, Func<CancellationToken, Task<FindingActionResult>>> _actionHandlers`, populated in ctor.

The five existing public commands (`ResetQuickAccessCommand`, `DisableTelemetryCommand`, `ToggleClassicMenuCommand`, `RebuildIndexCommand`, `UndoClassicMenuCommand`) become private async methods registered in `_actionHandlers` under the corresponding `ActionId`. Their bodies are unchanged — only the binding surface moves.

For tab-switch actions (`switch-to-cleanup-tab` only — Startup uses an external Task Manager launch instead, since WinTune has no Startup tab) the handler raises a `TabSwitchRequested(string tabKey)` event the host `MainWindowViewModel` subscribes to.

`ExecuteFindingActionCommand` implementation:
1. Look up `ActionId` in `_actionHandlers`. Missing → status `"No handler for {ActionId}"`, return.
2. If the corresponding `FindingAction.Confirm` is non-null, show `MessageBox.Show(Confirm, "Confirm", YesNo)`. No → return silently.
3. Set `IsActionRunning = true`, clear status.
4. Await handler, update `LastActionStatus` from `Note` or formatted `Errors`, set `LastActionWasError = !Success`.
5. Re-run diagnostics so the Finding list refreshes (snapshot may have been created/deleted, symptoms may now be gone). Restore selection to the same `Id` if it still exists.

## 7. View

`ui/MainWindow.xaml` Diagnose tab (lines `241–278`) replaced by:

```xml
<Grid>
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width="2*"/>
    <ColumnDefinition Width="3*"/>
  </Grid.ColumnDefinitions>

  <DataGrid Grid.Column="0" x:Name="DiagFindingsGrid"
            ItemsSource="{Binding Findings}"
            SelectedItem="{Binding SelectedFinding, Mode=TwoWay}"
            AutoGenerateColumns="False" IsReadOnly="True"
            HeadersVisibility="Column">
    <DataGrid.Columns>
      <DataGridTextColumn Header="Sev"   Binding="{Binding Severity}" Width="60"/>
      <DataGridTextColumn Header="Title" Binding="{Binding Title}"    Width="*"/>
    </DataGrid.Columns>
  </DataGrid>

  <Border Grid.Column="1" Padding="12" BorderThickness="1,0,0,0" BorderBrush="#DDD">
    <ContentControl Content="{Binding SelectedFinding}">
      <ContentControl.ContentTemplate>
        <DataTemplate>
          <StackPanel>
            <TextBlock Text="{Binding Title}" FontSize="16" FontWeight="SemiBold"/>
            <TextBlock Text="{Binding Detail}" TextWrapping="Wrap" Margin="0,8"/>
            <TextBlock Text="{Binding Hint}" FontStyle="Italic" Foreground="#666"
                       Margin="0,0,0,12"
                       Visibility="{Binding Hint,
                         Converter={StaticResource NullToCollapsed}}"/>
            <ItemsControl ItemsSource="{Binding Actions}">
              <ItemsControl.ItemsPanel>
                <ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate>
              </ItemsControl.ItemsPanel>
              <ItemsControl.ItemTemplate>
                <DataTemplate>
                  <Button Content="{Binding Label}" Margin="0,0,8,8" Padding="10,4"
                    Command="{Binding DataContext.ExecuteFindingActionCommand,
                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                    CommandParameter="{Binding ActionId}"/>
                </DataTemplate>
              </ItemsControl.ItemTemplate>
            </ItemsControl>
            <TextBlock Margin="0,12,0,0"
              Text="{Binding DataContext.LastActionStatus,
                     RelativeSource={RelativeSource AncestorType=UserControl}}"
              Foreground="{Binding DataContext.LastActionWasError,
                           RelativeSource={RelativeSource AncestorType=UserControl},
                           Converter={StaticResource BoolToBrush}}"/>
          </StackPanel>
        </DataTemplate>
      </ContentControl.ContentTemplate>
    </ContentControl>
  </Border>
</Grid>
```

Empty-selection (`SelectedFinding == null`): `ContentControl` renders the empty `DataTemplate`, plus a fallback `TextBlock "Select a finding to see actions"` shown via a `NullToVisible` converter on `SelectedFinding`.

Two new converters in `src/WinTune.App/Converters/`:
- `NullToCollapsedConverter` (Hint visibility)
- `BoolToBrushConverter` (red on error, default foreground on success)

The existing `WrapPanel` with the five fix buttons is deleted, along with their command-binding XAML.

## 8. Error handling

- powercfg exit code non-zero → `PowerFixResult.Success=false`, `Errors` carries stderr lines. ViewModel surfaces the first 2 in the status TextBlock; full list logged via Serilog.
- Snapshot read fails (malformed JSON, permissions) → Restore returns `Success=false, Note="snapshot file unreadable"`.
- Detector throws (powercfg missing in PATH on a Server SKU, etc.) → `CheckBatteryThrottling` catches, emits a Yellow Finding `"Battery throttling detector unavailable"` with no actions.
- Action handler throws → caught in `ExecuteFindingActionCommand`, surfaced as red status with exception message; no crash.

## 9. Testing

| Test file (new)                                          | Coverage                                                                                       |
| -------------------------------------------------------- | ---------------------------------------------------------------------------------------------- |
| `tests/WinTune.Core.Tests/PowerServiceTests.cs`          | Parse 4 golden `powercfg /query` fixtures. Apply writes snapshot only on first call. Restore reads + deletes snapshot. Restore with no snapshot returns `Success=false`. Apply failure (powercfg exit 1) leaves snapshot intact. |
| `tests/WinTune.Core.Tests/DiagnoseServiceBatteryTests.cs`| 6 `PowerState` permutations (all healthy → no Finding; each single knob throttled → Red Finding with 1 action; all three throttled → Red Finding with 4 actions; snapshot present → extra `restore` action included). |
| `tests/WinTune.Core.Tests/FindingActionContractTests.cs` | Static assertion: for every detector, every `ActionId` it can emit resolves to a key in the `DiagnoseViewModel._actionHandlers` registry (reflected from a public read-only test seam).  |
| Updated `DiagnoseViewModelTests`                         | `ExecuteFindingActionCommand` dispatches by `ActionId`, missing handler surfaces status, confirm-no path returns without invoking handler, successful path clears error flag, error path sets `LastActionWasError=true`. |

Manual smoke (Danli, on the lagging laptop):
1. Unplug AC. Verify typing lag still reproduces.
2. Launch WinTune, Diagnose → Run. Confirm `Battery throttling` Finding appears with correct symptoms.
3. Click `Unleash all`. Confirm status reads success, snapshot file exists in `%APPDATA%\WinTune`.
4. Resume typing — verify lag is gone.
5. Click `Restore prior`. Confirm snapshot deleted, DC values reverted, Finding either disappears (if Capture re-runs healthy) or re-renders with original symptoms.

## 10. Files touched

```
NEW:
  src/WinTune.Core/Models/FindingAction.cs
  src/WinTune.Core/Models/FindingActionResult.cs
  src/WinTune.Core/Models/PowerState.cs
  src/WinTune.Core/Models/PowerFixResult.cs
  src/WinTune.Core/Services/IPowerService.cs
  src/WinTune.Core/Services/PowerService.cs
  src/WinTune.Core/Services/IProcessRunner.cs
  src/WinTune.Core/Services/ProcessRunner.cs
  src/WinTune.Core/PowerCfgIds.cs
  src/WinTune.App/Converters/NullToCollapsedConverter.cs
  src/WinTune.App/Converters/BoolToBrushConverter.cs
  tests/WinTune.Core.Tests/PowerServiceTests.cs
  tests/WinTune.Core.Tests/DiagnoseServiceBatteryTests.cs
  tests/WinTune.Core.Tests/FindingActionContractTests.cs

MODIFIED:
  src/WinTune.Core/Models/Finding.cs                       (+Id, +Actions)
  src/WinTune.Core/Services/DiagnoseService.cs             (+CheckBatteryThrottling, +Actions on existing findings, +ctor IPowerService)
  src/WinTune.Core/WinTune.Core.csproj                     (no new deps)
  src/WinTune.App/ViewModels/DiagnoseViewModel.cs          (selection, dispatch, action registry)
  src/WinTune.App/App.xaml.cs                              (register IPowerService, IProcessRunner in existing ConfigureServices)
  ui/MainWindow.xaml                                        (Diagnose tab master-detail layout)
  tests/WinTune.Core.Tests/DiagnoseServiceTests.cs         (updated to new Finding ctor signature)
```

## 11. Risks / open questions

- **EPP attribute hidden by default on some OEMs.** Mitigation: PowerService probes first; on set failure with HRESULT 0x80070032 or text "is not currently available", invoke `powercfg /attributes ... -ATTRIB_HIDE` and retry once.
- **Cooling policy index map.** Documented as 0=Passive, 1=Active on consumer SKUs. Defensive: PowerService treats `>0` as Active when reading, writes 1 explicitly when fixing.
- **Localized powercfg output.** Capture parser keys off GUIDs and the literal `0x` prefix on the index line — both locale-invariant. Smoke test on a non-English Windows is out of scope for v1; flag in README.
- **`SCHEME_CURRENT` alias resolution.** All commands use `SCHEME_CURRENT`; `setactive SCHEME_CURRENT` is a documented no-op on the current scheme but cheap, and ensures the values commit.
- **Snapshot location across user profiles.** `%APPDATA%\WinTune\battery-snapshot.json` is per-user; an admin-elevated WinTune launched under a different account would write to that account's APPDATA. Acceptable for v1; document in README.
