using WinTune.Core.Models;

namespace WinTune.Core.Services;

public interface IMonitorService
{
    Task<PerfSnapshot> GetPerfSnapshotAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProcessSnapshot>> GetTopProcessesAsync(int count = 10, CancellationToken ct = default);
}
