using System.Collections.Concurrent;
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
    /// How long a just-rotated token keeps working. Rotation is only safe if the
    /// client actually RECEIVES the new token, and on mobile that is not a given:
    /// handing off between cell towers or moving from data to Wi-Fi kills
    /// in-flight connections, so a refresh can be rotated server-side while the
    /// response never lands. The client then still holds the previous token, and
    /// without this window its next refresh is a hard 401 — the app clears the
    /// cookie and the user is signed out purely for having changed networks.
    ///
    /// The client's own retry logic can't cover this: it already distinguishes
    /// "the server said no" from "I couldn't ask" and keeps the cookie on network
    /// errors. The token it kept is the one the server has already replaced, so
    /// only the server can forgive it.
    /// </summary>
    private static readonly TimeSpan RotationGrace = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long the REPLACED token keeps working, persisted on the session row.
    ///
    /// The short in-memory window above only covers a prompt retry. The failure
    /// that actually strands users is slower: the app loses a refresh response,
    /// stops making requests because it went to sleep, and does not try again
    /// until the user next opens it — which can be the following day. Nothing
    /// held in process memory survives that, so the fallback has to be on the
    /// row and has to be measured in hours.
    ///
    /// A day is long enough that reopening the app recovers the session, and
    /// short enough that a token captured from a device stops being useful
    /// quickly. Beyond it, a sign-in is a reasonable thing to ask for.
    /// </summary>
    private static readonly TimeSpan ReplacedTokenGrace = TimeSpan.FromHours(24);

    /// <summary>
    /// The token a rotation replaced, and the replacement that may never have
    /// arrived. Process-memory only and short-lived: it is a retry buffer, not
    /// state worth persisting, and losing it across a restart costs at most one
    /// sign-in. Keyed by session so only the immediately-previous token is ever
    /// honoured — a chain of old tokens does not accumulate.
    /// </summary>
    private sealed record GraceEntry(string ReplacedHash, string IssuedRawToken, DateTime ExpiresAt);

    private static readonly ConcurrentDictionary<Guid, GraceEntry> RecentRotations = new();

    /// <summary>
    /// Validates a raw refresh token and rotates it, returning the owning user
    /// and the NEW raw token. Null when the token is unknown, expired or
    /// revoked. Rotation keeps the same row so device identity survives.
    ///
    /// A token that was rotated within <see cref="RotationGrace"/> is still
    /// accepted, and replays the SAME replacement rather than rotating again —
    /// so a client repeating a request whose response it lost ends up holding
    /// exactly what the lost response carried, and a client that did receive the
    /// new token keeps a token that stays valid.
    /// </summary>
    public async Task<(UserEntity user, string newRawToken)?> RotateAsync(string rawToken, CancellationToken token = default)
    {
        DateTime now = DateTime.UtcNow;
        foreach (KeyValuePair<Guid, GraceEntry> stale in RecentRotations)
        {
            if (stale.Value.ExpiresAt <= now)
                RecentRotations.TryRemove(stale.Key, out _);
        }
        // O(n) over live sessions — the hash is salted per token, so it can't be
        // looked up directly. User bases here are small; the previous code did
        // the same over users.
        List<RefreshSessionEntity> candidates = await _db.RefreshSessions
            .Include(s => s.User)
            .Where(s => s.RevokedAt == null && s.ExpiresAt > now)
            .ToListAsync(token).ConfigureAwait(false);

        RefreshSessionEntity? match = candidates
            .FirstOrDefault(s => _jwt.ValidateRefreshToken(rawToken, s.TokenHash));

        if (match == null)
        {
            // Not the current token for any session. Before giving up — which
            // signs the device out — check whether it is one we ourselves
            // replaced moments ago.
            KeyValuePair<Guid, GraceEntry> grace = RecentRotations.FirstOrDefault(
                e => e.Value.ExpiresAt > now && _jwt.ValidateRefreshToken(rawToken, e.Value.ReplacedHash));
            if (grace.Value != null)
            {
                RefreshSessionEntity? session = candidates.FirstOrDefault(s => s.Id == grace.Key);
                if (session?.User == null || !session.User.IsActive)
                    return null;

                session.LastSeenAt = now;
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
                _logger.LogInformation(
                    "Refresh token for session {SessionId} was rotated moments ago; replaying the same replacement rather than signing the device out.",
                    session.Id);
                return (session.User, grace.Value.IssuedRawToken);
            }

            // Nothing in memory. Fall back to the replaced hash on the row, which
            // covers the device that went to sleep before it could retry.
            RefreshSessionEntity? stranded = candidates.FirstOrDefault(
                s => s.PreviousTokenHash != null
                     && s.PreviousTokenValidUntil > now
                     && _jwt.ValidateRefreshToken(rawToken, s.PreviousTokenHash));
            if (stranded?.User == null || !stranded.User.IsActive)
                return null;

            // No raw replacement to replay this time — only its hash was kept —
            // so issue a fresh one. The token being presented is by definition
            // the one the device still holds, so superseding the replacement it
            // never received costs nothing.
            (string retryRaw, string retryHash) = _jwt.GenerateRefreshToken();
            _logger.LogInformation(
                "Session {SessionId} refreshed with the token it held before a rotation {Age:F1}h ago — the replacement evidently never reached it. Issuing a new one instead of signing the device out.",
                stranded.Id, (ReplacedTokenGrace - (stranded.PreviousTokenValidUntil!.Value - now)).TotalHours);
            // The fallback stays pinned to the token the device demonstrably
            // holds, rather than advancing to the replacement it never received:
            // if this response is lost as well, that same token must still work
            // the next time the app wakes up.
            string presentedHash = stranded.PreviousTokenHash!;
            stranded.PreviousTokenValidUntil = now.Add(ReplacedTokenGrace);
            stranded.TokenHash = retryHash;
            stranded.ExpiresAt = now.AddDays(_jwt.GetRememberMeExpirationDays());
            stranded.LastSeenAt = now;
            await _db.SaveChangesAsync(token).ConfigureAwait(false);
            RecentRotations[stranded.Id] = new GraceEntry(presentedHash, retryRaw, now.Add(RotationGrace));
            return (stranded.User, retryRaw);
        }

        if (match.User == null || !match.User.IsActive)
            return null;

        (string newRaw, string newHash) = _jwt.GenerateRefreshToken();
        string replacedHash = match.TokenHash;
        match.PreviousTokenHash = replacedHash;
        match.PreviousTokenValidUntil = now.Add(ReplacedTokenGrace);
        match.TokenHash = newHash;
        match.ExpiresAt = now.AddDays(_jwt.GetRememberMeExpirationDays());
        match.LastSeenAt = now;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        RecentRotations[match.Id] = new GraceEntry(replacedHash, newRaw, now.Add(RotationGrace));
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
        // Sign-out must not leave a usable replacement token sitting in memory.
        // The grace lookup already refuses revoked sessions, so this is hygiene
        // rather than a second gate.
        RecentRotations.TryRemove(match.Id, out _);
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
        RecentRotations.TryRemove(session.Id, out _);
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
