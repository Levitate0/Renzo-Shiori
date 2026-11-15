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
    ///
    /// Beyond that window the replaced token is forgiven once (within its
    /// original 24h). A token the session has provably moved past — the one
    /// before the previous, the replacement a stranded device disowned, or the
    /// previous token presented again after its one forgiveness — means the
    /// tokens were copied, and the whole session is revoked.
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
            if (stranded == null)
            {
                // A token the session has moved past for good. No legitimate
                // client holds one (it already presented the token after it), so
                // somebody copied it.
                RefreshSessionEntity? retired = candidates.FirstOrDefault(s => IsRetired(s, rawToken, now));
                if (retired != null)
                    await RevokeForReuseAsync(retired, now, "a token it has already moved past", token).ConfigureAwait(false);
                return null;
            }
            if (stranded.User == null || !stranded.User.IsActive)
                return null;

            // The fallback is forgiven ONCE. It used to re-extend itself every
            // time, so a token lifted from a device before a rotation kept minting
            // fresh sessions forever, each one evicting the real device's token.
            // A device that lost the response to this fallback too is vanishingly
            // rare (and the 90s replay above still covers a prompt retry); a
            // second arrival on the same old token is a copy of it.
            if (stranded.PreviousTokenReplayedAt != null)
            {
                await RevokeForReuseAsync(stranded, now, "its previous token a second time", token).ConfigureAwait(false);
                return null;
            }
            // Nor once per rotation: a copier holding a pre-rotation token could
            // rotate, wait out the 90s replay, and be "stranded" again, each time
            // pinning one more disowned replacement until the real device's pin
            // fell off the list. A second forgiveness while an earlier one's pin
            // is still live is the same copied session, so it ends here.
            if (HasLivePin(stranded, now))
            {
                await RevokeForReuseAsync(stranded, now, "a previous token again while an earlier forgiveness was still pinned", token).ConfigureAwait(false);
                return null;
            }

            // No raw replacement to replay this time — only its hash was kept —
            // so issue a fresh one. The token being presented is by definition
            // the one the device still holds, so superseding the replacement it
            // never received costs nothing.
            (string retryRaw, string retryHash) = _jwt.GenerateRefreshToken();
            _logger.LogInformation(
                "Session {SessionId} refreshed with the token it held before a rotation {Age:F1}h ago — the replacement evidently never reached it. Issuing a new one instead of signing the device out.",
                stranded.Id, (ReplacedTokenGrace - (stranded.PreviousTokenValidUntil!.Value - now)).TotalHours);
            // The fallback stays pinned to the token the device demonstrably
            // holds, within its ORIGINAL deadline (not extended). The replacement
            // it says it never received is retired: if that one turns up later,
            // two parties hold this session's tokens and it is revoked.
            string presentedHash = stranded.PreviousTokenHash!;
            // Pinned, not rolled: rotating the new token quickly must not push
            // the disowned one out of the list. The device that really holds it
            // shows up with it at its next refresh, and that is the detection.
            if (!Retire(stranded, stranded.TokenHash, now, pinUntil: now.AddDays(_jwt.GetRememberMeExpirationDays())))
            {
                // Never drop a pin to make room: that is what lets a copy outlive the device.
                await RevokeForReuseAsync(stranded, now, "more disowned replacements than it can keep as tripwires", token).ConfigureAwait(false);
                return null;
            }
            stranded.PreviousTokenReplayedAt = now;
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
        // The previous token is now two steps behind: nothing legitimate will
        // present it again, so it joins the tripwires (added to, never replaced:
        // one overwritten slot let a copier rotate its way past detection).
        Retire(match, match.PreviousTokenHash, now, pinUntil: null);
        match.PreviousTokenHash = replacedHash;
        match.PreviousTokenValidUntil = now.Add(ReplacedTokenGrace);
        match.PreviousTokenReplayedAt = null;
        match.TokenHash = newHash;
        match.ExpiresAt = now.AddDays(_jwt.GetRememberMeExpirationDays());
        match.LastSeenAt = now;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        RecentRotations[match.Id] = new GraceEntry(replacedHash, newRaw, now.Add(RotationGrace));
        return (match.User, newRaw);
    }

    /// <summary>Most recent rolled-off tokens kept as tripwires per session.</summary>
    private const int MaxRolledRetired = 32;

    /// <summary>Pinned tripwires (replacements a stranded device disowned) kept per session.</summary>
    private const int MaxPinnedRetired = 16;

    /// <summary>
    /// <see cref="RefreshSessionEntity.RetiredTokenHash"/> holds a newline-separated
    /// list. A plain entry is a rolled-off token hash; "!{unixSeconds}:{hash}" is a
    /// pinned one that survives later rotations until that time. Base64 hashes
    /// never contain '!', ':' or a newline, and a value written before the list
    /// existed (a single hash) reads as one plain entry.
    /// </summary>
    private static List<string> RetiredEntries(RefreshSessionEntity s) =>
        string.IsNullOrEmpty(s.RetiredTokenHash)
            ? new List<string>()
            : s.RetiredTokenHash.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>The hash in a retired entry, or null for a pin that has run out.</summary>
    private static string? RetiredHash(string entry, DateTime now)
    {
        if (!entry.StartsWith('!'))
            return entry;
        int colon = entry.IndexOf(':');
        if (colon < 0 || !long.TryParse(entry.AsSpan(1, colon - 1), out long until)
            || DateTimeOffset.FromUnixTimeSeconds(until).UtcDateTime <= now)
            return null;
        return entry[(colon + 1)..];
    }

    private bool IsRetired(RefreshSessionEntity s, string rawToken, DateTime now) =>
        RetiredEntries(s).Any(e => RetiredHash(e, now) is { } h && _jwt.ValidateRefreshToken(rawToken, h));

    private static bool HasLivePin(RefreshSessionEntity s, DateTime now) =>
        RetiredEntries(s).Any(e => e.StartsWith('!') && RetiredHash(e, now) != null);

    /// <summary>
    /// Adds a tripwire. Rolled entries past <see cref="MaxRolledRetired"/> fall off
    /// oldest first; pins never do — false (nothing written) when a pin would
    /// overflow <see cref="MaxPinnedRetired"/>, and the caller revokes instead.
    /// </summary>
    private static bool Retire(RefreshSessionEntity s, string? hash, DateTime now, DateTime? pinUntil)
    {
        if (string.IsNullOrEmpty(hash))
            return true;
        List<string> entries = RetiredEntries(s).Where(e => RetiredHash(e, now) != null).ToList();
        entries.Add(pinUntil is DateTime u ? $"!{new DateTimeOffset(u, TimeSpan.Zero).ToUnixTimeSeconds()}:{hash}" : hash);
        List<string> rolled = entries.Where(e => !e.StartsWith('!')).ToList();
        foreach (string old in rolled.Take(Math.Max(0, rolled.Count - MaxRolledRetired)))
            entries.Remove(old);
        if (pinUntil != null && entries.Count(e => e.StartsWith('!')) > MaxPinnedRetired)
            return false;
        s.RetiredTokenHash = string.Join('\n', entries);
        return true;
    }

    /// <summary>
    /// A refresh token turned up that only a copy could still be holding.
    /// Revoking the whole session ends both the thief's and the device's
    /// tokens — the device signs in again, which is the point: there is no way
    /// to tell from here which of the two is the real one.
    ///
    /// No "just a race" window: a prompt duplicate of a refresh is already
    /// answered by the in-memory replay before this is reached, and a window
    /// keyed on the last refresh let a copier who refreshes every minute keep
    /// any reuse from ever revoking.
    /// </summary>
    private async Task RevokeForReuseAsync(RefreshSessionEntity session, DateTime now, string what, CancellationToken token)
    {
        session.RevokedAt = now;
        RecentRotations.TryRemove(session.Id, out _);
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        _logger.LogWarning(
            "Session {SessionId} ({Device}) presented {What}; its refresh tokens have been copied. Revoked the session — the device will have to sign in again.",
            session.Id, session.DeviceName ?? "unnamed device", what);
    }

    /// <summary>
    /// Whether <paramref name="rawToken"/> is a token this session will still
    /// honour: its current one, or the previous one inside its grace. Every
    /// match on a session token must accept both — a device can legitimately
    /// be holding the token it just replaced.
    /// </summary>
    public static bool HoldsToken(JwtTokenService jwt, RefreshSessionEntity session, string rawToken, DateTime now) =>
        jwt.ValidateRefreshToken(rawToken, session.TokenHash)
        || (session.PreviousTokenHash != null
            && session.PreviousTokenValidUntil > now
            && jwt.ValidateRefreshToken(rawToken, session.PreviousTokenHash));

    /// <summary>Revokes the single session a raw token belongs to (sign-out).</summary>
    public async Task RevokeByTokenAsync(string rawToken, CancellationToken token = default)
    {
        DateTime now = DateTime.UtcNow;
        List<RefreshSessionEntity> candidates = await _db.RefreshSessions
            .Where(s => s.RevokedAt == null)
            .ToListAsync(token).ConfigureAwait(false);
        // The previous token counts too: a device whose last refresh response
        // was lost signs out holding it, and that must still end the session.
        RefreshSessionEntity? match = candidates
            .FirstOrDefault(s => HoldsToken(_jwt, s, rawToken, now));
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

    /// <summary>
    /// Revokes every remembered device of a user, optionally sparing the one
    /// making the request. A password change or reset has to end the sessions
    /// it is meant to protect against: since sign-ins moved to one row per
    /// device, clearing the legacy column on the user revoked nothing.
    /// </summary>
    public async Task<int> RevokeAllAsync(Guid userId, string? exceptRawToken = null, CancellationToken token = default)
    {
        DateTime now = DateTime.UtcNow;
        List<RefreshSessionEntity> live = await _db.RefreshSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ToListAsync(token).ConfigureAwait(false);
        int count = 0;
        foreach (RefreshSessionEntity s in live)
        {
            if (exceptRawToken != null && HoldsToken(_jwt, s, exceptRawToken, now))
                continue;
            s.RevokedAt = now;
            RecentRotations.TryRemove(s.Id, out _);
            count++;
        }
        if (count > 0)
            await _db.SaveChangesAsync(token).ConfigureAwait(false);
        return count;
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
