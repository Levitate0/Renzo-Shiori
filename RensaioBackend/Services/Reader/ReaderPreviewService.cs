using RensaioBackend.Data;
using RensaioBackend.Extensions;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Services.Bridge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Extensions;

namespace RensaioBackend.Services.Reader;

/// <summary>
/// Preview reading for Browse items that are NOT in the library: fetches
/// chapter lists and page images live from the source through the Mihon
/// bridge, holding nothing on disk. Chapter/page lists are memory-cached for
/// 30 minutes so paging through a chapter doesn't re-hit the source's
/// list endpoints for every image.
/// </summary>
public class ReaderPreviewService
{
    private readonly AppDbContext _db;
    private readonly MihonBridgeService _mihon;
    private readonly IMemoryCache _cache;
    private readonly SiteAuth.SiteAuthService _siteAuth;
    private readonly ILogger _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    public ReaderPreviewService(AppDbContext db, MihonBridgeService mihon, IMemoryCache cache,
        SiteAuth.SiteAuthService siteAuth, ILogger<ReaderPreviewService> logger)
    {
        _db = db;
        _mihon = mihon;
        _cache = cache;
        _siteAuth = siteAuth;
        _logger = logger;
    }

    private async Task<(LatestSerieEntity entity, ISourceInterop source)?> ResolveAsync(string mihonId, CancellationToken token)
    {
        LatestSerieEntity? entity = await _db.LatestSeries.AsNoTracking()
            .FirstOrDefaultAsync(a => a.MihonId == mihonId, token).ConfigureAwait(false);
        if (entity == null || string.IsNullOrEmpty(entity.MihonProviderId))
            return null;
        ISourceInterop src = await _mihon.SourceFromProviderIdAsync(entity.MihonProviderId, token).ConfigureAwait(false);
        if (src == null)
            return null;
        return (entity, src);
    }

    public async Task<PreviewChaptersDto?> GetChaptersAsync(string mihonId, CancellationToken token = default)
    {
        var resolved = await ResolveAsync(mihonId, token).ConfigureAwait(false);
        if (resolved == null)
            return null;
        (LatestSerieEntity entity, ISourceInterop src) = resolved.Value;

        Manga? manga = entity.ToManga();
        if (manga == null)
            return null;

        List<ParsedChapter>? chapters = _cache.Get<List<ParsedChapter>>($"pv:ch:{mihonId}");
        if (chapters == null)
        {
            chapters = await src.GetChaptersAsync(manga, token).ConfigureAwait(false);
            if (chapters == null)
                return null;
            _cache.Set($"pv:ch:{mihonId}", chapters, CacheTtl);
        }

        return new PreviewChaptersDto
        {
            MihonId = mihonId,
            Title = entity.Title,
            Chapters = chapters.Select((c, i) => new PreviewChapterDto
            {
                Index = i,
                Name = string.IsNullOrEmpty(c.ParsedName) ? c.Name : c.ParsedName,
                Number = c.ParsedNumber != 0 ? c.ParsedNumber : (decimal?)null,
                DateUpload = c.DateUpload == default ? null : c.DateUpload.UtcDateTime
            }).ToList()
        };
    }

    public async Task<PreviewPagesDto?> GetPagesAsync(string mihonId, int chapterIndex, Guid? userId = null, CancellationToken token = default)
    {
        List<Page>? pages = await GetPageListAsync(mihonId, chapterIndex, userId, token).ConfigureAwait(false);
        return pages == null ? null : new PreviewPagesDto { PageCount = pages.Count };
    }

    public async Task<(Stream? stream, string contentType)> GetPageImageAsync(string mihonId, int chapterIndex, int pageIndex, Guid? userId = null, CancellationToken token = default)
    {
        List<Page>? pages = await GetPageListAsync(mihonId, chapterIndex, userId, token).ConfigureAwait(false);
        if (pages == null || pageIndex < 0 || pageIndex >= pages.Count)
            return (null, "");

        var resolved = await ResolveAsync(mihonId, token).ConfigureAwait(false);
        if (resolved == null)
            return (null, "");

        ContentTypeStream img = await resolved.Value.source.GetPageImageAsync(pages[pageIndex], token).ConfigureAwait(false);
        if (img == null)
            return (null, "");
        img.Position = 0;
        return (img, string.IsNullOrEmpty(img.ContentType) ? "image/jpeg" : img.ContentType);
    }

    private async Task<List<Page>?> GetPageListAsync(string mihonId, int chapterIndex, Guid? userId, CancellationToken token)
    {
        string key = $"pv:pg:{mihonId}:{chapterIndex}";
        List<Page>? pages = _cache.Get<List<Page>>(key);
        if (pages != null)
            return pages;

        var resolved = await ResolveAsync(mihonId, token).ConfigureAwait(false);
        if (resolved == null)
            return null;
        (LatestSerieEntity entity, ISourceInterop src) = resolved.Value;

        List<ParsedChapter>? chapters = _cache.Get<List<ParsedChapter>>($"pv:ch:{mihonId}");
        if (chapters == null)
        {
            Manga? manga = entity.ToManga();
            if (manga == null)
                return null;
            chapters = await src.GetChaptersAsync(manga, token).ConfigureAwait(false);
            if (chapters == null)
                return null;
            _cache.Set($"pv:ch:{mihonId}", chapters, CacheTtl);
        }
        if (chapterIndex < 0 || chapterIndex >= chapters.Count)
            return null;

        pages = await src.GetPagesAsync(chapters[chapterIndex], token).ConfigureAwait(false);

        // A locked/paid chapter comes back empty when the site session has
        // lapsed. If the user has a login for this source, re-authenticate
        // (refreshing cookies in the shared jar) and try once more.
        if ((pages == null || pages.Count == 0) && userId != null)
        {
            bool relogged = await _siteAuth.EnsureLoggedInAsync(userId.Value, entity.Provider, token).ConfigureAwait(false);
            if (relogged)
                pages = await src.GetPagesAsync(chapters[chapterIndex], token).ConfigureAwait(false);
        }

        if (pages != null)
            _cache.Set(key, pages, CacheTtl);
        return pages;
    }
}
