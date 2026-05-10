namespace WinTune.Core.Models;

public enum Severity { Red, Yellow, Green }

public sealed record Finding(
    Severity Severity,
    string Title,
    string Detail,
    string? Hint);
