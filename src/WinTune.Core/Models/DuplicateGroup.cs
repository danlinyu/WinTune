namespace WinTune.Core.Models;

public sealed record DuplicateGroup(
    int GroupId,
    string Hash,
    long SizeBytes,
    long WastedBytes,
    IReadOnlyList<DuplicateFile> Files);
