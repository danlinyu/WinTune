using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.ServiceProcess;
using Microsoft.Win32;
using WinTune.Core.Models;

namespace WinTune.Core.Services;

public sealed class DiagnoseService : IDiagnoseService
{
    private const string ClassicRightClickClsid = "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    public Task<IReadOnlyList<Finding>> InvokeDiagnosticsAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<Finding>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var f = new List<Finding>();
            CheckDiskHealth(f, ct);
            CheckFreeSpace(f, ct);
            CheckCloudShellExtensions(f, ct);
            CheckQuickAccessBloat(f, ct);
            CheckSearchIndex(f, ct);
            CheckDiagTrack(f, ct);
            CheckClassicRightClick(f, ct);
            CheckPagefile(f, ct);
            CheckStartupCount(f, ct);
            CheckRamPressure(f, ct);
            return f;
        }, ct);

    private static void CheckDiskHealth(List<Finding> findings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool foundIssue = false;
        try
        {
            using var search = new ManagementObjectSearcher(
                @"\\.\ROOT\Microsoft\Windows\Storage",
                "SELECT FriendlyName, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk");
            foreach (ManagementObject mo in search.Get())
            {
                using (mo)
                {
                    string name = mo["FriendlyName"] as string ?? "?";
                    var health = mo["HealthStatus"];
                    var op = mo["OperationalStatus"];
                    bool healthy = health is ushort hv && hv == 0; // 0 = Healthy
                    bool opOk = true;
                    if (op is ushort[] ops)
                    {
                        opOk = ops.All(x => x == 2); // 2 = OK
                    }
                    if (!healthy || !opOk)
                    {
                        foundIssue = true;
                        findings.Add(new Finding(
                            Severity.Red,
                            "Disk health warning",
                            $"{name}: Health={Format(health)}, Op={Format(op)}",
                            "Back up data immediately. Run vendor diagnostic tool."));
                    }
                }
            }
        }
        catch
        {
            // MSFT_PhysicalDisk not available or denied; ignore the check.
            return;
        }
        if (!foundIssue)
        {
            findings.Add(new Finding(
                Severity.Green, "Disk health", "All physical disks Healthy / OK.", null));
        }

        static string Format(object? value) => value switch
        {
            null => "?",
            ushort u => u.ToString(CultureInfo.InvariantCulture),
            ushort[] arr => string.Join(",", arr),
            _ => value.ToString() ?? "?"
        };
    }

    private static void CheckFreeSpace(List<Finding> findings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed) continue;
            if (!d.IsReady) continue;
            try
            {
                long total = d.TotalSize;
                long free = d.AvailableFreeSpace;
                if (total <= 0) continue;
                double totalGB = total / 1024.0 / 1024.0 / 1024.0;
                double freeGB = free / 1024.0 / 1024.0 / 1024.0;
                double pct = free * 100.0 / total;
                Severity sev = pct < 10 ? Severity.Red : (pct < 15 ? Severity.Yellow : Severity.Green);
                if (sev != Severity.Green)
                {
                    findings.Add(new Finding(
                        sev,
                        $"Drive {d.Name.TrimEnd('\\')}: low free space",
                        string.Format(CultureInfo.InvariantCulture,
                            "{0:F1} GB free of {1:F1} GB ({2:F1}%)", freeGB, totalGB, pct),
                        "Win11 throttles disk I/O below ~10% free. Free space or move data."));
                }
            }
            catch
            {
                // skip unreadable drive
            }
        }
    }

    private static void CheckCloudShellExtensions(List<Finding> findings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var hits = new List<string>();
        try
        {
            var explorers = Process.GetProcessesByName("explorer");
            if (explorers.Length == 0) return;
            using var explorer = explorers[0];
            for (int i = 1; i < explorers.Length; i++) explorers[i].Dispose();
            try
            {
                foreach (ProcessModule m in explorer.Modules)
                {
                    var fn = m.FileName ?? "";
                    if (fn.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ||
                        fn.Contains("FileSyncShell", StringComparison.OrdinalIgnoreCase) ||
                        fn.Contains("drivefsext", StringComparison.OrdinalIgnoreCase) ||
                        fn.Contains("GoogleDrive", StringComparison.OrdinalIgnoreCase) ||
                        fn.Contains("Dropbox", StringComparison.OrdinalIgnoreCase) ||
                        fn.Contains("\\Box\\", StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add(m.ModuleName);
                    }
                }
            }
            catch
            {
                // Module enumeration can fail on bitness mismatch.
            }
        }
        catch
        {
            return;
        }
        if (hits.Count > 1)
        {
            findings.Add(new Finding(
                Severity.Red,
                "Multiple cloud shell extensions loaded",
                $"Explorer has {hits.Count} cloud DLLs: {string.Join(", ", hits)}",
                "Pick ONE cloud, set BOTH to online-only / stream mode. Each shell ext queries cloud per file per folder open."));
        }
        else if (hits.Count == 1)
        {
            findings.Add(new Finding(
                Severity.Yellow,
                "Cloud shell extension loaded",
                $"Explorer has 1 cloud DLL: {hits[0]}",
                "OK if intentional. Set Files On-Demand / Stream mode to avoid local copies."));
        }
    }

    private static void CheckQuickAccessBloat(List<Finding> findings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var recentDir = Path.Combine(appData, "Microsoft", "Windows", "Recent");
        var autoDir = Path.Combine(recentDir, "AutomaticDestinations");

        int autoCount = SafeFileCount(autoDir);
        int recentCount = SafeTopLevelFileCount(recentDir);
        int totalQA = autoCount + recentCount;

        Severity sev = totalQA > 300 ? Severity.Red : (totalQA > 100 ? Severity.Yellow : Severity.Green);
        if (sev == Severity.Green)
        {
            findings.Add(new Finding(Severity.Green, "Quick Access", $"{totalQA} recent entries", null));
        }
        else
        {
            findings.Add(new Finding(
                sev,
                "Quick Access bloat",
                $"{autoCount} AutomaticDestinations + {recentCount} Recent shortcuts = {totalQA} total",
                "Stale entries referencing dead network paths cause Explorer to wait on timeout. Use \"Reset Quick Access\"."));
        }
    }

    private static int SafeFileCount(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0; }
        catch { return 0; }
    }

    private static int SafeTopLevelFileCount(string dir) => SafeFileCount(dir);

    private static void CheckSearchIndex(List<Finding> findings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var pd = Environment.GetEnvironmentVariable("ProgramData")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var idxBase = Path.Combine(pd, "Microsoft", "Search", "Data", "Applications",
            "Windows", "Projects", "SystemIndex", "Indexer", "CiFiles");
        if (!Directory.Exists(idxBase))
        {
            findings.Add(new Finding(
                Severity.Yellow,
                "Windows Search index path missing",
                "CiFiles folder not found.",
                "Use \"Rebuild Search Index\"."));
            return;
        }

        long bytes = 0;
        try
        {
            foreach (var fi in new DirectoryInfo(idxBase).EnumerateFiles("*", new System.IO.EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            }))
            {
                bytes += fi.Length;
            }
        }
        catch
        {
            // Permission failures are common here under non-elevated; tolerate.
        }
        long mb = bytes / (1024 * 1024);

        if (mb < 50)
        {
            findings.Add(new Finding(
                Severity.Red,
                "Windows Search index empty",
                $"Index size: {mb} MB (expected: hundreds of MB)",
                "Quick Access \"Frequent\" falls back to full FS scan. Use \"Rebuild Search Index\"."));
        }
        else if (mb > 5000)
        {
            findings.Add(new Finding(
                Severity.Yellow,
                "Windows Search index large",
                $"Index size: {mb} MB",
                "Trim indexed locations: Settings -> Searching Windows -> Find My Files -> Customize."));
        }
        else
        {
            findings.Add(new Finding(Severity.Green, "Windows Search index", $"{mb} MB", null));
        }
    }

    private static void CheckDiagTrack(List<Finding> findings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var sc = new ServiceController("DiagTrack");
            if (sc.Status == ServiceControllerStatus.Running)
            {
                findings.Add(new Finding(
                    Severity.Yellow,
                    "Telemetry (DiagTrack) running",
                    $"Status: {sc.Status}, StartType: {sc.StartType}",
                    "Background disk + network I/O. Use \"Disable Telemetry\" -- no functional loss."));
            }
        }
        catch
        {
            // Service may not exist on stripped images.
        }
    }

    private static void CheckClassicRightClick(List<Finding> findings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\CLSID\{ClassicRightClickClsid}\InprocServer32");
            if (key is not null)
            {
                findings.Add(new Finding(
                    Severity.Green, "Right-click menu", "Classic Win10 menu enabled.", null));
            }
            else
            {
                findings.Add(new Finding(
                    Severity.Yellow,
                    "Win11 right-click menu uses overlay",
                    "Each right-click loads the new menu PLUS a \"Show more options\" indirection.",
                    "Use \"Apply Classic Right-Click\" -- restores Win10-style instant menu."));
            }
        }
        catch
        {
            // Registry access failure is itself diagnostic-worthy but tolerated.
        }
    }

    private static void CheckPagefile(List<Finding> findings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Name FROM Win32_PageFileSetting");
            foreach (ManagementObject mo in search.Get())
            {
                using (mo)
                {
                    var name = mo["Name"] as string ?? "";
                    if (name.Length < 2 || name[1] != ':') continue;
                    string driveLetter = name[..1];
                    try
                    {
                        var di = new DriveInfo(driveLetter + ":\\");
                        if (!di.IsReady) continue;
                        if (di.TotalSize <= 0) continue;
                        double freePct = di.AvailableFreeSpace * 100.0 / di.TotalSize;
                        if (freePct < 15)
                        {
                            findings.Add(new Finding(
                                Severity.Red,
                                "Pagefile on full drive",
                                string.Format(CultureInfo.InvariantCulture,
                                    "Pagefile {0} on drive with {1:F1}% free.", name, freePct),
                                "Move pagefile to drive with >20% free. Settings -> Performance -> Advanced -> Virtual Memory."));
                        }
                    }
                    catch
                    {
                        // ignore unreadable drive
                    }
                }
            }
        }
        catch
        {
            // ignore WMI failure
        }
    }

    private static void CheckStartupCount(List<Finding> findings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Name FROM Win32_StartupCommand");
            int count = 0;
            foreach (var _ in search.Get()) count++;
            if (count > 15)
            {
                findings.Add(new Finding(
                    Severity.Yellow,
                    "Many startup programs",
                    $"{count} entries in Win32_StartupCommand",
                    "Open Task Manager Startup tab and disable items you don't recognize."));
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void CheckRamPressure(List<Finding> findings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var search = new ManagementObjectSearcher(
                "SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in search.Get())
            {
                using (mo)
                {
                    ulong freeKB = (ulong)mo["FreePhysicalMemory"];
                    ulong totalKB = (ulong)mo["TotalVisibleMemorySize"];
                    if (totalKB == 0) continue;
                    double pct = freeKB * 100.0 / totalKB;
                    if (pct < 10)
                    {
                        findings.Add(new Finding(
                            Severity.Red,
                            "RAM pressure high",
                            string.Format(CultureInfo.InvariantCulture, "Only {0:F1}% free", pct),
                            "Use Boost tab -> \"Free RAM (Empty Working Sets)\"."));
                    }
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    public Task<DiagnoseResult> ResetQuickAccessAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var paths = new[]
            {
                Path.Combine(appData, "Microsoft", "Windows", "Recent"),
                Path.Combine(appData, "Microsoft", "Windows", "Recent", "AutomaticDestinations"),
                Path.Combine(appData, "Microsoft", "Windows", "Recent", "CustomDestinations")
            };
            int removed = 0;
            var errors = new List<string>();
            foreach (var p in paths)
            {
                if (!Directory.Exists(p)) continue;
                try
                {
                    foreach (var file in Directory.GetFiles(p))
                    {
                        try { File.Delete(file); removed++; }
                        catch (Exception ex) { errors.Add(ex.Message); }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex.Message);
                }
            }
            return new DiagnoseResult(true,
                "Restart Explorer (Boost tab) to refresh Quick Access pane.",
                FilesRemoved: removed,
                Errors: errors);
        }, ct);

    public Task<DiagnoseResult> DisableTelemetryAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var diag = new ServiceController("DiagTrack");
                if (diag.Status == ServiceControllerStatus.Running)
                {
                    diag.Stop();
                    diag.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                }
                ConfigureServiceStartupDisabled("DiagTrack");

                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var pol = hklm.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection"))
                {
                    pol?.SetValue("AllowTelemetry", 0, RegistryValueKind.DWord);
                }

                TryDisableServiceIfPresent("dmwappushservice");

                return new DiagnoseResult(true,
                    "DiagTrack stopped + disabled, AllowTelemetry policy = 0. Reversible via registry + sc config.");
            }
            catch (Exception ex)
            {
                return new DiagnoseResult(false, ex.Message);
            }
        }, ct);

    private static void ConfigureServiceStartupDisabled(string serviceName)
    {
        // ServiceController has no managed API to change start type; shell out to sc.exe.
        var psi = new ProcessStartInfo("sc.exe", $"config \"{serviceName}\" start= disabled")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(5000);
    }

    private static void TryDisableServiceIfPresent(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
            }
            ConfigureServiceStartupDisabled(serviceName);
        }
        catch
        {
            // Service may not exist (e.g., dmwappushservice is removed in 24H2).
        }
    }

    public Task<DiagnoseResult> EnableClassicRightClickAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                using var key = hkcu.CreateSubKey(
                    $@"Software\Classes\CLSID\{ClassicRightClickClsid}\InprocServer32");
                key?.SetValue("", "", RegistryValueKind.String);
                return new DiagnoseResult(true,
                    "Classic right-click menu enabled. Restart Explorer to apply.");
            }
            catch (Exception ex)
            {
                return new DiagnoseResult(false, ex.Message);
            }
        }, ct);

    public Task<DiagnoseResult> DisableClassicRightClickAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                hkcu.DeleteSubKeyTree($@"Software\Classes\CLSID\{ClassicRightClickClsid}", throwOnMissingSubKey: false);
                return new DiagnoseResult(true,
                    "Win11 right-click menu restored. Restart Explorer to apply.");
            }
            catch (Exception ex)
            {
                return new DiagnoseResult(false, ex.Message);
            }
        }, ct);

    public Task<DiagnoseResult> StartSearchIndexRebuildAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using (var sc = new ServiceController("WSearch"))
                {
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    }
                }
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows Search", writable: true))
                {
                    key?.SetValue("SetupCompletedSuccessfully", 0, RegistryValueKind.DWord);
                }
                using (var sc = new ServiceController("WSearch"))
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                }
                return new DiagnoseResult(true,
                    "Search index marked for rebuild via registry. Service restarted.");
            }
            catch (Exception ex)
            {
                return new DiagnoseResult(false, ex.Message);
            }
        }, ct);
}
