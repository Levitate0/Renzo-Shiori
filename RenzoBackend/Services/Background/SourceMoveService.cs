using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Bridge;
using RenzoBackend.Services.Providers;
using RenzoBackend.Services.Series;
using RenzoBackend.Services.Settings;
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
///
/// Also follows a source to its new id. An extension can change a source's id
/// (a new versionId when the site changed too much to keep its links: Valir
/// Scans on 2026-08-28, Asmodeus Scans on 2026-10-15), and every library row on
/// the old id then goes uninstalled and stops updating, silently; the host pass
/// never sees it, since it follows one id. See <see cref="SourceIdChanges"/> for
/// how a change is recognised and FollowIdChangesAsync for what is done about
/// it. Off by default: "SourceIdFollow": "apply" in appsettings turns it on,
/// otherwise each pass only logs what it would do.
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

    /// <summary>
    /// One pass over every installed source: host moves, then id changes, each
    /// on its own so a failure in one never stops the other. Returns how many
    /// sources were moved to a new host.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken token)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        int moved = 0;
        try
        {
            moved = await MoveHostsAsync(scope, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Source address check failed");
        }
        try
        {
            await FollowIdChangesAsync(scope, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Source id check failed");
        }
        return moved;
    }

    private async Task<int> MoveHostsAsync(IServiceScope scope, CancellationToken token)
    {
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

    // ----- id changes -----

    /// <summary>Library rows looked at per pass: each can cost a few calls to the source.</summary>
    private const int RowsPerPass = 50;

    /// <summary>The row left the old id meanwhile (re-pointed, removed): nothing to remember.</summary>
    private const string Gone = "gone";

    /// <summary>
    /// Follows sources an extension update gave a new id (SourceIdChanges). The
    /// users who had the old id enabled get the new one, and each library row on
    /// the old id is re-pointed in place at its series on the new id once that is
    /// found with confidence (SourceMigrationService.FindReplacementAsync and
    /// RepointAsync). "SourceIdFollow" in appsettings: "log" (the default) reads
    /// and measures everything and changes nothing; "apply" does it.
    ///
    /// A re-pointed row is no longer on the old id, so success needs no record.
    /// Failures are kept in source-id-follow.local.json so a row is not searched
    /// again every pass: unreachable backs off, the rest are tried a week apart
    /// and after three tries left to the owner, with one warning.
    /// </summary>
    private async Task FollowIdChangesAsync(IServiceScope scope, CancellationToken token)
    {
        IConfiguration config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        MihonBridgeService mihon = scope.ServiceProvider.GetRequiredService<MihonBridgeService>();

        List<ProviderStorageEntity> providers = await db.Providers.AsNoTracking().ToListAsync(token).ConfigureAwait(false);
        List<SourceIdChange> changes = SourceIdChanges.Detect(providers, mihon.ListExtensions(), mihon.ListOnlineRepositories());
        if (changes.Count == 0)
            return;

        bool apply = string.Equals(config["SourceIdFollow"], "apply", StringComparison.OrdinalIgnoreCase);
        string runtimeDir = config["runtimeDirectory"] ?? ".";
        string stateFile = Path.Combine(runtimeDir, "source-id-follow.local.json");
        string undoFile = Path.Combine(runtimeDir, "source-id-follow.undo.jsonl");
        Dictionary<string, SourceIdChanges.FollowState> state = ReadFollowState(stateFile);
        bool stateChanged = false;
        int budget = RowsPerPass;

        foreach (SourceIdChange change in changes)
        {
            token.ThrowIfCancellationRequested();
            var rows = await db.SeriesProviders.AsNoTracking()
                .Where(p => p.MihonProviderId == change.OldProviderId && !p.IsUnknown && p.MihonId != null && p.MihonId != "")
                .OrderBy(p => p.SeriesId)
                .Select(p => new { p.Id, p.SeriesId })
                .ToListAsync(token).ConfigureAwait(false);
            List<Guid> users = await UsersToCarryAsync(db, change, token).ConfigureAwait(false);
            // Followed already: the old id stays in the extension's history, so say nothing.
            if (rows.Count == 0 && users.Count == 0)
                continue;

            string versions = Versions(change);
            _logger.LogInformation("{Provider} ({Lang}) has a new source id: {Old} → {New} ({Versions}); {Rows} library rows still on the old id{Mode}.",
                change.Name, change.Language, change.OldSourceId, change.NewSourceId, versions, rows.Count,
                apply ? "" : "; SourceIdFollow is \"log\", so nothing is changed");
            await CarryUserSourcesAsync(db, change, users, apply, token).ConfigureAwait(false);

            ProviderStorageEntity target = providers.First(p => p.MihonProviderId == change.NewProviderId);
            foreach (var r in rows)
            {
                string key = r.Id.ToString();
                state.TryGetValue(key, out SourceIdChanges.FollowState? before);
                if (!SourceIdChanges.Due(before, change, DateTime.UtcNow))
                    continue;
                if (budget == 0)
                    break;
                budget--;

                string status;
                try
                {
                    status = await FollowRowAsync(scope, change, target, r.SeriesId, r.Id, versions, apply, undoFile, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Following {Provider} to its new id failed for series {Series}", change.Name, r.SeriesId);
                    status = SourceIdChanges.Unreachable;
                }
                if (status is SourceIdChanges.Matched or Gone)
                {
                    stateChanged |= state.Remove(key);
                    continue;
                }
                SourceIdChanges.FollowState after = SourceIdChanges.Next(before, change, status, DateTime.UtcNow);
                if (after.Status == SourceIdChanges.Manual)
                    _logger.LogWarning(
                        "{Provider}: series {Series} could not be followed to the new id after {Tries} tries; it needs a manual migration. Its row stays on the old id.",
                        change.Name, r.SeriesId, after.Attempts);
                state[key] = after;
                stateChanged = true;
            }
        }
        // A dry run leaves no trace but its log.
        if (apply && stateChanged)
            WriteFollowState(stateFile, state);
    }

    /// <summary>One row: superseded, or looked for on the new id and, when matched, re-pointed (or, in log mode, measured).</summary>
    private async Task<string> FollowRowAsync(IServiceScope scope, SourceIdChange change, ProviderStorageEntity target, Guid seriesId, Guid rowId,
        string versions, bool apply, string undoFile, CancellationToken token)
    {
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SourceMigrationService migration = scope.ServiceProvider.GetRequiredService<SourceMigrationService>();
        UserContentPreferencesService prefs = scope.ServiceProvider.GetRequiredService<UserContentPreferencesService>();

        // Untracked: the measuring below changes this copy, never the stored row.
        SeriesEntity? series = await db.Series.Include(s => s.Sources).AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == seriesId, token).ConfigureAwait(false);
        SeriesProviderEntity? row = series?.Sources.FirstOrDefault(p => p.Id == rowId);
        if (series == null || row == null || row.MihonProviderId != change.OldProviderId)
            return Gone;
        // Already on the new id too (added by hand): two rows are never merged automatically.
        if (series.Sources.Any(p => p.MihonProviderId == change.NewProviderId))
        {
            _logger.LogWarning("{Provider}: '{Series}' already has a row on the new id; its row on the old id is left as it is.",
                change.Name, series.Title);
            return SourceIdChanges.Superseded;
        }

        ReplacementResult found = await migration.FindReplacementAsync(seriesId, rowId, target, series.OwnerId, token).ConfigureAwait(false);
        if (found.Status != SourceIdChanges.Matched || found.Chosen == null)
        {
            _logger.LogInformation("{Provider}: '{Series}' not followed to the new id: {Status}. {Message}",
                change.Name, series.Title, found.Status, found.Message);
            return found.Status == SourceIdChanges.Matched ? SourceIdChanges.NoMatch : found.Status;
        }

        // What the re-point would set off, on this copy: the first scan's
        // downloads, and the re-downloads the next priority-upgrade pass (a
        // priority change, Apply to All) would queue from the row once it is live.
        int files = row.Chapters.Count(c => !string.IsNullOrEmpty(c.Filename));
        RepointCounts counts = SourceMigrationService.ApplyRepoint(row, found.Chosen, null, DateTime.UtcNow);
        Guid owner = series.OwnerId;
        // The scan's own rule for queuing paid chapters (SeriesCommandService.OwnerHasSiteLoginAsync), per source.
        HashSet<string> logins = (await db.SiteCredentials.AsNoTracking()
            .Where(c => (c.Status == "ok" || c.Status == "manual_cookie") && (owner == Guid.Empty || c.UserId == owner))
            .Select(c => c.Provider)
            .ToListAsync(token).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        bool downloadAll = (await prefs.ForOwnerOfAsync(seriesId, token).ConfigureAwait(false)).DownloadAllChapters;
        int firstScan = SourceMigrationService.FirstScanDownloads(series, row, found.Chosen, downloadAll, logins.Contains(row.Provider));
        int exposure = SourceMigrationService.UpgradeExposure(series, row, logins);

        if (apply)
        {
            RepointCounts? done = await migration.RepointAsync(seriesId, rowId, change.OldProviderId, found.Chosen, undoFile, token).ConfigureAwait(false);
            if (done == null)
            {
                _logger.LogInformation("{Provider}: '{Series}' changed meanwhile; it is looked at again next pass.", change.Name, series.Title);
                return Gone;
            }
            counts = done.Value;
        }
        _logger.LogInformation(
            "{Provider}: {Done} row {Row} of '{Series}' from {Old} to {New} ({Versions}), matched '{Title}' at {Coverage:P0}{Disabled}. " +
            "{Files} files stay on the row; {Matched} chapters relinked, {Added} added, {Unmatched} not listed any more ({UnmatchedFiles} with a file). " +
            "First scan queues {FirstScan} downloads; a priority-upgrade pass would queue {Exposure} re-downloads from it.",
            change.Name, apply ? "re-pointed" : "would re-point", rowId, series.Title, change.OldSourceId, change.NewSourceId, versions,
            found.Chosen.Title, found.Coverage, row.IsDisabled ? " (switched off, stays off)" : "",
            files, counts.Matched, counts.Added, counts.Unmatched, counts.UnmatchedFiles, firstScan, exposure);
        return SourceIdChanges.Matched;
    }

    /// <summary>The users who have the old id enabled and not the new one.</summary>
    private static Task<List<Guid>> UsersToCarryAsync(AppDbContext db, SourceIdChange change, CancellationToken token) =>
        db.UserProviders.AsNoTracking()
            .Where(u => u.MihonProviderId == change.OldProviderId
                && !db.UserProviders.Any(n => n.UserId == u.UserId && n.MihonProviderId == change.NewProviderId))
            .Select(u => u.UserId).Distinct()
            .ToListAsync(token);

    /// <summary>Everyone who had the old id enabled gets the new one, so the source stays in their Search and Browse.</summary>
    private async Task CarryUserSourcesAsync(AppDbContext db, SourceIdChange change, List<Guid> users, bool apply, CancellationToken token)
    {
        if (users.Count == 0)
            return;
        if (apply)
        {
            foreach (Guid user in users)
                db.UserProviders.Add(new UserProviderEntity { Id = Guid.NewGuid(), UserId = user, MihonProviderId = change.NewProviderId });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }
        _logger.LogInformation("{Provider}: {Done} the new id for {Count} user(s): {Users}.",
            change.Name, apply ? "enabled" : "would enable", users.Count, string.Join(", ", users));
    }

    /// <summary>"versionId 1→2" when the ids are the usual hash of small versionIds; evidence for the log only.</summary>
    private static string Versions(SourceIdChange change)
    {
        int? from = SourceIdChanges.VersionOf(change.Name, change.Language, change.OldSourceId);
        int? to = SourceIdChanges.VersionOf(change.Name, change.Language, change.NewSourceId);
        return from != null && to != null ? $"versionId {from}→{to}" : "versionId unknown";
    }

    private Dictionary<string, SourceIdChanges.FollowState> ReadFollowState(string file)
    {
        try
        {
            if (!File.Exists(file))
                return new();
            return JsonSerializer.Deserialize<Dictionary<string, SourceIdChanges.FollowState>>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read {File}", file);
            return new();
        }
    }

    private void WriteFollowState(string file, Dictionary<string, SourceIdChanges.FollowState> state)
    {
        try
        {
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write {File}", file);
        }
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
