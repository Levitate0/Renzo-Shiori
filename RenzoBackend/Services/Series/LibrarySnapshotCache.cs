using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RenzoBackend.Data;
using RenzoBackend.Models.Dto;

namespace RenzoBackend.Services.Series;

/// <summary>
/// Per-series derived data for the three library-wide read paths — the library
/// grid, the Updates feed and the History feed.
///
/// All three used to load every series with every source and deserialize every
/// chapter list (~100 MB of JSON on a 358-series library) on every request, and
/// the Hub polls the library every 15 s. Whole-library caching does not help:
/// each finished download saves a source, so a busy queue would invalidate it
/// faster than the poll. This keeps one snapshot per series and rebuilds only
/// the series whose rows were saved since (<see cref="AppDbContext.SeriesVersion"/>).
///
/// Snapshots are templates. Everything handed out is a fresh copy, because the
/// controllers rewrite thumbnail URLs on the DTOs they return.
/// </summary>
public class LibrarySnapshotCache
{
    public sealed class Snapshot
    {
        public required long Version { get; init; }
        public required long Global { get; init; }
        public required Guid Id { get; init; }
        public required string Title { get; init; }
        public required string? ThumbnailUrl { get; init; }
        public required string? StoragePath { get; init; }
        /// <summary>The library-grid DTO, serialized so each request deserializes its own copy.</summary>
        public required byte[] InfoJson { get; init; }
        /// <summary>Updates-feed rows for this series, newest first (templates, copy before returning).</summary>
        public required List<UpdateFeedItemDto> UpdateRows { get; init; }
        /// <summary>Chapter number → (name, filename), for History.</summary>
        public required Dictionary<decimal, (string? Name, string? Filename)> Chapters { get; init; }
        /// <summary>The library grid's LastChapter: the highest number on any source.</summary>
        public required decimal? LastChapter { get; init; }
        /// <summary>The library grid's IsNsfw (flag or classifier).</summary>
        public required bool IsNsfw { get; init; }
        /// <summary>Chapter number → the earliest real publish date any source gives it.</summary>
        public required Dictionary<decimal, DateTime> Published { get; init; }
    }

    private readonly ConcurrentDictionary<Guid, Snapshot> _snapshots = new();
    // One rebuild at a time: after a restart the web UI and a Hub poll arrive
    // together, and two concurrent full-library parses are twice the memory.
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private readonly ILogger<LibrarySnapshotCache> _logger;

    // Same cap the Updates feed always used: a huge back-catalogue can't
    // dominate the merge, and the feed only surfaces recent releases anyway.
    private const int PerSeriesUpdateCap = 300;

    public LibrarySnapshotCache(ILogger<LibrarySnapshotCache> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Snapshots for every series the requester can see, in the database's
    /// order, rebuilding only the stale ones.
    /// </summary>
    public async Task<List<Snapshot>> GetAsync(AppDbContext db, SettingsDto settings, Guid requesterId, bool allowAll, CancellationToken token)
    {
        IQueryable<Models.Database.SeriesEntity> visible = db.Series.AsNoTracking();
        if (!allowAll)
            visible = visible.Where(s => s.OwnerId == requesterId || s.OwnerId == Guid.Empty);
        List<Guid> ids = await visible.Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);

        List<Guid> stale = ids.Where(IsStale).ToList();
        if (stale.Count > 0)
        {
            await _buildLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Re-check under the lock: a concurrent request may have just
                // built them.
                stale = stale.Where(IsStale).ToList();
                if (stale.Count > 0)
                    await BuildAsync(db, settings, stale, token).ConfigureAwait(false);
            }
            finally
            {
                _buildLock.Release();
            }
        }

        // A full listing is also the moment to forget series that no longer exist.
        if (allowAll && _snapshots.Count > ids.Count)
        {
            var live = new HashSet<Guid>(ids);
            foreach (Guid gone in _snapshots.Keys.Where(k => !live.Contains(k)).ToList())
                _snapshots.TryRemove(gone, out _);
        }

