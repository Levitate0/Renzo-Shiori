using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mihon.ExtensionsBridge.Models.Extensions;
using RenzoBackend.Services.SiteAuth;

namespace RenzoBackend.Services.Series;

/// <summary>
/// Serves the pages of a coin-gated chapter the user has ALREADY PURCHASED on a
/// "vcomics" (Astro) site — e.g. Magus Manga.
///
/// Why this is needed: on that platform the chapter HTML is a public, CDN-cached
/// shell that never contains the pages of a paid chapter — not even for a logged-in
/// purchaser (verified: the authenticated origin render is byte-identical to the
/// anonymous one). The reader fetches them client-side instead, from
/// {api}/api/chapter/content?mangaslug=..&amp;chapterslug=.., which honours the
/// session cookie:
///
///     anonymous → {"isAccessible":false,"images":[]}
///     purchased → {"isAccessible":true,"isPurchased":true,"images":[{url,order},…]}
///
/// So an extension that scrapes the page can only ever conclude "locked", however
/// valid the session is. This asks the same endpoint the site's own reader does,
/// reusing the session cookie already sitting in the shared extension jar.
///
/// It never purchases anything: an un-owned chapter simply reports isAccessible
/// false and this returns null, leaving the chapter locked.
/// </summary>
public class VComicsContentService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly CookieJarBridge _cookies;
    private readonly ILogger<VComicsContentService> _logger;

    // Discovered API host per web host — the site inlines it as PUBLIC_API_URL.
    private static readonly ConcurrentDictionary<string, string> _apiBase = new(StringComparer.OrdinalIgnoreCase);
    // Whether a host is this platform at all. Cached so we don't fetch a page —
    // or fire content requests — at every unrelated source.
    private static readonly ConcurrentDictionary<string, bool> _isPlatform = new(StringComparer.OrdinalIgnoreCase);
    // The endpoint+parameter spelling that actually worked for a host, so the
    // candidate sweep below happens once rather than on every page load.
    private static readonly ConcurrentDictionary<string, string> _endpointForm = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex ApiUrlRegex = new(
        @"""(?:PUBLIC_API_URL|NEXT_PUBLIC_API_URL|PUBLIC_API_BASE|apiUrl)""\s*:\s*""(?<u>https?://[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Endpoint spellings seen across builds of this platform. "{p}" is the path,
    // "{a}"/"{b}" the series- and chapter-slug parameter names. The first that
    // returns a usable payload for a host is remembered.
    private static readonly (string Path, string SeriesParam, string ChapterParam)[] ContentForms =
    {
        ("/api/chapter/content", "mangaslug",   "chapterslug"),
        ("/api/chapter/content", "seriesSlug",  "chapterSlug"),
        ("/api/chapters/content", "mangaslug",  "chapterslug"),
        ("/api/chapter-content",  "mangaslug",  "chapterslug"),
    };

    // Keys a build might use for the image list and for "the signed-in user may
    // read this", checked in order.
    private static readonly string[] ImageKeys = { "images", "pages", "chapterImages", "content_images" };
    private static readonly string[] AccessKeys = { "isAccessible", "accessible", "isPurchased", "hasPurchased", "purchased", "canRead" };

    public VComicsContentService(
        IHttpClientFactory httpFactory,
        CookieJarBridge cookies,
        ILogger<VComicsContentService> logger)
    {
        _httpFactory = httpFactory;
        _cookies = cookies;
        _logger = logger;
    }

    /// <summary>
    /// Pages for <paramref name="absoluteChapterUrl"/> if the signed-in user owns
    /// that chapter, else null (not this platform / not logged in / not purchased).
    /// Never throws.
    /// </summary>
    public async Task<List<Page>?> TryGetPurchasedPagesAsync(string? absoluteChapterUrl, CancellationToken token = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(absoluteChapterUrl) ||
                !Uri.TryCreate(absoluteChapterUrl, UriKind.Absolute, out Uri? uri))
                return null;

            // {…}/{series-slug}/{chapter-slug} — the trailing pair is what these
            // builds key on. Deliberately not requiring a "chapter" prefix: slugs
            // differ per site (chapter-5, ch-5, episode-5, a bare number…).
            string[] seg = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length < 2)
                return null;
            string chapterSlug = seg[^1];
            string mangaSlug = seg[^2];

            string host = uri.Host;
            // Only worth asking when we actually hold a session for the site.
            List<HarvestedCookie> jar = _cookies.Snapshot(host);
            if (jar.Count == 0)
                return null;

            // …and only when this host really is the platform, so unrelated
            // sources never get probed.
            if (!await IsPlatformAsync(uri, token).ConfigureAwait(false))
                return null;

            string cookieHeader = string.Join("; ", jar.Select(c => $"{c.Name}={c.Value}"));
            string apiBase = await ResolveApiBaseAsync(uri, token).ConfigureAwait(false);

            using HttpClient http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookieHeader);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", $"{uri.Scheme}://{host}");
            http.DefaultRequestHeaders.Referrer = uri;
            // The purchase state must not come from a cache.
            http.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-store");

            // Try the spelling that already worked for this host first.
            IEnumerable<(string Path, string SeriesParam, string ChapterParam)> forms = ContentForms;
            if (_endpointForm.TryGetValue(host, out string? known))
                forms = ContentForms.Where(f => Key(f) == known).Concat(ContentForms.Where(f => Key(f) != known));

            foreach ((string Path, string SeriesParam, string ChapterParam) form in forms)
            {
                string endpoint = $"{apiBase}{form.Path}" +
                                  $"?{form.SeriesParam}={Uri.EscapeDataString(mangaSlug)}" +
                                  $"&{form.ChapterParam}={Uri.EscapeDataString(chapterSlug)}";

                using HttpResponseMessage resp = await http.GetAsync(endpoint, token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    continue; // wrong spelling for this build — try the next

                List<Page>? pages;
                await using (Stream s = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                using (JsonDocument doc = await JsonDocument.ParseAsync(s, cancellationToken: token).ConfigureAwait(false))
                    pages = ParsePages(doc.RootElement);

                // A well-formed "you don't own this" answer still proves the
                // endpoint is right, so remember it and stop looking.
                _endpointForm[host] = Key(form);
                if (pages == null)
                    return null;

                _logger.LogInformation(
                    "Purchased-chapter pages: served {Count} page(s) for {Manga}/{Chapter} from {Host}",
                    pages.Count, mangaSlug, chapterSlug, host);
                return pages;
            }
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "vcomics purchased-page fetch failed for {Url}", absoluteChapterUrl);
            return null;
        }
    }

    private static string Key((string Path, string SeriesParam, string ChapterParam) f) =>
        $"{f.Path}|{f.SeriesParam}|{f.ChapterParam}";

    /// <summary>
    /// Pages from a content payload, or null when the response says the signed-in
    /// user doesn't own the chapter (or carries no usable images).
    /// </summary>
    private static List<Page>? ParsePages(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        // Some builds nest the payload under data/result.
        foreach (string wrap in new[] { "data", "result", "chapter" })
            if (root.TryGetProperty(wrap, out JsonElement inner) && inner.ValueKind == JsonValueKind.Object &&
                !ImageKeys.Any(k => root.TryGetProperty(k, out _)))
                root = inner;

        // Access must be positively granted. If a build reports no access flag at
        // all, a non-empty image list is itself the grant.
        bool sawAccessFlag = false, granted = false;
        foreach (string k in AccessKeys)
        {
            if (!root.TryGetProperty(k, out JsonElement a))
                continue;
            if (a.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                sawAccessFlag = true;
                granted |= a.ValueKind == JsonValueKind.True;
            }
        }
        if (sawAccessFlag && !granted)
            return null; // not owned — leave it locked

        JsonElement images = default;
        bool found = false;
        foreach (string k in ImageKeys)
            if (root.TryGetProperty(k, out images) && images.ValueKind == JsonValueKind.Array)
            {
                found = true;
                break;
            }
        if (!found)
            return null;

        var ordered = new List<(int Order, string Url)>();
        foreach (JsonElement img in images.EnumerateArray())
        {
            string? url = null;
            int order = ordered.Count;
            if (img.ValueKind == JsonValueKind.String)
            {
                url = img.GetString();
            }
            else if (img.ValueKind == JsonValueKind.Object)
            {
                foreach (string k in new[] { "url", "src", "image", "imageUrl", "path" })
                    if (img.TryGetProperty(k, out JsonElement u) && u.ValueKind == JsonValueKind.String)
                    {
                        url = u.GetString();
                        break;
                    }
                foreach (string k in new[] { "order", "index", "page" })
                    if (img.TryGetProperty(k, out JsonElement o) && o.ValueKind == JsonValueKind.Number &&
                        o.TryGetInt32(out int n))
                    {
                        order = n;
                        break;
                    }
            }
            if (!string.IsNullOrWhiteSpace(url))
                ordered.Add((order, url!));
        }
        if (ordered.Count == 0)
            return null;

        return ordered
            .OrderBy(p => p.Order)
            .Select((p, i) => new Page { Index = i, Url = p.Url, ImageUrl = p.Url })
            .ToList();
    }

    /// <summary>
    /// Whether this host runs the platform at all — checked once per host from the
    /// page's build fingerprint, so unrelated sources are never probed.
    /// </summary>
    private async Task<bool> IsPlatformAsync(Uri pageUri, CancellationToken token)
    {
        if (_isPlatform.TryGetValue(pageUri.Host, out bool known))
            return known;

        bool isPlatform = false;
        try
        {
            using HttpClient http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(25);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            string html = await http.GetStringAsync(pageUri, token).ConfigureAwait(false);

            Match m = ApiUrlRegex.Match(html);
            isPlatform = m.Success ||
                         html.IndexOf("/_vcomics/", StringComparison.OrdinalIgnoreCase) >= 0;
            if (m.Success)
                _apiBase[pageUri.Host] = m.Groups["u"].Value.TrimEnd('/');
        }
        catch { /* treat an unreachable page as "not this platform" */ }

        _isPlatform[pageUri.Host] = isPlatform;
        return isPlatform;
    }

    /// <summary>
    /// The site's API host. Read from the PUBLIC_API_URL the Astro shell inlines,
    /// cached per host; "api.{host}" is the conventional fallback.
    /// </summary>
    private async Task<string> ResolveApiBaseAsync(Uri chapterUri, CancellationToken token)
    {
        string host = chapterUri.Host;
        if (_apiBase.TryGetValue(host, out string? cached))
            return cached;

        string resolved = $"{chapterUri.Scheme}://api.{host}";
        try
        {
            using HttpClient http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(25);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            string html = await http.GetStringAsync(chapterUri, token).ConfigureAwait(false);
            Match m = ApiUrlRegex.Match(html);
            if (m.Success)
                resolved = m.Groups["u"].Value.TrimEnd('/');
        }
        catch { /* stick with the conventional host */ }

        _apiBase[host] = resolved;
        return resolved;
    }
}
