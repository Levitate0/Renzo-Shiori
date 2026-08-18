using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RenzoBackend.Data;
using RenzoBackend.Models.Database;

namespace RenzoBackend.Services.Auth;

/// <summary>
/// TV pairing — the OAuth device-authorisation flow, so a television can sign
/// in without anyone typing a password with a D-pad.
///
/// Why it exists: the only no-typing option today is running the whole server
/// with authentication disabled so profiles are picked from a list. That is
/// server-wide — enabling it for a child on the TV removes passwords for every
/// account on the instance. Pairing removes that trade.
///
/// Security model: the TV displays a short <c>userCode</c> and secretly holds a
/// high-entropy <c>deviceCode</c>. Only the requesting device knows both. The
/// user code is guessable by design (it's read across a room), so it is
/// rate-limited, short-lived, and locks itself after a few failed attempts;
/// possession of the user code alone can never mint a session, because the
/// session is only ever handed to whoever presents the device code.
/// </summary>
public class TvPairingService
{
    private readonly AppDbContext _db;
    private readonly JwtTokenService _jwt;
    private readonly ILogger<TvPairingService> _logger;

    /// <summary>No 0/O or 1/I/L — someone is reading this off a screen.</summary>
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int PollIntervalSeconds = 5;
    private const int MaxFailedApprovals = 5;

    /// <summary>
    /// A code locks only once misses come from at least this many distinct
    /// callers. A user code is meant to be read across a room, so anyone who
    /// can SEE it — a housemate, a shoulder-surfer, a screenshot — could
    /// otherwise name it five times and lock the pairing out from under the
    /// person trying to approve it. A real distributed guesser has many
    /// sources by definition; a griefer has one.
    /// </summary>
    private const int MinLockSources = 2;

    /// <summary>Global ceiling on live requests. `tv/code` needs no auth and
    /// writes a row per call, so without this the table's size in any 10-minute
    /// window is whatever callers ask for.</summary>
    private const int MaxLiveRequests = 500;

    /// <summary>Live requests one address may hold at once. Over this, that
    /// address's own oldest is dropped — which harms nobody else.</summary>
    private const int MaxLivePerSource = 10;

    /// <summary>
    /// Distinct callers that have missed on each live code, for the
    /// corroboration rule above. Process memory only: it is a defence against a
    /// burst, not state worth a schema change, and losing it on restart costs
    /// nothing but a reset counter.
    /// </summary>
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> MissSources = new();

    /// <summary>Cap per code, so a wide flood cannot grow the set without bound.</summary>
    private const int MaxMissSources = 8;

    public TvPairingService(AppDbContext db, JwtTokenService jwt, ILogger<TvPairingService> logger)
    {
        _db = db;
        _jwt = jwt;
        _logger = logger;
    }

    /// <summary>
    /// Creates a pending request. Returns the raw device code ONCE, or null when
    /// the instance is already holding as many live requests as it will hold.
    /// </summary>
    public async Task<(TvPairingRequestEntity request, string rawDeviceCode)?> CreateAsync(
        string? deviceName, string? ip, CancellationToken token = default)
    {
        await SweepAsync(token).ConfigureAwait(false);

        DateTime liveFrom = DateTime.UtcNow;
        string? source = Truncate(ip, 64);

        // This caller's own excess is reclaimed from this caller: dropping the
        // oldest request an address holds cannot strand anybody else's TV.
        if (source != null)
        {
            List<TvPairingRequestEntity> mine = await _db.TvPairingRequests
                .Where(r => r.ExpiresAt > liveFrom && r.RequestIp == source)
                .OrderBy(r => r.ExpiresAt)
                .ToListAsync(token).ConfigureAwait(false);
            if (mine.Count >= MaxLivePerSource)
            {
                _db.TvPairingRequests.RemoveRange(mine.Take(mine.Count - MaxLivePerSource + 1));
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
            }
        }

        // The global ceiling REFUSES rather than evicting. Evicting "the
        // biggest" during a wide flood of one-request sources picks the honest
        // household with three TVs; a request already waiting has a better claim
        // to the last slot than one just arrived.
        int live = await _db.TvPairingRequests
            .CountAsync(r => r.ExpiresAt > liveFrom, token).ConfigureAwait(false);
        if (live >= MaxLiveRequests)
        {
            _logger.LogWarning("TV pairing refused: {Live} live requests already held.", live);
            return null;
        }

        string userCode = await GenerateUniqueUserCodeAsync(token).ConfigureAwait(false);
        (string rawDeviceCode, string hash) = _jwt.GenerateRefreshToken();

        var request = new TvPairingRequestEntity
        {
            UserCode = userCode,
            DeviceCodeHash = hash,
            DeviceName = Truncate(deviceName, 64),
            RequestIp = Truncate(ip, 64),
            ExpiresAt = DateTime.UtcNow.Add(Lifetime),
            Status = TvPairingStatus.Pending,
        };
        _db.TvPairingRequests.Add(request);
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        return (request, rawDeviceCode);
    }

