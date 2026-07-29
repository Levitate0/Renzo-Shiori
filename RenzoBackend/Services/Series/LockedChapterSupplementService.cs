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
/// the source's own Mihon extension doesn't parse. Two platforms are recognised,
/// each detected by its own markup — never by a hardcoded host list:
///
///  1. The WordPress "lock chapters" plugin (mangareader-style theme): every
///     chapter is a &lt;li data-num="N"&gt;, but a locked one uses a purchase-modal
///     anchor (data-bs-target="#lockedChapterModal", data-coin, data-title) with
///     NO href, so the extension — which only follows real chapter links —
///     silently drops them. e.g. Violet Scans.
///
///  2. The "vcomics" Astro platform (e.g. Magus Manga): the page is a static
///     shell whose chapter list is hydrated client-side from a separate JSON API
///     (PUBLIC_API_URL -> /api/chapters?postId=N). Locked chapters carry
///     isLocked/price/isAccessible there. The extension only sees the server-
///     rendered free chapters, so the coin-gated ones never appear at all — and
///     none of the WordPress markers exist on these sites, so strategy 1 finds
///     nothing.
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

    // <li data-num="104"> ... </li>  — one per chapter (free or locked).
    private static readonly Regex LiRegex = new(
        @"<li[^>]*\bdata-num=""(?<num>[0-9]+(?:\.[0-9]+)?)""[^>]*>(?<body>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CoinRegex = new(@"data-coin=""(?<coin>[0-9]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitleAttrRegex = new(@"data-title=""(?<t>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DateRegex = new(@"class=""chapterdate""[^>]*>\s*(?<d>[^<]+)<", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns the coin-gated chapters found on the series page that aren't in
    /// <paramref name="existingNumbers"/>. Never throws — returns an empty list on
    /// any failure (missing FlareSolverr, non-lock-plugin site, parse miss, …).
    /// </summary>
    public async Task<List<Chapter>> FetchLockedChaptersAsync(
        string sampleChapterUrl,
        IReadOnlyCollection<decimal> existingNumbers,
        CancellationToken token = default)
    {
        var result = new List<Chapter>();
        try
        {
            if (string.IsNullOrWhiteSpace(sampleChapterUrl))
                return result;

            SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);

            // vcomics/Astro sites serve plain (usually un-challenged) HTML + a JSON
            // API, so this path works even with FlareSolverr switched off — try it
            // first and only fall through when the site isn't that platform.
            List<Chapter> viaApi = await TryVComicsAsync(sampleChapterUrl, existingNumbers, settings, token).ConfigureAwait(false);
            if (viaApi.Count > 0)
                return viaApi;

            // WordPress path. Fetched plain-first with a FlareSolverr fallback
            // (rather than requiring FlareSolverr up front) so a gated chapter is
            // always at least ATTEMPTED — the old hard gate meant that with
            // FlareSolverr off the paid chapters were silently dropped instead.
            if (sampleChapterUrl.IndexOf("-chapter-", StringComparison.OrdinalIgnoreCase) < 0)
                return result;

            string? seriesUrl = DeriveSeriesUrl(sampleChapterUrl);
            if (seriesUrl == null)
                return result;

            string? html = await FetchHtmlAsync(settings, seriesUrl, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(html) || html.IndexOf("lockedChapterModal", StringComparison.OrdinalIgnoreCase) < 0)
                return result; // not a lock-chapters site, or nothing gated

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
    /// Locked chapters on a vcomics/Astro site. The chapter list isn't in the HTML
    /// at all — the shell hydrates it from {api}/api/chapters?postId=N, which
    /// reports isLocked/price/isAccessible per chapter. Returns empty (never
    /// throws) when the site isn't this platform.
    /// </summary>
    private async Task<List<Chapter>> TryVComicsAsync(
        string sampleChapterUrl,
        IReadOnlyCollection<decimal> existingNumbers,
        SettingsDto settings,
        CancellationToken token)
    {
        var result = new List<Chapter>();
        try
        {
            string? seriesUrl = DeriveVComicsSeriesUrl(sampleChapterUrl);
            if (seriesUrl == null)
                return result;

            string? html = await FetchHtmlAsync(settings, seriesUrl, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(html))
                return result;

            // Platform fingerprint: the Astro build emits /_vcomics/ assets and
            // inlines the API host. Both absent -> not this platform.
            Match api = ApiUrlRegex.Match(html);
            if (!api.Success || html.IndexOf("/_vcomics/", StringComparison.OrdinalIgnoreCase) < 0)
                return result;

            Match post = PostIdRegex.Match(html);
            if (!post.Success)
                return result;

            string apiBase = api.Groups["u"].Value.TrimEnd('/');
            string postId = post.Groups["id"].Value;

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
                return result;

            await using Stream stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("post", out JsonElement postEl) ||
                !postEl.TryGetProperty("chapters", out JsonElement chapters) ||
                chapters.ValueKind != JsonValueKind.Array)
                return result;

            var seen = new HashSet<decimal>(existingNumbers);
            foreach (JsonElement c in chapters.EnumerateArray())
            {
                // Only supplement what the extension can't see: gated chapters.
                if (!(c.TryGetProperty("isLocked", out JsonElement locked) &&
                      locked.ValueKind == JsonValueKind.True))
                    continue;

                if (!TryGetDecimal(c, "number", out decimal num))
                    continue;
                if (!seen.Add(num))
                    continue; // extension already listed it

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

                result.Add(new Chapter
                {
                    Number = num,
                    Name = title,
                    Url = $"{seriesUrl.TrimEnd('/')}/{slug.Trim('/')}",
                    ProviderUploadDate = uploaded,
                    DateFetched = DateTime.UtcNow,
                    IsLocked = true,
                    ShouldDownload = false, // don't auto-queue a coin-gated chapter
                    IsDeleted = false,
                });
            }

            if (result.Count > 0)
                _logger.LogInformation(
                    "Locked-chapter supplement (vcomics): found {Count} coin-gated chapter(s) at {Series}",
                    result.Count, seriesUrl);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "vcomics locked-chapter supplement failed for {Url}", sampleChapterUrl);
        }
        return result;
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
