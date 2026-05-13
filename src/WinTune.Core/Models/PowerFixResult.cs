namespace WinTune.Core.Models;

public sealed record PowerFixResult(
    bool                     Success,
    PowerState?              After,
    string?                  Note,
    IReadOnlyList<string>?   Errors);
