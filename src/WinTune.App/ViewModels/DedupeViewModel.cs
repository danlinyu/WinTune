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
    private CancellationTokenSource? _cts;

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

    [ObservableProperty] private MinSizeOption selectedMinSize;
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

    public DedupeViewModel(IDedupService dedup)
    {
        _dedup = dedup;
        selectedMinSize = MinSizeOptions[1];
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
    /// Pick the keeper most likely to be the user's "real" file based on
    /// category and path heuristics, select the rest. Prefers UserContent
    /// over Other over AppManaged over BuildOutput / Backup, with a mild tie
    /// break toward shorter paths and non-cache locations.
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
        if (hidden > 0)
        {
            Status = $"{Groups.Count} user-actionable groups, {visibleWaste:N0} bytes — "
                   + $"{hidden} app-managed/build-output groups hidden ({totalWaste - visibleWaste:N0} bytes). "
                   + "Toggle 'Show app-managed' to see them.";
        }
        else
        {
            Status = $"{Groups.Count} groups, {visibleWaste:N0} bytes wasted.";
        }
    }

    private static int KeeperScore(DuplicateFile f)
    {
        int score = f.Category switch
        {
            FileCategory.UserContent => 200,
            FileCategory.Other => 100,
            FileCategory.AppManaged => 0,
            FileCategory.Backup => -100,
            FileCategory.BuildOutput => -200,
            _ => 0
        };
        // Mild tie-breaker: shorter paths win; slight penalty for living under
        // .cache trees that snuck through directory exclusion.
        score -= f.FullPath.Length / 10;
        if (f.FullPath.Contains(@"\.cache\", StringComparison.OrdinalIgnoreCase)) score -= 50;
        if (f.FullPath.Contains(@"\Recycle.Bin\", StringComparison.OrdinalIgnoreCase)) score -= 200;
        return score;
    }

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
