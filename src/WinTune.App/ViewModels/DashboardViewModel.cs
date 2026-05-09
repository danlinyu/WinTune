using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WinTune.Core.Models;
using WinTune.Core.Services;

namespace WinTune.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IMonitorService _monitor;
    private readonly DispatcherTimer _timer;
    private bool _isRefreshing;

    [ObservableProperty] private int cpuPct;
    [ObservableProperty] private int ramPct;
    [ObservableProperty] private int diskPct;
    [ObservableProperty] private string ramDisplay = "0 / 0 GB";
    [ObservableProperty] private string diskDisplay = "0 / 0 GB free";

    public ObservableCollection<ProcessSnapshot> TopProcesses { get; } = new();

    public DashboardViewModel(IMonitorService monitor)
    {
        _monitor = monitor;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var snap = await _monitor.GetPerfSnapshotAsync();
            CpuPct = snap.CpuPct;
            RamPct = (int)Math.Round(snap.RamPct);
            DiskPct = (int)Math.Round(snap.DiskPct);
            RamDisplay = $"{snap.RamUsedGB:F1} / {snap.RamTotalGB:F1} GB";
            DiskDisplay = $"{snap.DiskFreeGB:F1} / {snap.DiskTotalGB:F1} GB free";

            var top = await _monitor.GetTopProcessesAsync(10);
            TopProcesses.Clear();
            foreach (var p in top) TopProcesses.Add(p);
        }
        catch
        {
            // Transient WMI / counter failures should not crash the dashboard.
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
