using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinTune.Core.Models;
using WinTune.Core.Services;

namespace WinTune.App.ViewModels;

public sealed partial class DiagnoseViewModel : ObservableObject
{
    private readonly IDiagnoseService _diag;

    [ObservableProperty] private string summary = "";
    [ObservableProperty] private bool isRunning;

    public ObservableCollection<Finding> Findings { get; } = new();

    public DiagnoseViewModel(IDiagnoseService diag)
    {
        _diag = diag;
    }

    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        IsRunning = true;
        Summary = "Running diagnostics…";
        try
        {
            var findings = await _diag.InvokeDiagnosticsAsync();
            Findings.Clear();
            foreach (var f in findings) Findings.Add(f);
            int red = findings.Count(f => f.Severity == Severity.Red);
            int yellow = findings.Count(f => f.Severity == Severity.Yellow);
            int green = findings.Count(f => f.Severity == Severity.Green);
            Summary = $"{findings.Count} findings — {red} red / {yellow} yellow / {green} green";
        }
        catch (Exception ex)
        {
            Summary = $"Diagnostics failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task ResetQuickAccessAsync()
    {
        var r = await _diag.ResetQuickAccessAsync();
        Summary = $"Reset Quick Access: removed {r.FilesRemoved} files. {r.Note}";
        await RunDiagnosticsAsync();
    }

    [RelayCommand]
    private async Task DisableTelemetryAsync()
    {
        var r = await _diag.DisableTelemetryAsync();
        Summary = r.Success ? r.Note : $"Failed: {r.Note}";
        await RunDiagnosticsAsync();
    }

    [RelayCommand]
    private async Task EnableClassicRightClickAsync()
    {
        var r = await _diag.EnableClassicRightClickAsync();
        Summary = r.Success ? r.Note : $"Failed: {r.Note}";
        await RunDiagnosticsAsync();
    }

    [RelayCommand]
    private async Task DisableClassicRightClickAsync()
    {
        var r = await _diag.DisableClassicRightClickAsync();
        Summary = r.Success ? r.Note : $"Failed: {r.Note}";
        await RunDiagnosticsAsync();
    }

    [RelayCommand]
    private async Task RebuildSearchIndexAsync()
    {
        var r = await _diag.StartSearchIndexRebuildAsync();
        Summary = r.Success ? r.Note : $"Failed: {r.Note}";
    }
}
