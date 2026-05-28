using WinTune.Core.Models;

namespace WinTune.Core.Services;

public interface IOptimizerService
{
    MachineProfile BuildMachineProfile(
        PerfSnapshot snapshot,
        IReadOnlyList<DriveProfile>? fixedDrives = null,
        IReadOnlyList<GpuProfile>? gpus = null);

    Task<IReadOnlyList<DriveProfile>> GetFixedDrivesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GpuProfile>> GetGpuProfilesAsync(CancellationToken ct = default);

    OptimizationAssessment Assess(
        PerfSnapshot snapshot,
        IReadOnlyList<ProcessSnapshot> topProcesses,
        int consecutiveMemoryPressureSamples,
        DateTime? lastAutomaticActionUtc,
        DateTime nowUtc,
        IReadOnlyList<DriveProfile>? fixedDrives = null,
        IReadOnlyList<GpuProfile>? gpus = null);

    Task<OptimizationActionResult> ApplyActionAsync(
        OptimizationActionKind action,
        CancellationToken ct = default);

    Task<OptimizationActionResult> OptimizeDriveAsync(
        string driveRoot,
        CancellationToken ct = default);
}
