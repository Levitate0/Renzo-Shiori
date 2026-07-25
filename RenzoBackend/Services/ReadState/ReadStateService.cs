using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.ReadState;
using RenzoBackend.Services.Settings;
using System.Text.Json;

namespace RenzoBackend.Services.ReadState;

/// <summary>
/// Service for reading and writing read state to renzo.json files.
/// Read state is NEVER stored in the database.
///
/// All writes to renzo.json are delegated to RenzoJsonService which provides
/// atomic read-modify-write with per-file locking, preventing lost-update races
/// with concurrent writes from SeriesStateService.
/// </summary>
public class ReadStateService
{
    private readonly ReadStateCacheService _cacheService;
    private readonly SettingsService _settingsService;
    private readonly RenzoJsonService _renzoJsonService;
    private readonly ReadStateChangeNotifier _changeNotifier;
    private readonly ILogger _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };

    public ReadStateService(ILogger<ReadStateService> logger, ReadStateCacheService cacheService,
        SettingsService settingsService, RenzoJsonService renzoJsonService,
        ReadStateChangeNotifier changeNotifier)
    {
        _logger = logger;
        _cacheService = cacheService;
        _settingsService = settingsService;
        _renzoJsonService = renzoJsonService;
        _changeNotifier = changeNotifier;
    }

    /// <summary>
    /// Gets the read state for a specific chapter for a user.
    /// </summary>
    public ChapterReadState? GetReadState(string username, string seriesStoragePath, decimal chapterNumber)
    {
        var states = GetSeriesReadStates(username, seriesStoragePath);
        return states.FirstOrDefault(s => s.ChapterNumber == chapterNumber);
    }

    /// <summary>
    /// Gets all chapter read states for a user in a series.
    /// </summary>
    public List<ChapterReadState> GetSeriesReadStates(string username, string seriesStoragePath)
    {
        // Try cache first
        var cached = _cacheService.GetCachedReadStates(username, seriesStoragePath);
        if (cached != null)
            return cached;

        // Load from renzo.json
        var snapshot = _renzoJsonService.Load(seriesStoragePath);
        var userStates = snapshot?.UserReadStates?
            .FirstOrDefault(u => u.Username == username)?
            .Chapters ?? [];

        // Cache and return
        _cacheService.SetCachedReadStates(username, seriesStoragePath, userStates);
        return userStates;
    }

    public List<(SeriesEntity Series, List<ChapterReadState> ChaptersReadState)> GetUserSeriesReadStates(string username, List<SeriesEntity> series)
    {
        List<(SeriesEntity Series, List<ChapterReadState> ChaptersReadState)> result = new();
        foreach (SeriesEntity s in series)
        {
            var readStates = GetSeriesReadStates(username, s.StoragePath);
            result.Add((s, readStates));
        }
        return result;
    }
    public void PrefetchCache(List<SeriesEntity> series)
    {
        foreach (SeriesEntity s in series)
        {
            var snapshot = _renzoJsonService.Load(s.StoragePath);
            if (snapshot?.UserReadStates == null)
                continue;
            foreach (var userState in snapshot.UserReadStates)
            {
                var cached = _cacheService.GetCachedReadStates(userState.Username, s.StoragePath);
                if (cached == null)
                    _cacheService.SetCachedReadStates(userState.Username, s.StoragePath, userState.Chapters);
            }
        }
    }
    /// <summary>
    /// Gets all user read states (all users) from a series.
    /// </summary>
    public List<UserReadStateSnapshot> GetAllUserReadStates(string seriesStoragePath)
    {
        var snapshot = _renzoJsonService.Load(seriesStoragePath);
        return snapshot?.UserReadStates ?? [];
    }

    private static (bool update, ChapterReadState state) GetOrCreateChapterState(List<ChapterReadState> userState, decimal chapterNumber)
    {
        var chapterState = userState.FirstOrDefault(c => c.ChapterNumber == chapterNumber);
        if (chapterState == null)
        {
            chapterState = new ChapterReadState { ChapterNumber = chapterNumber };
            userState.Add(chapterState);
            return (true, chapterState);
        }
        return (false, chapterState);
    }

    /// <summary>
    /// Sets the read state for a chapter (page-level tracking).
    /// Also publishes a change event so that external scrobbler providers
    /// can be debounce-synced after 1 minute of inactivity.
    ///
    /// The entire read-modify-write cycle is atomic under a per-file lock
    /// via RenzoJsonService, preventing lost updates with concurrent
    /// SeriesStateService writes.
    /// </summary>
    public void SetReadState(string username, Guid userId, Guid seriesId, string filename, string? deviceid, string? devicename, string seriesStoragePath, decimal chapterNumber, int lastReadPage, int totalPages, bool updateLower = true)
    {
        var states = GetSeriesReadStates(username, seriesStoragePath);
        (bool update, ChapterReadState chapterState) = GetOrCreateChapterState(states, chapterNumber);
        float progress = totalPages > 0 ? (float)lastReadPage / totalPages : 0;
        if (progress > 1) progress = 1; // Cap at 100%
        if (progress < 0) progress = 0;
        // A completed chapter keeps its 100%: re-reading (or scrolling up through
        // it in the infinite scroll) must not drag the stored progress back down.
        if (chapterState.IsCompleted && progress < chapterState.Progress)
            return;
        if ((updateLower && progress < chapterState.Progress) || (progress > chapterState.Progress))
        {
            if (progress != chapterState.Progress)
            {
                chapterState.Progress = progress;
                // Completion is sticky against progress moving backward: reading to
                // the end completes a chapter, but scrolling back (a lower progress)
                // must not un-complete it. Only the explicit mark toggle un-reads.
                chapterState.IsCompleted = chapterState.IsCompleted || lastReadPage >= totalPages;
                chapterState.LastReadDeviceId = deviceid ?? "";
                chapterState.LastReadDeviceName = string.IsNullOrEmpty(devicename) ? "Renzo Shiori" : devicename;
                chapterState.LastReadFilename = filename;
                chapterState.LastReadAt = DateTime.UtcNow;
                update = true;
            }
        }
        if (update)
        {
            // Atomic read-modify-write under per-file lock:
            // Reads the current on-disk snapshot (which may have been modified by
            // SeriesStateService), applies UserReadStates, and writes back.
            _renzoJsonService.Modify(seriesStoragePath, snapshot =>
            {
                var current = snapshot ?? new ImportSeriesSnapshot
                {
                    Version = 2,
                    UserReadStates = []
                };

                if (current.UserReadStates == null)
                    current.UserReadStates = [];

                UserReadStateSnapshot? ushot = current.UserReadStates
                    .FirstOrDefault(a => a.Username == username);
                if (ushot == null)
                {
                    ushot = new UserReadStateSnapshot { Username = username, Chapters = [] };
                    current.UserReadStates.Add(ushot);
                }
                ushot.Chapters = states;
                current.Version = 2;
                return current;
            });

            // Notify background scrobbler sync (debounced) - outside the lock
            _changeNotifier.Notify(userId, seriesId);
        }
    }


    /// <summary>
    /// Force-sets a chapter's completion state (built-in reader "mark read/unread").
    /// Unlike SetReadState's monotonic progress rules, this sets exactly what the
    /// user asked for, in both directions.
    /// </summary>
    public void SetCompleted(string username, Guid userId, Guid seriesId, string seriesStoragePath, decimal chapterNumber, bool completed, string? filename = null)
    {
        var states = GetSeriesReadStates(username, seriesStoragePath);
        (_, ChapterReadState chapterState) = GetOrCreateChapterState(states, chapterNumber);
        chapterState.IsCompleted = completed;
        chapterState.Progress = completed ? 1f : 0f;
        chapterState.LastReadDeviceName = "Renzo Shiori Reader";
        if (filename != null)
            chapterState.LastReadFilename = filename;
        chapterState.LastReadAt = DateTime.UtcNow;
        PersistStates(username, seriesStoragePath, states);
        _changeNotifier.Notify(userId, seriesId);
    }

    /// <summary>
    /// Toggles/sets a chapter bookmark. Bookmarks live alongside read state in
    /// renzo.json and never affect progress or scrobbling.
    /// </summary>
    public void SetBookmark(string username, string seriesStoragePath, decimal chapterNumber, bool bookmarked)
    {
        var states = GetSeriesReadStates(username, seriesStoragePath);
        (_, ChapterReadState chapterState) = GetOrCreateChapterState(states, chapterNumber);
        chapterState.Bookmarked = bookmarked;
        PersistStates(username, seriesStoragePath, states);
    }

    private void PersistStates(string username, string seriesStoragePath, List<ChapterReadState> states)
    {
        _renzoJsonService.Modify(seriesStoragePath, snapshot =>
        {
            var current = snapshot ?? new ImportSeriesSnapshot { Version = 2, UserReadStates = [] };
            current.UserReadStates ??= [];
            UserReadStateSnapshot? ushot = current.UserReadStates.FirstOrDefault(a => a.Username == username);
            if (ushot == null)
            {
                ushot = new UserReadStateSnapshot { Username = username, Chapters = [] };
                current.UserReadStates.Add(ushot);
            }
            ushot.Chapters = states;
            current.Version = 2;
            return current;
        });
    }

    /// <summary>
    /// Calculates the number of unread chapters for a user in a series.
    /// </summary>
    public int GetUnreadChaptersCount(string username, string seriesStoragePath, int totalChapters)
    {
        var readStates = GetSeriesReadStates(username, seriesStoragePath);
        int completedCount = readStates.Count(c => c.IsCompleted);
        return Math.Max(0, totalChapters - completedCount);
    }

    /// <summary>
    /// Imports user read states into a series' renzo.json.
    /// Used during the import wizard flow.
    /// </summary>
    public void ImportUserReadStates(string seriesStoragePath, List<UserReadStateSnapshot> readStates)
    {
        // Atomic read-modify-write under per-file lock
        _renzoJsonService.Modify(seriesStoragePath, snapshot =>
        {
            var current = snapshot ?? new ImportSeriesSnapshot
            {
                Version = 2,
                UserReadStates = []
            };

            if (current.UserReadStates == null)
                current.UserReadStates = [];

            foreach (var incoming in readStates)
            {
                var existing = current.UserReadStates.FirstOrDefault(u => u.Username == incoming.Username);
                if (existing != null)
                {
                    // Merge chapters (incoming ones replace existing for same chapter number)
                    foreach (var chapter in incoming.Chapters)
                    {
                        var existingChapter = existing.Chapters.FirstOrDefault(c => c.ChapterNumber == chapter.ChapterNumber);
                        if (existingChapter != null)
                        {
                            existingChapter.Progress = chapter.Progress;
                            existingChapter.IsCompleted = chapter.IsCompleted;
                            existingChapter.LastReadDeviceId = chapter.LastReadDeviceId;
                            existingChapter.LastReadDeviceName = chapter.LastReadDeviceName;
                            existingChapter.LastReadFilename = chapter.LastReadFilename;
                            existingChapter.LastReadAt = chapter.LastReadAt;
                        }
                        else
                        {
                            existing.Chapters.Add(chapter);
                        }
                    }
                }
                else
                {
                    current.UserReadStates.Add(incoming);
                }
            }

            current.Version = 2;
            return current;
        });
    }
}