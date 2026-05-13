namespace WinTune.Core.Models;

public sealed record PowerState(
    Guid    ActiveScheme,
    int     DcCpuMaxPct,
    int     DcCpuMinPct,
    int     DcEpp,
    string  DcCoolingPolicy,
    int     BatterySaverThresholdPct);
