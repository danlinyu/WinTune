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
