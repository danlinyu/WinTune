namespace WinTune.Core.Models;

public sealed record DuplicateFile(string FullPath, long SizeBytes, DateTime LastWriteTime)
{
    public string FileName => System.IO.Path.GetFileName(FullPath);
}
