using Microsoft.EntityFrameworkCore;
using RenzoBackend.Data;
using RenzoBackend.Models.Dto;

namespace RenzoBackend.Services.Settings;

/// <summary>
/// The three Content Preferences — preferred languages, 18+ visibility and
/// download-all-chapters — resolved for one user.
/// </summary>
public sealed record ContentPreferences(
    string[] PreferredLanguages,
    NsfwVisibility NsfwVisibility,
    bool DownloadAllChapters);

/// <summary>
/// Resolves Content Preferences per user, falling back to the server-wide
/// values stored in <see cref="SettingsService"/>.
///
/// The fallback is the whole design. Each column on the user is NULLABLE and
/// starts NULL, so:
///
///  * an existing install behaves identically after the upgrade — nobody's
///    preferences change until they change them;
///  * the global setting keeps a job, as the default for new accounts;
///  * and the background work — library scans, download sweeps, the OPDS and
///    MCP surfaces — still has a defined answer when there is no user at all.
///
/// Prefer <see cref="ForOwnerOfAsync"/> over the global default in background
/// paths: a series knows who owns it even when the request does not.
/// </summary>
public sealed class UserContentPreferencesService
{
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;

    public UserContentPreferencesService(AppDbContext db, SettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    /// <summary>Server-wide values — the fallback, and what a null user gets.</summary>
    public async ValueTask<ContentPreferences> DefaultsAsync(CancellationToken token = default)
    {
        var s = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
        return new ContentPreferences(s.PreferredLanguages, s.NsfwVisibility, s.DownloadAllChapters);
    }

    /// <summary>
    /// Preferences for <paramref name="userId"/>, with any unset field taken
    /// from the server defaults. A null or unknown user gets the defaults.
    /// </summary>
    public async ValueTask<ContentPreferences> ForUserAsync(Guid? userId, CancellationToken token = default)
    {
        ContentPreferences defaults = await DefaultsAsync(token).ConfigureAwait(false);
        if (userId is not { } id || id == Guid.Empty)
            return defaults;

        var row = await _db.Users
            .Where(u => u.Id == id)
            .Select(u => new { u.PreferredLanguages, u.NsfwVisibility, u.DownloadAllChapters })
            .AsNoTracking()
            .FirstOrDefaultAsync(token)
            .ConfigureAwait(false);
        if (row == null)
            return defaults;

        // Each field falls back INDEPENDENTLY. A user who has only ever changed
        // the 18+ toggle must keep following the server's language order.
        string[] languages = ParseLanguages(row.PreferredLanguages) ?? defaults.PreferredLanguages;
        NsfwVisibility nsfw = row.NsfwVisibility is { } n && Enum.IsDefined(typeof(NsfwVisibility), n)
            ? (NsfwVisibility)n
            : defaults.NsfwVisibility;
        bool all = row.DownloadAllChapters ?? defaults.DownloadAllChapters;

        return new ContentPreferences(languages, nsfw, all);
    }

    /// <summary>
    /// Preferences of the user who OWNS the given series — the right question
    /// for a background job, which has no requester of its own.
    /// </summary>
    public async ValueTask<ContentPreferences> ForOwnerOfAsync(Guid seriesId, CancellationToken token = default)
    {
        Guid ownerId = await _db.Series
            .Where(s => s.Id == seriesId)
            .Select(s => s.OwnerId)
            .FirstOrDefaultAsync(token)
            .ConfigureAwait(false);
        return await ForUserAsync(ownerId == Guid.Empty ? null : ownerId, token).ConfigureAwait(false);
    }

    /// <summary>Persists a user's preferences. A null field clears it back to "inherit".</summary>
    public async Task SaveAsync(Guid userId, string[]? languages, NsfwVisibility? nsfw, bool? downloadAll,
        CancellationToken token = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, token).ConfigureAwait(false);
        if (user == null)
            return;

        user.PreferredLanguages = languages == null
            ? null
            : string.Join(',', languages.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()));
        user.NsfwVisibility = nsfw is { } v ? (int)v : null;
        user.DownloadAllChapters = downloadAll;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
    }

    /// <summary>Null (not an empty array) for "unset", so "inherit" and "none" stay distinguishable.</summary>
    private static string[]? ParseLanguages(string? csv)
    {
        if (csv == null)
            return null;
        string[] parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }
}
