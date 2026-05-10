namespace WinTune.Core.Models;

public sealed record DuplicateGroup(
    int GroupId,
    string Hash,
    long SizeBytes,
    long WastedBytes,
    IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>
    /// The single category that best summarises the group, used to drive UI
    /// filtering and tag colours. UserContent &gt; Backup &gt; Other &gt;
    /// AppManaged &gt; BuildOutput — i.e. the most user-relevant category among
    /// the group's files wins, since a single user-content file in the group is
    /// reason enough to show it.
    /// </summary>
    public FileCategory DominantCategory
    {
        get
        {
            var has = new HashSet<FileCategory>(Files.Select(f => f.Category));
            if (has.Contains(FileCategory.UserContent)) return FileCategory.UserContent;
            if (has.Contains(FileCategory.Backup)) return FileCategory.Backup;
            if (has.Contains(FileCategory.Other)) return FileCategory.Other;
            if (has.Contains(FileCategory.AppManaged)) return FileCategory.AppManaged;
            return FileCategory.BuildOutput;
        }
    }
}
