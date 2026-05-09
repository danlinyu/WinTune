using System.Collections.ObjectModel;
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
    private void RemoveScanPath()
    {
        if (string.IsNullOrEmpty(SelectedScanPath)) return;
        ScanPaths.Remove(SelectedScanPath);
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (ScanPaths.Count == 0) return;
        IsRunning = true;
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
                ScanPaths.ToList(),
                minSizeBytes: SelectedMinSize.Bytes,
                progress: progress,
                ct: _cts.Token);
            foreach (var g in groups) Groups.Add(g);
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
        foreach (var g in Groups)
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

        // Remove deleted files from in-memory groups.
        var deletedSet = new HashSet<string>(paths.Take(result.Deleted), StringComparer.OrdinalIgnoreCase);
        var refreshed = Groups
            .Select(g => g with { Files = g.Files.Where(f => !deletedSet.Contains(f.FullPath)).ToList() })
            .Where(g => g.Files.Count >= 2)
            .ToList();
        Groups.Clear();
        foreach (var g in refreshed) Groups.Add(g);
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

    [RelayCommand]
    private void ClearSelection() => Selected.Clear();

    private bool CanScan() => !IsRunning;

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
