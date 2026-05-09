using WinTune.Core.Models;

namespace WinTune.Core.Services;

public interface ICleanupService
{
    IReadOnlyList<CleanupTarget> GetAvailableTargets();

    Task<IReadOnlyList<CleanupResult>> InvokeCleanupAsync(
        IReadOnlyCollection<CleanupTarget> targets,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken ct = default);

    string? GetLastCleanupLogPath();

    string FormatBytes(long bytes);
}
