namespace WinTune.Core.Services;

/// <summary>
/// Persistent cache of file hashes keyed by full path. Entries are validated
/// against the file's current (size, last-write-time) tuple at lookup, so any
/// content change implicitly invalidates the cached hash. Cache hits make
/// repeat scans of the same paths nearly free since pass-2 disk reads can be
/// skipped entirely.
/// </summary>
public interface IDedupHashCache
{
    /// <summary>
    /// Try to retrieve a cached <em>head</em> hash for <paramref name="fullPath"/>.
    /// Returns true only if all three conditions hold: the path is in the cache,
    /// the cached (size, mtime) match the supplied values, AND the head hash is
    /// populated. Increments <see cref="HitCount"/> on success and
    /// <see cref="MissCount"/> otherwise.
    /// </summary>
    bool TryGetHead(string fullPath, long size, DateTime lastWriteTime, out string headHash);

    /// <summary>
    /// Same contract as <see cref="TryGetHead"/> but for the full-file hash.
    /// </summary>
    bool TryGetFull(string fullPath, long size, DateTime lastWriteTime, out string fullHash);

    /// <summary>
    /// Record one or both hashes for <paramref name="fullPath"/>. Existing
    /// entries with matching (size, mtime) are merged (so a head-hash recorded
    /// in pass 2a is preserved when pass 2b later records the full hash). Any
    /// (size, mtime) mismatch overwrites the previous entry entirely.
    /// </summary>
    void Update(string fullPath, long size, DateTime lastWriteTime, string? headHash, string? fullHash);

    /// <summary>Load cache from disk. Missing or corrupt files are treated as empty.</summary>
    void Load();

    /// <summary>Persist cache to disk via atomic temp-file rename.</summary>
    void Save();

    /// <summary>Number of entries currently held.</summary>
    int Count { get; }

    /// <summary>Cache hits since the last <see cref="ResetStats"/> call.</summary>
    int HitCount { get; }

    /// <summary>Cache misses since the last <see cref="ResetStats"/> call.</summary>
    int MissCount { get; }

    /// <summary>Reset per-scan hit/miss counters. Called at the start of each scan.</summary>
    void ResetStats();
}
