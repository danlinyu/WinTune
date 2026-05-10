using WinTune.Core.Models;

namespace WinTune.Core.Services;

public interface IStartupService
{
    Task<IReadOnlyList<StartupEntry>> GetStartupAppsAsync(CancellationToken ct = default);
    void OpenStartupTaskManager();
}
