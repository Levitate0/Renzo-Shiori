using Mihon.ExtensionsBridge.Core.Extensions;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Extensions;
using RenzoBackend.Migration.Models;
using RenzoBackend.Models;
using RenzoBackend.Models.Abstractions;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Downloads;
using RenzoBackend.Services.Helpers;
using RenzoBackend.Utils;
using System.Runtime;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RenzoBackend.Extensions
{
    /// <summary>
    /// Extension methods for model conversion and formatting
    /// </summary>
    public static class ModelExtensions
    {

        public static LatestSeriesDto ToSeriesInfo(this LatestSerieEntity serie)
        {
            return new LatestSeriesDto
            {
                MihonId = serie.MihonId,
                MihonProviderId = serie.MihonProviderId,
                Provider = serie.Provider,
                Status = serie.Status,
                Title = serie.Title,
                ThumbnailUrl =serie.ThumbnailUrl,
                Language = serie.Language,
                ChapterCount = serie.ChapterCount,
                FetchDate = serie.FetchDate,
                Url = serie.Url,
                Artist = serie.Artist,
                Author = serie.Author,
                Description = serie.Description,
                Genre = serie.Genre,
                LatestChapter = serie.LatestChapter,
                LatestChapterTitle = serie.LatestChapterTitle,
                InLibrary = serie.InLibrary,
                SeriesId = serie.SeriesId
            };
        }
        public static DownloadSummary? ToDownloadSummary(this EnqueueEntity e)
        {
            if (string.IsNullOrEmpty(e.JobParameters))
                return null;

            ChapterDownload? ch = JsonSerializer.Deserialize<ChapterDownload>(e.JobParameters);
            if (ch == null)
                return null;

            DownloadSummary summary = ch.ToDownloadSummary();
            summary.Id = e.Id;
            summary.Status = e.Status;
            summary.DownloadDateUTC = e.FinishedDate;
            summary.ScheduledDateUTC = e.ScheduledDate;
            summary.Retries = e.RetryCount;
            return summary;
        }
        public static DownloadChapterInfo? ToDownloadChapterInfo(this EnqueueEntity e)
        {
            if (string.IsNullOrEmpty(e.JobParameters))
                return null;
            ChapterDownload? ch = JsonSerializer.Deserialize<ChapterDownload>(e.JobParameters);
            if (ch == null)
                return null;
            return new DownloadChapterInfo
            {
                ChapterNumber = ch.Chapter?.ParsedNumber,
                DownloadDateUTC = e.FinishedDate,
                Status = e.Status,
                Chapter = ch
            };
        }
        public static DownloadInfoDto? ToDownloadInfo(this EnqueueEntity e)
        {
            return e.ToDownloadSummary()?.ToInfoDto();
        }
        public static int GetLocalGroupMax(this Dictionary<string, int> counts, string group, int max)
        {
            if (!counts.TryGetValue(group, out int value))
                return max;
            int count = max - value;
            if (count < 0)
                return 0;
            return count;
        }
        public static ImportSeriesSnapshot ToImportSeriesSnapshot(this SeriesEntity series)
        {
            ArgumentNullException.ThrowIfNull(series);
            var info = new ImportSeriesSnapshot
            {
                Title = series.Title,
                Status = series.Status,
                Artist = series.Artist,
                Author = series.Author,
                Description = series.Description,
                Genre = series.Genre?.ToList() ?? new List<string>(),
                Type = series.Type ?? string.Empty,
                ChapterCount = series.ChapterCount,
                IdDisabled = false,
                Path = series.StoragePath
            };
            if (series.Sources != null && series.Sources.Count != 0)
            {
                info.Providers = series.Sources
                    .Select(sp => new ImportProviderSnapshot
                    {
                        Provider = sp.Provider,
                        Language = sp.Language,
                        Scanlator = sp.Scanlator,
                        Title = sp.Title,
                        ThumbnailUrl = sp.ThumbnailUrl,
                        Status = sp.Status,
                        IsStorage = sp.IsStorage,
                        IsDisabled = sp.IsDisabled,
                        ChapterCount = (int)sp.Chapters.Count(c => !c.IsDeleted),
                        ChapterList = sp.Chapters
                            .Where(c => !c.IsDeleted)
                            .Select(c => c.Number)
                            .DecimalRanges()
                            .Select(r => new StartStop
                            {
                                Start = r.From,
                                End = r.To
                            }).ToList(),
                        Archives = sp.Chapters
                            .Where(c => !c.IsDeleted)
                            .Select(a => new ProviderArchiveSnapshot
                            {
                                ArchiveName = a.Filename ?? "",
                                ChapterNumber = a.Number,
                                Index = a.ProviderIndex,
                                CreationDate = a.DownloadDate ?? a.ProviderUploadDate
                            }).ToList()
                    })
                    .ToList();
            }
            else
            {
                info.Providers = new List<ImportProviderSnapshot>();
            }
            info.LastUpdatedUTC = series.Sources?.Max(a => a.FetchDate);
            return info;
        }
        public static Models.Chapter ToChapter(this ParsedChapter chapter)
        {
            return new Models.Chapter
            {
                Name = chapter.ParsedName,
                Number = chapter.ParsedNumber,
                ProviderUploadDate = chapter.DateUpload.DateTime,
                Url = chapter.RealUrl,
                ProviderIndex = chapter.Index
            };
        }
        public static ExtensionSourceDto ToExtensionSource(this TachiyomiSource source)
        {
            return new ExtensionSourceDto
            {
                Name = source.Name,
                Language = source.Language,
            };
        }
        public static ExtensionEntryDto ToExtensionEntry(this RepositoryEntry e, string repoName, string repoId)
        {
            return new ExtensionEntryDto
            {
                Name = e.Name,
                OnlineRepositoryName = repoName,
                OnlineRepositoryId = repoId,
                IsLocal = e.IsLocal,
                Package = e.Extension.Package,
                Version = e.Extension.Version,
                DownloadUTC = e.DownloadUTC,
                Id = e.Id.ToString(),
                Nsfw = e.Extension.Nsfw == 1,
                Sources = e.Extension.Sources.Select(s => s.ToExtensionSource()).ToList()
            };
        }
        public static ExtensionEntryDto ToExtensionEntry(this TachiyomiExtension e, string repoName, string repoId)
        {
            return new ExtensionEntryDto
            {
                Name = e.Name,
                OnlineRepositoryName = repoName,
                OnlineRepositoryId = repoId,
                IsLocal = false,
                Package = e.Package,
                Version = e.Version,
                Nsfw = e.Nsfw == 1,
                Sources = e.Sources.Select(s => s.ToExtensionSource()).ToList()
            };
        }
        /*
        public static Models.ExtensionView ToExtensionInfo(this ProviderWithExtension p)
        {
            return new Models.ExtensionView
            {
                Package = p.Provider.SourcePackageName,
                Name = p.Provider.Name,
                ThumbnailUrl = p.Provider.ThumbnailUrl,
                IsStorage = p.Provider.IsStorage,
                IsEnabled = p.Provider.IsEnabled,
                IsBroken = p.Provider.IsBroken,
                IsDead = p.Provider.IsDead,
                IsInstaled = true,
                ActiveEntry = p.RepoGroup?.ActiveEntry ?? 0,
                AutoUpdate = p.RepoGroup?.AutoUpdate ?? true,
                Entries = p.RepoGroup?.Entries.Select(e => e.ToExtensionEntry(p.Provider.RepositoryName)).ToList() ?? [],
            };
        }*/
        public static ExtensionDto ToExtensionInfo(this TachiyomiExtension ext)
        {
            return new ExtensionDto
            {
                Package = ext.Package,
                Name = ext.ParsedName(),
                IsStorage = true,
                IsEnabled = false,
                IsBroken = false,
                IsDead = false,
                IsInstaled = false,
                ActiveEntry = 0,
                AutoUpdate = false,
            }; 
        }
        public static void FillProviderStorage(this ProviderStorageEntity storage, RepositoryEntry entry, TachiyomiExtension extension, TachiyomiSource source, ISourceInterop? interop, string repoName, string repoId)
        {
            storage.Name = source.Name;
            storage.Language = source.Language;
            storage.SourceRepositoryId = repoId;
            storage.SourceRepositoryName = repoName;
            storage.SourcePackageName = extension.Package;
            storage.SourceSourceId = source.Id;
            storage.ThumbnailUrl = "ext://" + Path.Combine(entry.GetRelativeVersionFolder(), entry.Icon.FileName);
            storage.SupportLatest = interop?.SupportsLatest ?? false;
            storage.IsNSFW = extension.Nsfw == 1;
            storage.IsDead = false;
            storage.IsEnabled = true;
            storage.IsBroken = false;
        }
        public static Manga? ToManga(this IBridgeItemInfo info)
        {
            if (string.IsNullOrEmpty(info.BridgeItemInfo))
                return null;
            return JsonSerializer.Deserialize<Manga>(info.BridgeItemInfo);
        }
        public static void FillBridgeItemInfo(this Manga manga, IBridgeItemInfo item)
        {
            if (manga == null)
                return;
            item.BridgeItemInfo = JsonSerializer.Serialize<Manga>(manga);
        }

        public static List<string> GetGenres(this Manga m)
        {
            return m.Genre?.Split(',').Select(a => a.Trim()).Where(a => !string.IsNullOrWhiteSpace(a)).ToList() ?? [];
        }

        public static string? CategoryFromPath(string path, SettingsDto settings)
        {
            var categories = settings?.Categories ?? [];
            if (categories.Length == 0)
                return null;
            path = path.Replace('\\', '/');
            foreach (var category in categories)
            {
                string b1 = category + "/";
                if (path.StartsWith(b1, true, System.Globalization.CultureInfo.InvariantCulture))
                    return category;
            }
            return null;
        }
        public static SeriesExtendedDto ToSeriesExtendedInfo(this SeriesEntity s, SettingsDto settings)
        {
            var info = new SeriesExtendedDto
            {
                Id = s.Id,
                Title = s.Title,
                Description = s.Description,
                ThumbnailUrl = s.ThumbnailUrl,
                Artist = s.Artist,
                PausedDownloads = s.PauseDownloads,
                Author = s.Author,
                Genre = s.Genre?.ToDistinctPascalCase() ?? new List<string>(),
                Status = s.Status,
                Type = s.Type,
                StartFromChapter =  s.StartFromChapter,
                ReleaseCadenceDays = s.ReleaseCadenceDays.HasValue
                    ? (int?)Math.Abs(s.ReleaseCadenceDays.Value)
                    : null,
                Path = EnvironmentSetup.IsDocker ? s.StoragePath : Path.Combine(settings.StorageFolder, s.StoragePath),
                IsActive = s.Sources.Any(a => !a.IsDisabled && !a.IsUninstalled),
                HasUnknown = s.Sources.Any(a => a.IsUnknown),
                StoragePath = s.StoragePath,
                ChapterCount = s.ChapterCount,
                ChapterList = s.Sources
                    .SelectMany(a => a.Chapters)
                    .Where(c => !c.IsDeleted && !string.IsNullOrEmpty(c.Filename))
                    .Select(c => c.Number).Distinct()
                    .FormatDecimalRanges(),
                Providers = new List<ProviderExtendedDto>(),
                Category = CategoryFromPath(s.StoragePath, settings),
                Nsfw = s.Nsfw,
                IsNsfw = s.Nsfw
                         || AdultContentClassifier.IsAdult(s.Genre)
                         || s.Sources.Any(src => AdultContentClassifier.IsAdult(src.Genre))
            };
            SmallProviderDto? lastChangeProvider = null;
            DateTime dt = DateTime.MinValue;

            if (s.Sources != null && s.Sources.Count > 0)
            {
                // Sort providers by the user's per-series Priority (0 = top). Local/unknown/disabled/
                // uninstalled sink to the bottom; within each tier Priority orders, storage breaks ties.
                // This is also the order the reorder UI shows and edits.
                var sortedSources = s.Sources
                    .OrderBy(p => p.IsUnknown || p.IsDisabled || p.IsUninstalled || p.IsLocal ? 1 : 0)
                    .ThenBy(p => p.Priority)
                    .ThenBy(p => p.IsStorage ? 0 : 1)
                    .ToList();
                foreach (var provider in sortedSources)
                {
                    var providerDto = new ProviderExtendedDto
                    {
                        Id = provider.Id,
                        Provider = provider.Provider,
                        Scanlator = provider.Scanlator,
                        Lang = provider.Language,
                        Title = provider.Title,
                        Url = provider.Url,
                        ThumbnailUrl = string.IsNullOrEmpty(provider.ThumbnailUrl) ? s.ThumbnailUrl : provider.ThumbnailUrl,
                        Artist = provider.Artist ?? "",
                        Author = provider.Author ?? "",
                        Description = provider.Description ?? "",
                        Genre = provider.Genre?.ToList() ?? new List<string>(),
                        Status = provider.Status,
                        ChapterCount = provider.ChapterCount ?? 0,
                        IsStorage = provider.IsStorage,
                        Priority = provider.Priority,
                        UseTitle = provider.IsTitle,
                        UseCover = provider.IsCover,
                        UseStatus = provider.IsStatus,
                        IsDisabled = provider.IsDisabled,
                        IsUninstalled = provider.IsUninstalled,
                        IsUnknown = provider.IsUnknown,
                        LastChangeUTC = provider.Chapters.MaxNull(a => a.DownloadDate) ?? provider.FetchDate ?? DateTime.MinValue,
                        LastChapter = provider.Chapters.MaxNull(c => c.Number),
                        LastUpdatedUTC = provider.Chapters.MaxNull(a=>a.ProviderUploadDate) ?? provider.FetchDate ?? DateTime.MinValue,
                        ContinueAfterChapter = provider.ContinueAfterChapter,
                        ChapterList = provider.Chapters.Select(c => c.Number).FormatDecimalRanges() ?? "",
                    };
                    //Previously matched, but search failed.
                    if (string.IsNullOrWhiteSpace(provider.BridgeItemInfo))
                        providerDto.IsUnknown = true;
                    info.Providers.Add(providerDto);

                    SmallProviderDto sm = provider.ToSmallProviderDto();
                    DateTime? lastm = provider.Chapters.Where(a => !string.IsNullOrEmpty(a.Filename))
                        .MaxNull(c => c.DownloadDate);
                    if (lastm != null && lastm > dt)
                    {
                        dt = lastm.Value;
                        lastChangeProvider = sm;
                    }

                }
                info.LastChapter = s.Sources.Max((SeriesProviderEntity a) => a.Chapters.Max((Models.Chapter c) => c.Number));
                if (lastChangeProvider != null)
                {
                    info.LastChangeProvider = lastChangeProvider;
                    info.LastChangeUTC = dt;
                }
                else
                {
                    info.LastChangeProvider = new SmallProviderDto();
                    info.LastChangeUTC = DateTime.MinValue;
                }
            }

            return info;
        }

        /// <summary>
        /// Builds the unified, series-level chapter list by merging every source's chapter rows by
        /// number. For each chapter it reports whether a file is on disk (and which source holds it,
        /// preferring the storage source) versus genuinely missing, plus the remote-capable sources
        /// that know the chapter (for the re-download picker). DB-only — no provider network call;
        /// "missing" reflects chapters Renzo Shiori knows about that are not on disk, not a live upstream
        /// diff. Intentionally-skipped chapters (below a provider's cutoff, ShouldDownload == false
        /// and never downloaded) are omitted so they don't inflate the missing count.
        /// </summary>
        // Paid/locked chapters are surfaced by coin/premium extensions as markers
        // in the chapter TITLE (the chapter API has no lock field). Matched on word
        // boundaries so ordinary titles ("The Locksmith", "Blocked Road") can't trip
        // it, plus the lock/coin emoji that several sources prepend.
        private static readonly Regex ChapterLockMarker = new(
            @"(🔒|🔐|🪙|\[locked\]|\block(ed)?\b|\bpremium\b|\bpaid\b|\bunlock\b|\bcoins?\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>True when a chapter title carries a paid/locked marker.</summary>
        public static bool IsLockedChapterName(string? name) =>
            !string.IsNullOrEmpty(name) && ChapterLockMarker.IsMatch(name!);

        // Only the symbols a source adds to a title; words like "Coin" may be the
        // real title and are left alone.
        private static readonly Regex ChapterLockSymbol = new(@"\s*(🔒|🔐|🪙|\[locked\])\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The title without the source's lock symbol, for a chapter just proven
        /// owned. Philia keeps "🔒" on a bought chapter's title, and the badge reads
        /// the title as well as IsLocked, so the chapter kept showing as paid.
        /// </summary>
        public static string? StripLockSymbol(string? name) =>
            string.IsNullOrEmpty(name) ? name : ChapterLockSymbol.Replace(name, " ").Trim();

        // A paid chapter only reveals itself when the pages are asked for — the
        // chapter API has no lock field, so the source throws instead ("Chapter
        // requires purchase (0 pages)…", "Log in via Webview and purchased this
        // chapter to read."). Deliberately narrow, so a genuine transient
        // failure ("Timed out waiting for page list") is never mistaken for a
        // paywall and silently marked unreadable.
        private static readonly Regex PurchaseErrorMarker = new(
            @"(requires?\s+purchase|must\s+purchase|purchased?\s+this\s+chapter|log\s*in\s+via\s+webview|unlock\s+to\s+read|coins?\s+to\s+read|premium\s+chapter|chapter\s+locked|coins?\s+required)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>True when a page-fetch failure means "this chapter is paid".</summary>
        public static bool IsPurchaseError(Exception? ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
                if (!string.IsNullOrEmpty(e.Message) && PurchaseErrorMarker.IsMatch(e.Message))
                    return true;
            return false;
        }

        /// <summary>
        /// A real source publish date. The extension API reports "no date" as 0,
        /// which we store as the Unix epoch (1970) — a non-null but meaningless
        /// value — so treat epoch/near-epoch and DateTime.MinValue as "unknown".
        /// This lets callers fall back to another source that does have a date.
        /// </summary>
        public static bool HasRealUploadDate(DateTime? d) => d.HasValue && d.Value.Year > 1971;

        /// <summary>
        /// The chapters a series' chapter list shows, each with every source row
        /// behind its number: the ONE membership rule. The series page
        /// (<see cref="ToChapterDetailList"/>) and the reader's list
        /// (ReaderService.GetChaptersAsync) both build from it, so a chapter the
        /// series page shows can be opened, and next/previous walks the same list.
        /// They used to differ: the reader dropped disabled sources (110 listed
        /// chapters, 45 of them downloaded, opened to "Chapter not found") and the
        /// series page dropped rows flagged deleted.
        ///
        /// A number stays listed while ANY source still attached to the series
        /// lists it, in any state: disabled, uninstalled, or with its row flagged
        /// deleted (a duplicate copy cleaned up, a bad archive removed). Taking it
        /// off needs every source that lists it removed from the series. A site
        /// going down, moving or being switched off does not break the list.
        ///
        /// Two filters remain: "hide decimal chapters", and the start of the
        /// range the user collects (the lowest chapter wanted or downloaded):
        /// below it only what is downloaded or wanted shows, the back catalogue
        /// the user opted out of stays hidden.
        /// </summary>
        /// <param name="applyHideDecimal">False only for the gap analysis, which has to see hidden parts (see ToChapterDetailList).</param>
        public static List<IGrouping<decimal, (SeriesProviderEntity Provider, Models.Chapter Chapter)>> ListedChapterGroups(this SeriesEntity s,
            bool applyHideDecimal = true)
        {
            ArgumentNullException.ThrowIfNull(s);
            bool hideDecimal = applyHideDecimal && s.HideDecimalChapters;
            var rows = (s.Sources ?? [])
                .SelectMany(p => p.Chapters
                    .Where(c => c.Number != null
                        && !(hideDecimal && c.Number.Value != decimal.Truncate(c.Number.Value)))
                    .Select(c => (Provider: p, Chapter: c)))
                .ToList();

            // Lowest chapter number the user is actually collecting (wanted or already
            // downloaded on any source) — the effective start of their range. Chapters
            // AT or ABOVE this floor are shown even if an individual chapter's
            // ShouldDownload flag got cleared (e.g. a fetched-but-not-downloaded latest
            // chapter whose download stalled, or a source-side paid gate). Chapters
            // strictly BELOW it are intentionally-excluded back-catalogue and stay hidden.
            //
            // A deleted flag does not lower anything here (a series whose rows are
            // all flagged still lists them), and neither does deleting downloads:
            // a chapter whose download the user deleted stays in the range.
            decimal? collectFloor = rows
                .Where(r => r.Chapter.ShouldDownload || !string.IsNullOrEmpty(r.Chapter.Filename) || r.Chapter.DownloadDeleted)
                .Select(r => r.Chapter.Number)
                .DefaultIfEmpty(null)
                .Min();

            return rows
                .GroupBy(r => r.Chapter.Number!.Value)
                .Where(g => g.Any(r => !string.IsNullOrEmpty(r.Chapter.Filename))
                    || g.Any(r => r.Chapter.ShouldDownload)
                    || (collectFloor != null && g.Key >= collectFloor.Value))
                .OrderBy(g => g.Key)
                .ToList();
        }

        /// <summary>Sources a chapter can be fetched from: the re-download picker, and what "locked" is judged on.</summary>
        public static bool IsRemoteCapable(this SeriesProviderEntity p) =>
            !p.IsUnknown && !p.IsLocal && !p.IsDisabled && !p.IsUninstalled && !string.IsNullOrEmpty(p.MihonProviderId);

        /// <summary>The row whose file a downloaded chapter reads from: best (lowest) Priority, then storage.</summary>
        public static (SeriesProviderEntity Provider, Models.Chapter Chapter) ChapterFileHolder(
            this IEnumerable<(SeriesProviderEntity Provider, Models.Chapter Chapter)> rows) =>
            rows.Where(r => !string.IsNullOrEmpty(r.Chapter.Filename) && !r.Chapter.IsDeleted)
                .OrderBy(r => r.Provider.Priority)
                .ThenBy(r => r.Provider.IsStorage ? 0 : 1)
                .FirstOrDefault();

        /// <summary>A chapter's title: the file holder's, else the first a live row gives, else any.</summary>
        public static string ChapterName(this IEnumerable<(SeriesProviderEntity Provider, Models.Chapter Chapter)> rows,
            (SeriesProviderEntity Provider, Models.Chapter Chapter) holder)
        {
            if (!string.IsNullOrWhiteSpace(holder.Chapter?.Name))
                return holder.Chapter!.Name!;
            return rows.OrderBy(r => r.Chapter.IsDeleted)
                .Select(r => r.Chapter.Name)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty;
        }

        /// <summary>
        /// Locked means "no source will give me this", not "some source wants money
        /// for it". ANY-source was the old test, and it marked a chapter paid
        /// whenever a single source paywalled it, even with another source offering
        /// the same chapter free; that is also what "prioritize free chapters"
        /// exploits. Judged on the live rows of sources that can actually be fetched
        /// from (a free copy on a disabled source is not reachable), falling back to
        /// every live row, then every row, so a locked chapter from a now-disabled
        /// source still reads as locked. The series page and the reader share it:
        /// the Hub ORs their two answers, so they have to agree.
        /// </summary>
        public static bool IsChapterLocked(this IReadOnlyCollection<(SeriesProviderEntity Provider, Models.Chapter Chapter)> rows, bool downloaded)
        {
            if (downloaded || rows.Count == 0)
                return false;
            static bool LockedOn(Models.Chapter c) => c.IsLocked || IsLockedChapterName(c.Name);
            var judged = rows.Where(r => !r.Chapter.IsDeleted && r.Provider.IsRemoteCapable()).ToList();
            if (judged.Count == 0)
                judged = rows.Where(r => !r.Chapter.IsDeleted).ToList();
            if (judged.Count == 0)
                judged = rows.ToList();
            return judged.All(r => LockedOn(r.Chapter));
        }

        /// <summary>
        /// Whole numbers whose parts (N.1, N.2…) two or more different sources list
        /// while none lists N itself: those sources' own numbering, not a missing N
        /// (see ChapterGaps).
        /// </summary>
        public static HashSet<int> WholesAbsentBySources(
            this IEnumerable<IGrouping<decimal, (SeriesProviderEntity Provider, Models.Chapter Chapter)>> groups)
        {
            var list = groups.ToList();
            HashSet<decimal> numbers = list.Select(g => g.Key).ToHashSet();
            return PartsListedBySources(list).Where(n => !numbers.Contains(n)).ToHashSet();
        }

        /// <summary>
        /// Whole numbers whose parts (N.1, N.2…) two or more different sources list.
        /// What none of them lists under N is their numbering, not a missing part.
        /// </summary>
        public static HashSet<int> PartsListedBySources(
            this IEnumerable<IGrouping<decimal, (SeriesProviderEntity Provider, Models.Chapter Chapter)>> groups) =>
            groups
                .Where(g => g.Key != decimal.Truncate(g.Key) && g.Key >= 0 && g.Key <= int.MaxValue)
                .GroupBy(g => (int)decimal.Truncate(g.Key))
                .Where(byWhole => byWhole.SelectMany(g => g.Select(r => r.Provider.Provider)).Distinct().Count() >= 2)
                .Select(byWhole => byWhole.Key)
                .ToHashSet();

        private static readonly System.Text.RegularExpressions.Regex NamedNumber = new(
            @"(?:^|\b)Ch(?:apter)?\.?\s*(\d+(?:\.\d+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// The missing-chapter runs for the series page, keyed by the listed number
        /// each comes after (ChapterGaps). Worked out on everything the sources
        /// list, hidden decimals included: with "hide decimal chapters" on, a
        /// chapter numbered only in parts (37.1, 37.2) is hidden, not missing.
        /// A chapter 0 that is really another listed chapter misnumbered (a row 0
        /// named "Vol.12 Ch.99" next to 99) is left out, or the whole stretch
        /// below its twin read as missing. Never fails the list: gap rows are a
        /// courtesy, the chapters are what matters.
        /// </summary>
        private static Dictionary<decimal, List<Services.Series.ChapterGaps.Range>> MissingRuns(SeriesEntity s,
            List<IGrouping<decimal, (SeriesProviderEntity Provider, Models.Chapter Chapter)>> visible)
        {
            try
            {
                var all = s.HideDecimalChapters ? s.ListedChapterGroups(applyHideDecimal: false) : visible;
                HashSet<decimal> keys = all.Select(g => g.Key).ToHashSet();
                decimal? lowest = all.Count == 0 ? null : all.Min(g => g.Key);
                bool Misnumbered(IGrouping<decimal, (SeriesProviderEntity Provider, Models.Chapter Chapter)> g) =>
                    g.Key == lowest && g.All(r =>
                    {
                        var m = NamedNumber.Match(r.Chapter.Name ?? string.Empty);
                        return m.Success && decimal.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Number,
                                   System.Globalization.CultureInfo.InvariantCulture, out decimal named)
                               && named != g.Key && keys.Contains(named);
                    });
                var input = all.Where(g => !Misnumbered(g)).ToList();
                var runs = Services.Series.ChapterGaps.Find(input.Select(g => g.Key),
                    input.WholesAbsentBySources(), input.PartsListedBySources());
                if (!s.HideDecimalChapters)
                    return runs;

                // Decimals hidden: keep whole-number runs only, under the visible
                // chapter just below them.
                List<decimal> shown = visible.Select(g => g.Key).OrderBy(k => k).ToList();
                var moved = new Dictionary<decimal, List<Services.Series.ChapterGaps.Range>>();
                foreach (var (after, list) in runs.OrderBy(kv => kv.Key))
                {
                    foreach (var r in list.Where(r => r.From == decimal.Truncate(r.From)))
                    {
                        int at = shown.BinarySearch(r.From);
                        int below = (at >= 0 ? at : ~at) - 1;
                        if (below < 0)
                            continue;
                        if (!moved.TryGetValue(shown[below], out var into))
                            moved[shown[below]] = into = [];
                        into.Add(r);
                    }
                }
                return moved;
            }
            catch (Exception)
            {
                return [];
            }
        }

        public static List<ChapterDetailDto> ToChapterDetailList(this SeriesEntity s)
        {
            ArgumentNullException.ThrowIfNull(s);
            List<SeriesProviderEntity> sources = s.Sources?.ToList() ?? new List<SeriesProviderEntity>();

            // Sources we can actually fetch from — drives the re-download picker.
            List<SeriesProviderEntity> remoteCapable = sources.Where(p => p.IsRemoteCapable()).ToList();

            var groups = s.ListedChapterGroups();
            Dictionary<decimal, List<Services.Series.ChapterGaps.Range>> gaps = MissingRuns(s, groups);

            var result = new List<ChapterDetailDto>();
            foreach (var g in groups)
            {
                var rows = g.ToList();
                var holder = rows.ChapterFileHolder();
                bool downloaded = holder.Chapter != null;

                string name = rows.ChapterName(holder);

                // Chapter-list date: the source publish date is preferred (that's
                // "when it came out"). Prefer the holder's real date, else a real
                // date from any other source; when NO source reports one, fall back
                // to the "found" date (when the update scan first saw it).
                DateTime? uploadDate = HasRealUploadDate(holder.Chapter?.ProviderUploadDate)
                    ? holder.Chapter!.ProviderUploadDate
                    : rows.Select(r => r.Chapter.ProviderUploadDate)
                          .Where(HasRealUploadDate)
                          .DefaultIfEmpty(null)
                          .Max();
                if (uploadDate == null)
                    uploadDate = rows.Select(r => r.Chapter.DateFetched)
                                     .Where(d => d != null)
                                     .DefaultIfEmpty(null)
                                     .Min(); // first time it was found on any source

                // Source page URL (to open a purchase/unlock page).
                string? chapterUrl = rows
                    .OrderBy(r => r.Chapter.IsDeleted)
                    .Select(r => r.Chapter.Url)
                    .FirstOrDefault(u => !string.IsNullOrEmpty(u));

                List<ChapterSourceDto> available = remoteCapable
                    .Where(p => p.Chapters.Any(c => !c.IsDeleted && c.Number == g.Key))
                    .Select(p => new ChapterSourceDto { Id = p.Id, Name = p.Provider })
                    .ToList();

                result.Add(new ChapterDetailDto
                {
                    Number = g.Key,
                    Name = name,
                    Downloaded = downloaded,
                    SourceProviderId = holder.Provider?.Id,
                    SourceProviderName = holder.Provider?.Provider,
                    UploadDate = uploadDate,
                    Url = chapterUrl,
                    Locked = rows.IsChapterLocked(downloaded),
                    AvailableProviders = available,
                    MissingAfter = gaps.TryGetValue(g.Key, out var runs)
                        ? runs.Select(r => new MissingChapterRangeDto { From = r.From, To = r.To, Count = r.Count }).ToList()
                        : null,
                });
            }

            return result.OrderByDescending(c => c.Number).ToList();
        }
    }


}

