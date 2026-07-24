using System.Text.Json;
using RenzoBackend.Extensions;

namespace RenzoBackend.Services.Series;

/// <summary>
/// Resolves a series' category (Manga / Manhwa / Manhua / Comic / Other) from the
/// most reliable signal available. Country of origin is what actually defines
/// these buckets, and source genres/tags rarely carry it — so the primary signal
/// is MangaDex's <c>originalLanguage</c> (ja→Manga, ko→Manhwa, zh→Manhua, en→Comic),
/// matched by title. Explicit format tags and the source-name heuristic are used
/// when MangaDex can't confidently match, and everything else falls back to Manga.
///
/// <see cref="Resolution.Confident"/> is only true when a real signal fired (a
/// format tag or a good MangaDex title match) — never for the bare Manga default —
/// so the re-categorization pass only ever MOVES a series on solid evidence.
/// </summary>
public class SeriesCategoryResolver
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SeriesCategoryResolver> _logger;

    // Small process-wide cache so a re-run (or the same title across users) doesn't
    // re-hit MangaDex. Key = normalized title, value = originalLanguage or "".
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _langCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim _rateGate = new(1, 1);
    private static DateTime _lastCall = DateTime.MinValue;

    public SeriesCategoryResolver(IHttpClientFactory httpFactory, ILogger<SeriesCategoryResolver> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public readonly record struct Resolution(string? Category, bool Confident, string Signal);

    /// <summary>
    /// Map a raw external-scrobbler media-type string (MAL <c>media_type</c>, Kitsu
    /// <c>manga_type</c>, AniList format, MangaDex originalLanguage/demographic, …) to
    /// one of the configured category folders. Returns null when the type carries no
    /// country-of-origin signal (novel / one-shot / doujinshi / bare demographic) so
    /// the caller falls through to the next signal instead of mis-filing.
    /// </summary>
    public static string? MapScrobblerType(string? scrobblerType, Func<string, string?> cat)
    {
        if (string.IsNullOrWhiteSpace(scrobblerType)) return null;
        string n = scrobblerType.Trim().ToLowerInvariant();
        return n switch
        {
            "manga" or "ja" or "jp" or "japanese" => cat("Manga"),
            "manhwa" or "ko" or "kr" or "korean" => cat("Manhwa"),
            "manhua" or "zh" or "zh-hk" or "zh-ro" or "cn" or "chinese" => cat("Manhua"),
            "comic" or "oel" or "en" or "english" or "american" => cat("Comic") ?? cat("Other"),
            _ => null, // novel / light_novel / one_shot / oneshot / doujin(shi) / unknown / bare demographic
        };
    }

    public async Task<Resolution> ResolveAsync(
        string? title,
        IEnumerable<string>? genres,
        IEnumerable<string>? providerNames,
        string[] categories,
        CancellationToken token = default,
        string? scrobblerType = null)
    {
        if (categories == null || categories.Length == 0)
            return new Resolution(null, false, "no-categories");

        string? Cat(string wanted) => categories.FirstOrDefault(c => c.Equals(wanted, StringComparison.OrdinalIgnoreCase));

        // 0) External scrobbler media-type — the STRONGEST signal when present. It comes
        // from an ID-based match (MAL/Kitsu/AniList), not a fuzzy title guess, so it
        // outranks even the MangaDex title lookup and skips the network call entirely.
        if (MapScrobblerType(scrobblerType, Cat) is { } scrobMapped)
            return new Resolution(scrobMapped, true, $"scrobbler:{scrobblerType!.Trim().ToLowerInvariant()}");

        // 1) MangaDex country of origin — the one authoritative signal. Country of
        // origin is exactly what these buckets mean, and (unlike genre tags) sources
        // can't mislabel it. Requires a near-exact title match (see lookup).
        string? lang = await LookupOriginalLanguageAsync(title, token).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(lang))
        {
            string? mapped = lang switch
            {
                "ja" => Cat("Manga"),
                "ko" => Cat("Manhwa"),
                "zh" or "zh-hk" or "zh-ro" => Cat("Manhua"),
                "en" => Cat("Comic") ?? Cat("Other"),
                _ => Cat("Other"),
            };
            if (mapped != null)
                return new Resolution(mapped, true, $"mangadex:{lang}");
        }

        // 2) Only FORMAT-descriptive genre tags are trustworthy — "long strip"/
        // "webtoon" (Korean vertical-scroll) and "manhua" (Chinese). Raw "manga"/
        // "manhwa" tags are dropped: sources routinely mislabel them (a Japanese
        // series carrying a stray "manhwa" tag would move to the wrong shelf).
        if (genres != null)
        {
            foreach (string g in genres)
            {
                if (string.IsNullOrWhiteSpace(g)) continue;
                string n = g.Trim().ToLowerInvariant();
                if (n.Contains("manhua") && Cat("Manhua") is { } mh) return new Resolution(mh, true, "genre:manhua");
                if ((n.Contains("webtoon") || n.Contains("long strip")) && Cat("Manhwa") is { } mw)
                    return new Resolution(mw, true, "genre:webtoon");
            }
        }

        // 3) Source-name heuristic + Manga default (NOT confident — never triggers a move).
        string? heuristic = SeriesTypeClassifier.Classify(genres, providerNames, categories);
        return new Resolution(heuristic, false, "heuristic/default");
    }

    /// <summary>
    /// MangaDex originalLanguage for the best title match, or null. Cached; rate-limited
    /// to stay well under MangaDex's ~5 req/s. Never throws.
    /// </summary>
    private async Task<string?> LookupOriginalLanguageAsync(string? title, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        string key = title.Trim().ToLowerInvariant();
        if (_langCache.TryGetValue(key, out string? cached))
            return string.IsNullOrEmpty(cached) ? null : cached;

        try
        {
            await _rateGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // ~4 req/s ceiling.
                TimeSpan since = DateTime.UtcNow - _lastCall;
                if (since < TimeSpan.FromMilliseconds(260))
                    await Task.Delay(TimeSpan.FromMilliseconds(260) - since, token).ConfigureAwait(false);
                _lastCall = DateTime.UtcNow;
            }
            finally { _rateGate.Release(); }

            string url = "https://api.mangadex.org/manga?limit=5&order[relevance]=desc"
                + "&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica&contentRating[]=pornographic"
                + "&title=" + Uri.EscapeDataString(title.Trim());

            using HttpClient http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Renzo/1.0");

            using HttpResponseMessage resp = await http.GetAsync(url, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _langCache[key] = "";
                return null;
            }

            await using Stream s = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(s, cancellationToken: token).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            {
                _langCache[key] = "";
                return null;
            }

            // Only trust the TOP relevance result, and only when its OWN primary/
            // English title is a near-exact match — matching against every alt title
            // of every result (many languages/variants) produced false hits that
            // moved series into the wrong bucket. Strict distance (<=0.08).
            string? bestLang = null;
            JsonElement first = data.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("attributes", out JsonElement attr))
            {
                var primaryTitles = new List<string>();
                if (attr.TryGetProperty("title", out JsonElement t))
                    primaryTitles.AddRange(t.EnumerateObject().Select(p => p.Value.GetString() ?? ""));
                // Include only the English alt title (the romanization users search by),
                // not every language variant.
                if (attr.TryGetProperty("altTitles", out JsonElement alts) && alts.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement a in alts.EnumerateArray())
                        if (a.TryGetProperty("en", out JsonElement en))
                            primaryTitles.Add(en.GetString() ?? "");

                bool strong = primaryTitles.Any(c => !string.IsNullOrWhiteSpace(c) && c.AreStringSimilar(title!, 0.08));
                if (strong && attr.TryGetProperty("originalLanguage", out JsonElement ol))
                    bestLang = ol.GetString();
            }

            _langCache[key] = bestLang ?? "";
            return bestLang;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MangaDex origin lookup failed for '{Title}'", title);
            _langCache[key] = "";
            return null;
        }
    }
}
