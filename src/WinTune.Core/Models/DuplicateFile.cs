namespace WinTune.Core.Models;

public sealed record DuplicateFile(string FullPath, long SizeBytes, DateTime LastWriteTime);
