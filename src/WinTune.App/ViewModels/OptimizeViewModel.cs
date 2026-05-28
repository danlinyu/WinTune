using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinTune.Core.Models;
using WinTune.Core.Services;

namespace WinTune.App.ViewModels;

public sealed partial class OptimizeViewModel : ObservableObject, IDisposable
{
    private const int AdaptivePollSeconds = 15;

    private readonly IMonitorService   _monitor;
    private readonly IOptimizerService _optimizer;
    private readonly DispatcherTimer   _timer;

    private int       _consecutiveMemoryPressureSamples;
    private DateTime? _lastAutomaticActionUtc;
    private bool      _isRefreshing;

    [ObservableProperty] private bool   isAdaptiveModeEnabled;
    [ObservableProperty] private string machineProfile = "Machine profile pending.";
    [ObservableProperty] private string gpuProfile     = "GPU profile pending.";
    [ObservableProperty] private OptimizationSeverity health = OptimizationSeverity.Green;
    [ObservableProperty] private string livePressure   = "No sample yet.";
    [ObservableProperty] private string status         = "Run an assessment or enable adaptive smoothing.";
    [ObservableProperty] private string lastAction     = "No optimizer actions yet.";

    public ObservableCollection<OptimizationRecommendation> Recommendations { get; } = new();
    public ObservableCollection<DriveProfile> Drives { get; } = new();

    public OptimizeViewModel(IMonitorService monitor, IOptimizerService optimizer)
    {
        _monitor   = monitor;
        _optimizer = optimizer;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(AdaptivePollSeconds) };
        _timer.Tick += async (_, _) => await RefreshAsync(allowAutomaticAction: true);

