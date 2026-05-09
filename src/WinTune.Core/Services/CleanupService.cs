using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.ServiceProcess;
using System.Text;
using WinTune.Core.Models;
using WinTune.Core.NativeInterop;

namespace WinTune.Core.Services;

public sealed class CleanupService : ICleanupService
{
    private static readonly CleanupTarget[] AllTargets =
    {
        CleanupTarget.UserTemp,
        CleanupTarget.SystemTemp,
        CleanupTarget.Prefetch,
        CleanupTarget.WindowsErrorReports,
        CleanupTarget.WindowsUpdate,
        CleanupTarget.EdgeCache,
        CleanupTarget.ChromeCache,
        CleanupTarget.FirefoxCache,
        CleanupTarget.RecycleBin,
        CleanupTarget.DnsCache
    };

    private string? _lastLogPath;

    public IReadOnlyList<CleanupTarget> GetAvailableTargets() => AllTargets;

    public string? GetLastCleanupLogPath() => _lastLogPath;

    public string FormatBytes(long bytes)
    {
        const long KB = 1024L;
        const long MB = KB * 1024;
        const long GB = MB * 1024;
        if (bytes >= GB) return string.Format(CultureInfo.InvariantCulture, "{0:N2} GB", bytes / (double)GB);
        if (bytes >= MB) return string.Format(CultureInfo.InvariantCulture, "{0:N2} MB", bytes / (double)MB);
        if (bytes >= KB) return string.Format(CultureInfo.InvariantCulture, "{0:N2} KB", bytes / (double)KB);
        return string.Format(CultureInfo.InvariantCulture, "{0} B", bytes);
    }

