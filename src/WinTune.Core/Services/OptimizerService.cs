using System.IO;
using System.Management;
using WinTune.Core.Models;
using WinTune.Core.NativeInterop;

namespace WinTune.Core.Services;

public sealed class OptimizerService : IOptimizerService
{
    private static readonly TimeSpan AutomaticActionCooldown = TimeSpan.FromMinutes(5);
    private static readonly char[] LineSeparators = { '\r', '\n' };

    private readonly IBoostService   _boost;
    private readonly IProcessRunner  _proc;

    public OptimizerService(IBoostService boost, IProcessRunner proc)
    {
        _boost = boost;
        _proc  = proc;
    }

    public MachineProfile BuildMachineProfile(
        PerfSnapshot snapshot,
        IReadOnlyList<DriveProfile>? fixedDrives = null,
        IReadOnlyList<GpuProfile>? gpus = null)
    {
        int logicalProcessors = Environment.ProcessorCount;
        double ramTotalGB = snapshot.RamTotalGB;
        fixedDrives ??= CreateSystemDriveFallback(snapshot);
        gpus ??= Array.Empty<GpuProfile>();

        double diskTotalGB = fixedDrives.FirstOrDefault(d => d.IsSystemDrive)?.TotalGB
            ?? snapshot.DiskTotalGB;
        double totalFixedDriveGB = fixedDrives.Sum(d => d.TotalGB);

        var tier = ramTotalGB switch
        {
            < 8  => MachineCapabilityTier.Constrained,
            < 16 => MachineCapabilityTier.Balanced,
            < 32 => MachineCapabilityTier.HighCapacity,
            _    => MachineCapabilityTier.Workstation
        };

        int memoryThreshold = tier switch
        {
            MachineCapabilityTier.Constrained  => 76,
            MachineCapabilityTier.Balanced     => 82,
            MachineCapabilityTier.HighCapacity => 88,
            MachineCapabilityTier.Workstation  => 92,
            _                                  => 84
        };

        if (logicalProcessors <= 4)
        {
            memoryThreshold = Math.Max(70, memoryThreshold - 4);
        }

        int diskFreeWarningPct = tier switch
        {
            MachineCapabilityTier.Constrained => 15,
            MachineCapabilityTier.Balanced    => 12,
            _                                 => 10
        };
        double diskFreeWarningGB = Math.Round(Math.Max(20, diskTotalGB * diskFreeWarningPct / 100.0), 1);

        return new MachineProfile(
            tier,
            logicalProcessors,
            Math.Round(ramTotalGB, 1),
            Math.Round(diskTotalGB, 1),
            Math.Round(totalFixedDriveGB, 1),
            memoryThreshold,
            diskFreeWarningGB,
            diskFreeWarningPct,
            fixedDrives,
            gpus);
    }

    public Task<IReadOnlyList<DriveProfile>> GetFixedDrivesAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<DriveProfile>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var mediaByRoot = TryGetDriveMediaKinds();
            var activityByRoot = TryGetDriveActivity();
            var drives = new List<DriveProfile>();

