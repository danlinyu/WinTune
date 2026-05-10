using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinTune.Core.Models;
using WinTune.Core.Services;

namespace WinTune.App.ViewModels;

public sealed partial class BoostViewModel : ObservableObject
{
    private readonly IBoostService _boost;
    private readonly IStartupService _startup;

    [ObservableProperty] private string resultMessage = "";

    public ObservableCollection<StartupEntry> StartupApps { get; } = new();

    public BoostViewModel(IBoostService boost, IStartupService startup)
    {
        _boost = boost;
        _startup = startup;
        _ = LoadStartupAsync();
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
