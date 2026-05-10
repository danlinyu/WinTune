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
