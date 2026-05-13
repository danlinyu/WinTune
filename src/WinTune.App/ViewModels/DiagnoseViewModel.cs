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

#pragma warning disable CS0067 // event unused until T20 wires TabSwitchRequested
    public event Action<string>? TabSwitchRequested;
#pragma warning restore CS0067

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

#pragma warning disable CA1822 // will access _actionHandlers once T14/T15 populate it
    private void RegisterHandlers()
    {
        // Stub — populated by Task 14 (existing 5 fixes + cross-tab + boost)
        // and Task 15 (battery handlers).
    }
#pragma warning restore CA1822
}
