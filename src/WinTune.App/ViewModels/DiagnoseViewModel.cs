using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinTune.Core.Models;
using WinTune.Core.Services;

namespace WinTune.App.ViewModels;

public sealed partial class DiagnoseViewModel : ObservableObject
{
    private readonly IDiagnoseService _diag;
    private readonly IBoostService    _boost;
    private readonly IPowerService    _power;

    private readonly Dictionary<string, Func<CancellationToken, Task<FindingActionResult>>>
        _actionHandlers = new(StringComparer.Ordinal);

    [ObservableProperty] private string  summary               = "";
    [ObservableProperty] private bool    isRunning;
    [ObservableProperty] private bool    isActionRunning;
    [ObservableProperty] private Finding? selectedFinding;
    [ObservableProperty] private string? lastActionStatus;
    [ObservableProperty] private bool    lastActionWasError;

    public ObservableCollection<Finding> Findings { get; } = new();

    public event Action<string>? TabSwitchRequested;

    public IReadOnlyDictionary<string, Func<CancellationToken, Task<FindingActionResult>>>
        ActionHandlersForTesting => _actionHandlers;

    public DiagnoseViewModel(IDiagnoseService diag, IBoostService boost, IPowerService power)
    {
        _diag  = diag;
        _boost = boost;
        _power = power;

        RegisterHandlers();
    }

    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        IsRunning = true;
        Summary   = "Running diagnostics…";
        try
        {
            var previouslySelectedId = SelectedFinding?.Id;
            var findings             = await _diag.InvokeDiagnosticsAsync();

            Findings.Clear();
            foreach (var f in findings) Findings.Add(f);

            int red    = findings.Count(f => f.Severity == Severity.Red);
            int yellow = findings.Count(f => f.Severity == Severity.Yellow);
            int green  = findings.Count(f => f.Severity == Severity.Green);
            Summary = $"{findings.Count} findings — {red} red / {yellow} yellow / {green} green";

            if (previouslySelectedId is not null)
                SelectedFinding = Findings.FirstOrDefault(f => f.Id == previouslySelectedId);
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
    private async Task ExecuteFindingActionAsync(string? actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return;
        if (!_actionHandlers.TryGetValue(actionId, out var handler))
        {
            LastActionWasError = true;
            LastActionStatus   = $"No handler registered for action '{actionId}'";
            return;
        }

        var action = SelectedFinding?.Actions.FirstOrDefault(a => a.ActionId == actionId);
        if (action?.Confirm is string confirmText)
        {
            var ok = System.Windows.MessageBox.Show(
                confirmText, "Confirm",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (ok != System.Windows.MessageBoxResult.Yes) return;
        }

        IsActionRunning    = true;
        LastActionStatus   = null;
        LastActionWasError = false;
        try
        {
            var r = await handler(CancellationToken.None);
            LastActionWasError = !r.Success;
            LastActionStatus   = r.Note ?? (r.Errors is { Count: > 0 } ? string.Join("; ", r.Errors) : "");
        }
        catch (Exception ex)
        {
            LastActionWasError = true;
            LastActionStatus   = $"Action threw: {ex.Message}";
        }
        finally
        {
            IsActionRunning = false;
        }

        await RunDiagnosticsAsync();
    }

    private void RegisterHandlers()
    {
        // ---- Quick Access ----
        _actionHandlers["qa.reset"] = async ct =>
        {
            var r = await _diag.ResetQuickAccessAsync(ct);
            return new FindingActionResult(
                Success: true,
                Note:    $"Reset Quick Access: removed {r.FilesRemoved} files. {r.Note}",
                Errors:  null);
        };
        _actionHandlers["qa.open-folder-options"] = _ =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "rundll32.exe",
                Arguments       = "shell32.dll,Options_RunDLL 0",
                UseShellExecute = true
            });
            return Task.FromResult(new FindingActionResult(true, "Folder Options opened", null));
        };

        // ---- Search index ----
        _actionHandlers["idx.rebuild"] = async ct =>
        {
            var r = await _diag.StartSearchIndexRebuildAsync(ct);
            return new FindingActionResult(r.Success, r.Note, null);
        };
        _actionHandlers["idx.open-options"] = _ =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "control.exe",
                Arguments       = "/name Microsoft.IndexingOptions",
                UseShellExecute = true
            });
            return Task.FromResult(new FindingActionResult(true, "Indexing Options opened", null));
        };

        // ---- Telemetry ----
        _actionHandlers["diagtrack.disable"] = async ct =>
        {
            var r = await _diag.DisableTelemetryAsync(ct);
            return new FindingActionResult(r.Success, r.Note, null);
        };
        _actionHandlers["diagtrack.open-services"] = _ =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "services.msc",
                UseShellExecute = true
            });
            return Task.FromResult(new FindingActionResult(true, "services.msc opened", null));
        };

        // ---- Classic right-click ----
        _actionHandlers["classic.enable"] = async ct =>
        {
            var r = await _diag.EnableClassicRightClickAsync(ct);
            return new FindingActionResult(r.Success, r.Note, null);
        };
        _actionHandlers["classic.undo"] = async ct =>
        {
            var r = await _diag.DisableClassicRightClickAsync(ct);
            return new FindingActionResult(r.Success, r.Note, null);
        };

        // ---- Other one-shot opens ----
        _actionHandlers["pagefile.open-sysdm"] = _ =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "SystemPropertiesPerformance.exe",
                UseShellExecute = true
            });
            return Task.FromResult(new FindingActionResult(true, "System Properties opened", null));
        };
        _actionHandlers["open-reliability-monitor"] = _ =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "perfmon.exe",
                Arguments       = "/rel",
                UseShellExecute = true
            });
            return Task.FromResult(new FindingActionResult(true, "Reliability Monitor opened", null));
        };
        _actionHandlers["open-shell-ext-docs"] = _ =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "https://learn.microsoft.com/windows/win32/shell/shell-extensions",
                UseShellExecute = true
            });
            return Task.FromResult(new FindingActionResult(true, "Docs opened in browser", null));
        };

        // ---- Cross-tab nav ----
        _actionHandlers["switch-to-cleanup-tab"] = _ =>
        {
            TabSwitchRequested?.Invoke("Cleanup");
            return Task.FromResult(new FindingActionResult(true, "Switched to Cleanup tab", null));
        };
        _actionHandlers["open-task-manager"] = _ =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "taskmgr.exe",
                UseShellExecute = true
            });
            return Task.FromResult(new FindingActionResult(true, "Task Manager opened (click the Startup tab)", null));
        };

        // ---- Boost ----
        _actionHandlers["boost.clear-working-sets"] = async ct =>
        {
            var r = await _boost.ClearWorkingSetsAsync(ct);
            return new FindingActionResult(
                Success: true,
                Note:    $"Cleared working sets on {r.ProcessesTrimmed} processes",
                Errors:  null);
        };

        // ---- Battery (handlers wired in Task 15) ----
    }
}
