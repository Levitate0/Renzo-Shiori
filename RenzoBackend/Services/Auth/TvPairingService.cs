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

    public TvPairingService(AppDbContext db, JwtTokenService jwt, ILogger<TvPairingService> logger)
    {
        _db = db;
        _jwt = jwt;
        _logger = logger;
    }

    /// <summary>Creates a pending request. Returns the raw device code ONCE.</summary>
    public async Task<(TvPairingRequestEntity request, string rawDeviceCode)> CreateAsync(
        string? deviceName, string? ip, CancellationToken token = default)
    {
        await SweepAsync(token).ConfigureAwait(false);

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
        if (request.FailedAttempts >= MaxFailedApprovals)
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
    /// than the whole endpoint. Called when an approval names an unknown code.
    /// </summary>
    public async Task RecordFailedApprovalAsync(string userCode, CancellationToken token = default)
    {
        TvPairingRequestEntity? request = await FindPendingByUserCodeAsync(userCode, token).ConfigureAwait(false);
        if (request == null)
            return;
        request.FailedAttempts++;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
    }

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
        // Astronomically unlikely; better than looping forever.
        return RandomCode(4) + RandomCode(5);
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
