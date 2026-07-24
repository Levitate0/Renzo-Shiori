using Microsoft.EntityFrameworkCore;
using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Services.Settings;

namespace RenzoBackend.Services.Series;

/// <summary>
/// Automatic library categorization: (re)files series into Manga / Manhwa / Manhua /
/// Comic / Other using <see cref="SeriesCategoryResolver"/> (MangaDex country of
/// origin + format tags). Only relocates a series when the resolver is CONFIDENT and
/// the detected category differs from where it currently sits — so a series never
/// moves on a bare guess, and correctly-placed ones are left alone.
/// </summary>
public class CategoryMaintenanceService
{
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly SeriesCategoryResolver _resolver;
    private readonly SeriesRelocationService _relocation;
    private readonly ILogger<CategoryMaintenanceService> _logger;

    public CategoryMaintenanceService(
        AppDbContext db,
        SettingsService settings,
        SeriesCategoryResolver resolver,
        SeriesRelocationService relocation,
        ILogger<CategoryMaintenanceService> logger)
    {
        _db = db;
        _settings = settings;
        _resolver = resolver;
        _relocation = relocation;
        _logger = logger;
    }

    public sealed class CategorizeResult
    {
        public int Examined { get; set; }
        public int Moved { get; set; }
        public int AlreadyCorrect { get; set; }
        public int NoConfidentSignal { get; set; }
        public List<string> Changes { get; set; } = new();
    }

    /// <summary>Re-categorizes one series (used for newly added series). Best-effort.</summary>
    public async Task RecategorizeOneAsync(Guid seriesId, bool dryRun, CancellationToken token = default)
    {
        SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
        string[] categories = settings.Categories ?? [];
        if (!settings.CategorizedFolders || categories.Length == 0)
            return;

        SeriesEntity? s = await _db.Series.Include(x => x.Sources).FirstOrDefaultAsync(x => x.Id == seriesId, token).ConfigureAwait(false);
        if (s == null) return;
        await RecategorizeCoreAsync(s, categories, dryRun, new CategorizeResult(), token).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-categorizes every series (optionally scoped to one owner). When
    /// <paramref name="scrobblerSignalOnly"/> is true, only series whose category was
    /// resolved from an external scrobbler media-type (the ID-based, trustworthy signal)
    /// are moved — MangaDex title-match / heuristic results are ignored. Used for the
    /// automatic post-match pass so nothing moves on a fuzzy guess.
    /// </summary>
    public async Task<CategorizeResult> RecategorizeAllAsync(Guid? ownerFilter, bool dryRun, CancellationToken token = default, bool scrobblerSignalOnly = false)
    {
        var result = new CategorizeResult();
        SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
        string[] categories = settings.Categories ?? [];
        if (!settings.CategorizedFolders || categories.Length == 0)
        {
            _logger.LogInformation("Auto-categorization skipped: categorized folders disabled or no categories configured.");
            return result;
        }

        IQueryable<SeriesEntity> q = _db.Series.Include(x => x.Sources);
        if (ownerFilter is { } oid && oid != Guid.Empty)
            q = q.Where(x => x.OwnerId == oid);
        List<SeriesEntity> all = await q.ToListAsync(token).ConfigureAwait(false);

        foreach (SeriesEntity s in all)
        {
            token.ThrowIfCancellationRequested();
            await RecategorizeCoreAsync(s, categories, dryRun, result, token, scrobblerSignalOnly).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Auto-categorization {Mode}: examined {Examined}, {Verb} {Moved}, already-correct {Correct}, no-signal {NoSig}.",
            dryRun ? "(dry run)" : "complete", result.Examined, dryRun ? "would move" : "moved",
            result.Moved, result.AlreadyCorrect, result.NoConfidentSignal);
        if (dryRun)
            foreach (string c in result.Changes)
                _logger.LogInformation("  would re-file {Change}", c);
        return result;
    }

    private async Task RecategorizeCoreAsync(SeriesEntity s, string[] categories, bool dryRun, CategorizeResult result, CancellationToken token, bool scrobblerSignalOnly = false)
    {
        result.Examined++;

        string? currentCat = _relocation.GetCurrentCategory(s.StoragePath);
        IEnumerable<string> providerNames = s.Sources.Select(p => p.Provider).Where(p => !string.IsNullOrWhiteSpace(p))!;

        // In scrobbler-only mode a MangaDex result would be rejected anyway, so pass a
        // null title to skip that network lookup entirely — only the stored media-type
        // (matched via the scrobblerType arg, not the title) can qualify a move here.
        string? title = scrobblerSignalOnly ? null : s.Title;

        SeriesCategoryResolver.Resolution r = await _resolver
            .ResolveAsync(title, s.Genre, providerNames, categories, token, s.ScrobblerType).ConfigureAwait(false);

        // Only act on a confident signal; never move a series based on the bare default.
        if (!r.Confident || string.IsNullOrEmpty(r.Category))
        {
            result.NoConfidentSignal++;
            return;
        }
        // Automatic post-match pass: trust ONLY the ID-based scrobbler signal.
        if (scrobblerSignalOnly && !r.Signal.StartsWith("scrobbler:", StringComparison.Ordinal))
        {
            result.NoConfidentSignal++;
            return;
        }
        if (string.Equals(currentCat, r.Category, StringComparison.OrdinalIgnoreCase))
        {
            result.AlreadyCorrect++;
            return;
        }

        result.Changes.Add($"'{s.Title}': {currentCat ?? "(uncategorized)"} -> {r.Category} [{r.Signal}]");
        if (dryRun)
        {
            result.Moved++; // would-move
            return;
        }

        var rel = await _relocation.RelocateToCategoryAsync(s.Id, r.Category, token).ConfigureAwait(false);
        if (rel.Moved || rel.StoragePath.Contains($"/{r.Category}/", StringComparison.OrdinalIgnoreCase))
            result.Moved++;
    }
}
