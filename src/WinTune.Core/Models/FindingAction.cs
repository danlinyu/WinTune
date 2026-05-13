namespace WinTune.Core.Models;

public sealed record FindingAction(
    string ActionId,
    string Label,
    string? Confirm);
