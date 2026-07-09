using System.Globalization;
using com.sun.tools.@internal.xjc;
using RensaioBackend.Extensions;
using RensaioBackend.Models;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Helpers;
using RensaioBackend.Services.Import.KavitaParser;
using RensaioBackend.Services.Import.Models;
using RensaioBackend.Services.Jobs.Report;
using Mihon.ExtensionsBridge.Core.Extensions;
using Mihon.ExtensionsBridge.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Parser = RensaioBackend.Services.Import.KavitaParser.Parser;
using SeriesInfo = RensaioBackend.Services.Import.KavitaParser.SeriesInfo;

namespace RensaioBackend.Services.Import
{
    public class SeriesScanner
    {
        private readonly BasicParser _parser;
        private readonly ILogger _logger;

        private static readonly Regex RensaioRegex = new Regex("^\\[(?<provider>[^\\]]+)\\](?:\\[(?<lang>[^\\]]+)\\])?\\s+(?<title>.+?)(?:\\s+(?<chapterNumber>-?\\d+(?:\\.\\d+)?))?\\s*(?:\\((?<chapterName>[^)]+)\\))?$",
            RegexOptions.Compiled | RegexOptions.Singleline);

        public SeriesScanner(ILogger<SeriesScanner> logger)
        {
            _parser = new BasicParser();
            _logger = logger;
        }

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        /// <summary>
        /// Derives a clean display title from a folder name when no other title
        /// information is available (used by title-only scanning, see
        /// <see cref="ProcessDirectoryAsync"/>).
        /// </summary>
        private static string TitleFromFolderName(string seriesFolder)
        {
            int idx = seriesFolder.LastIndexOfAny(['\\', '/']);
            string title = idx >= 0
                ? seriesFolder[(idx + 1)..]
                : seriesFolder;
            title = title.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            title = title.Replace("_", " ").Replace(".", " ");
            title = title.Replace("  ", " ").Replace("  ", " ").Trim();
            return title;
        }

        private static readonly string[] ImageExtensions =
            { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".bmp" };

