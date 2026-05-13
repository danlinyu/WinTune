namespace WinTune.Core.Models;

public enum Severity { Red, Yellow, Green }

public sealed record Finding(
    string                          Id,
    Severity                        Severity,
    string                          Title,
    string                          Detail,
    string?                         Hint,
    IReadOnlyList<FindingAction>    Actions);
