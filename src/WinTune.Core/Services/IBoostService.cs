using WinTune.Core.Models;

namespace WinTune.Core.Services;

public interface IBoostService
{
    Task<WorkingSetResult> ClearWorkingSetsAsync(CancellationToken ct = default);
    Task<ExplorerRestartResult> RestartExplorerAsync(CancellationToken ct = default);
    Task<DnsFlushResult> FlushDnsCacheAsync(CancellationToken ct = default);
}
