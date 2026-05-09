using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualBasic.FileIO;
using WinTune.Core.Models;

namespace WinTune.Core.Services;

public sealed class DedupService : IDedupService
{
    private static readonly IReadOnlySet<string> DefaultExcludeExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".lnk", ".url", ".tmp", ".crdownload", ".partial"
        };

    public Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(
        IReadOnlyCollection<string> roots,
        long minSizeBytes = 1L * 1024 * 1024,
        bool includeHidden = false,
        IReadOnlySet<string>? excludeExtensions = null,
        IProgress<DedupeProgress>? progress = null,
        CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<DuplicateGroup>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var excludes = excludeExtensions ?? DefaultExcludeExtensions;

            // Pass 1: enumerate and group by exact byte size.
            var bySize = new Dictionary<long, List<FileInfo>>();
            int scanned = 0;
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

                IEnumerable<FileInfo> files;
                try
                {
                    files = new DirectoryInfo(root).EnumerateFiles("*", new System.IO.EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.None
                    });
                }
                catch
                {
                    continue;
                }

                foreach (var fi in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsSkippablePath(fi, includeHidden)) continue;
                    if (fi.Length < minSizeBytes) continue;
                    if (excludes.Contains(fi.Extension)) continue;

                    if (!bySize.TryGetValue(fi.Length, out var list))
                    {
                        list = new List<FileInfo>();
                        bySize[fi.Length] = list;
                    }
                    list.Add(fi);
                    scanned++;
                    if (scanned % 500 == 0)
                    {
                        progress?.Report(new DedupeProgress("Enumerate", scanned, 0, 0, 0));
                    }
                }
            }

            // Pass 2: hash candidates (size buckets with >= 2 files).
            var byHash = new Dictionary<string, List<HashedFile>>(StringComparer.Ordinal);
            int candidateCount = bySize.Values.Where(v => v.Count >= 2).Sum(v => v.Count);
            int hashed = 0;

            progress?.Report(new DedupeProgress("HashStart", scanned, 0, 0, 0));

            foreach (var (size, list) in bySize)
            {
                if (list.Count < 2) continue;
                foreach (var fi in list)
                {
                    ct.ThrowIfCancellationRequested();
                    string hex;
                    try
                    {
                        using var stream = File.OpenRead(fi.FullName);
                        // SHA-256 is used as a content fingerprint for dedup, not for security.
                        // Originally SHA1 in the PowerShell version; switched here to satisfy
                        // CA5350 and to make collision risk arbitrarily small.
                        var hashBytes = SHA256.HashData(stream);
                        hex = Convert.ToHexString(hashBytes);
                    }
                    catch
                    {
                        // Hash failure (locked file, race) — skip.
                        continue;
                    }

                    string key = $"{size}::{hex}";
                    if (!byHash.TryGetValue(key, out var bucket))
                    {
                        bucket = new List<HashedFile>();
                        byHash[key] = bucket;
                    }
                    bucket.Add(new HashedFile(fi.FullName, fi.Length, fi.LastWriteTime, hex));
                    hashed++;
                    if (hashed % 25 == 0)
                    {
                        progress?.Report(new DedupeProgress("Hash", scanned, hashed, 0, 0));
                    }
                }
            }

            // Pass 3: build duplicate groups, sort by waste descending.
            int groupId = 0;
            var groups = new List<DuplicateGroup>();
            long totalWasted = 0;
            foreach (var (_, bucket) in byHash)
            {
                if (bucket.Count < 2) continue;
                groupId++;
                long groupSize = bucket[0].Size;
                long wasted = groupSize * (bucket.Count - 1);
                totalWasted += wasted;
                var sortedFiles = bucket
                    .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new DuplicateFile(f.Path, f.Size, f.LastWriteTime))
                    .ToList();
                groups.Add(new DuplicateGroup(
                    GroupId: groupId,
                    Hash: bucket[0].Hash,
                    SizeBytes: groupSize,
                    WastedBytes: wasted,
                    Files: sortedFiles));
            }

            progress?.Report(new DedupeProgress("Done", scanned, hashed, groups.Count, totalWasted));

            return groups
                .OrderByDescending(g => g.WastedBytes)
                .ToList();
        }, ct);

    public Task<RemovalResult> RemoveDuplicateFilesAsync(
        IReadOnlyCollection<string> paths,
        bool permanent = false,
        CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            int deleted = 0;
            long bytes = 0;
            var errors = new List<string>();
            foreach (var p in paths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!File.Exists(p))
                    {
                        errors.Add($"Not found: {p}");
                        continue;
                    }
                    long size = new FileInfo(p).Length;
                    if (permanent)
                    {
                        File.Delete(p);
                    }
                    else
                    {
                        FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }
                    deleted++;
                    bytes += size;
                }
                catch (Exception ex)
                {
                    errors.Add($"{p}: {ex.Message}");
                }
            }
            return new RemovalResult(deleted, bytes, errors, permanent);
        }, ct);

    public IReadOnlyList<string> GetDefaultScanRoots()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };
        return candidates
            .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
            .ToList();
    }

    private static bool IsSkippablePath(FileInfo file, bool includeHidden)
    {
        var attrs = file.Attributes;
        if ((attrs & FileAttributes.ReparsePoint) != 0) return true;
        if ((attrs & FileAttributes.Offline) != 0) return true;
        if ((attrs & FileAttributes.System) != 0) return true;
        if (!includeHidden && (attrs & FileAttributes.Hidden) != 0) return true;
        return false;
    }

    private sealed record HashedFile(string Path, long Size, DateTime LastWriteTime, string Hash);
}