    /// <summary>Looks a pending request up by the code the user typed.</summary>
    public async Task<TvPairingRequestEntity?> FindPendingByUserCodeAsync(string userCode, CancellationToken token = default)
    {
        string normalized = NormalizeUserCode(userCode);
        if (normalized.Length == 0)
            return null;
        DateTime now = DateTime.UtcNow;
        return await _db.TvPairingRequests
            .FirstOrDefaultAsync(r => r.UserCode == normalized && r.ExpiresAt > now, token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Binds a pending request to the approving user. The approver's identity is
    /// what the device is granted — a username is never accepted from a caller.
    /// </summary>
    public async Task<TvPairingResult> ApproveAsync(string userCode, Guid approvingUserId, CancellationToken token = default)
    {
        TvPairingRequestEntity? request = await FindPendingByUserCodeAsync(userCode, token).ConfigureAwait(false);
        if (request == null)
            return TvPairingResult.NotFound;
        if (request.FailedAttempts >= MaxFailedApprovals && MissSourceCount(request.UserCode) >= MinLockSources)
            return TvPairingResult.Locked;
        if (request.Status != TvPairingStatus.Pending)
            return TvPairingResult.AlreadyResolved;

        request.Status = TvPairingStatus.Approved;
        request.ApprovedUserId = approvingUserId;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        _logger.LogInformation("TV pairing approved for device {Device} by user {UserId}.",
            request.DeviceName ?? "(unnamed)", approvingUserId);
        return TvPairingResult.Ok;
    }

    /// <summary>Explicit refusal — the device stops polling immediately.</summary>
    public async Task<TvPairingResult> DenyAsync(string userCode, CancellationToken token = default)
    {
        TvPairingRequestEntity? request = await FindPendingByUserCodeAsync(userCode, token).ConfigureAwait(false);
        if (request == null)
            return TvPairingResult.NotFound;
        if (request.Status != TvPairingStatus.Pending)
            return TvPairingResult.AlreadyResolved;
        request.Status = TvPairingStatus.Denied;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        return TvPairingResult.Ok;
    }

    /// <summary>
    /// Records a miss against a code so brute force locks the request out rather
    /// than the whole endpoint.
    ///
    /// This used to be called ONLY when an approval returned NotFound — i.e.
    /// exactly when the lookup below had already failed — so it returned early
    /// every time and <c>FailedAttempts</c> could never increment. The lockout,
    /// and the 429 the controller returns for it, were unreachable. Callers now
    /// pass the miss for codes that DO exist (a code named after it was already
    /// used, or a pending-lookup that was refused), which is the case the
    /// counter can actually see.
    /// </summary>
    public async Task RecordFailedApprovalAsync(string userCode, string? source = null, CancellationToken token = default)
    {
        TvPairingRequestEntity? request = await FindPendingByUserCodeAsync(userCode, token).ConfigureAwait(false);
        if (request == null)
            return;
        request.FailedAttempts++;
        if (source != null)
        {
            ConcurrentDictionary<string, byte> seen =
                MissSources.GetOrAdd(request.UserCode, _ => new ConcurrentDictionary<string, byte>());
            if (seen.Count < MaxMissSources) seen.TryAdd(source, 0);
        }
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
    }

    /// <summary>How many distinct callers have missed on this code.</summary>
    private static int MissSourceCount(string userCode) =>
        MissSources.TryGetValue(userCode, out ConcurrentDictionary<string, byte>? seen) ? seen.Count : 0;

    /// <summary>
    /// Resolves a polling device code. A device code is single-use: once the
    /// device has collected its approval the request is marked claimed, so a
    /// replay can never mint a second session.
    /// </summary>
    public async Task<(TvPairingRequestEntity request, UserEntity user)?> TryClaimAsync(
        string rawDeviceCode, CancellationToken token = default)
    {
        DateTime now = DateTime.UtcNow;
        List<TvPairingRequestEntity> live = await _db.TvPairingRequests
            .Where(r => r.ExpiresAt > now && !r.Claimed && r.Status == TvPairingStatus.Approved)
            .ToListAsync(token).ConfigureAwait(false);

        TvPairingRequestEntity? match = live
            .FirstOrDefault(r => _jwt.ValidateRefreshToken(rawDeviceCode, r.DeviceCodeHash));
        if (match?.ApprovedUserId == null)
            return null;

        UserEntity? user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == match.ApprovedUserId.Value, token).ConfigureAwait(false);
        if (user == null || !user.IsActive)
            return null;

        match.Claimed = true;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        return (match, user);
    }

    /// <summary>Current state of a device's request, for the polling response.</summary>
    public async Task<TvPairingRequestEntity?> FindByDeviceCodeAsync(string rawDeviceCode, CancellationToken token = default)
    {
        DateTime now = DateTime.UtcNow;
        List<TvPairingRequestEntity> live = await _db.TvPairingRequests
            .Where(r => r.ExpiresAt > now)
            .ToListAsync(token).ConfigureAwait(false);
        return live.FirstOrDefault(r => _jwt.ValidateRefreshToken(rawDeviceCode, r.DeviceCodeHash));
    }

    /// <summary>Expired requests are dead weight and a guessing surface.</summary>
    public async Task SweepAsync(CancellationToken token = default)
    {
        DateTime now = DateTime.UtcNow;
        List<TvPairingRequestEntity> dead = await _db.TvPairingRequests
            .Where(r => r.ExpiresAt < now)
            .ToListAsync(token).ConfigureAwait(false);
        if (dead.Count == 0)
            return;
        foreach (TvPairingRequestEntity r in dead)
            MissSources.TryRemove(r.UserCode, out _);
        _db.TvPairingRequests.RemoveRange(dead);
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
    }

    public static string NormalizeUserCode(string? raw) =>
        new string((raw ?? string.Empty).ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    private async Task<string> GenerateUniqueUserCodeAsync(CancellationToken token)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            string code = RandomCode(4) + RandomCode(4);
            bool taken = await _db.TvPairingRequests.AnyAsync(r => r.UserCode == code, token).ConfigureAwait(false);
            if (!taken)
                return code;
        }
        // Astronomically unlikely; better than looping forever. Stays 8 chars
        // like every other path — the UI groups 4-4, so a 9th would render as
        // ABCD-12345.
        return RandomCode(4) + RandomCode(4);
    }

    private static string RandomCode(int length)
    {
        Span<char> chars = stackalloc char[length];
        for (int i = 0; i < length; i++)
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(chars);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : (value.Length <= max ? value : value[..max]);
}

public enum TvPairingResult
{
    Ok,
    NotFound,
    Locked,
    AlreadyResolved,
}
