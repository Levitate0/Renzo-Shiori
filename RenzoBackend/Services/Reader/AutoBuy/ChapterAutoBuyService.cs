using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mihon.ExtensionsBridge.Core.Runtime.Sidecar;
using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Services.SiteAuth;

namespace RenzoBackend.Services.Reader.AutoBuy;

public sealed class AutoBuyResultDto
{
    /// <summary>"running" | "bought" | "failed" | "off" ("dry-run" for the diagnostic run).</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "failed";
    /// <summary>On failure: "no-login" | "signed-out" | "no-coins" | "no-button" | "not-confirmed" | "not-owner" | "unavailable".</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("price")] public decimal? Price { get; set; }
    [JsonPropertyName("balance")] public decimal? Balance { get; set; }
}

/// <summary>
/// Buys a paid chapter the owner has just reached in the reader, when auto-buy
/// is on for the series (or for the account, which a series can override).
///
/// Nothing here knows a site. The chapter page is opened in the extension
/// host's browser, which shares the extensions' cookie store and so the site
/// login; autobuy.js then does what a person would: finds the site's own
/// unlock/buy button for this one chapter, reads the price (and the balance,
/// when the page shows it), clicks, and confirms the site's dialog. A site
/// that keeps its sign-in outside cookies is signed in by filling its own form
/// with the saved login, unless a captcha is on it. Whether it worked is then
/// decided the same way as everywhere else: the reader asks the source for the
/// pages again, and pages coming back is the purchase.
///
/// Every requirement that isn't met comes back as a reason and a sentence for
/// the manual purchase screen.
/// </summary>
public class ChapterAutoBuyService
{
    private static readonly Lazy<string> Script = new(() =>
    {
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("autobuy.js")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });

    // One purchase at a time across the server, and a failed chapter isn't
    // retried for a while: reopening it shows the same answer instead of
    // another browser run.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<(Guid, decimal), (DateTime At, AutoBuyResultDto Result)> RecentFailures = new();
    private static readonly TimeSpan FailureMemory = TimeSpan.FromMinutes(10);

    private readonly AppDbContext _db;
    private readonly ReaderPreviewService _reader;
    private readonly SiteCredentialProtector _protector;
    private readonly CoinSiteRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly ILogger<ChapterAutoBuyService> _logger;

    public ChapterAutoBuyService(AppDbContext db, ReaderPreviewService reader, SiteCredentialProtector protector,
        CoinSiteRegistry registry, IServiceProvider services, ILogger<ChapterAutoBuyService> logger)
    {
        _db = db;
        _reader = reader;
        _protector = protector;
        _registry = registry;
        _services = services;
        _logger = logger;
    }

    /// <summary>What a purchase needs once the checks pass.</summary>
    private sealed record Plan(SeriesEntity Series, Guid OwnerId, SeriesProviderEntity Source, Chapter Chapter,
        SiteCredentialEntity Cred, SidecarProcessManager Sidecar);

    /// <summary>
    /// The answer that needs no browser, or null when there is a purchase to try:
    /// auto-buy off, not the owner, a failure remembered, no source to buy from,
    /// no login for it, no browser. The reader gets these at once (AutoBuyRunner),
    /// so it never shows "buying" for a chapter that won't be bought.
    /// </summary>
    public async Task<AutoBuyResultDto?> PrecheckAsync(Guid seriesId, decimal chapterNumber, UserEntity? requester, CancellationToken token, bool dryRun = false) =>
        (await PlanAsync(seriesId, chapterNumber, requester, dryRun, token).ConfigureAwait(false)).Answer;

