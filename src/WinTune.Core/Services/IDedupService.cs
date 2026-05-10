using WinTune.Core.Models;

namespace WinTune.Core.Services;

public interface IDedupService
{
    Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(
        IReadOnlyCollection<string> roots,
        long minSizeBytes = 1L * 1024 * 1024,
        bool includeHidden = false,
        IReadOnlySet<string>? excludeExtensions = null,
        IProgress<DedupeProgress>? progress = null,
        CancellationToken ct = default);

    Task<RemovalResult> RemoveDuplicateFilesAsync(
        IReadOnlyCollection<string> paths,
        bool permanent = false,
        CancellationToken ct = default);

    IReadOnlyList<string> GetDefaultScanRoots();
}
