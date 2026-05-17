using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinTune.Core.Models;
using WinTune.Core.Services;

namespace WinTune.App.ViewModels;

public sealed partial class BoostViewModel : ObservableObject, IDisposable
{
    // Auto-trim is threshold-triggered, not periodic: EmptyWorkingSet on a blind
    // timer just churns pages to standby/pagefile that re-fault on next access.
    // We poll the perf snapshot at PollIntervalSeconds, and only trim when the
    // RAM-pct sample crosses the user-set threshold. After a trim we sit in a
    // cooldown so we don't repeatedly trim while pressure stays high.
    private const int PollIntervalSeconds       = 60;
    private const int MinCooldownSeconds        = 5 * 60;
    private const int DefaultThresholdPct       = 85;
    private const int MinThresholdPct           = 60;
    private const int MaxThresholdPct           = 99;

    private readonly IBoostService   _boost;
    private readonly IStartupService _startup;
    private readonly IMonitorService _monitor;
    private readonly DispatcherTimer _autoTrimTimer;

    private DateTime _lastAutoTrim = DateTime.MinValue;
    private bool     _autoTrimInFlight;

    [ObservableProperty] private string resultMessage = "";

    [ObservableProperty] private bool   isAutoTrimEnabled;
    [ObservableProperty] private int    ramThresholdPercent = DefaultThresholdPct;
    [ObservableProperty] private string autoTrimStatus      = "Auto-trim off.";

    public ObservableCollection<StartupEntry> StartupApps { get; } = new();

    public BoostViewModel(IBoostService boost, IStartupService startup, IMonitorService monitor)
    {
        _boost   = boost;
        _startup = startup;
        _monitor = monitor;

        _autoTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(PollIntervalSeconds) };
        _autoTrimTimer.Tick += async (_, _) => await AutoTrimTickAsync();

        _ = LoadStartupAsync();
    }

    partial void OnIsAutoTrimEnabledChanged(bool value)
    {
        if (value)
        {
            _autoTrimTimer.Start();
            AutoTrimStatus = $"Auto-trim on. Watching RAM, will trim when > {RamThresholdPercent}%.";
            _ = AutoTrimTickAsync();
        }
        else
        {
            _autoTrimTimer.Stop();
            AutoTrimStatus = "Auto-trim off.";
        }
    }

    partial void OnRamThresholdPercentChanged(int value)
    {
        if (value < MinThresholdPct) RamThresholdPercent = MinThresholdPct;
        else if (value > MaxThresholdPct) RamThresholdPercent = MaxThresholdPct;
        else if (IsAutoTrimEnabled)
            AutoTrimStatus = $"Auto-trim on. Watching RAM, will trim when > {RamThresholdPercent}%.";
    }

    private async Task AutoTrimTickAsync()
    {
        if (!IsAutoTrimEnabled || _autoTrimInFlight) return;
        _autoTrimInFlight = true;
        try
        {
            var snap = await _monitor.GetPerfSnapshotAsync();
            int ramPct = (int)Math.Round(snap.RamPct);

            if (ramPct <= RamThresholdPercent)
            {
                AutoTrimStatus = $"Auto-trim on. RAM {ramPct}% ≤ {RamThresholdPercent}%, holding.";
                return;
            }

            var sinceLast = DateTime.UtcNow - _lastAutoTrim;
            if (sinceLast.TotalSeconds < MinCooldownSeconds)
            {
                int wait = MinCooldownSeconds - (int)sinceLast.TotalSeconds;
                AutoTrimStatus = $"Auto-trim on. RAM {ramPct}% over threshold, cooldown {wait}s.";
                return;
            }

            AutoTrimStatus = $"Auto-trim on. RAM {ramPct}% > {RamThresholdPercent}% — trimming…";
            var r = await _boost.ClearWorkingSetsAsync();
            _lastAutoTrim = DateTime.UtcNow;
            AutoTrimStatus =
                $"Auto-trim on. Last fired at {_lastAutoTrim.ToLocalTime():HH:mm:ss}: trimmed {r.ProcessesTrimmed} processes, ~{Format(r.BytesFreedEstimate)}.";
        }
        catch (Exception ex)
        {
            AutoTrimStatus = $"Auto-trim tick failed: {ex.Message}";
        }
        finally
        {
            _autoTrimInFlight = false;
        }
    }

    private async Task LoadStartupAsync()
    {
        try
        {
            var apps = await _startup.GetStartupAppsAsync();
            StartupApps.Clear();
            foreach (var a in apps) StartupApps.Add(a);
        }
        catch (Exception ex)
        {
            ResultMessage = $"Could not enumerate startup apps: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task FreeRamAsync()
    {
        ResultMessage = "Trimming working sets…";
        try
        {
            var r = await _boost.ClearWorkingSetsAsync();
            ResultMessage = $"Trimmed {r.ProcessesTrimmed} processes ({r.ProcessesSkipped} skipped). Estimated freed: {Format(r.BytesFreedEstimate)}";
        }
        catch (Exception ex)
        {
            ResultMessage = $"Free RAM failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RestartExplorerAsync()
    {
        ResultMessage = "Restarting Explorer…";
        try
        {
            var r = await _boost.RestartExplorerAsync();
            ResultMessage = $"Restarted explorer.exe (killed {r.ProcessesRestarted} instance(s))";
        }
        catch (Exception ex)
        {
            ResultMessage = $"Restart Explorer failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task FlushDnsAsync()
    {
        ResultMessage = "Flushing DNS cache…";
        try
        {
            var r = await _boost.FlushDnsCacheAsync();
            ResultMessage = r.Success
                ? "DNS cache flushed."
                : $"DNS flush failed: {r.Error}";
        }
        catch (Exception ex)
        {
            ResultMessage = $"DNS flush failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenStartupTaskManager() => _startup.OpenStartupTaskManager();

    public void Dispose()
    {
        _autoTrimTimer.Stop();
    }

    private static string Format(long bytes)
    {
        const long KB = 1024L;
        const long MB = KB * 1024;
        const long GB = MB * 1024;
        return bytes switch
        {
            >= GB => $"{bytes / (double)GB:N2} GB",
            >= MB => $"{bytes / (double)MB:N2} MB",
            >= KB => $"{bytes / (double)KB:N2} KB",
            _ => $"{bytes} B"
        };
    }
}
