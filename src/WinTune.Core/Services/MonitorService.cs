using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using WinTune.Core.Models;

namespace WinTune.Core.Services;

public sealed class MonitorService : IMonitorService
{
    private static readonly Lazy<PerformanceCounter?> CpuCounter = new(() =>
    {
        try
        {
            var pc = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            pc.NextValue();
            Thread.Sleep(100);
            return pc;
        }
        catch
        {
            return null;
        }
    });

    public Task<PerfSnapshot> GetPerfSnapshotAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            int cpuPct = ReadCpuPct();
            (double ramUsed, double ramTotal, double ramPct) = ReadMemory();
            (double diskUsed, double diskFree, double diskTotal, double diskPct) = ReadDisk();
            return new PerfSnapshot(
                cpuPct, ramUsed, ramTotal, ramPct,
                diskUsed, diskFree, diskTotal, diskPct,
                DateTime.Now);
        }, ct);

    public Task<IReadOnlyList<ProcessSnapshot>> GetTopProcessesAsync(int count = 10, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<ProcessSnapshot>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var snapshots = new List<ProcessSnapshot>();
            foreach (var p in Process.GetProcesses())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long ws = p.WorkingSet64;
                    if (ws <= 0) continue;
                    string startStr = "-";
                    try { startStr = p.StartTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture); } catch { /* protected/system processes throw on access */ }
                    snapshots.Add(new ProcessSnapshot(
                        Name: p.ProcessName,
                        Id: p.Id,
                        RamMB: Math.Round(ws / 1024.0 / 1024.0, 1),
                        Threads: p.Threads.Count,
                        StartTime: startStr));
                }
                catch
                {
                    // Protected/system processes throw on access; skip them.
                }
                finally
                {
                    p.Dispose();
                }
            }
            return snapshots
                .OrderByDescending(p => p.RamMB)
                .Take(count)
                .ToList();
        }, ct);

    private static int ReadCpuPct()
    {
        var pc = CpuCounter.Value;
        if (pc is not null)
        {
            try
            {
                float v = pc.NextValue();
                return (int)Math.Round(v, MidpointRounding.AwayFromZero);
            }
            catch
            {
                // fall through to WMI
            }
        }

        try
        {
            using var mc = new ManagementClass("Win32_Processor");
            using var moc = mc.GetInstances();
            int sum = 0, n = 0;
            foreach (ManagementObject mo in moc)
            {
                using (mo)
                {
                    if (mo["LoadPercentage"] is ushort load)
                    {
                        sum += load;
                        n++;
                    }
                }
            }
            return n == 0 ? 0 : sum / n;
        }
        catch
        {
            return 0;
        }
    }

    private static (double used, double total, double pct) ReadMemory()
    {
        try
        {
            using var search = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in search.Get())
            {
                using (mo)
                {
                    ulong totalKB = (ulong)mo["TotalVisibleMemorySize"];
                    ulong freeKB = (ulong)mo["FreePhysicalMemory"];
                    double totalGB = totalKB / 1024.0 / 1024.0;
                    double usedGB = (totalKB - freeKB) / 1024.0 / 1024.0;
                    double pct = totalKB == 0
                        ? 0
                        : Math.Round((totalKB - freeKB) * 100.0 / totalKB, 1);
                    return (Math.Round(usedGB, 2), Math.Round(totalGB, 2), pct);
                }
            }
        }
        catch
        {
            // fall through
        }
        return (0, 0, 0);
    }

    private static (double used, double free, double total, double pct) ReadDisk()
    {
        try
        {
            var sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var di = new DriveInfo(sysDrive);
            if (!di.IsReady) return (0, 0, 0, 0);
            double totalGB = di.TotalSize / 1024.0 / 1024.0 / 1024.0;
            double freeGB = di.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
            double usedGB = totalGB - freeGB;
            double pct = totalGB == 0 ? 0 : Math.Round(usedGB * 100.0 / totalGB, 1);
            return (Math.Round(usedGB, 2), Math.Round(freeGB, 2), Math.Round(totalGB, 2), pct);
        }
        catch
        {
            return (0, 0, 0, 0);
        }
    }
}