    public Task<IReadOnlyList<CleanupResult>> InvokeCleanupAsync(
        IReadOnlyCollection<CleanupTarget> targets,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<CleanupResult>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var results = new List<CleanupResult>();
            int total = targets.Count;
            int i = 0;
            foreach (var t in targets)
            {
                ct.ThrowIfCancellationRequested();
                i++;
                progress?.Report(new CleanupProgress("Start", i, total, t));
                var r = InvokeSingleTarget(t, ct);
                results.Add(r);
                progress?.Report(new CleanupProgress(
                    "TargetDone", i, total, t,
                    FilesRemoved: r.FilesRemoved,
                    BytesFreed: r.BytesFreed,
                    Skipped: r.Skipped,
                    ErrorCount: r.Errors.Count));
            }

            WriteRunLog(results, targets);
            return results;
        }, ct);

    private static CleanupResult InvokeSingleTarget(CleanupTarget target, CancellationToken ct)
    {
        var errors = new List<string>();
        try
        {
            switch (target)
            {
                case CleanupTarget.UserTemp:
                    return RunPathTarget(target,
                        Environment.GetEnvironmentVariable("TEMP")
                            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
                        ct);

                case CleanupTarget.SystemTemp:
                    return RunPathTarget(target,
                        Path.Combine(SystemRoot, "Temp"),
                        ct);

                case CleanupTarget.Prefetch:
                    return RunPathTarget(target,
                        Path.Combine(SystemRoot, "Prefetch"),
                        ct);

                case CleanupTarget.WindowsErrorReports:
                    return RunMultiPathTarget(target, new[]
                    {
                        Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"),
                        Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue"),
                        Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "Temp")
                    }, ct);

                case CleanupTarget.WindowsUpdate:
                    return RunWindowsUpdateTarget(ct);

                case CleanupTarget.EdgeCache:
                    return RunBrowserTarget(target, browserProcessName: "msedge",
                        userDataPath: Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data"),
                        ct);

                case CleanupTarget.ChromeCache:
                    return RunBrowserTarget(target, browserProcessName: "chrome",
                        userDataPath: Path.Combine(LocalAppData, "Google", "Chrome", "User Data"),
                        ct);

                case CleanupTarget.FirefoxCache:
                    return RunFirefoxTarget(ct);

                case CleanupTarget.RecycleBin:
                    return RunRecycleBinTarget();

                case CleanupTarget.DnsCache:
                    return RunDnsCacheTarget();

                default:
                    return new CleanupResult(target, 0, 0, Array.Empty<string>(), Skipped: true,
                        SkipReason: $"Unknown target: {target}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"Fatal: {ex.Message}");
            return new CleanupResult(target, 0, 0, errors, false, null);
        }
    }

    private static string SystemRoot =>
        Environment.GetEnvironmentVariable("SystemRoot")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static string ProgramData =>
        Environment.GetEnvironmentVariable("ProgramData")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static CleanupResult RunPathTarget(CleanupTarget target, string path, CancellationToken ct)
    {
        var (files, bytes, errors) = RemovePathContents(path, ct);
        return new CleanupResult(target, files, bytes, errors, false, null);
    }

    private static CleanupResult RunMultiPathTarget(CleanupTarget target, string[] paths, CancellationToken ct)
    {
        int totalFiles = 0;
        long totalBytes = 0;
        var errors = new List<string>();
        foreach (var p in paths)
        {
            ct.ThrowIfCancellationRequested();
            var (files, bytes, perPathErrors) = RemovePathContents(p, ct);
            totalFiles += files;
            totalBytes += bytes;
            errors.AddRange(perPathErrors);
        }
        return new CleanupResult(target, totalFiles, totalBytes, errors, false, null);
    }

    private static CleanupResult RunWindowsUpdateTarget(CancellationToken ct)
    {
        var stopped = new List<string>();
        var errors = new List<string>();
        foreach (var name in new[] { "wuauserv", "bits" })
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    stopped.Add(name);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"stop {name}: {ex.Message}");
            }
        }

        int files = 0;
        long bytes = 0;
        try
        {
            var path = Path.Combine(SystemRoot, "SoftwareDistribution", "Download");
            var (f, b, errs) = RemovePathContents(path, ct);
            files = f;
            bytes = b;
            errors.AddRange(errs);
        }
        finally
        {
            // Critical: services MUST come back even if delete throws.
            foreach (var name in stopped)
            {
                try
                {
                    using var sc = new ServiceController(name);
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                }
                catch (Exception ex)
                {
                    errors.Add($"start {name}: {ex.Message}");
                }
            }
        }
        return new CleanupResult(CleanupTarget.WindowsUpdate, files, bytes, errors, false, null);
    }

    private static CleanupResult RunBrowserTarget(CleanupTarget target, string browserProcessName, string userDataPath, CancellationToken ct)
    {
        if (Process.GetProcessesByName(browserProcessName).Length > 0)
        {
            return new CleanupResult(target, 0, 0, Array.Empty<string>(), Skipped: true,
                SkipReason: $"{browserProcessName} is running -- close it and re-run.");
        }

        int totalFiles = 0;
        long totalBytes = 0;
        var errors = new List<string>();
        string[] subProfiles = { "Default", "Profile 1", "Profile 2", "Profile 3" };
        string[] cacheFolders = { "Cache", "Code Cache", "GPUCache" };
        foreach (var sub in subProfiles)
        {
            foreach (var cache in cacheFolders)
            {
                ct.ThrowIfCancellationRequested();
                var p = Path.Combine(userDataPath, sub, cache);
                var (f, b, errs) = RemovePathContents(p, ct);
                totalFiles += f;
                totalBytes += b;
                errors.AddRange(errs);
            }
        }
        return new CleanupResult(target, totalFiles, totalBytes, errors, false, null);
    }

    private static CleanupResult RunFirefoxTarget(CancellationToken ct)
    {
        if (Process.GetProcessesByName("firefox").Length > 0)
        {
            return new CleanupResult(CleanupTarget.FirefoxCache, 0, 0, Array.Empty<string>(),
                Skipped: true, SkipReason: "Firefox is running -- close it and re-run.");
        }

        int totalFiles = 0;
        long totalBytes = 0;
        var errors = new List<string>();
        var profilesRoot = Path.Combine(LocalAppData, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(profilesRoot))
        {
            foreach (var profile in Directory.EnumerateDirectories(profilesRoot))
            {
                ct.ThrowIfCancellationRequested();
                var cache2 = Path.Combine(profile, "cache2");
                var (f, b, errs) = RemovePathContents(cache2, ct);
                totalFiles += f;
                totalBytes += b;
                errors.AddRange(errs);
            }
        }
        return new CleanupResult(CleanupTarget.FirefoxCache, totalFiles, totalBytes, errors, false, null);
    }

    private static CleanupResult RunRecycleBinTarget()
    {
        var errors = new List<string>();
        try
        {
            int rc = Shell32.SHEmptyRecycleBinW(
                IntPtr.Zero,
                null,
                Shell32.SHERB.NoConfirmation | Shell32.SHERB.NoProgressUI | Shell32.SHERB.NoSound);
            // S_OK = 0, E_UNEXPECTED = 0x8000FFFF when bin already empty.
            if (rc != 0 && (uint)rc != 0x8000_FFFF)
            {
                errors.Add($"SHEmptyRecycleBin returned 0x{rc:X8}");
            }
            return new CleanupResult(CleanupTarget.RecycleBin,
                FilesRemoved: errors.Count == 0 ? 1 : 0,
                BytesFreed: 0,
                Errors: errors,
                Skipped: false,
                SkipReason: null);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            return new CleanupResult(CleanupTarget.RecycleBin, 0, 0, errors, false, null);
        }
    }

    private static CleanupResult RunDnsCacheTarget()
    {
        var errors = new List<string>();
        try
        {
            bool ok = DnsApi.DnsFlushResolverCache();
            if (!ok) errors.Add("DnsFlushResolverCache returned false");
            return new CleanupResult(CleanupTarget.DnsCache,
                FilesRemoved: ok ? 1 : 0,
                BytesFreed: 0,
                Errors: errors,
                Skipped: false,
                SkipReason: null);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            return new CleanupResult(CleanupTarget.DnsCache, 0, 0, errors, false, null);
        }
    }

    /// <summary>
    /// Test seam: drives the recursive delete against a caller-supplied path.
    /// Tests use it to verify reparse-point handling without configuring real
    /// well-known cleanup targets.
    /// </summary>
    internal static (int filesRemoved, long bytesFreed, List<string> errors) RemoveDirectoryContentsSafeForTest(string path) =>
        RemovePathContents(path, CancellationToken.None);

    private static (int filesRemoved, long bytesFreed, List<string> errors) RemovePathContents(string path, CancellationToken ct)
    {
        int files = 0;
        long bytes = 0;
        var errors = new List<string>();
        if (!Directory.Exists(path)) return (files, bytes, errors);

        DirectoryInfo dir;
        try
        {
            dir = new DirectoryInfo(path);
        }
        catch (Exception ex)
        {
            errors.Add($"open {path}: {ex.Message}");
            return (files, bytes, errors);
        }

        FileSystemInfo[] entries;
        try
        {
            entries = dir.GetFileSystemInfos();
        }
        catch (Exception ex)
        {
            errors.Add($"enumerate {path}: {ex.Message}");
            return (files, bytes, errors);
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (IsReparsePoint(entry)) continue; // never follow / never delete the link
                if (entry is DirectoryInfo subDir)
                {
                    long sizeBefore = MeasureTreeSize(subDir);
                    RemoveDirectoryTreeSafe(subDir);
                    bytes += sizeBefore;
                    files += 1; // counts top-level entry, mirroring the PowerShell behavior
                }
                else if (entry is FileInfo file)
                {
                    bytes += file.Length;
                    File.Delete(file.FullName);
                    files += 1;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{ex.Message}: {entry.FullName}");
            }
        }
        return (files, bytes, errors);
    }

    private static long MeasureTreeSize(DirectoryInfo root)
    {
        long sum = 0;
        try
        {
            foreach (var info in root.EnumerateFileSystemInfos("*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.None
                     }))
            {
                if (IsReparsePoint(info)) continue;
                if (info is FileInfo fi)
                {
                    try { sum += fi.Length; } catch { /* ignore */ }
                }
            }
        }
        catch
        {
            // best-effort sizing
        }
        return sum;
    }

    private static void RemoveDirectoryTreeSafe(DirectoryInfo root)
    {
        // Recursive delete that NEVER follows reparse points (junctions, symlinks).
        // Defends against a malicious junction inside a cleanup target redirecting
        // the recursive delete to e.g. C:\Windows\System32. A parent that holds a
        // skipped reparse-point child will fail with "directory not empty" — that
        // is the intended signal that something unexpected lives there.
        FileSystemInfo[] children;
        try { children = root.GetFileSystemInfos(); }
        catch { return; }

        foreach (var child in children)
        {
            if (IsReparsePoint(child)) continue;
            if (child is DirectoryInfo sub)
            {
                RemoveDirectoryTreeSafe(sub);
            }
            else
            {
                try { File.Delete(child.FullName); } catch { /* swallowed at top level */ }
            }
        }
        try { Directory.Delete(root.FullName, recursive: false); } catch { /* leave non-empty dirs intact */ }
    }

    private static bool IsReparsePoint(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private void WriteRunLog(IEnumerable<CleanupResult> results, IEnumerable<CleanupTarget> targets)
    {
        try
        {
            var logDir = Path.Combine(LocalAppData, "WinTune", "logs");
            Directory.CreateDirectory(logDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var logPath = Path.Combine(logDir, $"cleanup-{stamp}.log");

            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"WinTune cleanup run -- {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Targets requested: {string.Join(", ", targets)}");
            sb.AppendLine(new string('=', 78));

            foreach (var r in results)
            {
                string status = r.Skipped ? "SKIPPED" : (r.Errors.Count > 0 ? "PARTIAL" : "OK");
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"[{status}] {r.Target}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  files removed : {r.FilesRemoved}");
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  bytes freed   : {r.BytesFreed} ({FormatBytes(r.BytesFreed)})");
                if (r.Skipped)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  skip reason   : {r.SkipReason}");
                }
                if (r.Errors.Count > 0)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  errors ({r.Errors.Count}):");
                    foreach (var e in r.Errors)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"    - {e}");
                    }
                }
            }

            File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
            _lastLogPath = logPath;
        }
        catch
        {
            // Logging failure must not break the cleanup return value.
        }
    }
}
