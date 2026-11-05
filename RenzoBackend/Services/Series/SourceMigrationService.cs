using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models;
using RenzoBackend.Models.Abstractions;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Services.Bridge;
using RenzoBackend.Services.Downloads;
using RenzoBackend.Services.Images;
using RenzoBackend.Services.Import;
using RenzoBackend.Services.Providers;
using RenzoBackend.Services.Search;
using RenzoBackend.Services.Settings;
using ParsedChapter = Mihon.ExtensionsBridge.Models.Extensions.ParsedChapter;

namespace RenzoBackend.Services.Series;

#region DTOs (web: lib/api/types.ts Migration*; Hub: SourcesModels.kt Migration*)

public sealed class MigrationSourcesDto
{
    [JsonPropertyName("from")] public List<MigrationFromDto> From { get; set; } = [];
    [JsonPropertyName("targets")] public List<MigrationTargetDto> Targets { get; set; } = [];
}

/// <summary>A source the user's series use, to migrate away from.</summary>
public sealed class MigrationFromDto
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("language")] public string Language { get; set; } = "";
    [JsonPropertyName("seriesCount")] public int SeriesCount { get; set; }
    [JsonPropertyName("disabledCount")] public int DisabledCount { get; set; }
    [JsonPropertyName("speed")] public SourceSpeedDto Speed { get; set; } = new();
}

/// <summary>A source to migrate to: one the user can search.</summary>
public sealed class MigrationTargetDto
{
    /// <summary>The source's id (MihonProviderId), what the start request lists.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("language")] public string Language { get; set; } = "";
    [JsonPropertyName("speed")] public SourceSpeedDto Speed { get; set; } = new();
    /// <summary>Reliable lately (90%+ of a fair number of downloads): ticked by default.</summary>
    [JsonPropertyName("suggested")] public bool Suggested { get; set; }
}

public sealed class MigrationStartDto
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("language")] public string? Language { get; set; }
    /// <summary>Source ids to look on, best first: the first one with a good match wins.</summary>
    [JsonPropertyName("targets")] public List<string> Targets { get; set; } = [];
    /// <summary>Only these series; all of the user's series on <see cref="From"/> when empty.</summary>
    [JsonPropertyName("seriesIds")] public List<Guid>? SeriesIds { get; set; }
}

public sealed class MigrationApplyDto
{
    [JsonPropertyName("items")] public List<MigrationApplyItemDto> Items { get; set; } = [];
    /// <summary>"disable" (default): the old source is switched off for each migrated series. "keep": left as it is.</summary>
    [JsonPropertyName("oldSource")] public string OldSource { get; set; } = "disable";
}

public sealed class MigrationApplyItemDto
{
    [JsonPropertyName("seriesId")] public Guid SeriesId { get; set; }
    [JsonPropertyName("mihonId")] public string MihonId { get; set; } = "";
}

public sealed class MigrationRunDto
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("from")] public string From { get; set; } = "";
    /// <summary>"searching", "ready", "applying", "done" or "failed".</summary>
    [JsonPropertyName("phase")] public string Phase { get; set; } = "";
    [JsonPropertyName("done")] public int Done { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("items")] public List<MigrationItemDto> Items { get; set; } = [];
}

public sealed class MigrationItemDto : IThumb
{
    [JsonPropertyName("seriesId")] public Guid SeriesId { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("thumbnailUrl")] public string? ThumbnailUrl { get; set; }
    /// <summary>Chapters the old source lists, which a match is measured against.</summary>
    [JsonPropertyName("chapters")] public int Chapters { get; set; }
    /// <summary>"pending", "matched", "none", "migrated", "skipped" or "failed".</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("match")] public MigrationCandidateDto? Match { get; set; }
    [JsonPropertyName("alternatives")] public List<MigrationCandidateDto> Alternatives { get; set; } = [];
}

public sealed class MigrationCandidateDto
{
    [JsonPropertyName("mihonId")] public string MihonId { get; set; } = "";
    [JsonPropertyName("sourceId")] public string SourceId { get; set; } = "";
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    /// <summary>The title the source gives it.</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// <summary>The series' other name it was found under, when not its own title.</summary>
    [JsonPropertyName("foundAs")] public string? FoundAs { get; set; }
    [JsonPropertyName("chapterCount")] public int ChapterCount { get; set; }
    [JsonPropertyName("latest")] public decimal? Latest { get; set; }
    /// <summary>Share (0..1) of the old source's chapters, by whole number, this one lists.</summary>
    [JsonPropertyName("coverage")] public double Coverage { get; set; }
    /// <summary>Lists far more chapters than the series has anywhere: likely another work with the same name. Never chosen on its own.</summary>
    [JsonPropertyName("suspect")] public bool Suspect { get; set; }
}

#endregion

/// <summary>
/// Source migration, Mihon's Browse › Migrate made for series that keep several
/// sources: when a source breaks (MangaFire behind a Cloudflare block,
/// 2026-11-04), every series on it is looked for on the sources the user picks,
/// best first, and the match is ADDED to the series as one more source. Nothing
/// is copied or lost: downloads stay where they are, read progress belongs to the
/// series' chapter numbers, and the old source is switched off rather than
/// removed (it can be switched back on per series).
///
/// Two steps, both background runs the client polls: a preview that finds and
/// measures a match per series (title, then every other name the sources list —
/// AltTitles), and an apply for the matches the user keeps.
///
/// A match is judged by how many of the old source's chapters it lists (by whole
/// number: sites split chapters differently). Sources are tried in the user's
/// order and the first that lists nine in ten wins; otherwise the best found that
/// lists at least half.
/// </summary>
public sealed class SourceMigrationService
{
    private const double GoodEnough = 0.9;
    private const double Acceptable = 0.5;
    private const int CandidatesPerSource = 2;
    private const int NamesSearched = 3;
    private const int Parallel = 3;
    private static readonly TimeSpan KeepRuns = TimeSpan.FromHours(6);

