namespace WinTune.Core.Models;

public enum StartupSource { Wmi, Registry, StartupFolder }

public sealed record StartupEntry(
    string Name,
    string Command,
    string Location,
    string User,
    StartupSource Source);
