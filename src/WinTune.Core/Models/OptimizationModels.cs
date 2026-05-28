namespace WinTune.Core.Models;

public enum MachineCapabilityTier
{
    Constrained,
    Balanced,
    HighCapacity,
    Workstation
}

public enum OptimizationSeverity
{
    Green,
    Yellow,
    Red
}

public enum OptimizationActionKind
{
    None,
    ClearWorkingSets,
    OptimizeDrive
}

public enum DriveMediaKind
{
    Unknown,
    Hdd,
    Ssd,
    Scm
}

public sealed record MachineProfile(
    MachineCapabilityTier Tier,
    int LogicalProcessors,
    double RamTotalGB,
    double SystemDriveTotalGB,
    double TotalFixedDriveGB,
    int MemoryPressureThresholdPct,
    double DiskFreeWarningGB,
    int DiskFreeWarningPct,
    IReadOnlyList<DriveProfile> FixedDrives,
    IReadOnlyList<GpuProfile> Gpus);

public sealed record DriveProfile(
    string RootPath,
    string DisplayName,
    DriveMediaKind MediaKind,
    double FreeGB,
    double TotalGB,
    double UsedPct,
    bool IsSystemDrive,
    int? ActiveTimePct = null,
    double? QueueLength = null,
    double? ThroughputMBps = null);

public sealed record GpuProfile(
    string Name,
    double? AdapterRamGB);

public sealed record OptimizationRecommendation(
    OptimizationSeverity Severity,
    string Area,
    string Detail,
    string Action);

public sealed record OptimizationAssessment(
    MachineProfile Profile,
    OptimizationSeverity Severity,
    string Summary,
    bool IsMemoryPressure,
    bool IsMemoryPressureSustained,
    TimeSpan? AutomaticActionCooldownRemaining,
    OptimizationActionKind SuggestedAutomaticAction,
    IReadOnlyList<OptimizationRecommendation> Recommendations);

public sealed record OptimizationActionResult(
    OptimizationActionKind Action,
    bool Success,
    string Note);
