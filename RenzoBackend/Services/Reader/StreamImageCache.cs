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

    public bool TryGet(string key, out Entry entry) => _cache.TryGetValue(key, out entry);

    public void Set(string key, byte[] bytes, string contentType)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length > MaxItemBytes)
            return;
        _cache.Set(key, new Entry(bytes, contentType), new MemoryCacheEntryOptions
        {
            Size = bytes.Length,
            SlidingExpiration = TimeSpan.FromMinutes(20),
        });
    }

    /// <summary>Drops every cached streamed image, freeing the whole budget immediately.</summary>
    public long Clear()
    {
        long freed = _cache.Count;
        _cache.Clear();
        return freed;
    }

    /// <summary>Number of streamed images currently held.</summary>
    public long Count => _cache.Count;

    public void Dispose() => _cache.Dispose();
}
