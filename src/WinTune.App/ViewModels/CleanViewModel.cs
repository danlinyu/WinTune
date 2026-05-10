using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinTune.Core.Models;
using WinTune.Core.Services;

namespace WinTune.App.ViewModels;

public sealed partial class CleanViewModel : ObservableObject, IDisposable
{
    private readonly ICleanupService _cleanup;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool cleanUserTemp = true;
    [ObservableProperty] private bool cleanSystemTemp = true;
    [ObservableProperty] private bool cleanPrefetch = true;
    [ObservableProperty] private bool cleanWindowsErrorReports = true;
    [ObservableProperty] private bool cleanWindowsUpdate;
    [ObservableProperty] private bool cleanEdgeCache = true;
    [ObservableProperty] private bool cleanChromeCache = true;
    [ObservableProperty] private bool cleanFirefoxCache = true;
    [ObservableProperty] private bool cleanRecycleBin = true;
    [ObservableProperty] private bool cleanDnsCache = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunCleanup))]
    [NotifyPropertyChangedFor(nameof(CanCancelCleanup))]
    [NotifyCanExecuteChangedFor(nameof(RunCleanupCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCleanupCommand))]
    private bool isRunning;

    [ObservableProperty] private string totalLabel = "";
    [ObservableProperty] private bool hasLog;

    public ObservableCollection<CleanupResult> Results { get; } = new();

    public bool CanRunCleanup => !IsRunning;
    public bool CanCancelCleanup => IsRunning;

    public CleanViewModel(ICleanupService cleanup)
    {
        _cleanup = cleanup;
    }

    [RelayCommand(CanExecute = nameof(CanRunCleanup))]
    private async Task RunCleanupAsync()
    {
        var targets = SelectedTargets();
        if (targets.Count == 0) return;
        IsRunning = true;
        Results.Clear();
        TotalLabel = "Running…";
        _cts = new CancellationTokenSource();
        var progress = new Progress<CleanupProgress>(p =>
        {
            if (p.Phase == "TargetDone")
            {
                TotalLabel = $"Done {p.Index} / {p.Total}";
            }
        });
        try
        {
            var results = await _cleanup.InvokeCleanupAsync(targets, progress, _cts.Token);
            foreach (var r in results) Results.Add(r);
            long totalBytes = results.Sum(r => r.BytesFreed);
            TotalLabel = $"Freed {_cleanup.FormatBytes(totalBytes)} across {results.Count} targets";
            HasLog = !string.IsNullOrEmpty(_cleanup.GetLastCleanupLogPath());
        }
        catch (OperationCanceledException)
        {
            TotalLabel = "Cancelled";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelCleanup))]
    private void CancelCleanup() => _cts?.Cancel();

    [RelayCommand]
    private void SelectAllCleanTargets()
    {
        CleanUserTemp = CleanSystemTemp = CleanPrefetch = CleanWindowsErrorReports =
            CleanWindowsUpdate = CleanEdgeCache = CleanChromeCache = CleanFirefoxCache =
            CleanRecycleBin = CleanDnsCache = true;
    }

    [RelayCommand]
    private void ClearAllCleanTargets()
    {
        CleanUserTemp = CleanSystemTemp = CleanPrefetch = CleanWindowsErrorReports =
            CleanWindowsUpdate = CleanEdgeCache = CleanChromeCache = CleanFirefoxCache =
            CleanRecycleBin = CleanDnsCache = false;
    }

    [RelayCommand]
    private void OpenLastCleanupLog()
    {
        var path = _cleanup.GetLastCleanupLogPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private List<CleanupTarget> SelectedTargets()
    {
        var list = new List<CleanupTarget>();
        if (CleanUserTemp) list.Add(CleanupTarget.UserTemp);
        if (CleanSystemTemp) list.Add(CleanupTarget.SystemTemp);
        if (CleanPrefetch) list.Add(CleanupTarget.Prefetch);
        if (CleanWindowsErrorReports) list.Add(CleanupTarget.WindowsErrorReports);
        if (CleanWindowsUpdate) list.Add(CleanupTarget.WindowsUpdate);
        if (CleanEdgeCache) list.Add(CleanupTarget.EdgeCache);
        if (CleanChromeCache) list.Add(CleanupTarget.ChromeCache);
        if (CleanFirefoxCache) list.Add(CleanupTarget.FirefoxCache);
        if (CleanRecycleBin) list.Add(CleanupTarget.RecycleBin);
        if (CleanDnsCache) list.Add(CleanupTarget.DnsCache);
        return list;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
