using CommunityToolkit.Mvvm.ComponentModel;

namespace WinTune.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    public DashboardViewModel Dashboard { get; }
    public CleanViewModel     Clean     { get; }
    public BoostViewModel     Boost     { get; }
    public DiagnoseViewModel  Diagnose  { get; }
    public DedupeViewModel    Dedupe    { get; }

    [ObservableProperty] private string statusBar       = "Ready. Run as Administrator for full cleanup access.";
    [ObservableProperty] private int    selectedTabIndex;

    public MainWindowViewModel(
        DashboardViewModel dashboard,
        CleanViewModel     clean,
        BoostViewModel     boost,
        DiagnoseViewModel  diagnose,
        DedupeViewModel    dedupe)
    {
        Dashboard = dashboard;
        Clean     = clean;
        Boost     = boost;
        Diagnose  = diagnose;
        Dedupe    = dedupe;

        Diagnose.TabSwitchRequested += OnTabSwitchRequested;
    }

    private void OnTabSwitchRequested(string tabKey)
    {
        SelectedTabIndex = tabKey switch
        {
            "Cleanup" => 1,
            _         => SelectedTabIndex
        };
    }

    public void Dispose()
    {
        Diagnose.TabSwitchRequested -= OnTabSwitchRequested;
        Dashboard.Dispose();
        Clean.Dispose();
        Dedupe.Dispose();
    }
}
