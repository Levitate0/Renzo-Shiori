using Microsoft.EntityFrameworkCore;
using RenzoBackend.Data;
using RenzoBackend.Services.Bridge;
using RenzoBackend.Services.Providers;
using RenzoBackend.Services.SiteAuth;

namespace RenzoBackend.Services.Background;

/// <summary>
/// Follows a source to its new domain.
///
/// When a site moves (violetscans.org → violetmanga.com, reimanga.com →
/// reimanga.net, …) the extension updates its base URL, but every link Renzo
/// Shiori stored before still names the old host: series pages, chapter links,
/// covers, the browse cache. Reading keeps working only while the old domain
/// redirects; covers and "open on site" break when it lapses, and paid-chapter
/// fetches look up the session for the old host and find none. On 2026-10-24
/// seven sources had moved, with ~6,700 stored links still on old hosts.
///
/// A move is recognised only on the extension's own evidence (see
/// <see cref="SourceHostsService"/>): a host counts as "old" because a version of
/// the same source id used it as its base URL. The live source must agree that
/// the new host is where it lives now, so a mirror picked in the source's own
/// settings is never overridden. Only the scheme+host of URLs on the exact old
/// host (or its www. form) is rewritten: a source id that keeps its id keeps its
/// paths, which is the extensions' own compatibility rule, and was checked
/// against the live chapter lists of all seven moved sources before this ran.
///
/// The rewrite is plain SQL REPLACE, so it never overwrites a concurrent save
/// with stale data; a save that lands in between can at worst put an old link
/// back, which the next pass fixes. Idempotent, so it simply runs periodically.
/// </summary>
public class SourceMoveService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SourceMoveService> _logger;

    /// <summary>After startup has loaded the extensions and restored site logins.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(4);

    /// <summary>Extensions auto-update in the background, so look again regularly.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>Rows per statement on the large browse cache, so downloads aren't held up behind one long write.</summary>
    private const int BatchSize = 2000;

    public SourceMoveService(IServiceScopeFactory scopeFactory, ILogger<SourceMoveService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Source address check failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass over every installed source. Returns how many sources were moved.</summary>
    public async Task<int> RunOnceAsync(CancellationToken token)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SourceHostsService hosts = scope.ServiceProvider.GetRequiredService<SourceHostsService>();
        MihonBridgeService mihon = scope.ServiceProvider.GetRequiredService<MihonBridgeService>();

        // Library and browse-cache provider ids, grouped by source id. The
        // stored id is "package|sourceId".
        List<string> ids = await db.SeriesProviders.AsNoTracking()
            .Where(p => p.MihonProviderId != null).Select(p => p.MihonProviderId!).Distinct()
            .ToListAsync(token).ConfigureAwait(false);
        ids.AddRange(await db.LatestSeries.AsNoTracking()
            .Select(p => p.MihonProviderId).Distinct()
            .ToListAsync(token).ConfigureAwait(false));
        Dictionary<long, List<string>> bySource = ids
            .Where(i => !string.IsNullOrEmpty(i))
            .Distinct()
            .Select(i => (Id: i, Source: long.TryParse(i[(i.LastIndexOf('|') + 1)..], out long s) ? s : 0))
            .Where(x => x.Source != 0)
            .GroupBy(x => x.Source)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());

        int moved = 0;
        foreach ((long sourceId, Uri current) in hosts.AllCurrent())
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyCollection<string> previous = hosts.PreviousHosts(sourceId);
            if (previous.Count == 0 || !bySource.TryGetValue(sourceId, out List<string>? providerIds))
                continue;

            string newHost = current.Host.ToLowerInvariant();
            List<string> stale = new();
            foreach (string old in previous)
                if (await HasLinksOnAsync(db, providerIds, old, token).ConfigureAwait(false))
                    stale.Add(old);
            if (stale.Count == 0)
                continue;

            // The live source has the last word: its base URL can come from its
            // own settings (a mirror the user picked), which the repository file
            // doesn't record.
            string? live = await LiveHostAsync(mihon, providerIds, token).ConfigureAwait(false);
            if (live == null || SourceHostsService.Normalize(live) != SourceHostsService.Normalize(newHost))
            {
                _logger.LogInformation(
                    "Source {Source} looks moved to {Host}, but the loaded source says {Live}; leaving its links alone.",
                    sourceId, newHost, live ?? "(not loaded)");
                continue;
            }

            string provider = await db.SeriesProviders.AsNoTracking()
                .Where(p => providerIds.Contains(p.MihonProviderId!)).Select(p => p.Provider)
                .FirstOrDefaultAsync(token).ConfigureAwait(false)
                ?? await db.LatestSeries.AsNoTracking()
                    .Where(p => providerIds.Contains(p.MihonProviderId)).Select(p => p.Provider)
                    .FirstOrDefaultAsync(token).ConfigureAwait(false)
                ?? sourceId.ToString();

            foreach (string old in stale)
            {
                (int library, int browse, int covers) = await RewriteAsync(db, providerIds, old, newHost, token).ConfigureAwait(false);
                _logger.LogInformation(
                    "{Provider} moved from {Old} to {New}: updated the links of {Library} library entries, {Browse} browse entries and {Covers} covers.",
                    provider, old, newHost, library, browse, covers);
            }
            AppDbContext.BumpLibraryVersion();
            moved++;

            // Its login was set up for the old host: sign in again on the new one.
            try
            {
                SiteAuthService auth = scope.ServiceProvider.GetRequiredService<SiteAuthService>();
                await auth.OnSourceMovedAsync(provider, newHost, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't renew the site login for {Provider} after its move", provider);
            }
        }
        return moved;
    }

    private static async Task<string?> LiveHostAsync(MihonBridgeService mihon, List<string> providerIds, CancellationToken token)
    {
        foreach (string id in providerIds)
        {
            try
            {
                var src = await mihon.SourceFromProviderIdAsync(id, token).ConfigureAwait(false);
                if (Uri.TryCreate(src.BaseUrl, UriKind.Absolute, out Uri? u) && !string.IsNullOrEmpty(u.Host))
                    return u.Host;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { /* this package id is gone (an older install); try the next */ }
        }
        return null;
    }

    /// <summary>The two spellings a stored link to <paramref name="host"/> can take.</summary>
    private static string[] Variants(string host) => new[] { host, "www." + host };

    /// <summary>"…://host/…" inside a value, or "…://host" closing a JSON string.</summary>
    private static List<(string From, string To)> Pairs(string oldHost, string newHost)
    {
        var pairs = new List<(string, string)>();
        foreach (string v in Variants(oldHost))
        {
            pairs.Add(("://" + v + "/", "://" + newHost + "/"));
            pairs.Add(("://" + v + "\"", "://" + newHost + "\""));
        }
        return pairs;
    }

    /// <summary>
    /// GLOB, not LIKE: LIKE ignores case and replace() doesn't, so a row in odd
    /// case would match forever without ever changing.
    /// </summary>
    private static List<string> Likes(string oldHost) =>
        Variants(oldHost).SelectMany(v => new[] { "*://" + v + "/*", "*://" + v + "\"*" }).ToList();

    private static async Task<bool> HasLinksOnAsync(AppDbContext db, List<string> providerIds, string oldHost, CancellationToken token)
    {
        foreach (string like in Likes(oldHost))
        {
            foreach (string id in providerIds)
            {
                bool any = await db.Database.SqlQueryRaw<int>(
                        "SELECT 1 AS Value FROM SeriesProviders WHERE MihonProviderId = {0} AND (Url GLOB {1} OR ThumbnailUrl GLOB {1} OR Chapters GLOB {1} OR BridgeItemInfo GLOB {1}) LIMIT 1",
                        id, like)
                    .AnyAsync(token).ConfigureAwait(false)
                    || await db.Database.SqlQueryRaw<int>(
                        "SELECT 1 AS Value FROM LatestSeries WHERE MihonProviderId = {0} AND (Url GLOB {1} OR ThumbnailUrl GLOB {1}) LIMIT 1",
                        id, like)
                    .AnyAsync(token).ConfigureAwait(false);
                if (any)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Rewrites every stored link on <paramref name="oldHost"/> to
    /// <paramref name="newHost"/> for these provider ids. Returns the rows changed
    /// in the library, the browse cache and the series covers.
    /// </summary>
    private async Task<(int Library, int Browse, int Covers)> RewriteAsync(
        AppDbContext db, List<string> providerIds, string oldHost, string newHost, CancellationToken token)
    {
        List<(string From, string To)> pairs = Pairs(oldHost, newHost);
        List<string> likes = Likes(oldHost);

        // replace(replace(col, {p0}, {p1}), {p2}, {p3})… with the pairs as
        // parameters; the GLOB patterns follow them.
        string Nested(string column)
        {
            string expr = column;
            for (int i = 0; i < pairs.Count; i++)
                expr = $"replace({expr}, {{{i * 2}}}, {{{i * 2 + 1}}})";
            return expr;
        }
        int likeBase = pairs.Count * 2;
        string Matches(params string[] columns) =>
            "(" + string.Join(" OR ", columns.SelectMany(c => likes.Select((_, i) => $"{c} GLOB {{{likeBase + i}}}"))) + ")";
        int idIndex = likeBase + likes.Count;
        object[] Args(string providerId) =>
            pairs.SelectMany(p => new object[] { p.From, p.To })
                .Concat(likes)
                .Append(providerId)
                .ToArray();

        int library = 0, browse = 0, covers = 0;
        foreach (string id in providerIds)
        {
            token.ThrowIfCancellationRequested();
            object[] args = Args(id);

            // Covers first: they are found through the library rows, by the
            // provider's id, before those rows stop matching.
            covers += await db.Database.ExecuteSqlRawAsync(
                $"UPDATE Series SET ThumbnailUrl = {Nested("ThumbnailUrl")} " +
                $"WHERE {Matches("ThumbnailUrl")} AND Id IN (SELECT SeriesId FROM SeriesProviders WHERE MihonProviderId = {{{idIndex}}})",
                args, token).ConfigureAwait(false);

            library += await db.Database.ExecuteSqlRawAsync(
                $"UPDATE SeriesProviders SET Url = {Nested("Url")}, ThumbnailUrl = {Nested("ThumbnailUrl")}, " +
                $"Chapters = {Nested("Chapters")}, BridgeItemInfo = {Nested("BridgeItemInfo")} " +
                $"WHERE MihonProviderId = {{{idIndex}}} AND {Matches("Url", "ThumbnailUrl", "Chapters", "BridgeItemInfo")}",
                args, token).ConfigureAwait(false);

            // The browse cache is large: batches, each its own short write. A
            // rewritten row stops matching, so each batch takes the next ones;
            // the cap is only a backstop.
            for (int round = 0; round < 1000; round++)
            {
                int n = await db.Database.ExecuteSqlRawAsync(
                    $"UPDATE LatestSeries SET Url = {Nested("Url")}, ThumbnailUrl = {Nested("ThumbnailUrl")}, " +
                    $"Chapters = {Nested("Chapters")}, BridgeItemInfo = {Nested("BridgeItemInfo")} " +
                    $"WHERE rowid IN (SELECT rowid FROM LatestSeries WHERE MihonProviderId = {{{idIndex}}} " +
                    $"AND {Matches("Url", "ThumbnailUrl", "Chapters", "BridgeItemInfo")} LIMIT {BatchSize})",
                    args, token).ConfigureAwait(false);
                browse += n;
                if (n < BatchSize)
                    break;
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
        return (library, browse, covers);
    }
}
