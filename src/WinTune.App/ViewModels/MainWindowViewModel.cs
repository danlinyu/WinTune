using CommunityToolkit.Mvvm.ComponentModel;

namespace WinTune.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    public DashboardViewModel Dashboard { get; }
    public CleanViewModel Clean { get; }
    public BoostViewModel Boost { get; }
    public DiagnoseViewModel Diagnose { get; }
    public DedupeViewModel Dedupe { get; }

    [ObservableProperty] private string statusBar = "Ready. Run as Administrator for full cleanup access.";

    public MainWindowViewModel(
        DashboardViewModel dashboard,
        CleanViewModel clean,
        BoostViewModel boost,
        DiagnoseViewModel diagnose,
        DedupeViewModel dedupe)
    {
        Dashboard = dashboard;
        Clean = clean;
        Boost = boost;
        Diagnose = diagnose;
        Dedupe = dedupe;
    }

    public void Dispose()
    {
        Dashboard.Dispose();
        Clean.Dispose();
        Dedupe.Dispose();
    }
}