        _ = RefreshAsync(allowAutomaticAction: false);
    }

    partial void OnIsAdaptiveModeEnabledChanged(bool value)
    {
        if (value)
        {
            _timer.Start();
            Status = $"Adaptive smoothing on. Sampling every {AdaptivePollSeconds}s.";
            _ = RefreshAsync(allowAutomaticAction: true);
        }
        else
        {
            _timer.Stop();
            Status = "Adaptive smoothing off.";
        }
    }

    [RelayCommand]
    private async Task AnalyzeNowAsync() =>
        await RefreshAsync(allowAutomaticAction: false);

    [RelayCommand]
    private async Task SmartOptimizeNowAsync() =>
        await RefreshAsync(allowAutomaticAction: true, forceMemoryActionWhenPressured: true);

    [RelayCommand]
    private void OpenResourceMonitor()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "resmon.exe",
                UseShellExecute = true
            });
            Status = "Resource Monitor opened. Use the Disk tab to find heavy I/O.";
        }
        catch (Exception ex)
        {
            Status = $"Could not open Resource Monitor: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task OptimizeDriveAsync(string? driveRoot)
    {
        if (string.IsNullOrWhiteSpace(driveRoot)) return;

        Status = $"Running Windows drive optimization for {driveRoot}...";
        try
        {
            var result = await _optimizer.OptimizeDriveAsync(driveRoot);
            LastAction = $"{DateTime.Now:HH:mm:ss}: {result.Note}";
            Status = result.Success
                ? "Windows drive optimization finished."
                : result.Note;
            await RefreshAsync(allowAutomaticAction: false);
        }
        catch (Exception ex)
        {
            Status = $"Drive optimization failed: {ex.Message}";
        }
    }

    private async Task RefreshAsync(
        bool allowAutomaticAction,
        bool forceMemoryActionWhenPressured = false)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var snapshot = await _monitor.GetPerfSnapshotAsync();
            var top = await _monitor.GetTopProcessesAsync(5);
            var drives = await _optimizer.GetFixedDrivesAsync();
            var gpus = await _optimizer.GetGpuProfilesAsync();
            var nowUtc = DateTime.UtcNow;

            var preliminary = _optimizer.Assess(
                snapshot,
                top,
                _consecutiveMemoryPressureSamples,
                _lastAutomaticActionUtc,
                nowUtc,
                drives,
                gpus);

            _consecutiveMemoryPressureSamples = preliminary.IsMemoryPressure
                ? _consecutiveMemoryPressureSamples + 1
                : 0;

            int pressureSamples = forceMemoryActionWhenPressured && preliminary.IsMemoryPressure
                ? Math.Max(2, _consecutiveMemoryPressureSamples)
                : _consecutiveMemoryPressureSamples;

            var assessment = _optimizer.Assess(
                snapshot,
                top,
                pressureSamples,
                _lastAutomaticActionUtc,
                nowUtc,
                drives,
                gpus);

            ApplyAssessment(snapshot, assessment);

            if (allowAutomaticAction && assessment.SuggestedAutomaticAction != OptimizationActionKind.None)
            {
                var result = await _optimizer.ApplyActionAsync(assessment.SuggestedAutomaticAction);
                _lastAutomaticActionUtc = DateTime.UtcNow;
                LastAction = $"{_lastAutomaticActionUtc.Value.ToLocalTime():HH:mm:ss}: {result.Note}";
                Status = result.Success
                    ? "Safe smoothing action applied."
                    : $"Optimizer action failed: {result.Note}";
            }
            else if (allowAutomaticAction)
            {
                Status = ActionHoldMessage(assessment);
            }
            else
            {
                Status = "Assessment complete.";
            }
        }
        catch (Exception ex)
        {
            Status = $"Optimizer assessment failed: {ex.Message}";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void ApplyAssessment(PerfSnapshot snapshot, OptimizationAssessment assessment)
    {
        MachineProfile = FormatProfile(assessment.Profile);
        Health = assessment.Severity;
        LivePressure =
            $"CPU {snapshot.CpuPct}% · RAM {snapshot.RamPct:N0}% " +
            $"({snapshot.RamUsedGB:N1}/{snapshot.RamTotalGB:N1} GB) · " +
            $"{assessment.Profile.FixedDrives.Count} fixed drive(s) · " +
            FormatBusiestDrive(assessment.Profile.FixedDrives);
        GpuProfile = FormatGpus(assessment.Profile.Gpus);

        Recommendations.Clear();
        foreach (var recommendation in assessment.Recommendations)
        {
            Recommendations.Add(recommendation);
        }

        Drives.Clear();
        foreach (var drive in assessment.Profile.FixedDrives)
        {
            Drives.Add(drive);
        }
    }

    private static string FormatProfile(MachineProfile profile) =>
        $"{profile.Tier} · {profile.LogicalProcessors} logical CPUs · " +
        $"{profile.RamTotalGB:N1} GB RAM · {profile.TotalFixedDriveGB:N0} GB fixed storage · " +
        $"RAM trim line {profile.MemoryPressureThresholdPct}%";

    private static string FormatGpus(IReadOnlyList<GpuProfile> gpus)
    {
        if (gpus.Count == 0) return "GPU: no WMI video adapter data available.";
        return "GPU: " + string.Join("; ", gpus.Select(g =>
            g.AdapterRamGB is { } gb
                ? $"{g.Name} ({gb:N1} GB adapter RAM)"
                : $"{g.Name} (VRAM not reported)"));
    }

    private static string FormatBusiestDrive(IReadOnlyList<DriveProfile> drives)
    {
        var busiest = drives
            .Where(d => d.ActiveTimePct is not null)
            .OrderByDescending(d => d.ActiveTimePct)
            .FirstOrDefault();
        return busiest is null
            ? "drive activity unavailable"
            : $"busiest {busiest.RootPath} {busiest.ActiveTimePct}% active";
    }

    private static string ActionHoldMessage(OptimizationAssessment assessment)
    {
        if (!assessment.IsMemoryPressure)
        {
            return "Adaptive smoothing on. Pressure is below the action line.";
        }

        if (!assessment.IsMemoryPressureSustained)
        {
            return "Adaptive smoothing on. Waiting for sustained pressure before acting.";
        }

        if (assessment.AutomaticActionCooldownRemaining is { } remaining)
        {
            return $"Adaptive smoothing on. Cooldown {remaining.TotalSeconds:N0}s.";
        }

        return "Adaptive smoothing on. No safe automatic action selected.";
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
