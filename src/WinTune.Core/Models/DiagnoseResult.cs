namespace WinTune.Core.Models;

public sealed record DiagnoseResult(
    bool Success,
    string Note,
    int FilesRemoved = 0,
    IReadOnlyList<string>? Errors = null);
