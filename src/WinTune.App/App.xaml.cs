using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using WinTune.App.ViewModels;
using WinTune.Core.Services;

namespace WinTune.App;

public partial class App : Application
{
    public static IHost? Host { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinTune", "logs");
        Directory.CreateDirectory(logsDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logsDir, "wintune-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true,
                formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();

        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMonitorService, MonitorService>();
                services.AddSingleton<IBoostService, BoostService>();
                services.AddSingleton<ICleanupService, CleanupService>();
                services.AddSingleton<IStartupService, StartupService>();
                services.AddSingleton<IProcessRunner, ProcessRunner>();
                services.AddSingleton<IPowerService,  PowerService>();
                services.AddSingleton<IOptimizerService, OptimizerService>();
                services.AddSingleton<IDiagnoseService, DiagnoseService>();
                services.AddSingleton<IDedupService, DedupService>();
                services.AddSingleton<IDedupHashCache, DedupHashCache>();
                services.AddSingleton<DashboardViewModel>();
                services.AddSingleton<OptimizeViewModel>();
                services.AddSingleton<CleanViewModel>();
                services.AddSingleton<BoostViewModel>();
                services.AddSingleton<DiagnoseViewModel>();
                services.AddSingleton<DedupeViewModel>();
                services.AddSingleton<MainWindowViewModel>();
                services.AddTransient<MainWindow>();
            })
            .Build();

        var window = Host.Services.GetRequiredService<MainWindow>();
        window.DataContext = Host.Services.GetRequiredService<MainWindowViewModel>();
        MainWindow = window;
        window.Show();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Host is not null)
        {
            if (Host.Services.GetService(typeof(MainWindowViewModel)) is IDisposable vm)
            {
                vm.Dispose();
            }
            Host.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
