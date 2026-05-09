namespace WinTune.Core.Models;

public sealed record CleanupResult(
    CleanupTarget Target,
    int FilesRemoved,
    long BytesFreed,
    IReadOnlyList<string> Errors,
    bool Skipped,
    string? SkipReason);
