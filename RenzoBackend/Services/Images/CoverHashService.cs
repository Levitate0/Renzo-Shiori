using RenzoBackend.Models.Database;
using NetVips;
using System.Collections.Concurrent;
using System.Numerics;

namespace RenzoBackend.Services.Images;

/// <summary>
/// Computes 64-bit difference-hash (dHash) fingerprints of series cover images so
/// import matching can recognize the same artwork across providers even when the
/// titles differ (localized or renamed series). Image bytes come through
/// ThumbCacheService, which disk-caches downloads and knows how to fetch
/// referer-protected provider images; computed hashes are additionally cached
/// per thumbnail URL for the process lifetime.
/// </summary>
public class CoverHashService
{
    private const string ImageApiPrefix = "/api/image/";

    private readonly ThumbCacheService _thumb;
    private readonly ILogger _logger;

    private static readonly ConcurrentDictionary<string, ulong?> _hashCache = new();

    public CoverHashService(ThumbCacheService thumb, ILogger<CoverHashService> logger)
    {
        _thumb = thumb;
        _logger = logger;
    }

    /// <summary>
    /// Number of differing bits between two hashes (0 = identical artwork,
    /// 64 = completely different). Re-encodes/rescales of the same cover
    /// typically land within a handful of bits of each other.
    /// </summary>
    public static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>
    /// Returns the dHash of the image behind a thumbnail URL (raw provider URL or
    /// an already-rewritten /api/image/{key} URL), or null when the image cannot
    /// be fetched or decoded. Failures are cached too, so a dead URL is only
    /// attempted once per process.
    /// </summary>
    public async Task<ulong?> ComputeHashAsync(string? thumbnailUrl, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(thumbnailUrl))
            return null;
        if (_hashCache.TryGetValue(thumbnailUrl, out ulong? cached))
            return cached;

        ulong? hash = null;
        try
        {
            string key = thumbnailUrl.StartsWith(ImageApiPrefix, StringComparison.OrdinalIgnoreCase)
                ? thumbnailUrl[ImageApiPrefix.Length..]
                : await _thumb.GetKeyAsync(thumbnailUrl, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(key))
            {
                EtagCacheEntity? entry = await _thumb.GetEtagAsync(key, token).ConfigureAwait(false);
                if (entry != null)
                {
                    Stream? stream = await _thumb.GetStreamAsync(entry, token).ConfigureAwait(false);
                    if (stream != null)
                    {
                        await using (stream)
                        {
                            hash = ComputeDHash(stream);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not compute cover hash for {Url}", thumbnailUrl);
        }
        _hashCache[thumbnailUrl] = hash;
        return hash;
    }

    private static ulong ComputeDHash(Stream stream)
    {
        // 9x8 grayscale reduction; each bit encodes the sign of the horizontal
        // gradient between adjacent pixels, which survives rescaling/re-encoding.
        using Image thumb = Image.ThumbnailStream(stream, 9, height: 8, size: Enums.Size.Force);
        using Image gray = thumb.Colourspace(Enums.Interpretation.Bw);
        using Image band = gray.ExtractBand(0);
        using Image cast = band.Cast(Enums.BandFormat.Uchar);
        byte[] pixels = cast.WriteToMemory();
        if (pixels.Length < 72)
            throw new InvalidOperationException("Unexpected pixel buffer size for dHash.");
        ulong hash = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                hash <<= 1;
                if (pixels[y * 9 + x] < pixels[y * 9 + x + 1])
                    hash |= 1;
            }
        }
        return hash;
    }
}
