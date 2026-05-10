using System.IO;
using System.Security.Cryptography;
using WinTune.Core.Models;
using WinTune.Core.NativeInterop;

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

                // AttributesToSkip applies to BOTH files and directories: setting
                // ReparsePoint here means recursion does not follow junctions/symlinks.
                // Hidden + System excludes %SystemRoot%\System Volume Information,
                // $Recycle.Bin, and per-user registry hives at recursion time.
                var skipAttrs = FileAttributes.System | FileAttributes.ReparsePoint;
                if (!includeHidden) skipAttrs |= FileAttributes.Hidden;

                IEnumerable<FileInfo> files;
                try
                {
                    files = new DirectoryInfo(root).EnumerateFiles("*", new System.IO.EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = skipAttrs
                    });
                }
                catch
                {
                    continue;
                }

                // Use an explicit enumerator so a sharing-violation IOException on one
                // file (which IgnoreInaccessible does NOT skip — it only handles
                // UnauthorizedAccessException) doesn't abort the whole scan.
                using var enumerator = files.GetEnumerator();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    FileInfo fi;
                    try
                    {
                        if (!enumerator.MoveNext()) break;
                        fi = enumerator.Current;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        continue;
                    }

                    long length;
                    string extension;
                    try
                    {
                        if (IsSkippablePath(fi, includeHidden)) continue;
                        length = fi.Length;
                        extension = fi.Extension;
                    }
                    catch
                    {
                        continue;
                    }

                    if (length < minSizeBytes) continue;
                    if (excludes.Contains(extension)) continue;

                    if (!bySize.TryGetValue(length, out var list))
                    {
                        list = new List<FileInfo>();
                        bySize[length] = list;
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
            if (paths.Count == 0)
            {
                return new RemovalResult(0, 0, Array.Empty<string>(), permanent);
            }

            // Snapshot existence + size before deleting. SHFileOperationW does
            // the recycle-bin batch atomically and we cannot ask it which files
            // succeeded — we determine that by checking File.Exists post-call.
            var snapshot = new List<(string Path, long Size)>(paths.Count);
            foreach (var p in paths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(p))
                    {
                        snapshot.Add((p, new FileInfo(p).Length));
                    }
                }
                catch
                {
                    // Snapshot best-effort; locked or otherwise inaccessible files
                    // are still passed to the delete call below.
                }
            }

            if (permanent)
            {
                int deletedP = 0;
                long bytesP = 0;
                var errorsP = new List<string>();
                foreach (var (p, size) in snapshot)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        File.Delete(p);
                        deletedP++;
                        bytesP += size;
                    }
                    catch (Exception ex)
                    {
                        errorsP.Add($"{p}: {ex.Message}");
                    }
                }
                return new RemovalResult(deletedP, bytesP, errorsP, Permanent: true);
            }

            // Build double-null-terminated wide-string path list for SHFileOperationW.
            var sb = new System.Text.StringBuilder();
            foreach (var (p, _) in snapshot)
            {
                sb.Append(p).Append('\0');
            }
            sb.Append('\0');

            var op = new Shell32.SHFILEOPSTRUCTW
            {
                hwnd = IntPtr.Zero,
                wFunc = Shell32.FO.Delete,
                pFrom = sb.ToString(),
                pTo = null,
                fFlags = Shell32.FOF.AllowUndo
                       | Shell32.FOF.NoConfirmation
                       | Shell32.FOF.NoErrorUI
                       | Shell32.FOF.Silent
                       | Shell32.FOF.FilesOnly,
                fAnyOperationsAborted = false,
                hNameMappings = IntPtr.Zero,
                lpszProgressTitle = null
            };
            // Return value is informational; we determine per-file success by
            // post-call File.Exists. A non-zero result simply means the call
            // didn't complete cleanly — captured implicitly in the skipped list.
            _ = Shell32.SHFileOperationW(ref op);

            int recycled = 0;
            long bytesFreed = 0;
            var skipped = new List<string>();
            foreach (var (p, size) in snapshot)
            {
                if (!File.Exists(p))
                {
                    recycled++;
                    bytesFreed += size;
                }
                else
                {
                    skipped.Add($"{p}: in use or access denied — skipped");
                }
            }
            return new RemovalResult(recycled, bytesFreed, skipped, Permanent: false);
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
