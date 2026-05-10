using System.IO;
using System.Text.Json;

namespace WinTune.Core.Services;

public sealed class DedupHashCache : IDedupHashCache
{
    private static readonly string DefaultCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinTune", "dedupe-cache.json");

    private readonly string _cachePath;
    private readonly object _lock = new();
    private Dictionary<string, DedupHashCacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _hits;
    private int _misses;

    public DedupHashCache() : this(DefaultCachePath) { }

    // Test-friendly: tests can target a temp path so they don't read or write
    // the user's real cache file.
    public DedupHashCache(string cachePath)
    {
        _cachePath = cachePath;
    }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    public int HitCount => Volatile.Read(ref _hits);
    public int MissCount => Volatile.Read(ref _misses);

    public void ResetStats()
    {
        Volatile.Write(ref _hits, 0);
        Volatile.Write(ref _misses, 0);
    }

    public bool TryGetHead(string fullPath, long size, DateTime lastWriteTime, out string headHash)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(fullPath, out var e)
                && e.Size == size
                && e.MtimeTicks == lastWriteTime.Ticks
                && e.HeadHash is not null)
            {
                headHash = e.HeadHash;
                Interlocked.Increment(ref _hits);
                return true;
            }
        }
        Interlocked.Increment(ref _misses);
        headHash = string.Empty;
        return false;
    }

    public bool TryGetFull(string fullPath, long size, DateTime lastWriteTime, out string fullHash)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(fullPath, out var e)
                && e.Size == size
                && e.MtimeTicks == lastWriteTime.Ticks
                && e.FullHash is not null)
            {
                fullHash = e.FullHash;
                Interlocked.Increment(ref _hits);
                return true;
            }
        }
        Interlocked.Increment(ref _misses);
        fullHash = string.Empty;
        return false;
    }

    public void Update(string fullPath, long size, DateTime lastWriteTime, string? headHash, string? fullHash)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(fullPath, out var existing)
                && existing.Size == size
                && existing.MtimeTicks == lastWriteTime.Ticks)
            {
                _entries[fullPath] = existing with
                {
                    HeadHash = headHash ?? existing.HeadHash,
                    FullHash = fullHash ?? existing.FullHash
                };
            }
            else
            {
                _entries[fullPath] = new DedupHashCacheEntry(size, lastWriteTime.Ticks, headHash, fullHash);
            }
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var json = File.ReadAllText(_cachePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, DedupHashCacheEntry>>(json);
            if (data is null) return;
            lock (_lock)
            {
                _entries = new Dictionary<string, DedupHashCacheEntry>(data, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Corrupt cache file → start fresh, do not surface errors to the user.
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            Dictionary<string, DedupHashCacheEntry> snapshot;
            lock (_lock)
            {
                snapshot = new Dictionary<string, DedupHashCacheEntry>(_entries, StringComparer.OrdinalIgnoreCase);
            }

            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
            if (File.Exists(_cachePath)) File.Delete(_cachePath);
            File.Move(tmp, _cachePath);
        }
        catch
        {
            // Persistence is best-effort; failed save just means a cold cache next session.
        }
    }
}

internal sealed record DedupHashCacheEntry(long Size, long MtimeTicks, string? HeadHash, string? FullHash);
