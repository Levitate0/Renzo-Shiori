using RensaioBackend.Data;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.ReadState;
using RensaioBackend.Services.Helpers;
using RensaioBackend.Services.Opds;
using RensaioBackend.Services.ReadState;
using RensaioBackend.Services.Settings;
using Microsoft.EntityFrameworkCore;
using NetVips;
using System.IO.Compression;

namespace RensaioBackend.Services.Reader;

/// <summary>
/// Backend for the built-in reader over downloaded (library) chapters. Serves
/// pages straight out of the CBZ archives — no extraction cache, no format
/// conversion (browsers decode jpg/png/webp/avif natively) — using the same
/// natural-sort page ordering as OPDS. Also computes per-page dimensions so
/// the frontend can pick a smart reading mode (webtoon / long-strip / paged).
/// </summary>
public class ReaderService
{
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly ReadStateService _readState;
    private readonly ILogger _logger;

    public ReaderService(AppDbContext db, SettingsService settings, ReadStateService readState, ILogger<ReaderService> logger)
    {
        _db = db;
        _settings = settings;
        _readState = readState;
        _logger = logger;
    }

    /// <summary>A page is "strip-shaped" when it's ≥3× taller than wide — a native webtoon panel.</summary>
    private const double StripAspectThreshold = 3.0;

    /// <summary>
    /// A page is a "sliver" when it's at least 2× wider than tall — a short
    /// horizontal band, e.g. the leftover bottom of a long strip that was sliced
    /// into pages. Normal pages are portrait and even a double-page spread is
    /// only ~1.4× wider than tall, so this can't catch ordinary artwork.
    /// </summary>
    private const double SliverAspectThreshold = 0.5;

    /// <summary>
    /// How many strip/sliver pages must appear among otherwise normal pages
    /// before the chapter is treated as a cut-up long strip.
    /// </summary>
    private const int CutStripPageThreshold = 4;

    public async Task<ReaderChaptersDto?> GetChaptersAsync(Guid seriesId, string username, CancellationToken token = default)
    {
        SeriesEntity? series = await _db.Series.Include(s => s.Sources)
            .AsNoTracking().FirstOrDefaultAsync(s => s.Id == seriesId, token).ConfigureAwait(false);
        if (series == null)
            return null;

        List<ChapterReadState> states = _readState.GetSeriesReadStates(username, series.StoragePath);

        // One entry per distinct chapter number; prefer the storage source's file.
        var byNumber = series.Sources
            .Where(p => !p.IsDisabled)
            .SelectMany(p => p.Chapters, (p, c) => (Provider: p, Chapter: c))
            .Where(x => x.Chapter.Number != null)
            .GroupBy(x => x.Chapter.Number!.Value)
            .OrderBy(g => g.Key);

        var chapters = new List<ReaderChapterDto>();
        foreach (var g in byNumber)
        {
            var withFile = g.Where(x => !string.IsNullOrEmpty(x.Chapter.Filename) && !x.Chapter.IsDeleted)
                .OrderByDescending(x => x.Provider.IsStorage)
                .FirstOrDefault();
            var any = withFile.Chapter != null ? withFile : g.First();
            ChapterReadState? st = states.FirstOrDefault(s => s.ChapterNumber == g.Key);
            chapters.Add(new ReaderChapterDto
            {
                Number = g.Key,
                Name = any.Chapter.Name ?? "",
                Filename = withFile.Chapter?.Filename,
                PageCount = withFile.Chapter?.PageCount,
                Progress = st?.Progress ?? 0,
                IsCompleted = st?.IsCompleted ?? false,
                Bookmarked = st?.Bookmarked ?? false,
                LastReadAt = st?.LastReadAt
            });
        }

        return new ReaderChaptersDto
        {
            SeriesId = series.Id,
            Title = series.Title,
            Type = series.Type,
            Chapters = chapters
        };
    }

