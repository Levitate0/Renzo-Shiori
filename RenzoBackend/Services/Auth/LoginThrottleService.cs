using System.Collections.Concurrent;

namespace RenzoBackend.Services.Auth;

/// <summary>
/// Per-account brute-force lockout, tracked in memory (singleton). Complements
/// the per-IP rate limiter: the IP limiter is bypassed by distributed/rotating
/// source IPs, so we also count consecutive failures per USERNAME and lock the
/// account for a cooldown after too many. Deliberately in-memory — a process
/// restart clears all lockouts (a safe escape hatch, never a permanent lockout),
/// and a single-instance deployment needs nothing shared.
/// </summary>
public class LoginThrottleService
{
    /// <summary>Consecutive failures before the account is locked.</summary>
    public const int MaxAttempts = 10;

    /// <summary>How long the account stays locked once the threshold is hit.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private sealed class Entry
    {
        public int Failures;
        public DateTime? LockedUntil;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string username) => (username ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// If the account is currently locked, returns the remaining cooldown;
    /// otherwise null. Clears a lock that has already expired.
    /// </summary>
    public TimeSpan? GetLockRemaining(string username)
    {
        if (!_entries.TryGetValue(Key(username), out var e) || e.LockedUntil == null)
            return null;
        TimeSpan remaining = e.LockedUntil.Value - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            // Expired — reset so the next attempt starts clean.
            _entries.TryRemove(Key(username), out _);
            return null;
        }
        return remaining;
    }

    /// <summary>Records a failed login; locks the account once the threshold is reached.</summary>
    public void RecordFailure(string username)
    {
        var e = _entries.GetOrAdd(Key(username), _ => new Entry());
        lock (e)
        {
            e.Failures++;
            if (e.Failures >= MaxAttempts)
            {
                e.LockedUntil = DateTime.UtcNow.Add(LockoutDuration);
                e.Failures = 0; // reset the counter; the lock itself now gates attempts
            }
        }
    }

    /// <summary>Clears all failure/lock state for an account after a successful login.</summary>
    public void RecordSuccess(string username) => _entries.TryRemove(Key(username), out _);
}
