using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WinTune.Core.Models;
using WinTune.Core.Services;

namespace WinTune.App.ViewModels;

public sealed partial class DedupeViewModel : ObservableObject, IDisposable
{
    private readonly IDedupService _dedup;
    private readonly IDedupHashCache _cache;
    private CancellationTokenSource? _cts;
    private bool _cacheLoaded;

    // Full unfiltered scan result. The visible Groups collection is filtered
    // from this based on ShowAppManaged. Keeping both lets the toggle flip
    // instantly without a re-scan.
    private readonly List<DuplicateGroup> _allGroups = new();

    public ObservableCollection<string> ScanPaths { get; } = new();
    public ObservableCollection<DuplicateGroup> Groups { get; } = new();
    public ObservableCollection<DuplicateFile> Selected { get; } = new();

    public IReadOnlyList<MinSizeOption> MinSizeOptions { get; } = new[]
    {
        new MinSizeOption("1 KB", 1024L),
        new MinSizeOption("1 MB", 1024L * 1024),
        new MinSizeOption("10 MB", 10L * 1024 * 1024),
        new MinSizeOption("100 MB", 100L * 1024 * 1024),
    };

    public IReadOnlyList<MaxSizeOption> MaxSizeOptions { get; } = new[]
    {
        new MaxSizeOption("Unlimited", 0L),
        new MaxSizeOption("100 MB",  100L * 1024 * 1024),
        new MaxSizeOption("500 MB",  500L * 1024 * 1024),
        new MaxSizeOption("1 GB",   1024L * 1024 * 1024),
        new MaxSizeOption("5 GB", 5L * 1024 * 1024 * 1024),
    };

    [ObservableProperty] private MinSizeOption selectedMinSize;
    [ObservableProperty] private MaxSizeOption selectedMaxSize;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string? selectedScanPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    private bool isRunning;

    /// <summary>
    /// When false (default), groups dominated by app-managed or build-output
    /// files are hidden from the UI. ~80% of dupes in a real user folder fall
    /// into those categories and are unsafe to delete; hiding them by default
    /// lets the user focus on actually-recoverable space.
    /// </summary>
    [ObservableProperty]
    private bool showAppManaged;

    partial void OnShowAppManagedChanged(bool value) => ApplyGroupFilter();

    public DedupeViewModel(IDedupService dedup, IDedupHashCache cache)
    {
        _dedup = dedup;
        _cache = cache;
        selectedMinSize = MinSizeOptions[1];
        selectedMaxSize = MaxSizeOptions[0];
        ResetScanPaths();
    }

    [RelayCommand]
    private void ResetScanPaths()
    {
        ScanPaths.Clear();
        foreach (var p in _dedup.GetDefaultScanRoots()) ScanPaths.Add(p);
    }

    [RelayCommand]
    private void AddScanPath()
    {
        var dlg = new OpenFolderDialog { Title = "Add a folder to the dedupe scan list" };
        if (dlg.ShowDialog() == true && Directory.Exists(dlg.FolderName) && !ScanPaths.Contains(dlg.FolderName))
        {
            ScanPaths.Add(dlg.FolderName);
        }
    }

