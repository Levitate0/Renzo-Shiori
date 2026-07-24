using Microsoft.EntityFrameworkCore;
using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Services.Settings;

namespace RenzoBackend.Services.Series;

/// <summary>
/// Moves a series into a category subfolder (Manga / Manhwa / Manhua / …) under
/// its owner's library, keeping the physical folder, the DB StoragePath, and the
/// on-disk renzo.json all in agreement. Idempotent: a series already sitting in
/// the requested category is a no-op.
///
/// StoragePath layout is {owner}/{category}/{leaf}. Relocation only ever inserts,
/// swaps, or removes the {category} segment — the {leaf} folder name (which can
/// differ from the title, e.g. a localized title) is preserved exactly, so moving
/// never renames a series' own folder.
/// </summary>
public class SeriesRelocationService
{
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly SeriesStateService _stateService;
    private readonly ILogger<SeriesRelocationService> _logger;

    public SeriesRelocationService(
        AppDbContext db,
        SettingsService settings,
        SeriesStateService stateService,
        ILogger<SeriesRelocationService> logger)
    {
        _db = db;
        _settings = settings;
        _stateService = stateService;
        _logger = logger;
    }

    public sealed record RelocationResult(bool Moved, string StoragePath, string? Reason = null);

    /// <summary>
    /// Splits a StoragePath into (owner, category-or-null, leaf). The category
    /// segment is only recognized when it matches one of the configured
    /// categories, so a real two-segment series folder ({owner}/{leaf}) isn't
    /// mistaken for a categorized one.
    /// </summary>
    public static (string owner, string? category, string leaf) SplitPath(string storagePath, string[] categories)
    {
        string[] parts = storagePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return (string.Empty, null, storagePath);
        if (parts.Length == 1)
            return (string.Empty, null, parts[0]);

        string owner = parts[0];
        // {owner}/{maybe category}/{leaf...}
        if (parts.Length >= 3 &&
            categories.Any(c => c.Equals(parts[1], StringComparison.OrdinalIgnoreCase)))
        {
            string leaf = string.Join('/', parts.Skip(2));
            return (owner, parts[1], leaf);
        }

        string leafNoCat = string.Join('/', parts.Skip(1));
        return (owner, null, leafNoCat);
    }

    /// <summary>The category segment currently encoded in a series' StoragePath, or null.</summary>
    public string? GetCurrentCategory(string storagePath)
    {
        string[] categories = _settings.DirectSettings?.Categories ?? [];
        return SplitPath(storagePath, categories).category;
    }

    public async Task<RelocationResult> RelocateToCategoryAsync(Guid seriesId, string? category, CancellationToken token = default)
    {
        SeriesEntity? series = await _db.Series.FirstOrDefaultAsync(s => s.Id == seriesId, token).ConfigureAwait(false);
        if (series == null)
            return new RelocationResult(false, string.Empty, "Series not found");

        SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
        string[] categories = settings.Categories ?? [];

        // Only accept a category the user actually has configured (or null/empty to
        // uncategorize). Guards against a stray value carving out a rogue folder.
        string? target = string.IsNullOrWhiteSpace(category)
            ? null
            : categories.FirstOrDefault(c => c.Equals(category.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(category) && target == null)
            return new RelocationResult(false, series.StoragePath, $"Unknown category '{category}'");

        (string owner, string? currentCat, string leaf) = SplitPath(series.StoragePath, categories);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(leaf))
            return new RelocationResult(false, series.StoragePath, "Unrecognized storage path layout");

        if (string.Equals(currentCat, target, StringComparison.OrdinalIgnoreCase))
            return new RelocationResult(false, series.StoragePath, "Already in target category");

        string newStoragePath = target == null ? $"{owner}/{leaf}" : $"{owner}/{target}/{leaf}";
        if (string.Equals(newStoragePath, series.StoragePath, StringComparison.Ordinal))
            return new RelocationResult(false, series.StoragePath, "No change");

        string? sourceAbs = settings.ResolveSeriesAbsolutePath(series.StoragePath);
        string? destAbs = settings.ResolveSeriesAbsolutePath(newStoragePath);
        if (sourceAbs == null || destAbs == null)
            return new RelocationResult(false, series.StoragePath, "Could not resolve absolute paths");

        // Physical move — skip when the source folder doesn't exist (DB-only fix is
        // still applied below so the row stops pointing at a stale location).
        bool didMove = false;
        if (Directory.Exists(sourceAbs))
        {
            if (Directory.Exists(destAbs) && Directory.EnumerateFileSystemEntries(destAbs).Any())
            {
                // A populated folder already sits at the destination — refuse to
                // clobber/merge blindly. Leave everything as-is for manual review.
                _logger.LogWarning(
                    "Relocation of '{Title}' skipped: destination {Dest} already exists and is non-empty.",
                    series.Title, destAbs);
                return new RelocationResult(false, series.StoragePath, "Destination already exists");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destAbs)!);
            if (Directory.Exists(destAbs))
                Directory.Delete(destAbs, false); // empty placeholder, safe to remove before rename
            Directory.Move(sourceAbs, destAbs);
            didMove = true;
        }
        else
        {
            _logger.LogWarning(
                "Relocation of '{Title}': source folder {Src} not found on disk; updating StoragePath only.",
                series.Title, sourceAbs);
        }

        series.StoragePath = newStoragePath;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);

        // Rewrite renzo.json at the new location so its embedded state matches.
        try { await _stateService.SyncToRenzoJsonAsync(series.Id, token).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not sync renzo.json after relocating '{Title}'", series.Title); }

        _logger.LogInformation("Relocated '{Title}' -> {Path}{MovedNote}",
            series.Title, newStoragePath, didMove ? "" : " (db-only, no files moved)");
        return new RelocationResult(didMove, newStoragePath);
    }
}