        var result = new List<Snapshot>(ids.Count);
        foreach (Guid id in ids)
        {
            if (_snapshots.TryGetValue(id, out Snapshot? snap))
                result.Add(snap);
        }
        return result;
    }

    private bool IsStale(Guid id) =>
        !_snapshots.TryGetValue(id, out Snapshot? snap)
        || snap.Version != AppDbContext.SeriesVersion(id)
        || snap.Global != AppDbContext.LibraryVersion;

    private async Task BuildAsync(AppDbContext db, SettingsDto settings, List<Guid> ids, CancellationToken token)
    {
        long global = AppDbContext.LibraryVersion;
        // Chunked: SQLite caps bound parameters, and a first build after a
        // restart is the whole library.
        foreach (Guid[] chunk in ids.Chunk(200))
        {
            // Versions captured BEFORE the read: a save landing mid-read bumps
            // the version past what is stored, so the next request rebuilds.
            Dictionary<Guid, long> versions = chunk.ToDictionary(id => id, AppDbContext.SeriesVersion);
            List<Models.Database.SeriesEntity> series = await db.Series
                .Include(s => s.Sources)
                .AsNoTracking()
                .Where(s => chunk.Contains(s.Id))
                .ToListAsync(token).ConfigureAwait(false);
            foreach (Models.Database.SeriesEntity s in series)
            {
                try
                {
                    _snapshots[s.Id] = Build(s, settings, versions[s.Id], global);
                }
                catch (Exception ex)
                {
                    // One malformed series must not take the library down; it is
                    // simply absent until its next save.
                    _logger.LogWarning(ex, "Library snapshot: couldn't build '{Title}'; leaving it out.", s.Title);
                }
            }
        }
    }

    private static Snapshot Build(Models.Database.SeriesEntity s, SettingsDto settings, long version, long global)
    {
        // Suwayomi-style "found" time: when the update scan first discovered a
        // chapter (DateFetched). Chapters recorded before that was tracked fall
        // back to a stable historical date (real publish date, else download
        // date) so they sort sensibly instead of flooding the top.
        static DateTime? FoundTime(Models.Chapter c) =>
            c.DateFetched
            ?? (Extensions.ModelExtensions.HasRealUploadDate(c.ProviderUploadDate) ? c.ProviderUploadDate : null)
            ?? c.DownloadDate;

        // One entry per chapter number. When several sources carry the same
        // chapter, use the EARLIEST time it was found on any of them, so adding
        // a new source doesn't resurface a whole back-catalogue.
        //
        // Deleted rows count towards that earliest time, though they are never
        // shown. A source that is disabled has its unread chapters marked deleted,
        // and the backup source's copy of the same chapter used to come up as
        // found-just-now: a chapter the reader had for weeks, back at the top of
        // Updates as if it were new.
        Dictionary<decimal, DateTime> firstFound = s.Sources
            .SelectMany(p => p.Chapters)
            .Select(c => (c.Number, Found: FoundTime(c)))
            .Where(x => x.Number != null && x.Found != null)
            .GroupBy(x => x.Number!.Value)
            .ToDictionary(g => g.Key, g => g.Min(x => x.Found!.Value));
        List<UpdateFeedItemDto> rows = s.Sources
            .SelectMany(p => p.Chapters, (p, c) => (Provider: p, Chapter: c, Found: FoundTime(c)))
            .Where(x => !x.Chapter.IsDeleted && x.Chapter.Number != null && x.Found != null)
            .GroupBy(x => x.Chapter.Number)
            .Select(g => g.OrderBy(x => x.Found).First())
            .Select(x => (x.Provider, x.Chapter, Found: firstFound[x.Chapter.Number!.Value]))
            .OrderByDescending(x => x.Found)
            .Take(PerSeriesUpdateCap)
            .Select(x => new UpdateFeedItemDto
            {
                SeriesId = s.Id,
                SeriesTitle = s.Title,
                ThumbnailUrl = s.ThumbnailUrl,
                Kind = UpdateFeedItemDto.KindNewChapter,
                ChapterNumber = x.Chapter.Number,
                ChapterName = x.Chapter.Name,
                Provider = x.Provider.Provider,
                Timestamp = x.Found,
            })
            .ToList();
        if (s.DateAdded != null)
        {
            rows.Add(new UpdateFeedItemDto
            {
                SeriesId = s.Id,
                SeriesTitle = s.Title,
                ThumbnailUrl = s.ThumbnailUrl,
                Kind = UpdateFeedItemDto.KindSeriesAdded,
                Timestamp = s.DateAdded.Value,
            });
        }

        // Chapter names live on the series, not on read state.
        Dictionary<decimal, (string?, string?)> chapters = s.Sources
            .SelectMany(p => p.Chapters)
            .Where(c => !c.IsDeleted && c.Number != null)
            .GroupBy(c => c.Number!.Value)
            .ToDictionary(g => g.Key, g => (g.First().Name, g.First().Filename));

        // When each chapter came out, for release notifications: a source can
        // carry a chapter it only found today that was published weeks ago.
        Dictionary<decimal, DateTime> published = s.Sources
            .SelectMany(p => p.Chapters)
            .Where(c => !c.IsDeleted && c.Number != null
                && Extensions.ModelExtensions.HasRealUploadDate(c.ProviderUploadDate))
            .GroupBy(c => c.Number!.Value)
            .ToDictionary(g => g.Key, g => g.Min(c => c.ProviderUploadDate!.Value));

        SeriesInfoDto info = s.ToSeriesInfo(settings);
        return new Snapshot
        {
            Version = version,
            Global = global,
            Id = s.Id,
            Title = s.Title,
            ThumbnailUrl = s.ThumbnailUrl,
            StoragePath = s.StoragePath,
            InfoJson = JsonSerializer.SerializeToUtf8Bytes(info),
            LastChapter = info.LastChapter,
            IsNsfw = info.IsNsfw,
            Published = published,
            UpdateRows = rows,
            Chapters = chapters,
        };
    }
}