    public async Task<AutoBuyResultDto> TryBuyAsync(Guid seriesId, decimal chapterNumber, UserEntity? requester, CancellationToken token, bool dryRun = false)
    {
        var (answer, plan) = await PlanAsync(seriesId, chapterNumber, requester, dryRun, token).ConfigureAwait(false);
        if (answer != null || plan == null)
            return answer ?? Fail("unavailable", "Couldn't work out where to buy this chapter.");

        if (!await Gate.WaitAsync(TimeSpan.FromMinutes(3), token).ConfigureAwait(false))
            return Fail("unavailable", "Another chapter is being bought right now. Try again in a moment.", plan.Source.Provider);
        try
        {
            AutoBuyResultDto result = await BuyAsync(plan.Sidecar, seriesId, chapterNumber, plan.OwnerId, plan.Source.Provider, plan.Chapter.Url!, plan.Cred, dryRun, token)
                .ConfigureAwait(false);
            _logger.LogInformation("Auto-buy {Series} ch {Chapter} on {Provider}: {Status} {Reason}",
                plan.Series.Title, chapterNumber, plan.Source.Provider, result.Status, result.Reason ?? "");
            return result.Status == "bought" || dryRun ? result : Remember(seriesId, chapterNumber, result);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<(AutoBuyResultDto? Answer, Plan? Plan)> PlanAsync(Guid seriesId, decimal chapterNumber, UserEntity? requester, bool dryRun, CancellationToken token)
    {
        SeriesEntity? series = await _db.Series.AsNoTracking().Include(s => s.Sources)
            .FirstOrDefaultAsync(s => s.Id == seriesId, token).ConfigureAwait(false);
        if (series == null)
            return (Fail("unavailable", "Series not found."), null);

        // Coins and site logins belong to the series' owner.
        Guid ownerId = series.OwnerId;
        if (ownerId != Guid.Empty && requester != null && requester.Id != ownerId)
            return (new AutoBuyResultDto { Status = "off", Reason = "not-owner" }, null);
        UserEntity? owner = ownerId == Guid.Empty ? requester
            : await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == ownerId, token).ConfigureAwait(false);
        bool enabled = series.AutoBuyPaidChapters ?? owner?.GetAutoBuyPaidChapters() ?? false;
        if (!enabled && !dryRun)
            return (new AutoBuyResultDto { Status = "off" }, null);

        if (!dryRun && RecentFailures.TryGetValue((seriesId, chapterNumber), out var recent) && DateTime.UtcNow - recent.At < FailureMemory)
            return (recent.Result, null);

        // Sources holding this chapter, paid ones first, then by priority.
        var holders = series.Sources
            .Where(p => !p.IsDisabled && !p.IsUninstalled && !p.IsLocal)
            .Select(p => (p, c: p.Chapters.FirstOrDefault(c => c.Number == chapterNumber && !c.IsDeleted
                                                               && Uri.IsWellFormedUriString(c.Url, UriKind.Absolute))))
            .Where(x => x.c != null)
            .OrderByDescending(x => x.c!.IsLocked || ModelExtensions.IsLockedChapterName(x.c.Name))
            .ThenBy(x => x.p.Priority)
            .ToList();
        if (holders.Count == 0)
            return (Remember(seriesId, chapterNumber, Fail("unavailable", "None of this series' sources has a link for this chapter to buy it from.")), null);

        List<SiteCredentialEntity> creds = await _db.SiteCredentials.AsNoTracking()
            .Where(c => (c.Status == "ok" || c.Status == "manual_cookie") && (ownerId == Guid.Empty || c.UserId == ownerId))
            .ToListAsync(token).ConfigureAwait(false);
        var pick = holders.Select(h => (h.p, h.c, cred: creds.FirstOrDefault(c => c.Provider.Equals(h.p.Provider, StringComparison.OrdinalIgnoreCase))))
            .FirstOrDefault(x => x.cred != null);
        if (pick.cred == null)
        {
            string site = holders[0].p.Provider;
            return (Remember(seriesId, chapterNumber, Fail("no-login",
                $"You aren't signed in to {site}. Add your {site} login under Settings → Site logins so chapters can be bought there.", site)), null);
        }

        if (_services.GetService(typeof(SidecarProcessManager)) is not SidecarProcessManager sidecar)
            return (Fail("unavailable", "Auto-buy needs the extension host's browser, which isn't running.", pick.p.Provider), null);

        return (null, new Plan(series, ownerId, pick.p, pick.c!, pick.cred, sidecar));
    }

    private async Task<AutoBuyResultDto> BuyAsync(SidecarProcessManager sidecar, Guid seriesId, decimal chapterNumber, Guid ownerId,
        string site, string url, SiteCredentialEntity cred, bool dryRun, CancellationToken token)
    {
        PageRun run = await RunAsync(sidecar, url, login: null, dryRun, token).ConfigureAwait(false);

        // Signed out in the browser although the server holds a session: sign
        // the browser in with the saved login, then try once more.
        if (run.Inspect is { SignedOut: true, Clicked: false })
        {
            string? password = _protector.TryDecrypt(cred.EncryptedPassword);
            CoinSiteDefinition? def = await _registry.GetDefinitionAsync(site, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(cred.Username) || def is { CaptchaRequired: true })
                return Fail("signed-out",
                    $"Renzo Shiori's browser isn't signed in to {site}, and {site} needs a person to sign in (a pasted cookie or a captcha), so it can't buy there.", site);
            run = await RunAsync(sidecar, url, login: (cred.Username, password), dryRun, token).ConfigureAwait(false);
            if (run.LoginOutcome is "captcha")
                return Fail("signed-out", $"{site}'s sign-in form has a captcha, so Renzo Shiori's browser can't sign in to buy this chapter.", site);
        }

        PageInspect? i = run.Inspect;
        if (i == null)
            return Fail("unavailable", $"Couldn't open the chapter on {site}.", site);

        // Dry run (diagnostics): everything up to the click, reported.
        if (dryRun)
            return new AutoBuyResultDto
            {
                Status = "dry-run", Provider = site, Price = i.Price, Balance = i.Balance,
                Reason = i.SignedOut ? "signed-out" : i.Buttons.Count == 0 ? (i.Readable ? "readable" : "no-button") : null,
                Message = $"Buttons: [{string.Join(" | ", i.Buttons)}]; signed out: {i.SignedOut}; readable: {i.Readable}; login: {run.LoginOutcome ?? "not needed"}",
            };

        if (!i.Clicked)
        {
            if (i.SignedOut)
                return Fail("signed-out", $"Renzo Shiori's browser couldn't sign in to {site} with your saved login, so it can't buy this chapter.", site);
            if (i.Price != null && i.Balance != null && i.Balance < i.Price)
                return Fail("no-coins", $"The account linked to {site} does not have enough coins to buy this chapter (it costs {i.Price:0.##}, the account has {i.Balance:0.##}).",
                    site, i.Price, i.Balance);
            // No button but the page reads: maybe owned already. The recheck below decides.
            if (!i.Readable)
                return Fail("no-button", $"Couldn't find a button on {site}'s chapter page to buy this chapter with coins, so it has to be bought by hand.", site);
        }

        // The definitive test: does the source hand over the pages now?
        PreviewPagesDto? pages = null;
        try
        {
            pages = await _reader.GetLibraryStreamPagesAsync(seriesId, chapterNumber, ownerId == Guid.Empty ? null : ownerId,
                forceRefresh: true, refreshChapterList: true, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auto-buy recheck failed for {SeriesId} ch {Chapter}", seriesId, chapterNumber);
        }
        if (pages is { PageCount: > 0 })
            return new AutoBuyResultDto { Status = "bought", Provider = site, Price = i.Price, Balance = i.Balance };

        if (run.NoCoins)
            return Fail("no-coins", $"The account linked to {site} does not have enough coins to buy this chapter." +
                                    (string.IsNullOrEmpty(run.SiteText) ? "" : $" {site} said: \"{run.SiteText}\""), site, i.Price, i.Balance);
        if (run.SignedOutAfter)
            return Fail("signed-out", $"{site} asked to sign in when the chapter was bought, so the login Renzo Shiori holds isn't accepted there any more.", site);
        // What the page did after the click, so a site's confirm box can be diagnosed from the log.
        _logger.LogInformation("Auto-buy on {Site} not confirmed: pressed \"{Button}\", confirm step: {Confirm}, outcome step: {Outcome}",
            site, i.Buttons.FirstOrDefault(), run.ConfirmStep ?? "no confirm box answered", run.OutcomeStep ?? "the button was still there");
        return Fail("not-confirmed", i.Clicked
            ? $"Pressed \"{i.Buttons.FirstOrDefault()}\" on {site}, but the chapter still doesn't open. {site} may need a step only a person can do."
            : $"The chapter page on {site} opened without a buy button, yet the chapter still doesn't load here.", site, i.Price, i.Balance);
    }

    private sealed class PageInspect
    {
        [JsonPropertyName("readable")] public bool Readable { get; set; }
        [JsonPropertyName("signedOut")] public bool SignedOut { get; set; }
        [JsonPropertyName("buttons")] public List<string> Buttons { get; set; } = [];
        [JsonPropertyName("price")] public decimal? Price { get; set; }
        [JsonPropertyName("balance")] public decimal? Balance { get; set; }
        [JsonPropertyName("clicked")] public bool Clicked { get; set; }
    }

    private sealed record PageRun(PageInspect? Inspect, bool NoCoins, string? SiteText, bool SignedOutAfter, string? LoginOutcome,
        string? ConfirmStep, string? OutcomeStep);

    private static string Call(string fn, params object?[] args) =>
        Script.Value + "\n;window." + fn + "(" + string.Join(",", args.Select(a => JsonSerializer.Serialize(a))) + ")";

    private static SidecarClient.PageScriptStep Ready() =>
        new("document.readyState=='complete'&&!!document.body&&document.body.innerText.length>200", 30_000);

    private static async Task<PageRun> RunAsync(SidecarProcessManager sidecar, string url, (string User, string Pass)? login, bool dryRun, CancellationToken token)
    {
        var steps = new List<SidecarClient.PageScriptStep>();
        int inspectAt;
        if (login is { } l)
        {
            steps.Add(Ready());
            steps.Add(new("", 3_000));
            steps.Add(new(Call("__rzLogin", "open"), 2_000));
            steps.Add(new(Call("__rzLogin", "form"), 20_000));
            steps.Add(new(Call("__rzLogin", "submit", l.User, l.Pass), 2_000));
            steps.Add(new(Call("__rzLogin", "done"), 15_000));
            steps.Add(new("", 3_000));
            steps.Add(new("location.href=" + JsonSerializer.Serialize(url) + ";true", 2_000));
            steps.Add(new("", 2_000));
        }
        steps.Add(Ready());
        steps.Add(new("", 4_000));
        inspectAt = steps.Count;
        steps.Add(new(Call("__rzStep", "inspect", dryRun), 1_000, 200));
        if (!dryRun)
        {
            steps.Add(new(Call("__rzStep", "confirm"), 8_000));
            steps.Add(new(Call("__rzStep", "outcome"), 20_000, 1_500));
        }

        List<string?> r = await sidecar.Client.RunPageScriptAsync(url, null, steps, token).ConfigureAwait(false);
        string? Result(int idx) => idx < r.Count ? Unwrap(r[idx]) : null;

        PageInspect? inspect = null;
        try
        {
            string? json = Result(inspectAt);
            if (!string.IsNullOrEmpty(json))
                inspect = JsonSerializer.Deserialize<PageInspect>(json);
        }
        catch (JsonException) { }

        bool noCoins = false, signedOutAfter = false;
        string? siteText = null;
        foreach (string? o in new[] { Result(inspectAt + 1), Result(inspectAt + 2) })
        {
            if (o == "signedOut") signedOutAfter = true;
            else if (o != null && o.StartsWith('{'))
            {
                try
                {
                    using JsonDocument d = JsonDocument.Parse(o);
                    if (d.RootElement.TryGetProperty("noCoins", out var nc) && nc.ValueKind == JsonValueKind.True)
                    {
                        noCoins = true;
                        siteText = d.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
                    }
                }
                catch (JsonException) { }
            }
        }
        string? loginOutcome = login != null ? Result(4) : null;
        return new PageRun(inspect, noCoins, siteText?.Length > 200 ? siteText[..200] : siteText, signedOutAfter, loginOutcome,
            Result(inspectAt + 1), Result(inspectAt + 2));
    }

    /// <summary>evaluateJavascript hands back JSON: a returned string arrives quoted.</summary>
    private static string? Unwrap(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null") return null;
        if (raw.StartsWith('"'))
        {
            try { return JsonSerializer.Deserialize<string>(raw); }
            catch (JsonException) { return raw; }
        }
        return raw;
    }

    private static AutoBuyResultDto Fail(string reason, string message, string? site = null, decimal? price = null, decimal? balance = null) =>
        new() { Status = "failed", Reason = reason, Message = message, Provider = site, Price = price, Balance = balance };

    private static AutoBuyResultDto Remember(Guid seriesId, decimal chapter, AutoBuyResultDto r)
    {
        if (r.Status == "failed" && r.Reason is not ("unavailable" or "no-login"))
            RecentFailures[(seriesId, chapter)] = (DateTime.UtcNow, r);
        return r;
    }
}