            foreach (var drive in DriveInfo.GetDrives())
            {
                ct.ThrowIfCancellationRequested();
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

                double totalGB = drive.TotalSize / 1024.0 / 1024.0 / 1024.0;
                double freeGB = drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
                double usedPct = totalGB == 0 ? 0 : Math.Round((totalGB - freeGB) * 100.0 / totalGB, 1);
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.Name
                    : $"{drive.Name} {drive.VolumeLabel}";

                activityByRoot.TryGetValue(drive.Name, out var activity);
                drives.Add(new DriveProfile(
                    drive.Name,
                    label.Trim(),
                    mediaByRoot.TryGetValue(drive.Name, out var mediaKind) ? mediaKind : DriveMediaKind.Unknown,
                    Math.Round(freeGB, 1),
                    Math.Round(totalGB, 1),
                    usedPct,
                    string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase),
                    activity?.ActiveTimePct,
                    activity?.QueueLength,
                    activity?.ThroughputMBps));
            }

            return drives;
        }, ct);

    public Task<IReadOnlyList<GpuProfile>> GetGpuProfilesAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<GpuProfile>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var dxgiAdapters = Dxgi.EnumerateAdapters();
                if (dxgiAdapters.Count > 0)
                {
                    return dxgiAdapters
                        .Select(a => new GpuProfile(
                            a.Description,
                            BytesToGB(a.DedicatedVideoMemoryBytes)))
                        .ToList();
                }
            }
            catch
            {
                // Fall back to WMI below. DXGI can be unavailable in unusual service/session contexts.
            }

            var gpus = new List<GpuProfile>();
            try
            {
                using var search = new ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM FROM Win32_VideoController");
                foreach (ManagementObject mo in search.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    using (mo)
                    {
                        string name = Convert.ToString(mo["Name"], System.Globalization.CultureInfo.InvariantCulture)
                            ?? "Unknown GPU";
                        double? ramGB = TryReadAdapterRamGB(mo["AdapterRAM"]);
                        gpus.Add(new GpuProfile(name, ramGB));
                    }
                }
            }
            catch
            {
                // GPU inventory is best-effort; many drivers expose partial WMI data.
            }

            return gpus;
        }, ct);

    public OptimizationAssessment Assess(
        PerfSnapshot snapshot,
        IReadOnlyList<ProcessSnapshot> topProcesses,
        int consecutiveMemoryPressureSamples,
        DateTime? lastAutomaticActionUtc,
        DateTime nowUtc,
        IReadOnlyList<DriveProfile>? fixedDrives = null,
        IReadOnlyList<GpuProfile>? gpus = null)
    {
        if (consecutiveMemoryPressureSamples < 0)
        {
            consecutiveMemoryPressureSamples = 0;
        }

        var profile = BuildMachineProfile(snapshot, fixedDrives, gpus);
        var recommendations = new List<OptimizationRecommendation>();
        var severity = OptimizationSeverity.Green;
        bool isMemoryPressure = snapshot.RamPct >= profile.MemoryPressureThresholdPct;
        bool isMemoryCritical = snapshot.RamPct >= 95 || RemainingRamGB(snapshot) < CriticalFreeRamGB(snapshot);
        bool isMemorySustained = isMemoryPressure && consecutiveMemoryPressureSamples >= 2;

        if (isMemoryPressure)
        {
            var memorySeverity = isMemoryCritical || isMemorySustained
                ? OptimizationSeverity.Red
                : OptimizationSeverity.Yellow;
            severity = Max(severity, memorySeverity);

            string topProcess = topProcesses.Count == 0
                ? "No process list available."
                : $"Largest RAM user: {topProcesses[0].Name} ({topProcesses[0].RamMB:N0} MB).";
            string detail = isMemorySustained
                ? $"RAM has stayed high for {consecutiveMemoryPressureSamples} samples: {snapshot.RamPct:N0}% used. {topProcess}"
                : $"RAM is above this machine's {profile.MemoryPressureThresholdPct}% pressure line: {snapshot.RamPct:N0}% used. {topProcess}";

            recommendations.Add(new OptimizationRecommendation(
                memorySeverity,
                "Memory",
                detail,
                "Use a reversible working-set trim. Do not kill apps automatically."));
        }

        foreach (var drive in profile.FixedDrives)
        {
            double freePct = Math.Max(0, 100 - drive.UsedPct);
            double warningGB = Math.Round(Math.Max(20, drive.TotalGB * profile.DiskFreeWarningPct / 100.0), 1);
            bool diskLowByGb = drive.FreeGB > 0 && drive.FreeGB < warningGB;
            bool diskLowByPct = freePct > 0 && freePct < profile.DiskFreeWarningPct;
            if (diskLowByGb || diskLowByPct)
            {
                var diskSeverity = drive.FreeGB < 10 || freePct < 5
                    ? OptimizationSeverity.Red
                    : OptimizationSeverity.Yellow;
                severity = Max(severity, diskSeverity);
                recommendations.Add(new OptimizationRecommendation(
                    diskSeverity,
                    drive.IsSystemDrive ? "System drive" : $"Drive {drive.RootPath}",
                    $"{drive.DisplayName} free space is {drive.FreeGB:N1} GB ({freePct:N0}%). " +
                    $"This drive's warning line is {warningGB:N1} GB or {profile.DiskFreeWarningPct}%.",
                    "Run Cleanup or Dedupe manually; automatic deletion is intentionally not enabled."));
            }

            if (drive.ActiveTimePct is { } activePct)
            {
                double queue = drive.QueueLength ?? 0;
                bool saturated = activePct >= 95 || queue >= 4;
                bool busy = activePct >= 80 || queue >= 2;
                if (saturated || busy)
                {
                    var ioSeverity = saturated ? OptimizationSeverity.Red : OptimizationSeverity.Yellow;
                    severity = Max(severity, ioSeverity);
                    string throughput = drive.ThroughputMBps is { } mbps
                        ? $", {mbps:N1} MB/s"
                        : "";
                    recommendations.Add(new OptimizationRecommendation(
                        ioSeverity,
                        drive.IsSystemDrive ? "System drive I/O" : $"Drive {drive.RootPath} I/O",
                        $"{drive.DisplayName} is busy: {activePct}% active, queue {queue:N1}{throughput}.",
                        "Open Resource Monitor and sort the Disk tab by Total B/sec before running cleanup or optimize."));
                }
            }
        }

        foreach (var drive in profile.FixedDrives.Where(d => d.MediaKind == DriveMediaKind.Hdd))
        {
            recommendations.Add(new OptimizationRecommendation(
                OptimizationSeverity.Green,
                $"Drive {drive.RootPath}",
                "Mechanical drive detected. Fragmentation can affect random and startup I/O on HDDs.",
                "Use the Optimize button when the machine is idle; Windows chooses the correct operation."));
        }

        if (snapshot.CpuPct >= 85)
        {
            var cpuSeverity = snapshot.CpuPct >= 95 ? OptimizationSeverity.Red : OptimizationSeverity.Yellow;
            severity = Max(severity, cpuSeverity);
            recommendations.Add(new OptimizationRecommendation(
                cpuSeverity,
                "CPU",
                $"CPU is currently {snapshot.CpuPct}%.",
                "Identify the active app in Task Manager; WinTune will not terminate processes automatically."));
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add(new OptimizationRecommendation(
                OptimizationSeverity.Green,
                "System pressure",
                "CPU, RAM, and fixed-drive free space are inside this machine's adaptive thresholds.",
                "No action needed."));
        }

        var suggestedAction = OptimizationActionKind.None;
        TimeSpan? cooldownRemaining = null;
        if (isMemorySustained)
        {
            cooldownRemaining = CooldownRemaining(lastAutomaticActionUtc, nowUtc);
            if (cooldownRemaining.Value <= TimeSpan.Zero)
            {
                cooldownRemaining = null;
                suggestedAction = OptimizationActionKind.ClearWorkingSets;
            }
        }

        string summary = severity switch
        {
            OptimizationSeverity.Red    => "Pressure detected. Safe reversible action may help responsiveness.",
            OptimizationSeverity.Yellow => "Watch state. The machine is near a pressure line.",
            _                           => "Healthy. No smoothing action needed."
        };

        return new OptimizationAssessment(
            profile,
            severity,
            summary,
            isMemoryPressure,
            isMemorySustained,
            cooldownRemaining,
            suggestedAction,
            recommendations);
    }

    public async Task<OptimizationActionResult> ApplyActionAsync(
        OptimizationActionKind action,
        CancellationToken ct = default)
    {
        switch (action)
        {
            case OptimizationActionKind.None:
                return new OptimizationActionResult(action, true, "No action needed.");

            case OptimizationActionKind.ClearWorkingSets:
                var result = await _boost.ClearWorkingSetsAsync(ct);
                return new OptimizationActionResult(
                    action,
                    true,
                    $"Trimmed {result.ProcessesTrimmed} processes ({result.ProcessesSkipped} skipped), " +
                    $"estimated freed {FormatBytes(result.BytesFreedEstimate)}.");

            default:
                return new OptimizationActionResult(action, false, $"Unsupported optimizer action: {action}");
        }
    }

    public async Task<OptimizationActionResult> OptimizeDriveAsync(
        string driveRoot,
        CancellationToken ct = default)
    {
        string driveArg = NormalizeDriveArgument(driveRoot);
        var result = await _proc.RunAsync(
            "defrag.exe",
            new[] { driveArg, "/O", "/U", "/V" },
            ct);

        string output = FirstMeaningfulLine(result.Stdout);
        if (result.ExitCode == 0)
        {
            string note = string.IsNullOrWhiteSpace(output)
                ? $"Windows drive optimization completed for {driveArg}."
                : $"Windows drive optimization completed for {driveArg}: {output}";
            return new OptimizationActionResult(OptimizationActionKind.OptimizeDrive, true, note);
        }

        string error = string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stdout.Trim()
            : result.Stderr.Trim();
        return new OptimizationActionResult(
            OptimizationActionKind.OptimizeDrive,
            false,
            $"Windows drive optimization failed for {driveArg}: {error}");
    }

    private static DriveProfile[] CreateSystemDriveFallback(PerfSnapshot snapshot)
    {
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        return new[]
        {
            new DriveProfile(
                systemRoot,
                systemRoot,
                DriveMediaKind.Unknown,
                snapshot.DiskFreeGB,
                snapshot.DiskTotalGB,
                snapshot.DiskPct,
                true)
        };
    }

    private static Dictionary<string, DriveMediaKind> TryGetDriveMediaKinds()
    {
        var partitionMedia = new Dictionary<string, DriveMediaKind>(StringComparer.OrdinalIgnoreCase);
        var driveMedia = new Dictionary<string, DriveMediaKind>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var physicalMedia = new Dictionary<string, DriveMediaKind>(StringComparer.OrdinalIgnoreCase);
            using (var search = new ManagementObjectSearcher(
                       @"root\Microsoft\Windows\Storage",
                       "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk"))
            {
                foreach (ManagementObject mo in search.Get())
                {
                    using (mo)
                    {
                        var id = Convert.ToString(mo["DeviceId"], System.Globalization.CultureInfo.InvariantCulture);
                        if (id is null) continue;
                        physicalMedia[id] = ToMediaKind(mo["MediaType"]);
                    }
                }
            }

            using (var search = new ManagementObjectSearcher(
                       "SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition"))
            {
                foreach (ManagementObject mo in search.Get())
                {
                    using (mo)
                    {
                        string? antecedent = Convert.ToString(mo["Antecedent"], System.Globalization.CultureInfo.InvariantCulture);
                        string? dependent = Convert.ToString(mo["Dependent"], System.Globalization.CultureInfo.InvariantCulture);
                        string? diskIndex = ExtractPhysicalDriveIndex(antecedent);
                        string? partitionDevice = ExtractDeviceId(dependent);
                        if (diskIndex is not null &&
                            partitionDevice is not null &&
                            physicalMedia.TryGetValue(diskIndex, out var mediaKind))
                        {
                            partitionMedia[partitionDevice] = mediaKind;
                        }
                    }
                }
            }

            using (var search = new ManagementObjectSearcher(
                       "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
            {
                foreach (ManagementObject mo in search.Get())
                {
                    using (mo)
                    {
                        string? antecedent = Convert.ToString(mo["Antecedent"], System.Globalization.CultureInfo.InvariantCulture);
                        string? dependent = Convert.ToString(mo["Dependent"], System.Globalization.CultureInfo.InvariantCulture);
                        string? partitionDevice = ExtractDeviceId(antecedent);
                        string? driveRoot = ExtractDeviceId(dependent);
                        if (partitionDevice is not null &&
                            driveRoot is not null &&
                            partitionMedia.TryGetValue(partitionDevice, out var mediaKind))
                        {
                            driveMedia[driveRoot + "\\"] = mediaKind;
                        }
                    }
                }
            }
        }
        catch
        {
            // Media kind is optional; defrag /O remains media-aware even if WMI mapping fails.
        }

        return driveMedia;
    }

    private static Dictionary<string, DriveActivity> TryGetDriveActivity()
    {
        var activity = new Dictionary<string, DriveActivity>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var search = new ManagementObjectSearcher(
                "SELECT Name, PercentDiskTime, CurrentDiskQueueLength, DiskBytesPersec " +
                "FROM Win32_PerfFormattedData_PerfDisk_LogicalDisk");
            foreach (ManagementObject mo in search.Get())
            {
                using (mo)
                {
                    string? name = Convert.ToString(mo["Name"], System.Globalization.CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(name) || name == "_Total") continue;
                    if (name.Length != 2 || name[1] != ':') continue;

                    int activePct = Math.Min(100, Math.Max(0,
                        Convert.ToInt32(mo["PercentDiskTime"] ?? 0, System.Globalization.CultureInfo.InvariantCulture)));
                    double queue = Convert.ToDouble(
                        mo["CurrentDiskQueueLength"] ?? 0,
                        System.Globalization.CultureInfo.InvariantCulture);
                    double bytesPerSec = Convert.ToDouble(
                        mo["DiskBytesPersec"] ?? 0,
                        System.Globalization.CultureInfo.InvariantCulture);

                    activity[name + "\\"] = new DriveActivity(
                        activePct,
                        Math.Round(queue, 1),
                        Math.Round(bytesPerSec / 1024.0 / 1024.0, 1));
                }
            }
        }
        catch
        {
            // Perf counters can be missing or rebuilding; drive capacity still remains useful.
        }

        return activity;
    }

    private static string? ExtractPhysicalDriveIndex(string? path)
    {
        string? deviceId = ExtractDeviceId(path);
        if (deviceId is null || !deviceId.StartsWith(@"\\.\PHYSICALDRIVE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return deviceId[@"\\.\PHYSICALDRIVE".Length..];
    }

    private static string? ExtractDeviceId(string? path)
    {
        if (path is null) return null;
        int marker = path.IndexOf("DeviceID=\"", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        int start = marker + "DeviceID=\"".Length;
        int end = path.IndexOf('"', start);
        if (end <= start) return null;
        return path[start..end].Replace(@"\\", @"\", StringComparison.Ordinal);
    }

    private static DriveMediaKind ToMediaKind(object? value)
    {
        int mediaType = Convert.ToInt32(value ?? 0, System.Globalization.CultureInfo.InvariantCulture);
        return mediaType switch
        {
            3 => DriveMediaKind.Hdd,
            4 => DriveMediaKind.Ssd,
            5 => DriveMediaKind.Scm,
            _ => DriveMediaKind.Unknown
        };
    }

    private static double? TryReadAdapterRamGB(object? adapterRam)
    {
        if (adapterRam is null) return null;
        try
        {
            ulong bytes = Convert.ToUInt64(adapterRam, System.Globalization.CultureInfo.InvariantCulture);
            return bytes == 0 ? null : Math.Round(bytes / 1024.0 / 1024.0 / 1024.0, 1);
        }
        catch
        {
            return null;
        }
    }

    private static double? BytesToGB(ulong bytes) =>
        bytes == 0 ? null : Math.Round(bytes / 1024.0 / 1024.0 / 1024.0, 1);

    private static string NormalizeDriveArgument(string driveRoot)
    {
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            throw new ArgumentException("drive root is required", nameof(driveRoot));
        }

        string root = Path.GetPathRoot(driveRoot.Trim()) ?? driveRoot.Trim();
        if (root.Length >= 2 && root[1] == ':')
        {
            return root[..2].ToUpperInvariant();
        }

        throw new ArgumentException($"unsupported drive root '{driveRoot}'", nameof(driveRoot));
    }

    private static string FirstMeaningfulLine(string output)
    {
        foreach (var line in output.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }

        return "";
    }

    private static TimeSpan CooldownRemaining(DateTime? lastAutomaticActionUtc, DateTime nowUtc)
    {
        if (lastAutomaticActionUtc is null) return TimeSpan.Zero;
        var elapsed = nowUtc - lastAutomaticActionUtc.Value;
        return elapsed >= AutomaticActionCooldown
            ? TimeSpan.Zero
            : AutomaticActionCooldown - elapsed;
    }

    private static double RemainingRamGB(PerfSnapshot snapshot) =>
        Math.Max(0, snapshot.RamTotalGB - snapshot.RamUsedGB);

    private static double CriticalFreeRamGB(PerfSnapshot snapshot) =>
        Math.Min(1.5, Math.Max(0.75, snapshot.RamTotalGB * 0.08));

    private static OptimizationSeverity Max(OptimizationSeverity left, OptimizationSeverity right) =>
        (OptimizationSeverity)Math.Max((int)left, (int)right);

    private static string FormatBytes(long bytes)
    {
        const long KB = 1024L;
        const long MB = KB * 1024;
        const long GB = MB * 1024;
        return bytes switch
        {
            >= GB => $"{bytes / (double)GB:N2} GB",
            >= MB => $"{bytes / (double)MB:N2} MB",
            >= KB => $"{bytes / (double)KB:N2} KB",
            _     => $"{bytes} B"
        };
    }

    private sealed record DriveActivity(
        int ActiveTimePct,
        double QueueLength,
        double ThroughputMBps);
}
