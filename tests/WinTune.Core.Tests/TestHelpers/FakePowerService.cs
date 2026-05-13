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