    [RelayCommand]
    private void RemoveScanPath(IList? selectedItems)
    {
        var toRemove = SnapshotStrings(selectedItems);
        if (toRemove.Count == 0)
        {
            if (string.IsNullOrEmpty(SelectedScanPath)) return;
            ScanPaths.Remove(SelectedScanPath);
            return;
        }
        foreach (var p in toRemove) ScanPaths.Remove(p);
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync(IList? selectedItems)
    {
        var selected = SnapshotStrings(selectedItems);
        var roots = selected.Count > 0 ? selected : ScanPaths.ToList();
        if (roots.Count == 0) return;
        IsRunning = true;
        _allGroups.Clear();
        Groups.Clear();
        Selected.Clear();
        Status = "Scanning…";
        _cts = new CancellationTokenSource();

        // Lazy-load the persistent hash cache on the first scan; subsequent
        // scans reuse the in-memory state. Loading is best-effort: a corrupt
        // file or missing directory simply means a cold first-scan.
        if (!_cacheLoaded)
        {
            _cache.Load();
            _cacheLoaded = true;
        }
        _cache.ResetStats();

        var progress = new Progress<DedupeProgress>(p =>
        {
            Status = p.Phase switch
            {
                "Enumerate" => $"Enumerating: {p.FilesScanned} files",
                "HashStart" => $"Hashing {p.FilesScanned} candidates…",
                "Hash" => $"Hashing: {p.FilesHashed} / {p.FilesScanned}",
                "Done" => $"Done — {p.GroupsFound} groups, {p.WastedBytes:N0} bytes wasted",
                _ => Status
            };
        });
        try
        {
            var groups = await _dedup.FindDuplicatesAsync(
                roots,
                minSizeBytes: SelectedMinSize.Bytes,
                maxSizeBytes: SelectedMaxSize.Bytes,
                hashCache: _cache,
                progress: progress,
                ct: _cts.Token);
            _allGroups.AddRange(groups);
            ApplyGroupFilter();
            UpdateStatusAfterScan();
        }
        catch (OperationCanceledException)
        {
            Status = "Scan cancelled";
        }
        catch (Exception ex)
        {
            Status = $"Scan failed: {ex.Message}";
        }
        finally
        {
            // Persist whatever new entries the scan recorded, even on cancel —
            // partial caching is still a win for the next scan.
            _cache.Save();
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void CancelScan() => _cts?.Cancel();

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (Selected.Count == 0) return;

        // Safety stop: refuse if we'd delete every file in any group.
        foreach (var g in _allGroups)
        {
            int selectedFromGroup = g.Files.Count(f => Selected.Any(s => s.FullPath == f.FullPath));
            if (selectedFromGroup >= g.Files.Count)
            {
                Status = $"Refusing: group with hash {g.Hash[..8]} would have all files deleted. Un-select at least one.";
                return;
            }
        }

        var paths = Selected.Select(f => f.FullPath).ToList();
        Status = $"Deleting {paths.Count} files…";
        var result = await _dedup.RemoveDuplicateFilesAsync(paths, permanent: false);
        Status = $"Deleted {result.Deleted} files ({result.BytesFreed:N0} bytes), {result.Errors.Count} errors";

        // Remove deleted files from the underlying full result so a toggle
        // of ShowAppManaged later doesn't resurrect them.
        var deletedSet = new HashSet<string>(paths.Take(result.Deleted), StringComparer.OrdinalIgnoreCase);
        var refreshed = _allGroups
            .Select(g => g with { Files = g.Files.Where(f => !deletedSet.Contains(f.FullPath)).ToList() })
            .Where(g => g.Files.Count >= 2)
            .ToList();
        _allGroups.Clear();
        _allGroups.AddRange(refreshed);
        ApplyGroupFilter();
        Selected.Clear();
    }

    /// <summary>
    /// CONSERVATIVE auto-select: only marks files for deletion when the keeper
    /// has a clear category-based advantage (gap of one full category step or
    /// more). Groups where every file shares the same category — typical of
    /// "is this OneDrive copy or my local copy the real one?" — are skipped
    /// entirely so the user can review them manually. Recommended starting
    /// point.
    /// </summary>
    [RelayCommand]
    private void SelectOnlyObviousDupes()
    {
        Selected.Clear();
        int skipped = 0;
        foreach (var g in Groups)
        {
            var ranked = g.Files
                .OrderByDescending(f => CategoryScore(f.Category))
                .ThenByDescending(f => TieBreakerScore(f))
                .ToList();
            int gap = CategoryScore(ranked[0].Category) - CategoryScore(ranked[1].Category);
            if (gap < 100)
            {
                skipped++;
                continue;
            }
            foreach (var f in ranked.Skip(1)) Selected.Add(f);
        }
        Status = $"Selected {Selected.Count} files in {Groups.Count - skipped} groups with an obvious keeper. "
               + $"Skipped {skipped} ambiguous groups — review those manually.";
    }

    [RelayCommand]
    private void SelectAllButOldest()
    {
        Selected.Clear();
        foreach (var g in Groups)
        {
            var oldest = g.Files.OrderBy(f => f.LastWriteTime).First();
            foreach (var f in g.Files) if (!ReferenceEquals(f, oldest)) Selected.Add(f);
        }
    }

    [RelayCommand]
    private void SelectAllButNewest()
    {
        Selected.Clear();
        foreach (var g in Groups)
        {
            var newest = g.Files.OrderByDescending(f => f.LastWriteTime).First();
            foreach (var f in g.Files) if (!ReferenceEquals(f, newest)) Selected.Add(f);
        }
    }

    [RelayCommand]
    private void SelectAllButShortestPath()
    {
        Selected.Clear();
        foreach (var g in Groups)
        {
            var keep = g.Files.OrderBy(f => f.FullPath.Length).First();
            foreach (var f in g.Files) if (!ReferenceEquals(f, keep)) Selected.Add(f);
        }
    }

    /// <summary>
    /// AGGRESSIVE auto-select: pick a "best-guess" keeper per group based on
    /// category + path heuristics and select everything else, even when no
    /// file has a clear category advantage. Falls back to path-length tie-
    /// breaks for same-category groups, which can be arbitrary — review the
    /// selection before deleting.
    /// </summary>
    [RelayCommand]
    private void SelectAllButSmartKeeper()
    {
        Selected.Clear();
        foreach (var g in Groups)
        {
            var keep = g.Files.OrderByDescending(KeeperScore).First();
            foreach (var f in g.Files) if (!ReferenceEquals(f, keep)) Selected.Add(f);
        }
    }

    [RelayCommand]
    private void ClearSelection() => Selected.Clear();

    [RelayCommand]
    private void RevealInExplorer(DuplicateFile? file)
    {
        if (file is null || string.IsNullOrEmpty(file.FullPath)) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{file.FullPath}\"",
                UseShellExecute = false,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Status = $"Could not open Explorer: {ex.Message}";
        }
    }

    private void ApplyGroupFilter()
    {
        Groups.Clear();
        Selected.Clear();
        foreach (var g in _allGroups)
        {
            if (!ShowAppManaged && IsHiddenByDefault(g.DominantCategory)) continue;
            Groups.Add(g);
        }
        UpdateStatusAfterScan();
    }

    private static bool IsHiddenByDefault(FileCategory category) =>
        category == FileCategory.AppManaged || category == FileCategory.BuildOutput;

    private void UpdateStatusAfterScan()
    {
        if (_allGroups.Count == 0) return;
        long visibleWaste = Groups.Sum(g => g.WastedBytes);
        long totalWaste = _allGroups.Sum(g => g.WastedBytes);
        int hidden = _allGroups.Count - Groups.Count;

        var cacheNote = "";
        int hits = _cache.HitCount;
        int misses = _cache.MissCount;
        if (hits + misses > 0)
        {
            int pct = (int)Math.Round(100.0 * hits / Math.Max(1, hits + misses));
            cacheNote = $" Cache: {hits} hit / {misses} miss ({pct}%).";
        }

        if (hidden > 0)
        {
            Status = $"{Groups.Count} user-actionable groups, {visibleWaste:N0} bytes — "
                   + $"{hidden} app-managed/build-output groups hidden ({totalWaste - visibleWaste:N0} bytes). "
                   + "Toggle 'Show app-managed' to see them." + cacheNote;
        }
        else
        {
            Status = $"{Groups.Count} groups, {visibleWaste:N0} bytes wasted." + cacheNote;
        }
    }

    private static int CategoryScore(FileCategory category) => category switch
    {
        FileCategory.UserContent => 200,
        FileCategory.Other => 100,
        FileCategory.AppManaged => 0,
        FileCategory.Backup => -100,
        FileCategory.BuildOutput => -200,
        _ => 0
    };

    private static int TieBreakerScore(DuplicateFile f)
    {
        // Mild tie-breakers; intentionally bounded to <100 so they cannot
        // flip a "clear category gap" decision in conservative mode.
        int s = -Math.Min(99, f.FullPath.Length / 10);
        if (f.FullPath.Contains(@"\.cache\", StringComparison.OrdinalIgnoreCase)) s -= 50;
        if (f.FullPath.Contains(@"\Recycle.Bin\", StringComparison.OrdinalIgnoreCase)) s -= 200;
        return s;
    }

    private static int KeeperScore(DuplicateFile f) =>
        CategoryScore(f.Category) + TieBreakerScore(f);

    private bool CanScan() => !IsRunning;

    private static List<string> SnapshotStrings(IList? source)
    {
        if (source is null) return new List<string>();
        var result = new List<string>(source.Count);
        foreach (var item in source)
        {
            if (item is string s) result.Add(s);
        }
        return result;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

public sealed record MinSizeOption(string Label, long Bytes)
{
    public override string ToString() => Label;
}

public sealed record MaxSizeOption(string Label, long Bytes)
{
    public override string ToString() => Label;
}
