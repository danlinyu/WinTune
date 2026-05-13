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

    private readonly IPowerService _power;

    public DiagnoseService(IPowerService power)
    {
        _power = power;
    }

    public async Task<IReadOnlyList<Finding>> InvokeDiagnosticsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var findings = new List<Finding>();
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            CheckDiskHealth(findings, ct);
            CheckFreeSpace(findings, ct);
            CheckCloudShellExtensions(findings, ct);
            CheckQuickAccessBloat(findings, ct);
            CheckSearchIndex(findings, ct);
            CheckDiagTrack(findings, ct);
            CheckClassicRightClick(findings, ct);
            CheckPagefile(findings, ct);
            CheckStartupCount(findings, ct);
            CheckRamPressure(findings, ct);
        }, ct);
        var battery = await CheckBatteryThrottling(ct);
        if (battery is not null) findings.Add(battery);
        return findings;
    }

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
                            Id: "diag.disk",
                            Severity: Severity.Red,
                            Title: "Disk health warning",
                            Detail: $"{name}: Health={Format(health)}, Op={Format(op)}",
                            Hint: "Back up data immediately. Run vendor diagnostic tool.",
                            Actions: Array.Empty<FindingAction>()));
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
                Id: "diag.disk",
                Severity: Severity.Green,
                Title: "Disk health",
                Detail: "All physical disks Healthy / OK.",
                Hint: null,
                Actions: Array.Empty<FindingAction>()));
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
                        Id: "diag.freespace",
                        Severity: sev,
                        Title: $"Drive {d.Name.TrimEnd('\\')}: low free space",
                        Detail: string.Format(CultureInfo.InvariantCulture,
                            "{0:F1} GB free of {1:F1} GB ({2:F1}%)", freeGB, totalGB, pct),
                        Hint: "Win11 throttles disk I/O below ~10% free. Free space or move data.",
                        Actions: Array.Empty<FindingAction>()));
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
                Id: "diag.shell-ext",
                Severity: Severity.Red,
                Title: "Multiple cloud shell extensions loaded",
                Detail: $"Explorer has {hits.Count} cloud DLLs: {string.Join(", ", hits)}",
                Hint: "Pick ONE cloud, set BOTH to online-only / stream mode. Each shell ext queries cloud per file per folder open.",
                Actions: Array.Empty<FindingAction>()));
        }
        else if (hits.Count == 1)
        {
            findings.Add(new Finding(
                Id: "diag.shell-ext",
                Severity: Severity.Yellow,
                Title: "Cloud shell extension loaded",
                Detail: $"Explorer has 1 cloud DLL: {hits[0]}",
                Hint: "OK if intentional. Set Files On-Demand / Stream mode to avoid local copies.",
                Actions: Array.Empty<FindingAction>()));
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
            findings.Add(new Finding(
                Id: "diag.qa-bloat",
                Severity: Severity.Green,
                Title: "Quick Access",
                Detail: $"{totalQA} recent entries",
                Hint: null,
                Actions: Array.Empty<FindingAction>()));
        }
        else
        {
            findings.Add(new Finding(
                Id: "diag.qa-bloat",
                Severity: sev,
                Title: "Quick Access bloat",
                Detail: $"{autoCount} AutomaticDestinations + {recentCount} Recent shortcuts = {totalQA} total",
                Hint: "Stale entries referencing dead network paths cause Explorer to wait on timeout. Use \"Reset Quick Access\".",
                Actions: Array.Empty<FindingAction>()));
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
                Id: "diag.search-idx",
                Severity: Severity.Yellow,
                Title: "Windows Search index path missing",
                Detail: "CiFiles folder not found.",
                Hint: "Use \"Rebuild Search Index\".",
                Actions: Array.Empty<FindingAction>()));
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
                Id: "diag.search-idx",
                Severity: Severity.Red,
                Title: "Windows Search index empty",
                Detail: $"Index size: {mb} MB (expected: hundreds of MB)",
                Hint: "Quick Access \"Frequent\" falls back to full FS scan. Use \"Rebuild Search Index\".",
                Actions: Array.Empty<FindingAction>()));
        }
        else if (mb > 5000)
        {
            findings.Add(new Finding(
                Id: "diag.search-idx",
                Severity: Severity.Yellow,
                Title: "Windows Search index large",
                Detail: $"Index size: {mb} MB",
                Hint: "Trim indexed locations: Settings -> Searching Windows -> Find My Files -> Customize.",
                Actions: Array.Empty<FindingAction>()));
        }
        else
        {
            findings.Add(new Finding(
                Id: "diag.search-idx",
                Severity: Severity.Green,
                Title: "Windows Search index",
                Detail: $"{mb} MB",
                Hint: null,
                Actions: Array.Empty<FindingAction>()));
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
                    Id: "diag.diagtrack",
                    Severity: Severity.Yellow,
                    Title: "Telemetry (DiagTrack) running",
                    Detail: $"Status: {sc.Status}, StartType: {sc.StartType}",
                    Hint: "Background disk + network I/O. Use \"Disable Telemetry\" -- no functional loss.",
                    Actions: Array.Empty<FindingAction>()));
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
                    Id: "diag.classic",
                    Severity: Severity.Green,
                    Title: "Right-click menu",
                    Detail: "Classic Win10 menu enabled.",
                    Hint: null,
                    Actions: Array.Empty<FindingAction>()));
            }
            else
            {
                findings.Add(new Finding(
                    Id: "diag.classic",
                    Severity: Severity.Yellow,
                    Title: "Win11 right-click menu uses overlay",
                    Detail: "Each right-click loads the new menu PLUS a \"Show more options\" indirection.",
                    Hint: "Use \"Apply Classic Right-Click\" -- restores Win10-style instant menu.",
                    Actions: Array.Empty<FindingAction>()));
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
                                Id: "diag.pagefile",
                                Severity: Severity.Red,
                                Title: "Pagefile on full drive",
                                Detail: string.Format(CultureInfo.InvariantCulture,
                                    "Pagefile {0} on drive with {1:F1}% free.", name, freePct),
                                Hint: "Move pagefile to drive with >20% free. Settings -> Performance -> Advanced -> Virtual Memory.",
                                Actions: Array.Empty<FindingAction>()));
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
                    Id: "diag.startup",
                    Severity: Severity.Yellow,
                    Title: "Many startup programs",
                    Detail: $"{count} entries in Win32_StartupCommand",
                    Hint: "Open Task Manager Startup tab and disable items you don't recognize.",
                    Actions: Array.Empty<FindingAction>()));
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
                            Id: "diag.ram",
                            Severity: Severity.Red,
                            Title: "RAM pressure high",
                            Detail: string.Format(CultureInfo.InvariantCulture, "Only {0:F1}% free", pct),
                            Hint: "Use Boost tab -> \"Free RAM (Empty Working Sets)\".",
                            Actions: Array.Empty<FindingAction>()));
                    }
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private async Task<Finding?> CheckBatteryThrottling(CancellationToken ct)
    {
        PowerState state;
        try
        {
            state = await _power.CaptureCurrentAsync(ct);
        }
        catch (Exception ex)
        {
            return new Finding(
                Id:       "diag.battery",
                Severity: Severity.Yellow,
                Title:    "Battery throttling detector unavailable",
                Detail:   $"Could not read DC power settings: {ex.Message}",
                Hint:     null,
                Actions:  Array.Empty<FindingAction>());
        }

        var symptoms = new List<string>();
        var actions  = new List<FindingAction>();

        if (state.DcCpuMaxPct < 100)
        {
            symptoms.Add($"  - CPU max on DC: {state.DcCpuMaxPct}% (should be 100%)");
            actions.Add(new FindingAction("battery.fix-cpu-max", "Fix CPU max", null));
        }
        if (state.DcEpp >= 32)
        {
            symptoms.Add($"  - EPP on DC: {state.DcEpp} (should be 0..31 / Performance)");
            actions.Add(new FindingAction("battery.fix-epp", "Fix EPP", null));
        }
        if (state.DcCoolingPolicy == "Passive")
        {
            symptoms.Add("  - Cooling policy on DC: Passive (should be Active)");
            actions.Add(new FindingAction("battery.fix-cooling", "Fix cooling", null));
        }

        var snapshotPresent = _power.SnapshotExists;
        if (symptoms.Count == 0 && !snapshotPresent)
            return null;

        if (symptoms.Count > 0)
        {
            actions.Insert(0, new FindingAction("battery.unleash-all", "Unleash all", null));
        }
        if (snapshotPresent)
        {
            actions.Add(new FindingAction(
                "battery.restore", "Restore prior settings",
                Confirm: "Revert DC power settings to your prior values. Continue?"));
        }

        var detail = symptoms.Count > 0
            ? $"Active plan: {state.ActiveScheme}\n" + string.Join("\n", symptoms)
            : "DC power settings look healthy. A snapshot from a prior fix is still on disk; use Restore to delete it.";

        return new Finding(
            Id:       "diag.battery",
            Severity: symptoms.Count > 0 ? Severity.Red : Severity.Green,
            Title:    symptoms.Count > 0
                          ? $"Battery throttling: {symptoms.Count} issue{(symptoms.Count == 1 ? "" : "s")}"
                          : "Battery throttling: healthy (snapshot present)",
            Detail:   detail,
            Hint:     symptoms.Count > 0
                          ? "Tier B fix: CPU max 100, EPP Performance, cooling Active. Reversible."
                          : null,
            Actions:  actions);
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
