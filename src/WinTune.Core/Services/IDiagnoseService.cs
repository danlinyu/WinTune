using WinTune.Core.Models;

namespace WinTune.Core.Services;

public interface IDiagnoseService
{
    Task<IReadOnlyList<Finding>> InvokeDiagnosticsAsync(CancellationToken ct = default);
    Task<DiagnoseResult> ResetQuickAccessAsync(CancellationToken ct = default);
    Task<DiagnoseResult> DisableTelemetryAsync(CancellationToken ct = default);
    Task<DiagnoseResult> EnableClassicRightClickAsync(CancellationToken ct = default);
    Task<DiagnoseResult> DisableClassicRightClickAsync(CancellationToken ct = default);
    Task<DiagnoseResult> StartSearchIndexRebuildAsync(CancellationToken ct = default);
}