    private sealed class Run
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Guid UserId { get; init; }
        public string From { get; init; } = "";
        public string? Language { get; init; }
        public DateTime Started { get; } = DateTime.UtcNow;
        public volatile string Phase = "searching";
        public int Done;
        public int Total;
        public string? Error;
        public List<MigrationItemDto> Items { get; } = [];
        /// <summary>The scanlator group chosen per candidate (what the preview measured), reused by apply.</summary>
        public ConcurrentDictionary<string, ProviderSeriesDetails> Details { get; } = new(StringComparer.Ordinal);
        public CancellationTokenSource Cancel { get; } = new();
        public DateTime? Finished;
    }

    /// <summary>Start and apply decide under this one gate, so two requests cannot both pass.</summary>
    private readonly object _gate = new();

    private readonly ConcurrentDictionary<Guid, Run> _runs = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SourceSpeedStats _speed;
    private readonly ILogger<SourceMigrationService> _logger;

    public SourceMigrationService(IServiceScopeFactory scopeFactory, SourceSpeedStats speed, ILogger<SourceMigrationService> logger)
    {
        _scopeFactory = scopeFactory;
        _speed = speed;
        _logger = logger;
    }

    public async Task<MigrationSourcesDto> SourcesAsync(Guid userId, CancellationToken token)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var search = scope.ServiceProvider.GetRequiredService<SearchQueryService>();

        var rows = await db.SeriesProviders.AsNoTracking()
            .Where(p => !p.IsUnknown && !p.IsLocal && db.Series.Any(s => s.Id == p.SeriesId && s.OwnerId == userId))
            .Select(p => new { p.SeriesId, p.Provider, p.Language, p.IsDisabled })
            .ToListAsync(token).ConfigureAwait(false);
        var from = rows.GroupBy(r => (r.Provider, r.Language))
            .Select(g => new MigrationFromDto
            {
                Provider = g.Key.Provider,
                Language = g.Key.Language,
                SeriesCount = g.Select(r => r.SeriesId).Distinct().Count(),
                DisabledCount = g.Where(r => r.IsDisabled).Select(r => r.SeriesId).Distinct().Count(),
                Speed = _speed.For(g.Key.Provider),
            })
            .OrderByDescending(f => f.SeriesCount)
            .ToList();

        var (sources, _) = await search.UserSearchSourcesAsync(userId, token).ConfigureAwait(false);
        var targets = sources.Select(s =>
            {
                SourceSpeedDto speed = _speed.For(s.Name);
                return new MigrationTargetDto
                {
                    Id = s.MihonProviderId,
                    Provider = s.Name,
                    Language = s.Language,
                    Speed = speed,
                    Suggested = speed.Attempts >= 10 && speed.SuccessRate >= 0.9,
                };
            })
            .OrderByDescending(t => t.Suggested)
            .ThenBy(t => t.Speed.MedianSeconds ?? double.MaxValue)
            .ThenBy(t => t.Provider, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new MigrationSourcesDto { From = from, Targets = targets };
    }

    public MigrationRunDto? Get(Guid userId, Guid id) =>
        _runs.TryGetValue(id, out Run? run) && run.UserId == userId ? Snapshot(run) : null;

    /// <summary>The user's latest run, so the page picks it up again after navigating away.</summary>
    public MigrationRunDto? Current(Guid userId)
    {
        Prune();
        Run? run = _runs.Values.Where(r => r.UserId == userId).OrderByDescending(r => r.Started).FirstOrDefault();
        return run == null ? null : Snapshot(run);
    }

    /// <summary>Forgets a run; a search still going is stopped. One being applied is not (it would leave a series half done).</summary>
    public bool Discard(Guid userId, Guid id)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out Run? run) || run.UserId != userId || run.Phase == "applying")
                return false;
            if (run.Phase == "searching")
                run.Cancel.Cancel();
            return _runs.TryRemove(id, out _);
        }
    }

    /// <summary>Starts a preview; null with a reason when it cannot.</summary>
    public async Task<(MigrationRunDto? Run, string? Error)> StartAsync(Guid userId, MigrationStartDto request, CancellationToken token)
    {
        Prune();
        if (string.IsNullOrWhiteSpace(request.From) || request.Targets.Count == 0)
            return (null, "Pick a source to migrate from and at least one to look on.");

        using (IServiceScope scope = _scopeFactory.CreateScope())
        {
            var search = scope.ServiceProvider.GetRequiredService<SearchQueryService>();
            var (sources, _) = await search.UserSearchSourcesAsync(userId, token).ConfigureAwait(false);
            HashSet<string> allowed = sources.Select(s => s.MihonProviderId).ToHashSet(StringComparer.Ordinal);
            if (request.Targets.Any(t => !allowed.Contains(t)))
                return (null, "One of the sources to look on is not one you can search.");
            if (sources.Where(s => request.Targets.Contains(s.MihonProviderId)).Any(s => s.Name == request.From))
                return (null, "The source you migrate from cannot be one to look on.");
        }

        var run = new Run { UserId = userId, From = request.From, Language = request.Language };
        lock (_gate)
        {
            if (_runs.Values.Any(r => r.UserId == userId && r.Phase is "searching" or "applying"))
                return (null, "A migration is already running.");
            // One run to review at a time: an older one left ready could be applied later by mistake.
            foreach (var old in _runs.Values.Where(r => r.UserId == userId).ToList())
                _runs.TryRemove(old.Id, out _);
            _runs[run.Id] = run;
        }
        var targets = request.Targets.ToList();
        var only = request.SeriesIds?.ToHashSet();
        _ = Task.Run(() => PreviewAsync(run, targets, only, run.Cancel.Token));
        return (Snapshot(run), null);
    }

    /// <summary>Applies the chosen matches of a finished preview; null with a reason when it cannot.</summary>
    public (MigrationRunDto? Run, string? Error) Apply(Guid userId, Guid id, MigrationApplyDto request)
    {
        if (!_runs.TryGetValue(id, out Run? run) || run.UserId != userId)
            return (null, "That migration is gone; start it again.");
        List<MigrationApplyItemDto> chosen;
        lock (run.Items)
        {
            chosen = request.Items
                .Where(i => run.Items.Any(it => it.SeriesId == i.SeriesId))
                .GroupBy(i => i.SeriesId).Select(g => g.First())
                .ToList();
        }
        if (chosen.Count == 0)
            return (null, "Nothing chosen to migrate.");
        bool disableOld = !string.Equals(request.OldSource, "keep", StringComparison.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (run.Phase != "ready")
                return (null, "The migration is not ready to apply.");
            run.Phase = "applying";
            run.Finished = null;
        }
        Interlocked.Exchange(ref run.Done, 0);
        run.Total = chosen.Count;
        _ = Task.Run(() => ApplyAllAsync(run, chosen, disableOld));
        return (Snapshot(run), null);
    }

    private static MigrationRunDto Snapshot(Run run)
    {
        lock (run.Items)
        {
            return new MigrationRunDto
            {
                Id = run.Id,
                From = run.From,
                Phase = run.Phase,
                Done = run.Done,
                Total = run.Total,
                Error = run.Error,
                Items = run.Items.Select(i => new MigrationItemDto
                {
                    SeriesId = i.SeriesId,
                    Title = i.Title,
                    ThumbnailUrl = i.ThumbnailUrl,
                    Chapters = i.Chapters,
                    Status = i.Status,
                    Message = i.Message,
                    Match = i.Match,
                    Alternatives = [.. i.Alternatives],
                }).ToList(),
            };
        }
    }

    private void Prune()
    {
        foreach (var (id, run) in _runs)
        {
            if (run.Finished is DateTime f && DateTime.UtcNow - f > KeepRuns && run.Phase is not ("searching" or "applying"))
                _runs.TryRemove(id, out _);
        }
    }

    // ----- preview -----

    private async Task PreviewAsync(Run run, List<string> targets, HashSet<Guid>? only, CancellationToken token)
    {
        try
        {
            List<(Guid Id, string Title, string? Thumb)> series;
            Dictionary<string, Dictionary<string, List<string>>> catalogue = [];
            List<ProviderStorageEntity> targetSources;
            using (IServiceScope scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var search = scope.ServiceProvider.GetRequiredService<SearchQueryService>();
                var (sources, _) = await search.UserSearchSourcesAsync(run.UserId, token).ConfigureAwait(false);
                targetSources = targets.Select(t => sources.FirstOrDefault(s => s.MihonProviderId == t)).Where(s => s != null).Select(s => s!).ToList();

                series = await db.Series.AsNoTracking()
                    .Where(s => s.OwnerId == run.UserId && s.Sources.Any(p => p.Provider == run.From && !p.IsUnknown && !p.IsLocal
                        && (run.Language == null || p.Language == run.Language)))
                    .OrderBy(s => s.Title)
                    .Select(s => new ValueTuple<Guid, string, string?>(s.Id, s.Title, s.ThumbnailUrl))
                    .ToListAsync(token).ConfigureAwait(false);
                if (only is { Count: > 0 })
                    series = series.Where(s => only.Contains(s.Id)).ToList();

                // The catalogue cache, by title key, per source looked on: most
                // matches come from here without asking the site anything.
                foreach (ProviderStorageEntity src in targetSources)
                    catalogue[src.MihonProviderId] = await CatalogueIndexAsync(db, src, token).ConfigureAwait(false);
            }

            lock (run.Items)
            {
                foreach (var s in series)
                    run.Items.Add(new MigrationItemDto { SeriesId = s.Id, Title = s.Title, ThumbnailUrl = s.Thumb });
                run.Total = series.Count;
            }

            await System.Threading.Tasks.Parallel.ForEachAsync(series,
                new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = token },
                async (s, ct) =>
                {
                    MigrationItemDto item;
                    lock (run.Items)
                        item = run.Items.First(i => i.SeriesId == s.Id);
                    try
                    {
                        await PreviewOneAsync(run, item, targetSources, catalogue, ct).ConfigureAwait(false);
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogWarning(e, "Migration preview failed for '{Series}'.", s.Title);
                        item.Status = "failed";
                        item.Message = "Could not be searched.";
                    }
                    Interlocked.Increment(ref run.Done);
                }).ConfigureAwait(false);
            run.Phase = "ready";
            _logger.LogInformation("Migration from {From}: {Matched} of {Total} series matched.",
                run.From, run.Items.Count(i => i.Match != null), run.Items.Count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            run.Error = "Cancelled.";
            run.Phase = "failed";
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Migration preview from {From} failed.", run.From);
            run.Error = "The search failed; try again.";
            run.Phase = "failed";
        }
        finally
        {
            run.Finished = DateTime.UtcNow;
        }
    }

    private async Task PreviewOneAsync(Run run, MigrationItemDto item, List<ProviderStorageEntity> targets,
        Dictionary<string, Dictionary<string, List<string>>> catalogue, CancellationToken token)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var search = scope.ServiceProvider.GetRequiredService<SearchQueryService>();
        var augment = scope.ServiceProvider.GetRequiredService<SearchCommandService>();
        var settings = await scope.ServiceProvider.GetRequiredService<SettingsService>().GetSettingsAsync(token).ConfigureAwait(false);

        SeriesEntity? series = await db.Series.Include(s => s.Sources).AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == item.SeriesId, token).ConfigureAwait(false);
        if (series == null)
        {
            item.Status = "failed";
            item.Message = "The series is gone.";
            return;
        }

        HashSet<decimal> wanted = WantedWholes(series, run.From, run.Language);
        item.Chapters = wanted.Count;

        // Already moved, or never needed to be: another source is on the series,
        // switched on, and lists what the old one did. Any source, not only the
        // ones looked on this time: a second run with other sources, to catch the
        // rest, must not add a second replacement to what the first run moved.
        SeriesProviderEntity? done = series.Sources
            .Where(p => !p.IsDisabled && !p.IsUninstalled && !p.IsLocal && !p.IsUnknown
                && !(p.Provider == run.From && (run.Language == null || p.Language == run.Language)))
            .FirstOrDefault(p => Coverage(wanted, p.Chapters.Where(Real).Select(c => c.Number)) >= GoodEnough);
        if (done != null)
        {
            item.Status = "none";
            item.Message = $"Already has {done.Provider}, which lists its chapters.";
            return;
        }

        List<string> names = NamesOf(series);
        HashSet<string> nameKeys = names.Select(AltTitles.Key).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        // The newest chapter the series has anywhere: a candidate with many
        // chapters past it is likely another work with the same name.
        decimal seriesLatest = Latest(series.Sources.SelectMany(p => p.Chapters).Where(Real).Select(c => c.Number!.Value)) ?? 0;

        var found = new List<MigrationCandidateDto>();
        var skipped = new List<string>();
        var unreachable = new HashSet<string>(StringComparer.Ordinal);
        foreach (ProviderStorageEntity target in targets)
        {
            // Already one of its sources, switched off or not: apply would skip it,
            // so the next source looked on gets its turn.
            SeriesProviderEntity? existing = series.Sources.FirstOrDefault(p => p.MihonProviderId == target.MihonProviderId && !p.IsUninstalled);
            if (existing != null)
            {
                skipped.Add(existing.IsDisabled ? $"{target.Name} (switched off)" : target.Name);
                continue;
            }

            // Measured, or failed twice. One that failed once (a timeout or a 429
            // under the preview's load) is tried again when the site's own search
            // returns it.
            var measured = new HashSet<string>(StringComparer.Ordinal);
            var failedOnce = new HashSet<string>(StringComparer.Ordinal);
            MigrationCandidateDto? best = null;
            async Task MeasureAsync(LinkedSeriesDto hit)
            {
                if (hit.MihonId == null || measured.Contains(hit.MihonId))
                    return;
                AugmentedResponseDto aug = await augment.AugmentSeriesAsync([hit], run.UserId, token).ConfigureAwait(false);
                // One scanlator group is what apply adds: the one that lists the most
                // of the old source's chapters. It is measured alone.
                ProviderSeriesDetails? group = aug.Series
                    .Where(g => g.MihonId == hit.MihonId)
                    .OrderByDescending(g => Coverage(wanted, g.Chapters.Where(Real).Select(c => c.Number)))
                    .ThenByDescending(g => g.Chapters.Count(Real))
                    .FirstOrDefault();
                if (group == null)
                {
                    if (aug.DroppedSeries.Any(d => d.Reason == "unreachable"))
                        unreachable.Add(target.Name);
                    if (!failedOnce.Add(hit.MihonId))
                        measured.Add(hit.MihonId);
                    return;
                }
                measured.Add(hit.MihonId);
                run.Details[hit.MihonId] = group;
                var numbers = group.Chapters.Where(Real).Select(c => c.Number!.Value).ToList();
                var candidate = new MigrationCandidateDto
                {
                    MihonId = hit.MihonId,
                    SourceId = target.MihonProviderId,
                    Provider = target.Name,
                    Title = group.Title,
                    FoundAs = hit.FoundAs,
                    ChapterCount = numbers.Distinct().Count(),
                    Latest = Latest(numbers),
                    Coverage = Coverage(wanted, numbers.Select(n => (decimal?)n)),
                };
                candidate.Suspect = IsSuspect(numbers, seriesLatest);
                found.Add(candidate);
                if (Better(candidate, best))
                    best = candidate;
            }

            foreach (LinkedSeriesDto hit in await CatalogueHitsAsync(db, target, names, catalogue, token).ConfigureAwait(false))
                await MeasureAsync(hit).ConfigureAwait(false);
            // The catalogue can be stale (dead links, a work since renamed): ask the
            // site too unless it already gave a good match.
            if (best is not { Coverage: >= GoodEnough, Suspect: false })
            {
                foreach (string name in names.Take(NamesSearched))
                {
                    var results = await search.SearchSeriesAsync(name, [target], settings, 0, token).ConfigureAwait(false);
                    var hits = results.Where(r => r.MihonId != null && !measured.Contains(r.MihonId) && nameKeys.Contains(AltTitles.Key(r.Title)))
                        .Take(CandidatesPerSource).ToList();
                    foreach (LinkedSeriesDto h in hits)
                    {
                        h.IsStorage = false;
                        if (AltTitles.Key(h.Title) != AltTitles.Key(series.Title))
                            h.FoundAs = name;
                        await MeasureAsync(h).ConfigureAwait(false);
                    }
                    if (hits.Count > 0)
                        break;
                }
            }
            if (best is { Coverage: >= GoodEnough, Suspect: false })
                break; // the best source in the user's order that has it: done
        }

        var ranked = found
            .OrderBy(c => c.Suspect)
            .ThenByDescending(c => c.Coverage >= GoodEnough)
            .ThenBy(c => targets.FindIndex(t => t.MihonProviderId == c.SourceId))
            .ThenByDescending(c => c.Coverage)
            .ToList();
        MigrationCandidateDto? match = ranked.FirstOrDefault(c => !c.Suspect && c.Coverage >= Acceptable);
        item.Match = match;
        item.Alternatives = ranked.Where(c => c != match).Take(3).ToList();
        item.Status = match != null ? "matched" : "none";
        item.Message = match != null ? null
            : found.Count > 0 ? (found.All(c => c.Suspect) ? "Found only titles with many more chapters than this series; check before choosing." : "Found, but missing most of its chapters.")
            : unreachable.Count > 0 ? $"Could not be checked on {string.Join(", ", unreachable)}; try again later."
            : skipped.Count > 0 ? $"Not found; already has {string.Join(", ", skipped)}."
            : "Not found on the sources chosen.";
    }

    private static bool Real(Chapter c) => c.Number is decimal n && n >= 0 && n <= int.MaxValue && !ModelExtensions.IsLockedChapterName(c.Name);

    /// <summary>
    /// The newest chapter number, passing over strays: a whole number with no
    /// other within five below it ("Chapter 2023" after 63, "Chapter 1000" after
    /// 280) is not where the series is. Null with no numbers.
    /// </summary>
    internal static decimal? Latest(IEnumerable<decimal> numbers)
    {
        var list = numbers.ToList();
        if (list.Count == 0)
            return null;
        var wholes = list.Select(decimal.Truncate).Distinct().OrderByDescending(n => n).ToList();
        decimal top = wholes.FirstOrDefault(n => wholes.Any(m => m < n && m >= n - 5), wholes[0]);
        return list.Where(n => decimal.Truncate(n) <= top).Max();
    }

    /// <summary>Share of <paramref name="wanted"/> (whole numbers) that <paramref name="numbers"/> lists; 0 when nothing is wanted (nothing to judge by).</summary>
    internal static double Coverage(HashSet<decimal> wanted, IEnumerable<decimal?> numbers)
    {
        if (wanted.Count == 0)
            return 0;
        var have = numbers.Where(n => n != null && n >= 0 && n <= int.MaxValue).Select(n => decimal.Truncate(n!.Value)).ToHashSet();
        return wanted.Count(have.Contains) / (double)wanted.Count;
    }

    private static bool Better(MigrationCandidateDto a, MigrationCandidateDto? b) =>
        b == null || (a.Suspect != b.Suspect ? !a.Suspect : a.Coverage > b.Coverage);

    /// <summary>
    /// Many more chapters than the series has anywhere: likely another work with
    /// the same name. Counted, not the highest number: sources list the odd stray
    /// ("Chapter 2023" in a 63-chapter series).
    /// </summary>
    internal static bool IsSuspect(IEnumerable<decimal> numbers, decimal seriesLatest) =>
        numbers.Select(decimal.Truncate).Distinct().Count(n => n > seriesLatest) > Math.Max(20, seriesLatest / 2);

    /// <summary>The old source's chapters by whole number (locked titles and notices left out): what a match must list.</summary>
    internal static HashSet<decimal> WantedWholes(SeriesEntity series, string from, string? language)
    {
        var rows = series.Sources.Where(p => p.Provider == from && (language == null || p.Language == language)).ToList();
        var numbers = rows.SelectMany(p => p.Chapters)
            .Where(c => c.Number is decimal n && n >= 0 && !ModelExtensions.IsLockedChapterName(c.Name))
            .Select(c => decimal.Truncate(c.Number!.Value))
            .ToHashSet();
        if (numbers.Count == 0)
            numbers = series.ListedChapterGroups().Select(g => g.Key).Where(n => n >= 0).Select(decimal.Truncate).ToHashSet();
        return numbers;
    }

    /// <summary>Every name the series goes by: its title, each source's title, then the other names the sources list.</summary>
    internal static List<string> NamesOf(SeriesEntity series)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? n)
        {
            if (!string.IsNullOrWhiteSpace(n) && seen.Add(AltTitles.Key(n)) && AltTitles.Key(n).Length > 0)
                names.Add(n.Trim());
        }
        Add(series.Title);
        foreach (SeriesProviderEntity p in series.Sources)
            Add(p.Title);
        // Its own names passed in, so a related work a source lists ("High School
        // DxD" on the spin-off) is left out.
        string?[] own = [series.Title, .. series.Sources.Select(p => p.Title)];
        foreach (SeriesProviderEntity p in series.Sources)
        {
            foreach (string alt in AltTitles.Parse(p.Description, own))
                Add(alt);
        }
        // Names written in Latin letters first: what these sources can find.
        return names.Select((n, i) => (n, i))
            .OrderByDescending(x => x.i == 0 || AltTitles.Latin(x.n))
            .ThenBy(x => x.i)
            .Select(x => x.n)
            .ToList();
    }

    /// <summary>A source's catalogue cache by title key: the series ids filed under each.</summary>
    private static async Task<Dictionary<string, List<string>>> CatalogueIndexAsync(AppDbContext db, ProviderStorageEntity src, CancellationToken token)
    {
        var titles = await db.LatestSeries.AsNoTracking()
            .Where(l => l.MihonProviderId == src.MihonProviderId)
            .Select(l => new { l.MihonId, l.Title })
            .ToListAsync(token).ConfigureAwait(false);
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var t in titles)
        {
            string k = AltTitles.Key(t.Title);
            if (k.Length == 0 || string.IsNullOrEmpty(t.MihonId))
                continue;
            if (!index.TryGetValue(k, out List<string>? ids))
                index[k] = ids = [];
            ids.Add(t.MihonId);
        }
        return index;
    }

    private static async Task<List<LinkedSeriesDto>> CatalogueHitsAsync(AppDbContext db, ProviderStorageEntity target, List<string> names,
        Dictionary<string, Dictionary<string, List<string>>> catalogue, CancellationToken token)
    {
        if (!catalogue.TryGetValue(target.MihonProviderId, out var index))
            return [];
        string titleKey = AltTitles.Key(names.FirstOrDefault());
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (index.TryGetValue(AltTitles.Key(name), out List<string>? ids))
            {
                foreach (string id in ids)
                    byId.TryAdd(id, name);
            }
        }
        if (byId.Count == 0)
            return [];
        // Usable rows only (the bridge payload is what the source is asked with),
        // the most recently seen first: stub and long-dead rows sit in the cache too.
        List<string> keys = byId.Keys.ToList();
        var rows = await db.LatestSeries.AsNoTracking()
            .Where(l => keys.Contains(l.MihonId) && l.BridgeItemInfo != null && l.BridgeItemInfo != "")
            .OrderByDescending(l => l.FetchDate)
            .Take(CandidatesPerSource)
            .ToListAsync(token).ConfigureAwait(false);
        return rows.Select(row => new LinkedSeriesDto
        {
            MihonId = row.MihonId,
            MihonProviderId = row.MihonProviderId ?? target.MihonProviderId,
            BridgeItemInfo = row.BridgeItemInfo,
            Provider = target.Name,
            Lang = row.Language == "all" ? string.Empty : row.Language,
            Title = row.Title,
            IsStorage = false,
            LinkedIds = [row.MihonId],
            FoundAs = AltTitles.Key(byId[row.MihonId]) == titleKey ? null : byId[row.MihonId],
        }).ToList();
    }

    // ----- apply -----

    private async Task ApplyAllAsync(Run run, List<MigrationApplyItemDto> chosen, bool disableOld)
    {
        foreach (MigrationApplyItemDto pick in chosen)
        {
            MigrationItemDto item;
            lock (run.Items)
                item = run.Items.First(i => i.SeriesId == pick.SeriesId);
            // The candidate applied becomes the item's match, so "Added {provider}"
            // names it on any client, also after another match was picked.
            MigrationCandidateDto? applied = item.Match?.MihonId == pick.MihonId ? item.Match
                : item.Alternatives.FirstOrDefault(a => a.MihonId == pick.MihonId);
            if (applied != null && applied != item.Match)
            {
                item.Alternatives = [.. item.Alternatives.Where(a => a != applied).Prepend(item.Match).OfType<MigrationCandidateDto>()];
                item.Match = applied;
            }
            try
            {
                (item.Status, item.Message) = await ApplyOneAsync(run, pick, disableOld).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Migrating '{Series}' failed.", item.Title);
                item.Status = "failed";
                item.Message = "Adding the source failed.";
            }
            Interlocked.Increment(ref run.Done);
        }
        run.Finished = DateTime.UtcNow;
        run.Phase = "done";
        _logger.LogInformation("Migration from {From}: {Migrated} series migrated, {Failed} failed, {Skipped} skipped.",
            run.From, run.Items.Count(i => i.Status == "migrated"), run.Items.Count(i => i.Status == "failed"), run.Items.Count(i => i.Status == "skipped"));
    }

    /// <summary>
    /// Adds the chosen match to one series as a new source. Not through the
    /// add-series merge (AddSeriesAsync): that puts the new source at the top
    /// priority, rescans every source of the series and lowers other sources'
    /// cutoffs. This adds just the one row, the way the import does: not storage,
    /// not the title or cover source, directly below the old source in the order,
    /// with its cutoff.
    ///
    /// No priority-upgrade pass either: it covers every source of the series, so
    /// with the owner's "re-download from higher-priority sources" on it would
    /// queue some 1,600 re-downloads from sources the migration never touched
    /// (AllManga, Comic Asura…). The old source sits above the new row, so its
    /// files are never upgraded; the new row's first scan fetches what no source
    /// has a file for, and (that setting on) what only a source below it has, as
    /// the old source would have.
    /// </summary>
    private async Task<(string Status, string? Message)> ApplyOneAsync(Run run, MigrationApplyItemDto pick, bool disableOld)
    {
        CancellationToken token = CancellationToken.None;
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var thumbs = scope.ServiceProvider.GetRequiredService<ThumbCacheService>();
        var providers = scope.ServiceProvider.GetRequiredService<SeriesProviderService>();
        var state = scope.ServiceProvider.GetRequiredService<SeriesStateService>();

        if (!run.Details.TryGetValue(pick.MihonId, out ProviderSeriesDetails? chosen))
            return ("failed", "That match is no longer known; run the search again.");
        string sourceId = chosen.MihonProviderId ?? "";

        SeriesProviderEntity sp;
        List<SeriesProviderEntity> fromRows;
        bool paused;
        // The check and the insert under the series lock, on rows read inside it:
        // a second apply of the same series must find the first one's row.
        using (await DownloadCommandService.SeriesLock.LockAsync(pick.SeriesId.ToString(), token).ConfigureAwait(false))
        {
            SeriesEntity? series = await db.Series.Include(s => s.Sources)
                .FirstOrDefaultAsync(s => s.Id == pick.SeriesId, token).ConfigureAwait(false);
            if (series == null || (series.OwnerId != run.UserId && series.OwnerId != Guid.Empty))
                return ("failed", "The series is gone.");
            if (series.Sources.Any(p => p.MihonProviderId == sourceId && !p.IsUninstalled))
                return ("skipped", "Already one of its sources.");
            paused = series.PauseDownloads;

            fromRows = series.Sources
                .Where(p => p.Provider == run.From && (run.Language == null || p.Language == run.Language))
                .ToList();
            // Removed from the series since the preview: there is no place in the
            // order or cutoff to take, and nothing to move off.
            if (fromRows.Count == 0)
                return ("skipped", "The old source is no longer on this series.");
            // A copy of the measured group: the cached one is shared, and creating
            // the row writes into it.
            ProviderSeriesDetails fs = System.Text.Json.JsonSerializer.Deserialize<ProviderSeriesDetails>(
                System.Text.Json.JsonSerializer.Serialize(chosen))!;
            fs.IsStorage = false;
            fs.UseTitle = false;
            fs.UseCover = false;
            fs.UseStatus = false;
            fs.IsLocal = false;
            SeriesProviderEntity first = fromRows.OrderBy(p => p.Priority).First();
            fs.ContinueAfterChapter = first.ContinueAfterChapter;

            sp = await fs.CreateOrUpdateAsync(thumbs, null, token).ConfigureAwait(false);
            sp.SeriesId = series.Id;
            // Directly below the old source, never tied with it: on a tie the
            // series page shows the switched-on row first, and the next reorder
            // there numbers it above, which queues every file the old source holds
            // for re-download (and deletes it after). The rows below move down one.
            foreach (SeriesProviderEntity p in series.Sources.Where(p => p.Priority > first.Priority))
                p.Priority++;
            sp.Priority = first.Priority + 1;

            if (disableOld)
            {
                // The status updates came from the old source; they come from the new one now.
                if (fromRows.Any(p => p.IsStatus))
                {
                    sp.IsStatus = true;
                    fromRows.ForEach(p => p.IsStatus = false);
                }
                fromRows.ForEach(p => p.IsDisabled = true);
            }
            db.SeriesProviders.Add(sp);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }
        await providers.CheckIfTheStorageFlagsChangedTheInLibraryStatusOfLastSeriesAsync([sp], [], token).ConfigureAwait(false);
        // Its chapter scan now; the old source's scheduled scan off when it was switched off.
        await providers.RescheduleIfNeededAsync(disableOld ? fromRows.Append(sp) : [sp], true, paused, token).ConfigureAwait(false);
        await state.SyncToRenzoJsonAsync(pick.SeriesId, token).ConfigureAwait(false);

        _logger.LogInformation("Migrated series {Series} from {From} to {To} ('{Title}'){Old}.",
            pick.SeriesId, run.From, sp.Provider, sp.Title, disableOld ? "; old source switched off" : "");
        return ("migrated", null);
    }

    // ----- following a source to its new id (SourceMoveService) -----

    /// <summary>One live search on the new id; a slow or blocked site is "unreachable", never a solver.</summary>
    private static readonly TimeSpan LiveSearchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Finds a library row's series on the same source under its new id, after an
    /// extension update changed the id (SourceIdChanges). Stricter than a
    /// migration preview, since nobody reviews the match before it is applied:
    /// exactly one series on the new id has the row's own title, its group for the
    /// row's scanlator lists nine in ten of the row's chapters (by whole number),
    /// and it is not suspect. The catalogue cache first, then the site's search
    /// under the row's and the series' names (other names only when distinctive).
    /// </summary>
    public async Task<ReplacementResult> FindReplacementAsync(Guid seriesId, Guid rowId, ProviderStorageEntity target, Guid ownerId,
        CancellationToken token)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var augment = scope.ServiceProvider.GetRequiredService<SearchCommandService>();
        var mihon = scope.ServiceProvider.GetRequiredService<MihonBridgeService>();

        SeriesEntity? series = await db.Series.Include(s => s.Sources).AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == seriesId, token).ConfigureAwait(false);
        SeriesProviderEntity? stored = series?.Sources.FirstOrDefault(p => p.Id == rowId);
        if (series == null || stored == null)
            return new ReplacementResult { Status = SourceIdChanges.NoMatch, Message = "The row is gone." };
        SeriesProviderEntity row = stored;

        HashSet<decimal> wanted = RowWholes(row);
        string titleKey = AltTitles.Key(row.Title);
        var sameTitle = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<ReplacementCandidate>();
        bool unreachable = false;
        ReplacementResult Verdict() => JudgeReplacement(row, target.Name, wanted.Count, sameTitle, groups, unreachable);
        if (wanted.Count == 0 || titleKey.Length == 0)
            return Verdict();

        decimal seriesLatest = Latest(series.Sources.SelectMany(p => p.Chapters).Where(Real).Select(c => c.Number!.Value)) ?? 0;
        var measured = new HashSet<string>(StringComparer.Ordinal);
        async Task MeasureAsync(LinkedSeriesDto hit)
        {
            if (hit.MihonId == null || !measured.Add(hit.MihonId))
                return;
            AugmentedResponseDto aug = await augment.AugmentSeriesAsync([hit], ownerId, token).ConfigureAwait(false);
            List<ProviderSeriesDetails> found = aug.Series.Where(g => g.MihonId == hit.MihonId).ToList();
            if (found.Count == 0 && aug.DroppedSeries.Any(d => d.Reason == "unreachable"))
                unreachable = true;
            foreach (ProviderSeriesDetails g in found)
            {
                var numbers = g.Chapters.Where(Real).Select(c => c.Number!.Value).ToList();
                groups.Add(new ReplacementCandidate(hit.MihonId, g.Scanlator,
                    Coverage(wanted, numbers.Select(n => (decimal?)n)), IsSuspect(numbers, seriesLatest), g));
            }
        }

        // Only the row's own title can be accepted, so the catalogue is asked for
        // that alone; two usable rows under it are already an ambiguity.
        var catalogue = new Dictionary<string, Dictionary<string, List<string>>>
        {
            [target.MihonProviderId] = await CatalogueIndexAsync(db, target, token).ConfigureAwait(false),
        };
        foreach (LinkedSeriesDto hit in await CatalogueHitsAsync(db, target, [row.Title], catalogue, token).ConfigureAwait(false))
        {
            sameTitle.Add(hit.MihonId!);
            await MeasureAsync(hit).ConfigureAwait(false);
        }
        // More results cannot settle an ambiguity, only add to it.
        ReplacementResult fromCatalogue = Verdict();
        if (fromCatalogue.Status is SourceIdChanges.Matched or SourceIdChanges.Ambiguous)
            return fromCatalogue;

        // The catalogue can be stale or not have it at all: ask the site.
        foreach (string name in ReplacementNames(series, row).Take(NamesSearched))
        {
            (List<LinkedSeriesDto> results, bool failed) = await LiveSearchAsync(mihon, target, name, token).ConfigureAwait(false);
            unreachable |= failed;
            List<LinkedSeriesDto> same = results.Where(r => r.MihonId != null && AltTitles.Key(r.Title) == titleKey).ToList();
            same.ForEach(r => sameTitle.Add(r.MihonId!));
            foreach (LinkedSeriesDto h in same.Where(r => !measured.Contains(r.MihonId!)).Take(CandidatesPerSource))
                await MeasureAsync(h).ConfigureAwait(false);
            if (same.Count > 0)
                break;
        }
        return Verdict();
    }

    private async Task<(List<LinkedSeriesDto> Results, bool Failed)> LiveSearchAsync(MihonBridgeService mihon, ProviderStorageEntity target,
        string name, CancellationToken token)
    {
        try
        {
            var src = await mihon.SourceFromProviderIdAsync(target.MihonProviderId, token).ConfigureAwait(false);
            var page = await SourceTimeout.RunAsync(c => src.SearchAsync(1, name, c), LiveSearchTimeout, token).ConfigureAwait(false);
            var found = (page?.Mangas ?? []).Select(m => (m, target.MihonProviderId, src.Language)).ToList();
            List<LinkedSeriesDto> linked = found.FindAndLinkSimilarSeries();
            linked.ForEach(l =>
            {
                l.Provider = target.Name;
                l.IsStorage = false;
            });
            return (linked.DistinctBy(l => l.MihonId).ToList(), false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogInformation("Searching {Source} for '{Name}' failed: {Error}", target.Name, name, e.Message);
            return ([], true);
        }
    }

    /// <summary>
    /// Re-points a library row at the same source's new id, in place: the same
    /// row, so its id, files, priority, flags, cutoff, switched-off state and
    /// scheduled scan all stay. Only where it points changes, plus its chapter
    /// links, merged by exact number (<see cref="ApplyRepoint"/>) in the same
    /// save, so the first scan finds the listing already known and queues no
    /// upgrades for it. No file is moved, renamed or reassigned, and no
    /// add-series merge or priority-upgrade pass runs (see ApplyOneAsync for why).
    ///
    /// The row as it was goes to <paramref name="undoFile"/> first, one JSON line,
    /// so a wrong match can be put back. Null when the row changed meanwhile.
    /// </summary>
    public async Task<RepointCounts?> RepointAsync(Guid seriesId, Guid rowId, string oldProviderId, ProviderSeriesDetails chosen,
        string undoFile, CancellationToken token)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var thumbs = scope.ServiceProvider.GetRequiredService<ThumbCacheService>();
        var providers = scope.ServiceProvider.GetRequiredService<SeriesProviderService>();
        var state = scope.ServiceProvider.GetRequiredService<SeriesStateService>();

        // The cover first, the way creating a row does: registering it saves
        // through this context, which has nothing else pending yet.
        string? thumb = null;
        if (!string.IsNullOrWhiteSpace(chosen.ThumbnailUrl))
        {
            thumb = chosen.ThumbnailUrl.StartsWith("/api/image/")
                ? (await thumbs.GetEtagAsync(chosen.ThumbnailUrl[11..], token).ConfigureAwait(false))?.Url
                : await thumbs.AddUrlAsync(chosen.ThumbnailUrl, chosen.MihonProviderId, token).ConfigureAwait(false);
        }

        SeriesProviderEntity row;
        string oldMihonId;
        bool paused;
        RepointCounts counts;
        using (await DownloadCommandService.SeriesLock.LockAsync(seriesId.ToString(), token).ConfigureAwait(false))
        {
            SeriesEntity? series = await db.Series.Include(s => s.Sources)
                .FirstOrDefaultAsync(s => s.Id == seriesId, token).ConfigureAwait(false);
            SeriesProviderEntity? found = series?.Sources.FirstOrDefault(p => p.Id == rowId);
            if (series == null || found == null || found.MihonProviderId != oldProviderId
                || Superseded(series, found, chosen.MihonProviderId, chosen.Provider))
                return null;
            row = found;
            oldMihonId = row.MihonId ?? "";
            paused = series.PauseDownloads;

            await File.AppendAllTextAsync(undoFile, UndoLine(row, chosen.MihonProviderId) + "\n", token).ConfigureAwait(false);
            counts = ApplyRepoint(row, chosen, thumb, DateTime.UtcNow);
            db.Touch(row, r => r.Chapters);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }
        // Saved on the new id: nothing after this may strand it. Its own scan
        // first (left off while switched off or paused), since without one the
        // row never updates; then the new id's catalogue row shows as in the
        // library and the old one's no longer, and renzo.json follows.
        await AfterCommitAsync(_logger, $"Re-pointing row {rowId} of series {seriesId}",
            ("rescheduling its scan", c => providers.RescheduleIfNeededAsync([row], true, paused, c)),
            ("updating the catalogue's in-library flags", c => providers.CheckIfTheStorageFlagsChangedTheInLibraryStatusOfLastSeriesAsync([row], [oldMihonId], c)),
            ("writing renzo.json", c => state.SyncToRenzoJsonAsync(seriesId, c))).ConfigureAwait(false);
        AppDbContext.BumpLibraryVersion();
        return counts;
    }

    /// <summary>
    /// The steps after a committed change, in order, each on its own: one that
    /// throws is logged and the rest still run, and none gets the caller's
    /// token, so neither a shutdown nor one failure leaves the change half
    /// followed up.
    /// </summary>
    internal static async Task AfterCommitAsync(ILogger logger, string what, params (string Step, Func<CancellationToken, Task> Run)[] steps)
    {
        foreach ((string step, Func<CancellationToken, Task> run) in steps)
        {
            try
            {
                await run(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "{What}: {Step} failed; the change itself is saved.", what, step);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="series"/> already has a row on the new id for
    /// <paramref name="row"/>'s scanlator group (added by hand, or another row
    /// followed first): two rows are never merged automatically. Only the same
    /// group counts, so rows of two groups on the old id are both followed.
    /// </summary>
    /// <param name="targetProvider">The new id's source name (<see cref="SameScanlator"/>).</param>
    internal static bool Superseded(SeriesEntity series, SeriesProviderEntity row, string? newProviderId, string? targetProvider) =>
        series.Sources.Any(p => p.Id != row.Id && p.MihonProviderId == newProviderId
            && (p.Scanlator == row.Scanlator || SameScanlator(row, p.Scanlator, targetProvider)));

    /// <summary>The row's pointer and per-chapter link state before a re-point, as one JSON line.</summary>
    private static string UndoLine(SeriesProviderEntity row, string? to) => System.Text.Json.JsonSerializer.Serialize(new
    {
        at = DateTime.UtcNow,
        rowId = row.Id,
        seriesId = row.SeriesId,
        to,
        mihonProviderId = row.MihonProviderId,
        mihonId = row.MihonId,
        bridgeItemInfo = row.BridgeItemInfo,
        url = row.Url,
        thumbnailUrl = row.ThumbnailUrl,
        isUninstalled = row.IsUninstalled,
        isLocal = row.IsLocal,
        consecutiveErrorCount = row.ConsecutiveErrorCount,
        lastErrorDate = row.LastErrorDate,
        chapters = row.Chapters.Select(c => new
        {
            number = c.Number,
            url = c.Url,
            providerIndex = c.ProviderIndex,
            isLocked = c.IsLocked,
            lockedCheckedAt = c.LockedCheckedAt,
            gaveUpAt = c.GaveUpAt,
            giveUpCount = c.GiveUpCount,
        }),
    });

    /// <summary>A row's own chapters by whole number (locked titles and notices left out): what its replacement must list.</summary>
    internal static HashSet<decimal> RowWholes(SeriesProviderEntity row) =>
        row.Chapters.Where(c => c.Number is decimal n && n >= 0 && !ModelExtensions.IsLockedChapterName(c.Name))
            .Select(c => decimal.Truncate(c.Number!.Value))
            .ToHashSet();

    /// <summary>
    /// What a row's series is searched for under on the new id: the row's own
    /// title, the series' and its sources' titles, then other names the sources
    /// list, but only distinctive ones ("Dear" or "One Day" find other works).
    /// </summary>
    internal static List<string> ReplacementNames(SeriesEntity series, SeriesProviderEntity row)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? n)
        {
            string k = AltTitles.Key(n);
            if (k.Length > 0 && seen.Add(k))
                names.Add(n!.Trim());
        }
        Add(row.Title);
        Add(series.Title);
        foreach (SeriesProviderEntity p in series.Sources)
            Add(p.Title);
        string?[] own = [series.Title, .. series.Sources.Select(p => p.Title)];
        foreach (SeriesProviderEntity p in series.Sources)
        {
            foreach (string alt in AltTitles.Parse(p.Description, own).Where(AltTitles.Distinctive))
                Add(alt);
        }
        // Names written in Latin letters first, after the row's own (NamesOf).
        return names.Select((n, i) => (n, i))
            .OrderByDescending(x => x.i == 0 || AltTitles.Latin(x.n))
            .ThenBy(x => x.i)
            .Select(x => x.n)
            .ToList();
    }

    /// <summary>
    /// The scan's scanlator scoping (SeriesCommandService, DownloadsExtensions):
    /// a row named after its source takes the chapters with no group or the
    /// source's name, any other row only its own group. A replacement group must
    /// pass it, or the row's scans would find nothing after the re-point.
    ///
    /// For a row named after its source, the new id's own name counts too: a
    /// versionId bump can come with a recased name, and a chapter with no group
    /// is listed under the new name (AugmentSeriesAsync) while the scan files it
    /// under the row's. Exactly that name, not any casing of it: chapters the
    /// site itself labels "VALIR SCANS" are dropped by the scan's exact match.
    /// </summary>
    /// <param name="targetProvider">The new id's source name.</param>
    internal static bool SameScanlator(SeriesProviderEntity row, string? group, string? targetProvider) =>
        string.IsNullOrEmpty(row.Scanlator) || row.Scanlator == row.Provider
            ? string.IsNullOrEmpty(group) || group == row.Provider
                || (!string.IsNullOrEmpty(targetProvider) && group == targetProvider)
            : group == row.Scanlator;

    /// <summary>The accept rule of <see cref="FindReplacementAsync"/>, on what it found.</summary>
    /// <param name="targetProvider">The new id's source name (<see cref="SameScanlator"/>).</param>
    /// <param name="wanted">The row's own whole chapter numbers (<see cref="RowWholes"/>).</param>
    /// <param name="sameTitle">Series ids on the new id whose title has the row title's key, measured or not.</param>
    /// <param name="groups">The scanlator groups measured, of any of them.</param>
    /// <param name="unreachable">A search or a read of the new id failed.</param>
    internal static ReplacementResult JudgeReplacement(SeriesProviderEntity row, string? targetProvider, int wanted, IReadOnlyCollection<string> sameTitle,
        IReadOnlyList<ReplacementCandidate> groups, bool unreachable)
    {
        ReplacementResult Fail(string status, string message, ReplacementCandidate? c = null) =>
            new() { Status = status, Message = message, Coverage = c?.Coverage ?? 0, Chosen = c?.Details };
        if (wanted == 0)
            return Fail(SourceIdChanges.LowCoverage, "It lists no chapter numbers to compare with.");
        if (sameTitle.Count == 0)
            return unreachable
                ? Fail(SourceIdChanges.Unreachable, "The new id could not be searched.")
                : Fail(SourceIdChanges.NoMatch, $"No series named '{row.Title}' on the new id.");
        if (sameTitle.Count > 1)
            return Fail(SourceIdChanges.Ambiguous, $"{sameTitle.Count} series named '{row.Title}' on the new id.");
        string id = sameTitle.First();
        List<ReplacementCandidate> mine = groups.Where(g => g.MihonId == id).ToList();
        if (mine.Count == 0)
            return unreachable
                ? Fail(SourceIdChanges.Unreachable, "Its chapters could not be read.")
                : Fail(SourceIdChanges.NoMatch, "It lists no chapters.");
        ReplacementCandidate? pick = mine.Where(g => SameScanlator(row, g.Scanlator, targetProvider))
            .OrderByDescending(g => g.Coverage)
            .FirstOrDefault();
        if (pick == null)
            return Fail(SourceIdChanges.NoMatch, $"Only other groups list it ({string.Join(", ", mine.Select(g => g.Scanlator))}).");
        if (pick.Suspect)
            return Fail(SourceIdChanges.NoMatch, "It lists far more chapters than the series has: likely another work.", pick);
        if (pick.Coverage < GoodEnough)
            return Fail(SourceIdChanges.LowCoverage, $"It lists {pick.Coverage:P0} of the row's chapters.", pick);
        return new ReplacementResult { Status = SourceIdChanges.Matched, Coverage = pick.Coverage, Chosen = pick.Details };
    }

    /// <summary>
    /// Points <paramref name="row"/> at <paramref name="chosen"/> and merges its
    /// chapter list by exact (decimal) number. A chapter on both takes the new
    /// link and index and its give-up state starts over. Its paywall state
    /// (IsLocked, LockedCheckedAt) stays: the scan only ever unlocks, so a lock
    /// cleared here would never come back. Its file, download state, deletion
    /// marks and name stay too. A number only the new id lists is added as a
    /// scan adds it; one only the row has stays as it is.
    /// Title, provider, scanlator, priority, flags, cutoff and switched-off state
    /// are not touched. Works on a detached copy too, for the dry run.
    /// </summary>
    internal static RepointCounts ApplyRepoint(SeriesProviderEntity row, ProviderSeriesDetails chosen, string? thumbnailUrl, DateTime now)
    {
        row.MihonProviderId = chosen.MihonProviderId;
        row.MihonId = chosen.MihonId;
        row.BridgeItemInfo = chosen.BridgeItemInfo;
        row.Url = chosen.Url;
        if (!string.IsNullOrEmpty(thumbnailUrl))
            row.ThumbnailUrl = thumbnailUrl;
        row.IsUninstalled = false;
        row.IsLocal = false;
        row.ConsecutiveErrorCount = 0;
        row.LastErrorDate = null;

        var incoming = new Dictionary<decimal, Chapter>();
        foreach (Chapter c in chosen.Chapters)
        {
            if (c.Number is decimal n)
                incoming.TryAdd(n, c);
        }
        int matched = 0, unmatched = 0, unmatchedFiles = 0;
        var have = new HashSet<decimal>();
        foreach (Chapter c in row.Chapters)
        {
            if (c.Number is not decimal n || !incoming.TryGetValue(n, out Chapter? nc))
            {
                unmatched++;
                if (!string.IsNullOrEmpty(c.Filename))
                    unmatchedFiles++;
                continue;
            }
            have.Add(n);
            c.Url = nc.Url;
            c.ProviderIndex = nc.ProviderIndex;
            c.GaveUpAt = null;
            c.GiveUpCount = 0;
            matched++;
        }
        bool established = row.Chapters.Count > 0;
        int added = 0;
        foreach (Chapter nc in chosen.Chapters)
        {
            if (nc.Number is not decimal n || !have.Add(n))
                continue;
            row.Chapters.Add(new Chapter
            {
                Name = nc.Name,
                Number = n,
                ProviderUploadDate = nc.ProviderUploadDate,
                Url = nc.Url,
                ProviderIndex = nc.ProviderIndex,
                DateFetched = established ? now : (ModelExtensions.HasRealUploadDate(nc.ProviderUploadDate) ? nc.ProviderUploadDate : null),
                ShouldDownload = row.ContinueAfterChapter < n || row.ContinueAfterChapter == null,
            });
            added++;
        }
        row.ChapterCount = row.Chapters.Count;
        return new RepointCounts(matched, added, unmatched, unmatchedFiles);
    }

    /// <summary>
    /// Downloads the re-pointed row's first scan would queue if the site lists
    /// what was measured: the scan's own selection on <paramref name="series"/>
    /// (a detached copy holding the re-pointed <paramref name="row"/>). None for
    /// a switched-off row or a paused series: they are not scanned.
    /// </summary>
    internal static int FirstScanDownloads(SeriesEntity series, SeriesProviderEntity row, ProviderSeriesDetails chosen, bool downloadAll, bool allowLocked)
    {
        if (row.IsDisabled || series.PauseDownloads)
            return 0;
        List<ParsedChapter> listing = chosen.Chapters.Where(c => c.Number != null)
            .Select(c => SeriesCommandService.ChapterToParsedChapter(c, row))
            .ToList();
        return series.GenerateDownloadsFromChapterData(row, listing, downloadAll, allowLocked).Count;
    }

    /// <summary>
    /// Re-downloads the next priority-upgrade pass (a priority change, or Apply
    /// to All) would queue from <paramref name="row"/> once it is live again: the
    /// holder rule of SeriesCommandService.QueuePriorityUpgradesAsync, change one,
    /// change all. A better source that lists a chapter takes it first, so this
    /// is what the row adds to the pass. Measured, never queued: the re-point
    /// runs no such pass.
    /// </summary>
    /// <param name="loginProviders">Providers the owner has a working site login for: their paid chapters count.</param>
    internal static int UpgradeExposure(SeriesEntity series, SeriesProviderEntity row, IReadOnlySet<string> loginProviders)
    {
        Dictionary<decimal, int> holders = series.Sources
            .Where(sp => !sp.IsUninstalled)
            .SelectMany(sp => sp.Chapters
                .Where(c => c.Number != null && !c.IsDeleted && !string.IsNullOrEmpty(c.Filename))
                .Select(c => (number: c.Number!.Value, priority: sp.Priority)))
            .GroupBy(x => x.number)
            .ToDictionary(g => g.Key, g => g.Min(x => x.priority));
        int count = 0;
        foreach (SeriesProviderEntity sp in series.Sources.Where(DownloadSourceSelector.CanDownloadFrom).OrderBy(p => p.Priority))
        {
            bool allowLocked = loginProviders.Contains(sp.Provider);
            foreach (Chapter c in sp.Chapters)
            {
                if (c.Number == null || c.IsDeleted || !string.IsNullOrEmpty(c.Filename))
                    continue;
                if (!holders.TryGetValue(c.Number.Value, out int holderPriority) || holderPriority <= sp.Priority)
                    continue;
                if (!allowLocked && (c.IsLocked || ModelExtensions.IsLockedChapterName(c.Name)))
                    continue;
                holders[c.Number.Value] = sp.Priority;
                if (sp.Id == row.Id)
                    count++;
            }
        }
        return count;
    }
}

/// <summary>One scanlator group of a series on the new id, as measured against the row.</summary>
internal sealed record ReplacementCandidate(string MihonId, string? Scanlator, double Coverage, bool Suspect, ProviderSeriesDetails? Details);

/// <summary>
/// What <see cref="SourceMigrationService.FindReplacementAsync"/> found: one of
/// SourceIdChanges' statuses (matched, ambiguous, no-match, low-coverage,
/// unreachable), the group to re-point to when matched.
/// </summary>
public sealed class ReplacementResult
{
    public string Status { get; init; } = "";
    public string? Message { get; init; }
    public double Coverage { get; init; }
    public ProviderSeriesDetails? Chosen { get; init; }
}

/// <summary>What a re-point did to the row's chapter list. Unmatched rows keep their old link; UnmatchedFiles of them hold a file.</summary>
public readonly record struct RepointCounts(int Matched, int Added, int Unmatched, int UnmatchedFiles);
