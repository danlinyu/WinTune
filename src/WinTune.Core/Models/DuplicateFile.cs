namespace WinTune.Core.Models;

public sealed record DuplicateFile(
    string FullPath,
    long SizeBytes,
    DateTime LastWriteTime,
    FileCategory Category)
{
    public string FileName => System.IO.Path.GetFileName(FullPath);
}
