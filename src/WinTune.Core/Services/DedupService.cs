using System.Buffers;
using System.IO;
using System.IO.Enumeration;
using System.Security.Cryptography;
using WinTune.Core.Models;
using WinTune.Core.NativeInterop;

namespace WinTune.Core.Services;

public sealed class DedupService : IDedupService
{
    // First-pass partial-hash window. Files in the same size bucket whose first
    // 64 KB don't match cannot be duplicates, so the full hash is skipped. Cuts
    // pass-2 work substantially on real user data where same-size files differ
    // early (installers, video formats with similar frame sizes, etc).
    private const int HeadHashSize = 64 * 1024;

    // Streaming read buffer for hashing. 1 MB amortises syscall overhead while
    // keeping memory pressure modest. Reused via ArrayPool across files.
    private const int HashBufferSize = 1 << 20;

    private static readonly IReadOnlySet<string> DefaultExcludeExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".lnk", ".url", ".tmp", ".crdownload", ".partial"
        };

    // Directory names where duplicates are expected and necessary — managed by
    // tools / OS / package managers / build systems. Never user-recoverable.
    // Skipping recursion into these prevents users from being asked to delete
    // files that would break their projects, browsers, runtimes, or Windows.
    //
    // Note: this list filters *recursion into* a child directory by name, not
    // the root itself. If a user explicitly scans a path inside AppData (or any
    // excluded dir) by adding it as a root, the scan still proceeds.
    private static readonly IReadOnlySet<string> DefaultExcludeDirectoryNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Source-control internals
            ".git", ".svn", ".hg",
            // Package-manager content stores (canonical roots)
            "node_modules", "vendor", "Pods", "bower_components",
            ".nuget", ".cargo", ".rustup", ".pnpm-store", ".yarn", ".npm",
            ".gradle", ".m2", ".gem", ".dotnet", ".bun", ".volta",
            ".nvm", ".pyenv", ".deno", ".cocoapods",
            // Conda/Anaconda installations (every tree under these is package-managed)
            "miniconda3", "anaconda3", "miniforge3", "mambaforge", ".conda",
            // Python venv / cache
            "__pycache__", ".venv", "venv", ".tox", ".pytest_cache",
            // Browser + system caches
            "INetCache", "WebCache", "Code Cache", "GPUCache", "Cache_Data",
            // Windows-managed installer / SxS / store content
            "WinSxS", "WindowsApps", "Installer", "Package Cache",
            "DriverStore", "$Recycle.Bin", "$RECYCLE.BIN",
            // Dev IDE internals
            ".vs", ".idea", ".vscode", ".vscode-insiders", ".vscode-server",
            // Per-user app-data umbrella (opt-in: only blocks recursion INTO a
            // subdir named AppData; explicit AppData root still scans).
            "AppData", "Application Data", "Local Settings",
            // Build output directories common to .NET, Java, Rust, JS, Go
            "bin", "obj", "target", "dist", "build", "out",
            // Framework / bundler caches inside repos
            ".next", ".nuxt", ".parcel-cache", ".turbo", ".angular", ".cache"
        };

    // Path-substring exclusions for cases where the directory name alone is too
    // generic to blacklist. Matched case-insensitively against the path of the
    // directory we're about to recurse into. These cover plugin/extension
    // content stores in Claude Code and its forks (and VS Code derivatives) —
    // the same shape as node_modules: tools install duplicate content into
    // multiple paths by design (cache vs marketplace, install vs upgrade), and
    // a user-driven dedupe sweep can break the active install.
    private static readonly string[] DefaultExcludePathSubstrings =
    {
        @"\.claude\plugins",
        @"\.claude-code\plugins",
        @"\.antigravity\extensions",
        @"\.trae\extensions",
        @"\.cursor\extensions",
        @"\.windsurf\extensions"
    };

    public Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(
        IReadOnlyCollection<string> roots,
        long minSizeBytes = 1L * 1024 * 1024,
        long maxSizeBytes = 0L,
        bool includeHidden = false,
        IReadOnlySet<string>? excludeExtensions = null,
        IReadOnlySet<string>? excludeDirectoryNames = null,
        IReadOnlyList<string>? excludePathSubstrings = null,
        IDedupHashCache? hashCache = null,
        IProgress<DedupeProgress>? progress = null,
        CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<DuplicateGroup>>(() =>
            // Run the entire scan with very-low I/O + paging priority on this
            // thread, so large hash reads do not evict the user's working set
            // or starve their foreground app of disk.
            Kernel32.RunWithBackgroundIoPriority(() =>
                ScanCore(roots, minSizeBytes, maxSizeBytes, includeHidden, excludeExtensions, excludeDirectoryNames, excludePathSubstrings, hashCache, progress, ct)),
            ct);

    private static List<DuplicateGroup> ScanCore(
        IReadOnlyCollection<string> roots,
        long minSizeBytes,
        long maxSizeBytes,
        bool includeHidden,
        IReadOnlySet<string>? excludeExtensions,
        IReadOnlySet<string>? excludeDirectoryNames,
        IReadOnlyList<string>? excludePathSubstrings,
        IDedupHashCache? hashCache,
        IProgress<DedupeProgress>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var excludes = excludeExtensions ?? DefaultExcludeExtensions;
        var excludeDirs = excludeDirectoryNames ?? DefaultExcludeDirectoryNames;
        var excludeSubstrings = excludePathSubstrings ?? DefaultExcludePathSubstrings;

        // Pass 1: enumerate and group by exact byte size, capturing category up-front.
        var bySize = new Dictionary<long, List<EnumeratedFile>>();
        int scanned = 0;
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

            var skipAttrs = FileAttributes.System | FileAttributes.ReparsePoint;
            if (!includeHidden) skipAttrs |= FileAttributes.Hidden;

            FileSystemEnumerable<FileInfo>? files;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = skipAttrs
                };
                files = new FileSystemEnumerable<FileInfo>(
                    root,
                    (ref FileSystemEntry entry) => (FileInfo)entry.ToFileSystemInfo(),
                    options)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
                    ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                    {
                        if (excludeDirs.Contains(entry.FileName.ToString())) return false;
                        if (excludeSubstrings.Count == 0) return true;

                        // Check the would-be subdirectory path for blacklisted
                        // substrings (parent + name). Allows excluding cases
                        // like ".claude\plugins" where "plugins" alone is too
                        // generic to blacklist as a directory name.
                        var fullPath = Path.Join(entry.Directory, entry.FileName);
                        foreach (var needle in excludeSubstrings)
                        {
                            if (fullPath.Contains(needle, StringComparison.OrdinalIgnoreCase)) return false;
                        }
                        return true;
                    }
                };
            }
            catch
            {
                continue;
            }

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
                DateTime lastWrite;
                try
                {
                    if (IsSkippablePath(fi, includeHidden)) continue;
                    length = fi.Length;
                    extension = fi.Extension;
                    lastWrite = fi.LastWriteTime;
                }
                catch
                {
                    continue;
                }

                if (length < minSizeBytes) continue;
                if (maxSizeBytes > 0 && length > maxSizeBytes) continue;
                if (excludes.Contains(extension)) continue;

                var category = Categorize(fi.FullName);
                if (!bySize.TryGetValue(length, out var list))
                {
                    list = new List<EnumeratedFile>();
                    bySize[length] = list;
                }
                list.Add(new EnumeratedFile(fi.FullName, length, lastWrite, category));
                scanned++;
                if (scanned % 500 == 0)
                {
                    progress?.Report(new DedupeProgress("Enumerate", scanned, 0, 0, 0));
                }
            }
        }

        // Pass 2a: head-hash the first HeadHashSize bytes of every file in any
        // size-bucket of 2+. Files whose head doesn't match cannot be duplicates.
        var headBuckets = new Dictionary<string, HashBucket>(StringComparer.Ordinal);
        int hashed = 0;
        progress?.Report(new DedupeProgress("HashStart", scanned, 0, 0, 0));

        var headBuf = ArrayPool<byte>.Shared.Rent(HashBufferSize);
        try
        {
            using var headHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var (size, list) in bySize)
            {
                if (list.Count < 2) continue;
                long headBytes = Math.Min(size, HeadHashSize);
                foreach (var f in list)
                {
                    ct.ThrowIfCancellationRequested();
                    string headHex;
                    if (hashCache is null
                        || !hashCache.TryGetHead(f.FullPath, f.SizeBytes, f.LastWriteTime, out headHex))
                    {
                        if (!TryHash(f.FullPath, headBytes, headBuf, headHasher, out headHex))
                            continue;
                        hashCache?.Update(f.FullPath, f.SizeBytes, f.LastWriteTime, headHash: headHex, fullHash: null);
                    }

                    string key = $"{size}::{headHex}";
                    if (!headBuckets.TryGetValue(key, out var bucket))
                    {
                        bucket = new HashBucket(size, headHex);
                        headBuckets[key] = bucket;
                    }
                    bucket.Files.Add(f);
                    hashed++;
                    if (hashed % 50 == 0)
                    {
                        progress?.Report(new DedupeProgress("Hash", scanned, hashed, 0, 0));
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headBuf);
        }

        // Pass 2b: full-hash only buckets that survived head-hash matching AND
        // are larger than the head window (smaller files are already fully hashed).
        var byHash = new Dictionary<string, List<HashedFile>>(StringComparer.Ordinal);
        var fullBuf = ArrayPool<byte>.Shared.Rent(HashBufferSize);
        try
        {
            using var fullHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var bucket in headBuckets.Values)
            {
                if (bucket.Files.Count < 2) continue;

                if (bucket.Size <= HeadHashSize)
                {
                    // Head IS full. Promote directly without re-reading.
                    string key = $"{bucket.Size}::{bucket.Hash}";
                    var promoted = bucket.Files
                        .Select(f => new HashedFile(f, bucket.Hash))
                        .ToList();
                    byHash[key] = promoted;
                    continue;
                }

                foreach (var f in bucket.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    string fullHex;
                    if (hashCache is null
                        || !hashCache.TryGetFull(f.FullPath, f.SizeBytes, f.LastWriteTime, out fullHex))
                    {
                        if (!TryHash(f.FullPath, -1L, fullBuf, fullHasher, out fullHex))
                            continue;
                        hashCache?.Update(f.FullPath, f.SizeBytes, f.LastWriteTime, headHash: null, fullHash: fullHex);
                    }

                    string key = $"{bucket.Size}::{fullHex}";
                    if (!byHash.TryGetValue(key, out var fullBucket))
                    {
                        fullBucket = new List<HashedFile>();
                        byHash[key] = fullBucket;
                    }
                    fullBucket.Add(new HashedFile(f, fullHex));
                    hashed++;
                    if (hashed % 25 == 0)
                    {
                        progress?.Report(new DedupeProgress("Hash", scanned, hashed, 0, 0));
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(fullBuf);
        }

        // Pass 3: build duplicate groups, sort by waste descending.
        int groupId = 0;
        var groups = new List<DuplicateGroup>();
        long totalWasted = 0;
        foreach (var bucket in byHash.Values)
        {
            if (bucket.Count < 2) continue;
            groupId++;
            long groupSize = bucket[0].File.SizeBytes;
            long wasted = groupSize * (bucket.Count - 1);
            totalWasted += wasted;
            var sortedFiles = bucket
                .OrderBy(f => f.File.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(f => new DuplicateFile(f.File.FullPath, f.File.SizeBytes, f.File.LastWriteTime, f.File.Category))
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
    }

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

    /// <summary>
    /// Stream-hash a file, optionally only its first <paramref name="maxBytes"/>
    /// bytes (-1 for full file). Returns false on I/O failure (locked file,
    /// race) so the caller can skip without aborting the scan.
    /// </summary>
    private static bool TryHash(
        string fullPath,
        long maxBytes,
        byte[] buffer,
        IncrementalHash hasher,
        out string hexHash)
    {
        try
        {
            // bufferSize: 1 disables FileStream's internal buffering — we manage
            // our own 1 MB buffer above. SequentialScan signals NTFS to prefetch
            // forward and to age cache pages out quickly so we don't pollute the
            // page cache with multi-GB hash reads (a major cause of "still
            // sluggish AFTER the scan").
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.SequentialScan);

            long remaining = maxBytes;
            while (true)
            {
                int wantThisRead;
                if (remaining < 0)
                {
                    wantThisRead = buffer.Length;
                }
                else
                {
                    if (remaining == 0) break;
                    wantThisRead = remaining > buffer.Length ? buffer.Length : (int)remaining;
                }

                int read = stream.Read(buffer, 0, wantThisRead);
                if (read == 0) break;
                hasher.AppendData(buffer, 0, read);
                if (remaining > 0)
                {
                    remaining -= read;
                }
            }

            hexHash = Convert.ToHexString(hasher.GetHashAndReset());
            return true;
        }
        catch
        {
            // Reset hasher state in case AppendData partially ran before the failure.
            try { _ = hasher.GetHashAndReset(); } catch { /* hasher already clean */ }
            hexHash = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Classify a path into a coarse user-relevance bucket. Drives the UI's
    /// default "show only user-actionable groups" filter and the smart-keeper
    /// auto-selection.
    /// </summary>
    internal static FileCategory Categorize(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        if (LooksLikeBackup(fileName)) return FileCategory.Backup;

        var segments = fullPath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        bool sawAppData = false;
        bool sawUserContent = false;

        foreach (var raw in segments)
        {
            var seg = raw.ToLowerInvariant();
            if (BuildOutputSegments.Contains(seg)) return FileCategory.BuildOutput;
            if (AppDataSegments.Contains(seg)) sawAppData = true;
            if (IsUserContentSegment(seg)) sawUserContent = true;
        }

        if (sawUserContent) return FileCategory.UserContent;
        if (sawAppData) return FileCategory.AppManaged;
        return FileCategory.Other;
    }

    private static bool LooksLikeBackup(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var lower = fileName.ToLowerInvariant();
        if (lower.EndsWith(".bak", StringComparison.Ordinal)) return true;
        if (lower.EndsWith(".old", StringComparison.Ordinal)) return true;
        if (lower.StartsWith("copy of ", StringComparison.Ordinal)) return true;
        if (lower.StartsWith("~$", StringComparison.Ordinal)) return true;
        // " (N).ext" — Windows / browser default for "Save again" copies.
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @" \(\d+\)\.[^.\s]+$")) return true;
        return false;
    }

    private static bool IsUserContentSegment(string segLower)
    {
        if (UserContentExact.Contains(segLower)) return true;
        // "OneDrive - Personal", "OneDrive - Anthropic", etc.
        foreach (var prefix in UserContentPrefixes)
        {
            if (segLower.StartsWith(prefix + " ", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static readonly char[] PathSeparators = ['\\', '/'];

    private static readonly HashSet<string> UserContentExact = new(StringComparer.Ordinal)
    {
        "desktop", "documents", "downloads", "pictures", "videos", "music",
        "onedrive", "dropbox", "box", "icloud drive", "icloud", "google drive"
    };

    private static readonly string[] UserContentPrefixes = new[]
    {
        "onedrive",
        "google drive"
    };

    private static readonly HashSet<string> AppDataSegments = new(StringComparer.Ordinal)
    {
        "appdata", "application data", "local settings"
    };

    private static readonly HashSet<string> BuildOutputSegments = new(StringComparer.Ordinal)
    {
        // Build outputs across .NET / Java / Rust / JS / Go ecosystems
        "bin", "obj", "target", "dist", "build", "out",
        // VCS / IDE caches sometimes within repos
        ".vs", ".idea", ".gradle",
        // Framework / bundler caches
        ".next", ".nuxt", ".parcel-cache", ".turbo", ".angular", ".cache",
        // Package-manager subtrees that may slip through when the canonical root
        // wasn't excluded (e.g. a vendor copy under a deeply nested project).
        "node_modules", "vendor", "pods", "bower_components", "packages",
        "site-packages", "pkgs"
    };

    /// <summary>Internal accumulator: file metadata captured during pass 1.</summary>
    private sealed record EnumeratedFile(
        string FullPath,
        long SizeBytes,
        DateTime LastWriteTime,
        FileCategory Category);

    /// <summary>Internal accumulator: head-hash bucket for pass 2a.</summary>
    private sealed class HashBucket
    {
        public long Size { get; }
        public string Hash { get; }
        public List<EnumeratedFile> Files { get; } = new();
        public HashBucket(long size, string hash) { Size = size; Hash = hash; }
    }

    /// <summary>Internal accumulator: full-hash bucket for pass 2b.</summary>
    private sealed record HashedFile(EnumeratedFile File, string Hash);
}
