using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Services.Settings;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using RenzoBackend.Services.ReadState;

namespace RenzoBackend.Services.Series;

/// <summary>
/// Central service for synchronizing series state between the database and renzo.json files.
/// Ensures renzo.json always reflects the current DB state while preserving UserReadStates.
/// Called after any metadata or file-state change (add, update, chapter fetch, download,
/// archive rename, cleanup, integrity verify, provider match, etc.).
///
/// All writes to renzo.json are delegated to RenzoJsonService which provides
/// atomic read-modify-write with per-file locking, preventing lost-update races
/// with concurrent writes from ReadStateService.
/// </summary>
public class SeriesStateService
{
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly RenzoJsonService _renzoJsonService;
    private readonly ILogger<SeriesStateService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SeriesStateService(
        AppDbContext db,
        SettingsService settings,
        RenzoJsonService renzoJsonService,
        ILogger<SeriesStateService> logger)
    {
        _db = db;
        _settings = settings;
        _renzoJsonService = renzoJsonService;
        _logger = logger;
    }

    /// <summary>
    /// Syncs the current DB state to renzo.json for a series.
    /// Always preserves UserReadStates from the existing renzo.json.
    /// Called after any metadata or file-state change.
    /// </summary>
    public async Task SyncToRenzoJsonAsync(Guid seriesId, CancellationToken token = default)
    {
        SeriesEntity? series = await _db.Series
            .Include(s => s.Sources)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == seriesId, token)
            .ConfigureAwait(false);

        if (series == null)
        {
            _logger.LogWarning("Cannot sync renzo.json: Series {SeriesId} not found", seriesId);
            return;
        }

        await SyncToRenzoJsonAsync(series, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Syncs the current DB state to renzo.json using a pre-loaded SeriesEntity.
    /// This overload avoids double-loading the entity from the database.
    /// Always preserves UserReadStates from the existing renzo.json.
    /// </summary>
    public async Task SyncToRenzoJsonAsync(SeriesEntity series, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(series);

        try
        {
            string? seriesFolder = _settings.DirectSettings?.ResolveSeriesAbsolutePath(series.StoragePath);
            if (string.IsNullOrEmpty(seriesFolder))
            {
                _logger.LogWarning("Cannot resolve series folder for Series {SeriesId} with storage path {StoragePath}", series.Id, series.StoragePath);
                return;
            }

            // Storage-readiness guard. This sync writes a snapshot built from the
            // DB and re-attaches read state read back off disk — so if the folder
            // is not there to read from, it writes a file with NO read state and
            // (because the writer creates missing directories) does so on a path
            // that may only exist because the library mount hasn't come up yet.
            //
            // That is how read state went missing across restarts: the startup
            // integrity verify calls this for every series, and a boot that races
            // the mergerfs /series mount finds no renzo.json, preserves nothing,
            // and writes DB-only files over the whole library. The in-memory cache
            // still held the state for the rest of that session, so it only
            // surfaced after the NEXT restart — which is what made it look
            // intermittent and unrelated to booting.
            //
            // Same rule the download-record verify already follows: never treat
            // absent storage as absent data. See SeriesArchiveService's own guards.
            if (!Directory.Exists(seriesFolder))
            {
                _logger.LogWarning(
                    "Skipping renzo.json sync for '{Title}': its folder {Folder} isn't there. Storage is likely not mounted yet; writing now would discard the read state stored in it.",
                    series.Title, seriesFolder);
                return;
            }

            // Step 1: Build snapshot from current DB state
            ImportSeriesSnapshot snapshot = series.ToImportSeriesSnapshot();

            // Step 1.5: Populate ExternalMappings from SeriesMappings table
            var mappings = await _db.SeriesMappings
                .Where(m => m.SeriesId == series.Id)
                .ToListAsync(token);

            if (mappings.Count > 0)
            {
                snapshot.Series.ExternalMappings = mappings.Select(a =>
                new ExternalMapping
                {
                    ExternalId = a.ExternalSeriesId,
                    Provider = a.Provider.ToString(),
                    ExternalTitle = a.ExternalSeriesTitle ?? ""
                }).ToList();
            }

            // Step 2 & 3: Atomically read existing renzo.json, merge UserReadStates, and write
            // The per-file lock in RenzoJsonService ensures that concurrent writes from
            // ReadStateService do not interleave, preventing lost updates.
            await _renzoJsonService.ModifyAsync(seriesFolder, existingSnapshot =>
            {
                if (existingSnapshot?.UserReadStates != null && existingSnapshot.UserReadStates.Count > 0)
                {
                    snapshot.UserReadStates = existingSnapshot.UserReadStates;
                    snapshot.Version = Math.Max(snapshot.Version, existingSnapshot.Version);
                }
                return snapshot;
            }, token).ConfigureAwait(false);

            _logger.LogDebug("Synced renzo.json for series {SeriesId} ({Title})", series.Id, series.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing renzo.json for series {SeriesId}: {Message}", series.Id, ex.Message);
        }
    }

    /// <summary>
    /// Reads current state from renzo.json for a series folder.
    /// Returns null if the file doesn't exist or is unreadable.
    /// </summary>
    public async Task<ImportSeriesSnapshot?> LoadFromRenzoJsonAsync(string seriesFolder, CancellationToken token = default)
    {
        return await _renzoJsonService.LoadAsync(seriesFolder, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the current DB state as an ImportSeriesSnapshot (without reading renzo.json).
    /// Useful for verification, export, or when read state preservation isn't needed.
    /// </summary>
    public async Task<ImportSeriesSnapshot> GetCurrentSnapshotAsync(Guid seriesId, CancellationToken token = default)
    {
        SeriesEntity? series = await _db.Series
            .Include(s => s.Sources)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == seriesId, token)
            .ConfigureAwait(false);

        return series?.ToImportSeriesSnapshot() ?? new ImportSeriesSnapshot();
    }
}
