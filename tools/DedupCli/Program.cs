using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinTune.Core.Models;
using WinTune.Core.Services;


// Investigation harness — runs DedupService against given roots, captures
// timing + process I/O counters + per-group categorization, writes JSON report.

string[] roots;
long minSize;

if (args.Length == 0)
{
    roots = new[] { @"C:\Users\danli" };
    minSize = 1L * 1024 * 1024;
}
else
{
    var ms = args.FirstOrDefault(a => a.StartsWith("--min-mb=", StringComparison.Ordinal));
    minSize = ms is null ? 1L * 1024 * 1024 : long.Parse(ms.AsSpan(9)) * 1024 * 1024;
    roots = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
    if (roots.Length == 0) roots = new[] { @"C:\Users\danli" };
}

Console.WriteLine($"WinTune dedupe harness");
Console.WriteLine($"Roots: {string.Join(" ; ", roots)}");
Console.WriteLine($"Min size: {minSize / 1024.0 / 1024:N1} MB");
Console.WriteLine($"Press Ctrl+C to cancel.");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\n[cancel requested]"); };

// Harness-local cache file (not shared with the UI's cache). Lets the same
// invocation cold-load then warm-load on a second run without polluting the
// real WinTune cache at %LOCALAPPDATA%\WinTune\.
var harnessCachePath = Path.Combine(Path.GetTempPath(), "wintune-dedup-harness-cache.json");
var cache = new DedupHashCache(harnessCachePath);
cache.Load();
Console.WriteLine($"Cache: {cache.Count} entries loaded from {harnessCachePath}");

var svc = new DedupService();
var sw = Stopwatch.StartNew();
long lastPrint = 0;
var progress = new Progress<DedupeProgress>(p =>
{
    var now = sw.ElapsedMilliseconds;
    if (now - lastPrint < 750 && p.Phase != "Done" && p.Phase != "HashStart") return;
    lastPrint = now;
    Console.WriteLine(
        $"[{sw.Elapsed:mm\\:ss}] {p.Phase,-10} scanned={p.FilesScanned,7:N0} hashed={p.FilesHashed,6:N0} groups={p.GroupsFound,5:N0} wasted={p.WastedBytes / 1024.0 / 1024 / 1024,6:N2} GB");
});

IReadOnlyList<DuplicateGroup> groups;
try
{
    groups = await svc.FindDuplicatesAsync(
        roots, minSizeBytes: minSize, hashCache: cache, progress: progress, ct: cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
    cache.Save();
    return 1;
}
sw.Stop();
cache.Save();

GetProcessIoCounters(Process.GetCurrentProcess().Handle, out var io);
var totalWasted = groups.Sum(g => g.WastedBytes);

Console.WriteLine();
Console.WriteLine($"Elapsed: {sw.Elapsed}");
Console.WriteLine($"Groups: {groups.Count}");
Console.WriteLine($"Wasted: {totalWasted / 1024.0 / 1024 / 1024:N2} GB ({totalWasted:N0} bytes)");
Console.WriteLine($"Process disk read: {io.ReadTransferCount / 1024.0 / 1024 / 1024:N2} GB ({io.ReadOperationCount:N0} ops)");
Console.WriteLine($"Process disk write: {io.WriteTransferCount / 1024.0 / 1024 / 1024:N2} GB ({io.WriteOperationCount:N0} ops)");
int hits = cache.HitCount, misses = cache.MissCount, lookups = hits + misses;
double hitPct = lookups == 0 ? 0 : 100.0 * hits / lookups;
Console.WriteLine($"Cache: {hits:N0} hit / {misses:N0} miss ({hitPct:N1}%) — {cache.Count:N0} entries persisted");

// Categorize each group by where its files live.
var byCategory = groups
    .GroupBy(g => g.DominantCategory.ToString())
    .OrderByDescending(grp => grp.Sum(g => g.WastedBytes))
    .ToList();

Console.WriteLine();
Console.WriteLine("Wasted bytes by category:");
foreach (var cat in byCategory)
{
    var w = cat.Sum(g => g.WastedBytes);
    Console.WriteLine($"  {cat.Key,-18} {cat.Count(),4} groups   {w / 1024.0 / 1024 / 1024,7:N2} GB   ({100.0 * w / Math.Max(1, totalWasted),5:N1}%)");
}

Console.WriteLine();
Console.WriteLine("Top 20 groups by wasted bytes:");
foreach (var g in groups.Take(20))
{
    Console.WriteLine($"  {g.WastedBytes / 1024.0 / 1024,8:N1} MB  ×{g.Files.Count}  [{g.DominantCategory.ToString()}]  {g.Files[0].FileName}");
    foreach (var f in g.Files.Take(4))
    {
        var rel = ShortenPath(f.FullPath);
        Console.WriteLine($"      {rel}");
    }
    if (g.Files.Count > 4) Console.WriteLine($"      ... +{g.Files.Count - 4} more");
}

var reportPath = Path.Combine(Path.GetTempPath(), $"dedupe-report-{DateTime.Now:yyyyMMdd-HHmmss}.json");
var report = new
{
    Elapsed = sw.Elapsed.ToString(),
    ElapsedSeconds = sw.Elapsed.TotalSeconds,
    Roots = roots,
    MinSizeBytes = minSize,
    Groups = groups.Count,
    WastedBytes = totalWasted,
    DiskReadBytes = io.ReadTransferCount,
    DiskReadOps = io.ReadOperationCount,
    Categories = byCategory.ToDictionary(c => c.Key, c => new { Count = c.Count(), WastedBytes = c.Sum(g => g.WastedBytes) }),
    AllGroups = groups.Select(g => new
    {
        g.GroupId,
        g.SizeBytes,
        g.WastedBytes,
        Category = g.DominantCategory.ToString(),
        Files = g.Files.Select(f => f.FullPath).ToArray()
    }).ToArray()
};
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine();
Console.WriteLine($"Full report: {reportPath}");
return 0;

static string ShortenPath(string p)
{
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return p.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + p[home.Length..] : p;
}

[StructLayout(LayoutKind.Sequential)]
struct IoCounters
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

partial class Program
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessIoCounters(IntPtr ProcessHandle, out IoCounters lpIoCounters);
}
