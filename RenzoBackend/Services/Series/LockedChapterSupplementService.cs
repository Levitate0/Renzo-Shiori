using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Services.Settings;

namespace RenzoBackend.Services.Series;

/// <summary>
/// Supplements a provider's chapter list with the paid/coin-gated chapters that
/// the source's own Mihon extension doesn't parse. Three platforms are
/// recognised, each detected by its own markup — never by a hardcoded host list.
/// It runs for EVERY source, login or not (Sana Scans showed none of its paid
/// chapters because the check used to need a saved site login). Each host's
/// platform is remembered, so a site with none of them costs one probe a day:
///
///  1. The WordPress "lock chapters" plugin (mangareader-style theme): every
///     chapter is a &lt;li data-num="N"&gt;, but a locked one uses a purchase-modal
///     anchor (data-bs-target="#lockedChapterModal", data-coin, data-title) with
///     NO href, so the extension — which only follows real chapter links —
///     silently drops them. e.g. Violet Scans.
///
///  2. The "vcomics" Astro platform (Magus Manga, Vortex, Sana Scans): the page is a static
///     shell whose chapter list is hydrated client-side from a separate JSON API
///     (PUBLIC_API_URL -> /api/chapters?postId=N). Locked chapters carry
///     isLocked/price/isAccessible there. The extension only sees the server-
///     rendered free chapters, so the coin-gated ones never appear at all — and
///     none of the WordPress markers exist on these sites, so strategy 1 finds
///     nothing.
///
///  3. The "vapi" platform (EZmanga): see FetchChapterStatesAsync.
///
/// vcomics and vapi report lock state as data, so <see cref="FetchChapterStatesAsync"/>
/// re-reads it on every scan (these sites publish a chapter paid and free it
/// later). The WordPress plugin only marks what is locked right now, so it is
/// discovery-only, via <see cref="FetchLockedChaptersAsync"/>.
///
/// Either way the gated chapters are returned as <see cref="Chapter"/> rows
/// flagged <see cref="Chapter.IsLocked"/>. They then show as "locked" in the UI;
/// the download only succeeds once the user's logged-in session actually owns the
/// chapter (i.e. they already spent the coins on the site), which the existing
/// site-login + allow-locked download path handles. Nothing here ever spends
/// coins — purchasing stays a deliberate action on the site itself.
/// </summary>
public class LockedChapterSupplementService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly SettingsService _settings;
    private readonly ILogger<LockedChapterSupplementService> _logger;

    public LockedChapterSupplementService(
        IHttpClientFactory httpFactory,
        SettingsService settings,
        ILogger<LockedChapterSupplementService> logger)
    {
        _httpFactory = httpFactory;
        _settings = settings;
        _logger = logger;
    }

    private enum Platform { None, Vapi, VComics, WordPressLock }

    // Per host, for the process lifetime. A platform found stays found (it is a
    // site-wide build, not per series) until its fingerprint stops matching; "none"
    // is re-checked daily, or on every scan for a source the owner has a login for.
    private static readonly ConcurrentDictionary<string, (Platform P, DateTime At)> Platforms =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan NoneRecheck = TimeSpan.FromHours(24);

    private static Platform? Known(string host, bool recheckNone)
    {
        if (!Platforms.TryGetValue(host, out var v))
            return null;
        if (v.P == Platform.None && (recheckNone || DateTime.UtcNow - v.At > NoneRecheck))
            return null;
        return v.P;
    }

    private static void Remember(string host, Platform p) => Platforms[host] = (p, DateTime.UtcNow);

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? u) ? u.Host : "";

    // <li data-num="104"> ... </li>  — one per chapter (free or locked).
    private static readonly Regex LiRegex = new(
        @"<li[^>]*\bdata-num=""(?<num>[0-9]+(?:\.[0-9]+)?)""[^>]*>(?<body>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CoinRegex = new(@"data-coin=""(?<coin>[0-9]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitleAttrRegex = new(@"data-title=""(?<t>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DateRegex = new(@"class=""chapterdate""[^>]*>\s*(?<d>[^<]+)<", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// WordPress lock plugin: the coin-gated chapters on the series page that
    /// aren't in <paramref name="existingNumbers"/>. Call it only when
    /// <see cref="FetchChapterStatesAsync"/> found nothing. Never throws — returns
    /// an empty list on any failure (missing FlareSolverr, other platform, parse miss, …).
    /// </summary>
    public async Task<List<Chapter>> FetchLockedChaptersAsync(
        string sampleChapterUrl,
        IReadOnlyCollection<decimal> existingNumbers,
        bool recheck,
        CancellationToken token = default)
    {
        var result = new List<Chapter>();
        try
        {
            if (string.IsNullOrWhiteSpace(sampleChapterUrl))
                return result;

            string host = HostOf(sampleChapterUrl);
            Platform? known = Known(host, recheck);
            if (known is not (null or Platform.WordPressLock))
                return result;

            // Fetched plain-first with a FlareSolverr fallback (rather than
            // requiring FlareSolverr up front) so a gated chapter is always at
            // least ATTEMPTED — the old hard gate meant that with FlareSolverr
            // off the paid chapters were silently dropped instead.
            string? seriesUrl = sampleChapterUrl.IndexOf("-chapter-", StringComparison.OrdinalIgnoreCase) < 0
                ? null
                : DeriveSeriesUrl(sampleChapterUrl);
            if (seriesUrl == null)
            {
                Remember(host, Platform.None); // the source's URL shape isn't this plugin's
                return result;
            }

            SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            string? html = await FetchHtmlAsync(settings, seriesUrl, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(html))
                return result; // couldn't fetch: decide nothing
            if (html.IndexOf("lockedChapterModal", StringComparison.OrdinalIgnoreCase) < 0)
            {
                // Nothing gated on THIS series. The plugin only shows on a series
                // with a locked chapter, so a known lock site stays known.
                if (known == null)
                    Remember(host, Platform.None);
                return result;
            }
            Remember(host, Platform.WordPressLock);

            string chapterBase = sampleChapterUrl.Substring(0, sampleChapterUrl.LastIndexOf("-chapter-", StringComparison.OrdinalIgnoreCase));
            var seen = new HashSet<decimal>(existingNumbers);

            foreach (Match li in LiRegex.Matches(html))
            {
                string body = li.Groups["body"].Value;
                Match coin = CoinRegex.Match(body);
                if (!coin.Success)
                    continue; // free chapter — the extension already has it

                if (!decimal.TryParse(li.Groups["num"].Value, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out decimal num))
                    continue;
                if (!seen.Add(num))
                    continue; // already known

                string title = TitleAttrRegex.Match(body) is { Success: true } tm && !string.IsNullOrWhiteSpace(tm.Groups["t"].Value)
                    ? tm.Groups["t"].Value.Trim()
                    : $"Chapter {TrimNum(num)}";

                DateTime? uploaded = null;
                Match dm = DateRegex.Match(body);
                if (dm.Success && DateTime.TryParse(dm.Groups["d"].Value.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime dt))
                    uploaded = dt;

                result.Add(new Chapter
                {
                    Number = num,
                    Name = title,
                    // The real reader URL once unlocked follows the site's slug pattern.
                    Url = $"{chapterBase}-chapter-{TrimNum(num)}/",
                    ProviderUploadDate = uploaded,
                    DateFetched = DateTime.UtcNow,
                    IsLocked = true,
                    ShouldDownload = false, // don't auto-queue a coin-gated chapter
                    IsDeleted = false,
                });
            }

            if (result.Count > 0)
                _logger.LogInformation("Locked-chapter supplement: found {Count} coin-gated chapter(s) at {Series}", result.Count, seriesUrl);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Locked-chapter supplement failed for {Url}", sampleChapterUrl);
        }
        return result;
    }

    // --- vcomics (Astro) platform -------------------------------------------------

    // Runtime env the Astro shell inlines: "PUBLIC_API_URL":"https://api.example.org"
    private static readonly Regex ApiUrlRegex = new(
        @"""(?:PUBLIC_API_URL|NEXT_PUBLIC_API_URL)""\s*:\s*""(?<u>https?://[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Astro serialises island props as [0,value], so the series id reads
    // "postId":[0,928]. Accept the plain "postId":928 form too.
    private static readonly Regex PostIdRegex = new(
        @"&quot;postId&quot;\s*:\s*\[\s*0\s*,\s*(?<id>\d+)|""postId""\s*:\s*(?:\[\s*0\s*,\s*)?(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Every chapter of a vcomics/Astro series with its lock state. The chapter
    /// list isn't in the HTML at all — the shell hydrates it from
    /// {api}/api/chapters?postId=N, which reports isLocked/price/isAccessible per
    /// chapter, all in one response. IsPlatform is false (never throws) when the
    /// site isn't this platform.
    /// </summary>
    private async Task<(bool IsPlatform, List<ChapterState> States)> TryVComicsAsync(
        string sampleChapterUrl,
        CancellationToken token)
    {
        var result = new List<ChapterState>();
        bool isPlatform = false;
        try
        {
            string? seriesUrl = DeriveVComicsSeriesUrl(sampleChapterUrl);
            if (seriesUrl == null)
                return (false, result);

            SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            string? html = await FetchHtmlAsync(settings, seriesUrl, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(html))
                return (false, result);

            // Platform fingerprint: the Astro build emits /_vcomics/ assets and
            // inlines the API host. Both absent -> not this platform.
            Match api = ApiUrlRegex.Match(html);
            if (!api.Success || html.IndexOf("/_vcomics/", StringComparison.OrdinalIgnoreCase) < 0)
                return (false, result);
            isPlatform = true;

            string? postId = FindPostId(html, seriesUrl);
            if (postId == null)
            {
                _logger.LogWarning("vcomics: no series id found on {Series}; its paid chapters can't be listed", seriesUrl);
                return (true, result);
            }

            string apiBase = api.Groups["u"].Value.TrimEnd('/');

            string endpoint = $"{apiBase}/api/chapters?postId={postId}";
            using HttpClient http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(25);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            // The API is CORS-locked to the site origin; send it so it answers.
            http.DefaultRequestHeaders.Referrer = new Uri(seriesUrl);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", OriginOf(seriesUrl));

            using HttpResponseMessage resp = await http.GetAsync(endpoint, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (true, result);

            await using Stream stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("post", out JsonElement postEl) ||
                !postEl.TryGetProperty("chapters", out JsonElement chapters) ||
                chapters.ValueKind != JsonValueKind.Array)
                return (true, result);

            foreach (JsonElement c in chapters.EnumerateArray())
            {
                bool isLocked = c.TryGetProperty("isLocked", out JsonElement locked) &&
                                locked.ValueKind == JsonValueKind.True;
                if (!TryGetDecimal(c, "number", out decimal num))
                    continue;

                string? slug = c.TryGetProperty("slug", out JsonElement s) ? s.GetString() : null;
                if (string.IsNullOrWhiteSpace(slug))
                    continue; // no slug -> no readable URL

                string title = c.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String &&
                               !string.IsNullOrWhiteSpace(t.GetString())
                    ? t.GetString()!.Trim()
                    : $"Chapter {TrimNum(num)}";

                DateTime? uploaded = null;
                if (c.TryGetProperty("createdAt", out JsonElement ca) && ca.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(ca.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out DateTime dt))
                    uploaded = dt;

                result.Add(new ChapterState(num, title, $"{seriesUrl.TrimEnd('/')}/{slug.Trim('/')}", uploaded, isLocked));
            }

            if (result.Count > 0)
                _logger.LogInformation(
                    "Locked-chapter supplement (vcomics): {Total} chapter(s) at {Series}, {Locked} still paid.",
                    result.Count, seriesUrl, result.Count(c => c.IsLocked));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "vcomics locked-chapter supplement failed for {Url}", sampleChapterUrl);
            return (isPlatform, []);
        }
        return (isPlatform, result);
    }

    /// <summary>
    /// The site's id for this series. The older (Astro) build serialises it as
    /// "postId":[0,928]; the newer one (TanStack Start: Vortex Scans, 2026-10)
    /// only as post:{id:477,slug:"…"} in its seroval payload, which the old
    /// pattern never matched, so no locked chapter there was ever listed. The
    /// newer forms are read next to THIS series' slug, so a recommended series
    /// elsewhere on the page can't be picked up instead.
    /// </summary>
    internal static string? FindPostId(string html, string seriesUrl)
    {
        Match legacy = PostIdRegex.Match(html);
        if (legacy.Success)
            return legacy.Groups["id"].Value;

        if (!Uri.TryCreate(seriesUrl, UriKind.Absolute, out Uri? uri))
            return null;
        string slug = uri.AbsolutePath.TrimEnd('/');
        slug = slug[(slug.LastIndexOf('/') + 1)..];
        if (slug.Length == 0)
            return null;

        // Both the seroval form (id:477,slug:"x") and JSON ("id":477,"slug":"x"),
        // in either order; escaped quotes flattened first.
        string flat = html.Replace("\\\"", "\"");
        string s = Regex.Escape(slug);
        foreach (string pattern in new[]
        {
            $@"\bid""?\s*:\s*(?<id>\d+)\s*,\s*""?slug""?\s*:\s*""{s}""",
            $@"""?slug""?\s*:\s*""{s}""\s*,\s*""?id""?\s*:\s*(?<id>\d+)",
        })
        {
            Match m = Regex.Match(flat, pattern);
            if (m.Success)
                return m.Groups["id"].Value;
        }
        return null;
    }

    /// <summary>
    /// {origin}/series/{slug}/chapter-N  ->  {origin}/series/{slug}
    /// (vcomics puts the chapter in its own path segment, unlike the WordPress
    /// "{slug}-chapter-N" form.)
    /// </summary>
    private static string? DeriveVComicsSeriesUrl(string chapterUrl)
    {
        if (!Uri.TryCreate(chapterUrl, UriKind.Absolute, out Uri? uri))
            return null;

        string path = uri.AbsolutePath.TrimEnd('/');
        int slash = path.LastIndexOf('/');
        if (slash <= 0)
            return null;

        // The chapter must be its own segment ("chapter-104"). Merely *containing*
        // "chapter" would also swallow the WordPress "{series-slug}-chapter-12"
        // form, whose parent path is a listing page, not a series — costing a
        // pointless (Cloudflare-challenged) fetch before the fingerprint check
        // rejects it.
        string last = path[(slash + 1)..];
        if (!last.StartsWith("chapter", StringComparison.OrdinalIgnoreCase))
            return null;

        return $"{uri.Scheme}://{uri.Authority}{path[..slash]}";
    }

    private static string OriginOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? u) ? $"{u.Scheme}://{u.Authority}" : "";

    // --- "vapi" platform (e.g. EZmanga) -------------------------------------------
    //
    // A separate JSON API host alongside the site ("ezmanga.org" -> "vapi.ezmanga.org")
    // serving /api/v1/series/{slug}/chapters. Unlike the two platforms above it
    // reports lock state as DATA rather than as a marker in the chapter title:
    //
    //   { "number": 38, "price": 100, "isFree": false, "requiresPurchase": true,
    //     "becameFreeAt": null, "createdAt": "..." }
    //
    // which is what makes proper unlock-tracking possible — these sites release a
    // chapter paid and flip it free once the next one lands (becameFreeAt), so a
    // chapter's lock state has to be re-read on every scan, not just at discovery.
    //
    // The list is PAGINATED (20/page). Reading only the first page is not merely
    // incomplete, it silently hides the newest chapters on a long series.

    /// <summary>A chapter as the source currently reports it, lock state included.</summary>
    public sealed record ChapterState(decimal Number, string Name, string Url, DateTime? Uploaded, bool IsLocked);

    /// <summary>
    /// Every chapter the source reports for this series, with its CURRENT lock
    /// state — including ones already known, so a caller can unlock what has since
    /// become free. Covers the platforms that report lock state as data (vapi,
    /// vcomics). Empty when the site is on neither. <paramref name="recheck"/>
    /// ignores a remembered "none" (the owner has a login for this source).
    /// </summary>
    public async Task<List<ChapterState>> FetchChapterStatesAsync(string sampleChapterUrl, bool recheck, CancellationToken token = default)
    {
        string host = HostOf(sampleChapterUrl);
        if (host.Length == 0)
            return [];
        Platform? known = Known(host, recheck);
        if (known is Platform.None or Platform.WordPressLock)
            return [];

        if (known is null or Platform.Vapi)
        {
            var (isVapi, states) = await TryVapiAsync(sampleChapterUrl, token).ConfigureAwait(false);
            if (isVapi)
            {
                Remember(host, Platform.Vapi);
                return states;
            }
        }
        if (known is null or Platform.VComics)
        {
            var (isVComics, states) = await TryVComicsAsync(sampleChapterUrl, token).ConfigureAwait(false);
            if (isVComics)
            {
                Remember(host, Platform.VComics);
                return states;
            }
        }
        // A remembered platform that no longer matches: the site changed. Probe
        // afresh next time; FetchLockedChaptersAsync decides on WordPress/none.
        if (known != null)
            Platforms.TryRemove(host, out _);
        return [];
    }

    private async Task<(bool IsPlatform, List<ChapterState> States)> TryVapiAsync(string sampleChapterUrl, CancellationToken token)
    {
        var result = new List<ChapterState>();
        bool isPlatform = false;
        try
        {
            if (!Uri.TryCreate(sampleChapterUrl, UriKind.Absolute, out Uri? uri))
                return (false, result);

            // /series/{slug}/chapter-N  ->  slug
            string[] parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            int si = Array.FindIndex(parts, p => p.Equals("series", StringComparison.OrdinalIgnoreCase));
            if (si < 0 || si + 1 >= parts.Length)
                return (false, result);
            string slug = parts[si + 1];

            string apiBase = $"{uri.Scheme}://vapi.{uri.Host}/api/v1";
            using HttpClient http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(25);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.Referrer = new Uri($"{uri.Scheme}://{uri.Host}/");

            for (int page = 1; page <= 50; page++)   // hard stop; a series is never 1000 pages
            {
                string url = $"{apiBase}/series/{Uri.EscapeDataString(slug)}/chapters?page={page}";
                using HttpResponseMessage resp = await http.GetAsync(url, token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    break;

                using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                    break;

                foreach (JsonElement row in data.EnumerateArray())
                {
                    // Platform fingerprint: this pair of fields is what distinguishes
                    // the API from any other /chapters endpoint that might answer.
                    if (!row.TryGetProperty("requiresPurchase", out JsonElement rp) ||
                        !row.TryGetProperty("isFree", out JsonElement free))
                        return (false, []);
                    isPlatform = true;
                    if (!TryGetDecimal(row, "number", out decimal num))
                        continue;

                    bool locked = rp.ValueKind == JsonValueKind.True && free.ValueKind != JsonValueKind.True;

                    string chapterSlug = row.TryGetProperty("slug", out JsonElement sl) && sl.ValueKind == JsonValueKind.String
                        ? sl.GetString()! : $"chapter-{TrimNum(num)}";
                    string name = row.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(t.GetString())
                        ? t.GetString()!.Trim() : $"Chapter {TrimNum(num)}";

                    DateTime? uploaded = null;
                    if (row.TryGetProperty("createdAt", out JsonElement ca) && ca.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(ca.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out DateTime dt))
                        uploaded = dt;

                    result.Add(new ChapterState(num, name,
                        $"{uri.Scheme}://{uri.Host}/series/{slug}/{chapterSlug}", uploaded, locked));
                }

                // Stop at the last page: "next" is null once there are no more.
                if (!doc.RootElement.TryGetProperty("next", out JsonElement next) || next.ValueKind == JsonValueKind.Null)
                    break;
            }

            if (result.Count > 0)
                _logger.LogInformation(
                    "Locked-chapter supplement (vapi): {Total} chapter(s) for {Slug}, {Locked} still paid.",
                    result.Count, slug, result.Count(c => c.IsLocked));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "vapi chapter-state supplement failed for {Url}", sampleChapterUrl);
            return (isPlatform, []);
        }
        return (isPlatform, result);
    }

    private static bool TryGetDecimal(JsonElement obj, string name, out decimal value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out JsonElement el))
            return false;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(el.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    /// <summary>
    /// Plain fetch first, FlareSolverr only as a fallback — these sites usually
    /// answer a normal request, so this keeps working when FlareSolverr is off.
    /// </summary>
    private async Task<string?> FetchHtmlAsync(SettingsDto settings, string url, CancellationToken token)
    {
        try
        {
            using HttpClient http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(25);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            using HttpResponseMessage resp = await http.GetAsync(url, token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch { /* fall through to FlareSolverr */ }

        if (!settings.FlareSolverrEnabled || string.IsNullOrWhiteSpace(settings.FlareSolverrUrl))
            return null;
        return await FetchViaFlareSolverrAsync(settings, url, token).ConfigureAwait(false);
    }

    /// <summary>{base}-chapter-N/  ->  {base}/  (the series landing page).</summary>
    private static string? DeriveSeriesUrl(string chapterUrl)
    {
        int i = chapterUrl.LastIndexOf("-chapter-", StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return null;
        return chapterUrl.Substring(0, i) + "/";
    }

    private static string TrimNum(decimal n) =>
        n == decimal.Truncate(n) ? ((long)n).ToString(System.Globalization.CultureInfo.InvariantCulture)
                                 : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string?> FetchViaFlareSolverrAsync(SettingsDto settings, string url, CancellationToken token)
    {
        int ms = (int)Math.Clamp(settings.FlareSolverrTimeout.TotalMilliseconds, 15000, 120000);
        var payload = new { cmd = "request.get", url, maxTimeout = ms };

        using HttpClient http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMilliseconds(ms + 15000);
        string endpoint = settings.FlareSolverrUrl.TrimEnd('/') + "/v1";

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using HttpResponseMessage resp = await http.PostAsync(endpoint, content, token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;

        await using Stream s = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(s, cancellationToken: token).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("solution", out JsonElement sol))
            return null;
        if (sol.TryGetProperty("status", out JsonElement st) && st.ValueKind == JsonValueKind.Number && st.GetInt32() >= 400)
            return null;
        return sol.TryGetProperty("response", out JsonElement r) ? r.GetString() : null;
    }
}
