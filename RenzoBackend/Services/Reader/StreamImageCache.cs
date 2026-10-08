using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Caching.Memory;

namespace RenzoBackend.Services.Reader;

/// <summary>
/// A bounded in-memory cache for STREAMED page images (chapters read live from a
/// source without downloading). Serving a re-requested page from RAM makes
/// scrolling back — and infinite scroll in either direction — instant, and it
/// cuts repeated hits to the source (fewer requests = less ban risk / fewer
/// leaked connections).
///
/// Downloaded chapters are deliberately NOT cached here: they read straight from
/// their CBZ archive on disk, which is already fast and browser-cached.
/// </summary>
public sealed class StreamImageCache : IDisposable
{
    public readonly record struct Entry(byte[] Bytes, string ContentType);

    // A single runaway image (e.g. a giant stitched strip) must not evict the
    // whole cache; skip caching anything larger than this.
    private const long MaxItemBytes = 24L * 1024 * 1024;

    // Total transient budget, LRU-evicted by byte size — enough to hold several
    // streamed chapters for smooth back-and-forth scrolling.
    private const long TotalBudgetBytes = 256L * 1024 * 1024;

    private readonly MemoryCache _cache =
        new(new MemoryCacheOptions { SizeLimit = TotalBudgetBytes });

    // MemoryCache cannot be enumerated, so the keys are mirrored here — the only
    // way to drop "every chapter of this series except these" (TrimSeries). An
    // eviction callback keeps it in step when the LRU or the sliding expiry
    // removes an entry on its own.
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);

    public bool TryGet(string key, out Entry entry) => _cache.TryGetValue(key, out entry);

    public void Set(string key, byte[] bytes, string contentType)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length > MaxItemBytes)
            return;
        var options = new MemoryCacheEntryOptions
        {
            Size = bytes.Length,
            SlidingExpiration = TimeSpan.FromMinutes(20),
        };
        options.RegisterPostEvictionCallback((k, _, reason, _) =>
        {
            // A Replaced entry is still present under the same key.
            if (reason != EvictionReason.Replaced && k is string s)
                _keys.TryRemove(s, out _);
        });
        _cache.Set(key, new Entry(bytes, contentType), options);
        _keys[key] = 0;
    }

    /// <summary>
    /// Drops every cached page of <paramref name="seriesId"/> whose chapter is not
    /// in <paramref name="keep"/>. The paged reader calls this as it moves, so a
    /// long session holds the chapters around the reader rather than every
    /// chapter it ever passed through until the byte budget forces them out.
    ///
    /// Chapter numbers are compared as decimals, not as key text: the web sends
    /// "60" and Hub sends "60.0" for the same chapter, and decimal equality
    /// ignores scale where string equality would not.
    /// </summary>
    public int TrimSeries(Guid seriesId, IReadOnlyCollection<decimal> keep)
    {
        string prefix = $"lib:img:{seriesId}:";
        int removed = 0;
        foreach (string key in _keys.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            // lib:img:{series}:{chapter}:{page} — the chapter is everything up to
            // the LAST colon of the remainder.
            string rest = key[prefix.Length..];
            int colon = rest.LastIndexOf(':');
            if (colon <= 0)
                continue;
            string chapterText = rest[..colon];
            if (!decimal.TryParse(chapterText, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal chapter)
                && !decimal.TryParse(chapterText, NumberStyles.Number, CultureInfo.InvariantCulture, out chapter))
                continue;
            if (keep.Contains(chapter))
                continue;
            _cache.Remove(key);
            _keys.TryRemove(key, out _);
            removed++;
        }
        return removed;
    }

    /// <summary>
    /// Drops one cached image. Used to force a genuine re-pull of a chapter: the
    /// keys for a chapter are dense (…:0, …:1, …), so the caller sweeps a range
    /// rather than needing this cache to be enumerable.
    /// </summary>
    public void Remove(string key)
    {
        _cache.Remove(key);
        _keys.TryRemove(key, out _);
    }

    /// <summary>Drops every cached streamed image, freeing the whole budget immediately.</summary>
    public long Clear()
    {
        long freed = _cache.Count;
        _cache.Clear();
        _keys.Clear();
        return freed;
    }

    /// <summary>Number of streamed images currently held.</summary>
    public long Count => _cache.Count;

    public void Dispose() => _cache.Dispose();
}
