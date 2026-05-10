namespace WinTune.Core.Models;

public sealed record ProcessSnapshot(
    string Name,
    int Id,
    double RamMB,
    int Threads,
    string StartTime);