        // Suwayomi-style folders have no archives at the series level, only chapter subfolders
        // full of loose images. This distinguishes an actual series folder from an intermediate
        // grouping folder (whose children are themselves series folders, not chapter folders) and
        // from a chapter folder itself (images live directly inside it, not one level further).
        private static bool HasChapterFolderWithImages(string seriesFolder)
        {
            foreach (string subDir in Directory.EnumerateDirectories(seriesFolder))
            {
                if (Directory.EnumerateFiles(subDir).Any(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
                    return true;
            }
            return false;
        }

        private static ImportSeriesSnapshot? BuildTitleOnlyStub(string seriesFolder, string relativePath, SeriesInfo? seriesInfo)
        {
            if (!HasChapterFolderWithImages(seriesFolder))
                return null;

            string title = seriesInfo != null && !string.IsNullOrWhiteSpace(seriesInfo.metadata?.name)
                ? seriesInfo.metadata!.name!
                : TitleFromFolderName(seriesFolder);
            if (string.IsNullOrWhiteSpace(title))
                return null;

            return new ImportSeriesSnapshot
            {
                Title = title,
                Providers = [],
                Version = 2,
                Path = relativePath
            };
        }

        /// <summary>
        /// Scans a single series folder.
        /// </summary>
        /// <param name="titleOnly">
        /// When true, a folder with no directly-contained archive files is not skipped
        /// (the normal behavior) but instead registered as a bare title-only import
        /// stub, provided it looks like an actual series folder (a chapter subfolder
        /// containing images directly) rather than an intermediate grouping folder or
        /// a chapter folder itself. Exists for migrating libraries downloaded by other
        /// tools (e.g. Suwayomi, which stores each chapter as a folder of loose images —
        /// series/chapter/001.jpg — rather than Rensaio's one-CBZ-per-chapter layout
        /// directly inside the series folder). The resulting bare import still flows
        /// through the normal SearchSeriesAsync auto-match pipeline unchanged, since
        /// that already falls back to a pure title search when a provider snapshot has
        /// no existing provider info to match against.
        /// </param>
        public async Task<ImportSeriesSnapshot?> ProcessDirectoryAsync(List<TachiyomiRepository> repos, string directoryPath, string seriesFolder, bool titleOnly = false, CancellationToken token = default)
        {
            string path = seriesFolder[directoryPath.Length..];
            path = path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Check if series.json exists
            SeriesInfo? seriesInfo = null;
            var seriesJsonPath = Path.Combine(seriesFolder, "series.json");
            if (File.Exists(seriesJsonPath))
            {
                try
                {
                    var jsonContent = await File.ReadAllTextAsync(seriesJsonPath, token).ConfigureAwait(false);
                    seriesInfo = JsonSerializer.Deserialize<SeriesInfo>(jsonContent, _jsonOptions);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error parsing series.json in {seriesFolder}",seriesFolder);
                }
            }

            // Get all files in this series folder and its subdirectories
            var allFiles = Directory.GetFiles(seriesFolder, "*.*", SearchOption.TopDirectoryOnly);

            // Filter to only archives and supported formats
            var archiveFiles = allFiles.Where(f => Parser.IsArchive(f)).ToList();

            if (archiveFiles.Count == 0)
                return titleOnly ? BuildTitleOnlyStub(seriesFolder, path, seriesInfo) : null;

            LibraryType[] libraryTypes = { LibraryType.Manga, LibraryType.Comic };
            Dictionary<LibraryType, List<NewDetectedChapter>> detected =
                new Dictionary<LibraryType, List<NewDetectedChapter>>();

            foreach (LibraryType lib in libraryTypes)
            {
                detected[lib] = new List<NewDetectedChapter>();
                foreach (var archiveFile in archiveFiles)
                {
                    FileInfo finfo = new FileInfo(archiveFile);
                    string pre_parsed = archiveFile.RestoreOriginalPathCharacters().Replace("_", " ").Replace("  ", " ");

                    NewDetectedChapter nc = new NewDetectedChapter();
                    string kavname = Path.GetFileNameWithoutExtension(archiveFile);
                    Match kavmatch = RensaioRegex.Match(kavname);
                    if (kavmatch.Success)
                    {
                        string language = "en";
                        decimal? chapterNumber = null;

                        string[] provider_scanlator = kavmatch.Groups["provider"].Value.Trim().Split("-");
                        if (kavmatch.Groups.TryGetValue("lang", out var kavmatchGroup))
                        {
                            language = kavmatchGroup.Value.Trim();
                        }
                        string seriesTitle = kavmatch.Groups["title"].Value.Trim();

                        if (kavmatch.Groups.TryGetValue("chapterNumber", out var chapterMatchGroup) && !string.IsNullOrEmpty(chapterMatchGroup.Value.Trim()))
                        {
                            chapterNumber = decimal.Parse(chapterMatchGroup.Value.Trim(), CultureInfo.InvariantCulture);
                        }
                        else if (kavmatch.Groups.TryGetValue("chapterName", out var chapterNameGroup))
                        {
                            if (decimal.TryParse(chapterNameGroup.Value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsedChapter))
                            {
                                chapterNumber = parsedChapter;
                            }
                        }

                        string provider = provider_scanlator[0].Trim();
                        string scanlator = provider_scanlator.Length > 1 ? provider_scanlator[1].Trim() : string.Empty;
                        TachiyomiExtension? ext = repos.SelectMany(a=>a.Extensions).FirstOrDefault(a => a.ParsedName().Equals(provider, StringComparison.InvariantCultureIgnoreCase) && a.Sources.Any(a=>a.Language.Equals(language, StringComparison.InvariantCultureIgnoreCase) || a.Language=="all"));
                        if (ext != null)
                        {
                            TachiyomiRepository repo = repos.First(a => a.Extensions.Contains(ext));
                            TachiyomiSource? source = ext.Sources.FirstOrDefault(a => a.Language.Equals(language, StringComparison.InvariantCultureIgnoreCase));
                            if (source==null)
                                source = ext.Sources.FirstOrDefault(a => a.Language=="all")!;
                            detected[lib].Add(new NewDetectedChapter
                            {
                                MihonProviderId = ext.Package+"|"+source.Id,
                                Provider = provider,
                                ThumbnailUrl = ext.GetIconUrl(repo),
                                Scanlator = scanlator,
                                Title = seriesTitle,
                                Language = language.ToLowerInvariant(),
                                Chapter = chapterNumber,
                                IsRensaioMatch = true,
                                Filename = archiveFile,
                                CreationDate = finfo.CreationTimeUtc
                            });
                            continue;
                        }

                    }

                    // Parse the file using BasicParser
                    var parsedInfo = _parser.Parse(pre_parsed, seriesFolder, lib);
                    decimal chap = 0;
                    if (parsedInfo != null)
                    {
                        _ = decimal.TryParse(!string.IsNullOrEmpty(parsedInfo.Chapters) &&
                                         parsedInfo.Chapters != Parser.DefaultChapter
                            ? parsedInfo.Chapters
                            : "0", NumberStyles.Any, CultureInfo.InvariantCulture, out chap);
                    }
                    if (!string.IsNullOrEmpty(parsedInfo?.Scanlator))
                    {
                        string[] provider_scanlator = parsedInfo.Scanlator.Split("-");
                        string provider = provider_scanlator[0].Trim();
                        string scanlator = provider_scanlator.Length > 1 ? provider_scanlator[1].Trim() : string.Empty;
                        TachiyomiExtension? ext = repos.SelectMany(a => a.Extensions).FirstOrDefault(a => a.ParsedName().Equals(provider, StringComparison.InvariantCultureIgnoreCase));
                        if (ext != null)
                        {
                            TachiyomiRepository repo = repos.First(a => a.Extensions.Contains(ext));
                            TachiyomiSource? source = ext.Sources.FirstOrDefault(a => a.Language.Equals("en", StringComparison.InvariantCultureIgnoreCase));
                            if (source == null)
                                source = ext.Sources.FirstOrDefault(a => a.Language == "all");
                            if (source == null)
                                source = ext.Sources.First();
                            detected[lib].Add(new NewDetectedChapter
                            {
                                Provider = provider,
                                MihonProviderId = ext.Package + "|" + source.Id,
                                ThumbnailUrl = ext.GetIconUrl(repo),
                                Scanlator = scanlator,
                                Title = parsedInfo.Series,
                                Language = "en",
                                Chapter = chap,
                                IsRensaioMatch = true,
                                Filename = archiveFile,
                                CreationDate = finfo.CreationTimeUtc
                            });
                            continue;
                        }

                        parsedInfo.Scanlator = string.Empty;
                    }

                    var d = new NewDetectedChapter
                    {
                        Provider = string.Empty,
                        ThumbnailUrl = "",
                        Scanlator = string.Empty,
                        Title = parsedInfo?.Series ?? "Unknown",
                        Language = "en",
                        Chapter = chap,
                        IsRensaioMatch = false,
                        Filename = archiveFile,
                        CreationDate = finfo.CreationTimeUtc
                    };

                    if (seriesInfo != null && !string.IsNullOrEmpty(seriesInfo.metadata.name))
                        d.Title = seriesInfo.metadata.name;

                    string[] invalidTitles = new[] { "Chapter", "Ch.", "Episode", "Ep." };
                    if (string.IsNullOrEmpty(d.Title) ||
                        invalidTitles.Any(invalidTitle =>
                            d.Title.StartsWith(invalidTitle, StringComparison.InvariantCultureIgnoreCase) ||
                            d.Title.Equals(invalidTitle, StringComparison.InvariantCultureIgnoreCase)))
                    {
                        d.Title = TitleFromFolderName(seriesFolder);
                    }
                    detected[lib].Add(d);
                }
            }

            // Determine the best library type based on detected chapters
            LibraryType flib;
            if (detected[LibraryType.Manga].Select(a => a.Title).Distinct().Count() <
                detected[LibraryType.Comic].Select(a => a.Title).Distinct().Count())
            {
                flib = LibraryType.Manga;
            }
            else if (detected[LibraryType.Comic].Select(a => a.Title).Distinct().Count() <
                     detected[LibraryType.Manga].Select(a => a.Title).Distinct().Count())
            {
                flib = LibraryType.Comic;
            }
            else if (detected[LibraryType.Manga][0].Title.Length <= detected[LibraryType.Comic][0].Title.Length)
                flib = LibraryType.Manga;
            else
                flib = LibraryType.Comic;

            var choose = detected[flib];


            ImportSeriesSnapshot? detectedInfo = choose.ToImportSeriesSnapshot();
            if (detectedInfo == null)
                return null;

            detectedInfo.Path = seriesFolder;
            detectedInfo.Type = flib == LibraryType.Manga ? "Manga" : "Comics";

            // Check if rensaio.json exists
            ImportSeriesSnapshot? ImportSeriesSnapshot = await seriesFolder.LoadImportSeriesSnapshotFromDirectoryAsync(_logger, token).ConfigureAwait(false);
            if (ImportSeriesSnapshot != null)
            {
                 foreach (ImportProviderSnapshot info in ImportSeriesSnapshot.Providers.ToList())
                {
                    List<ProviderArchiveSnapshot> ProviderArchiveSnapshots = info.Archives.Where(a => !string.IsNullOrEmpty(a.ArchiveName)).ToList();
                    foreach (ProviderArchiveSnapshot i in ProviderArchiveSnapshots)
                    {
                        string fpath = Path.Combine(seriesFolder, i.ArchiveName);
                        if (!File.Exists(fpath))
                        {
                            info.Archives.Remove(i);
                        }
                    }
                    if (info.Archives.All(a => string.IsNullOrEmpty(a.ArchiveName)))
                    {
                        ImportSeriesSnapshot.Providers.Remove(info);
                    }
                }

                (_, detectedInfo) = ImportSeriesSnapshot.Merge(detectedInfo);

            }
            detectedInfo.Path = path;
            return detectedInfo;
        }

        /// <param name="titleOnly">
        /// See <see cref="ProcessDirectoryAsync(List{TachiyomiRepository}, string, string, bool, CancellationToken)"/>.
        /// Registers folders with no directly-contained archive files as bare
        /// title-only import stubs (subject to the series-folder heuristic) instead of
        /// skipping them, and skips existing-series path/title reconciliation entirely
        /// since title-only scans are rooted outside StorageFolder.
        /// </param>
        public async Task RecurseDirectoryAsync(List<RensaioBackend.Models.Database.SeriesEntity> allseries, List<TachiyomiRepository> repos,
            List<ImportSeriesSnapshot> seriesDict, string directoryPath, string seriesFolder,
            ProgressReporter scanProgress, bool titleOnly = false, CancellationToken token = default)
        {
            var seriesFolders = await Task.Run(() => Directory.GetDirectories(seriesFolder, "*.*", SearchOption.AllDirectories), token).ConfigureAwait(false);
            if (seriesFolders.Length == 0)
                return;

            float step = 100 / (float)seriesFolders.Length;
            float acum = 0F;

            foreach (var n in seriesFolders)
            {
                ImportSeriesSnapshot? det = await ProcessDirectoryAsync(repos, directoryPath, n, titleOnly, token).ConfigureAwait(false);
                acum += step;

                if (det != null)
                {
                    // Title-only scans are rooted outside StorageFolder (ImportFolder), so existing
                    // series' StoragePath (which lives under StorageFolder) can't be meaningfully
                    // reconciled against paths found here — skip matching entirely to avoid
                    // clobbering a real series' StoragePath with an unrelated import-folder path.
                    if (!titleOnly)
                    {
                        var seriesComparer = new SeriesComparer();
                        List<RensaioBackend.Models.Database.SeriesEntity> findMatchingSeries = seriesComparer.FindMatchingSeries(allseries, det);

                        if (findMatchingSeries.Count > 0)
                        {
                            Dictionary<RensaioBackend.Models.Database.SeriesEntity, ArchiveCompare> matches = [];
                            foreach (RensaioBackend.Models.Database.SeriesEntity s in findMatchingSeries)
                            {
                                matches.Add(s, seriesComparer.CompareArchives(det, s));
                            }

                            ArchiveCompare bt = ArchiveCompare.Equal;
                            KeyValuePair<RensaioBackend.Models.Database.SeriesEntity, ArchiveCompare>? r = matches.FirstOrDefault(a => (a.Value & ArchiveCompare.Equal) == ArchiveCompare.Equal);
                            if (r == null || r?.Key==null)
                            {
                                bt = ArchiveCompare.MissingDB;
                                r = matches.FirstOrDefault(a =>
                                    (a.Value & ArchiveCompare.MissingDB) == ArchiveCompare.MissingDB);
                            }

                            if (r == null || r?.Key==null)
                            {
                                bt = ArchiveCompare.MissingArchive;
                                r = matches.FirstOrDefault(a =>
                                    (a.Value & ArchiveCompare.MissingDB) == ArchiveCompare.MissingArchive);
                            }

                            if (r != null && r?.Key!=null)
                            {
                                det.ArchiveCompare = bt;
                                det.MatchExisting = r.Value.Key.Id;
                                if (r.Value.Key.StoragePath != det.Path)
                                    r.Value.Key.StoragePath = det.Path;
                            }
                        }
                    }
                    seriesDict.Add(det);
                    scanProgress.Report(ProgressStatus.InProgress, (decimal)acum, det.Title ?? string.Empty);
                }
            }
        }
    }
}
