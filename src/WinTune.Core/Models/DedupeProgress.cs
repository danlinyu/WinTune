namespace WinTune.Core.Models;

public sealed record DedupeProgress(
    string Phase,
    int FilesScanned,
    int FilesHashed,
    int GroupsFound,
    long WastedBytes);
