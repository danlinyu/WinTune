namespace WinTune.Core.Models;

public sealed record RemovalResult(
    int Deleted,
    long BytesFreed,
    IReadOnlyList<string> Errors,
    bool Permanent);
