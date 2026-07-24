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
/// the source's own Mihon extension doesn't parse. Many scanlation sites run the
/// WordPress "lock chapters" plugin (mangareader-style theme): every chapter is a
/// &lt;li data-num="N"&gt;, but a locked one uses a purchase-modal anchor
/// (data-bs-target="#lockedChapterModal", data-coin, data-title) with NO href, so
/// the extension — which only follows real chapter links — silently drops them.
///
/// This fetches the series page through FlareSolverr (the same Cloudflare-bypass
/// the bridge uses), parses those locked entries, and returns them as
/// <see cref="Chapter"/> rows flagged <see cref="Chapter.IsLocked"/>. They then
/// show as "locked" in the UI; the download only succeeds once the user's
/// logged-in session actually owns the chapter (i.e. they spent the coins), which
/// the existing site-login + allow-locked download path already handles.
///
/// Generic across any site using this plugin — detection is by the markup, not a
/// hardcoded host list.
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
            if (!settings.FlareSolverrEnabled || string.IsNullOrWhiteSpace(settings.FlareSolverrUrl))
                return result;

            string? seriesUrl = DeriveSeriesUrl(sampleChapterUrl);
            if (seriesUrl == null)
                return result;

            string? html = await FetchViaFlareSolverrAsync(settings, seriesUrl, token).ConfigureAwait(false);
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
