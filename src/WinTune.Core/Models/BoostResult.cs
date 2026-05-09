namespace WinTune.Core.Models;

public sealed record WorkingSetResult(
    int ProcessesTrimmed,
    int ProcessesSkipped,
    long BytesFreedEstimate);

public sealed record ExplorerRestartResult(int ProcessesRestarted);

public sealed record DnsFlushResult(bool Success, string? Error);
