using Microsoft.EntityFrameworkCore;
using RenzoBackend.Data;
using RenzoBackend.Models.Database;

namespace RenzoBackend.Services.Auth;

/// <summary>
/// Per-device remember-me sessions.
///
/// Previously a user had exactly one refresh token (a column on the user row),
/// so signing in on a second device silently invalidated the first — and a
/// paired TV would be evicted by the next sign-in anywhere. Each device now
/// owns a row: rotation updates it in place, so the device keeps its identity
/// across refreshes, and revoking one leaves the rest signed in.
/// </summary>
public class RefreshSessionService
{
    private readonly AppDbContext _db;
    private readonly JwtTokenService _jwt;
    private readonly ILogger<RefreshSessionService> _logger;

    public RefreshSessionService(AppDbContext db, JwtTokenService jwt, ILogger<RefreshSessionService> logger)
    {
        _db = db;
        _jwt = jwt;
        _logger = logger;
    }

    /// <summary>Issues a new remembered session and returns the RAW token for the cookie.</summary>
    public async Task<(string rawToken, RefreshSessionEntity session)> CreateAsync(
        UserEntity user, string? deviceName, string? ip, bool isTvPairing, CancellationToken token = default)
    {
        (string rawToken, string hash) = _jwt.GenerateRefreshToken();
        var session = new RefreshSessionEntity
        {
            UserId = user.Id,
            TokenHash = hash,
            ExpiresAt = DateTime.UtcNow.AddDays(_jwt.GetRememberMeExpirationDays()),
            DeviceName = Truncate(deviceName, 64),
            CreatedIp = Truncate(ip, 64),
            IsTvPairing = isTvPairing,
        };
        _db.RefreshSessions.Add(session);
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        return (rawToken, session);
    }

    /// <summary>
    /// Validates a raw refresh token and rotates it, returning the owning user
    /// and the NEW raw token. Null when the token is unknown, expired or
    /// revoked. Rotation keeps the same row so device identity survives.
    /// </summary>
    public async Task<(UserEntity user, string newRawToken)?> RotateAsync(string rawToken, CancellationToken token = default)
    {
        DateTime now = DateTime.UtcNow;
        // O(n) over live sessions — the hash is salted per token, so it can't be
        // looked up directly. User bases here are small; the previous code did
        // the same over users.
        List<RefreshSessionEntity> candidates = await _db.RefreshSessions
            .Include(s => s.User)
            .Where(s => s.RevokedAt == null && s.ExpiresAt > now)
            .ToListAsync(token).ConfigureAwait(false);

        RefreshSessionEntity? match = candidates
            .FirstOrDefault(s => _jwt.ValidateRefreshToken(rawToken, s.TokenHash));
        if (match?.User == null || !match.User.IsActive)
            return null;

        (string newRaw, string newHash) = _jwt.GenerateRefreshToken();
        match.TokenHash = newHash;
        match.ExpiresAt = now.AddDays(_jwt.GetRememberMeExpirationDays());
        match.LastSeenAt = now;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        return (match.User, newRaw);
    }

    /// <summary>Revokes the single session a raw token belongs to (sign-out).</summary>
    public async Task RevokeByTokenAsync(string rawToken, CancellationToken token = default)
    {
        DateTime now = DateTime.UtcNow;
        List<RefreshSessionEntity> candidates = await _db.RefreshSessions
            .Where(s => s.RevokedAt == null)
            .ToListAsync(token).ConfigureAwait(false);
        RefreshSessionEntity? match = candidates
            .FirstOrDefault(s => _jwt.ValidateRefreshToken(rawToken, s.TokenHash));
        if (match == null)
            return;
        match.RevokedAt = now;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
    }

    /// <summary>Revokes one device by id, for the device list. Owner-scoped.</summary>
    public async Task<bool> RevokeAsync(Guid userId, Guid sessionId, CancellationToken token = default)
    {
        RefreshSessionEntity? session = await _db.RefreshSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, token).ConfigureAwait(false);
        if (session == null)
            return false;
        session.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        return true;
    }

    /// <summary>Live (unrevoked, unexpired) sessions for the device list.</summary>
    public async Task<List<RefreshSessionEntity>> ListAsync(Guid userId, CancellationToken token = default)
    {
        DateTime now = DateTime.UtcNow;
        return await _db.RefreshSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(token).ConfigureAwait(false);
    }

    /// <summary>Drops rows that are long dead, so the table doesn't grow forever.</summary>
    public async Task SweepAsync(CancellationToken token = default)
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-7);
        List<RefreshSessionEntity> dead = await _db.RefreshSessions
            .Where(s => s.ExpiresAt < cutoff || (s.RevokedAt != null && s.RevokedAt < cutoff))
            .ToListAsync(token).ConfigureAwait(false);
        if (dead.Count == 0)
            return;
        _db.RefreshSessions.RemoveRange(dead);
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        _logger.LogInformation("Swept {Count} expired/revoked refresh session(s).", dead.Count);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : (value.Length <= max ? value : value[..max]);
}
