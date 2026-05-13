namespace WinTune.Core.Models;

public sealed record FindingActionResult(
    bool                     Success,
    string?                  Note,
    IReadOnlyList<string>?   Errors);