    /// <summary>
    /// Opens a chapter archive and returns page count, per-page dimensions, and
    /// a suggested reading mode. Dimension reads are header-only (NetVips) and
    /// tolerant of odd entries.
    /// </summary>
    public async Task<ReaderChapterInfoDto?> GetChapterInfoAsync(Guid seriesId, string filename, CancellationToken token = default)
    {
        string? path = await ResolveArchivePathAsync(seriesId, filename, token).ConfigureAwait(false);
        if (path == null)
            return null;

        var info = new ReaderChapterInfoDto { Filename = filename };
        using ZipArchive zip = ZipFile.OpenRead(path);
        List<ZipArchiveEntry> entries = GetSortedImageEntries(zip);
        info.PageCount = entries.Count;

        int strips = 0;
        int slivers = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var dims = new ReaderPageDimsDto { Index = i };
            try
            {
                using Stream s = entries[i].Open();
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms, token).ConfigureAwait(false);
                using Image img = Image.NewFromBuffer(ms.ToArray());
                dims.Width = img.Width;
                dims.Height = img.Height;
                if (img.Width > 0)
                {
                    double aspect = (double)img.Height / img.Width;
                    if (aspect >= StripAspectThreshold)
                    {
                        dims.IsStrip = true;
                        strips++;
                    }
                    else if (aspect <= SliverAspectThreshold)
                    {
                        dims.IsSliver = true;
                        slivers++;
                    }
                }
            }
            catch
            {
                // Unreadable header — leave dims null; mode detection just has less data.
            }
            info.Pages.Add(dims);
        }

        // Smart mode:
        //  - mostly tall strip images  -> webtoon (native long strip)
        //  - a handful of tall strips OR wide slivers mixed into otherwise normal
        //    pages -> longstrip: that's a continuous strip that was cut into
        //    "pages", and the cut leaves short horizontal off-cuts. Those only
        //    read correctly stitched edge-to-edge at a matched width.
        //  - otherwise plain paged.
        int known = info.Pages.Count(p => p.Width != null);
        int cutMarkers = strips + slivers;
        if (known > 0 && strips * 2 >= known)
            info.SuggestedMode = "webtoon";
        else if (cutMarkers > CutStripPageThreshold)
            info.SuggestedMode = "longstrip";
        else
            info.SuggestedMode = "paged";

        return info;
    }

    /// <summary>Streams one page image out of the chapter archive.</summary>
    public async Task<(Stream? stream, string contentType)> GetPageAsync(Guid seriesId, string filename, int pageIndex, CancellationToken token = default)
    {
        string? path = await ResolveArchivePathAsync(seriesId, filename, token).ConfigureAwait(false);
        if (path == null)
            return (null, "");

        ZipArchive zip = ZipFile.OpenRead(path);
        try
        {
            List<ZipArchiveEntry> entries = GetSortedImageEntries(zip);
            if (pageIndex < 0 || pageIndex >= entries.Count)
            {
                zip.Dispose();
                return (null, "");
            }
            ZipArchiveEntry entry = entries[pageIndex];
            // Buffer the page so the archive handle isn't held across the response.
            var ms = new MemoryStream();
            using (Stream s = entry.Open())
                await s.CopyToAsync(ms, token).ConfigureAwait(false);
            ms.Position = 0;
            return (ms, ContentTypeFor(entry.Name));
        }
        finally
        {
            zip.Dispose();
        }
    }

    private async Task<string?> ResolveArchivePathAsync(Guid seriesId, string filename, CancellationToken token)
    {
        SeriesEntity? series = await _db.Series.Include(s => s.Sources)
            .AsNoTracking().FirstOrDefaultAsync(s => s.Id == seriesId, token).ConfigureAwait(false);
        if (series == null)
            return null;
        // The filename must belong to the series — never trust it as a raw path.
        bool known = series.Sources.SelectMany(p => p.Chapters)
            .Any(c => c.Filename != null && c.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase));
        if (!known || filename.Contains("..") || filename.Contains('/') || filename.Contains('\\'))
            return null;
        var settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
        string path = Path.Combine(settings.StorageFolder, series.StoragePath, filename);
        return File.Exists(path) ? path : null;
    }

    private static List<ZipArchiveEntry> GetSortedImageEntries(ZipArchive zip)
    {
        return zip.Entries
            .Where(e => e.Length > 0 && ArchiveHelperService.ArchiveIsImage(e.FullName))
            .OrderBy(e => e.FullName, new NaturalSortComparer())
            .ToList();
    }

    private static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream"
    };
}
