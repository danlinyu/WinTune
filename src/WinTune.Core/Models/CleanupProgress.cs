namespace WinTune.Core.Models;

public sealed record CleanupProgress(
    string Phase,
    int Index,
    int Total,
    CleanupTarget Target,
    int FilesRemoved = 0,
    long BytesFreed = 0,
    bool Skipped = false,
    int ErrorCount = 0);
