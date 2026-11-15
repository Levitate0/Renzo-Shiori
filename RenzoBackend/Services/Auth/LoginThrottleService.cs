using System.Collections.Concurrent;

namespace RenzoBackend.Services.Auth;

/// <summary>
/// Brute-force lockout, tracked in memory (singleton). Complements the per-IP
/// rate limiter, which distributed/rotating source IPs get around.
///
/// It used to lock the USERNAME outright after 10 failures from anywhere, and
/// checked that before the password — so anyone could keep the Owner locked out
/// of their own server indefinitely by typing a wrong password every few
/// minutes. Failures now count at two levels:
///  - per (username, client IP): 10 failures lock that pairing for 15 minutes.
///    One noisy source only ever locks itself out.
///  - per username: a much higher threshold (spread across many IPs) locks the
///    account, but only for clients the account has never signed in from. A
///    device the user already uses — an IP with a live remembered session or a
///    recent successful sign-in — still gets through with the right password.
///
/// Deliberately in-memory — a process restart clears all lockouts (a safe
/// escape hatch, never a permanent lockout), and a single-instance deployment
/// needs nothing shared.
/// </summary>
public class LoginThrottleService
{
    /// <summary>Failures from one IP for one username before that pairing locks.</summary>
    public const int MaxAttempts = 10;

    /// <summary>Failures for one username, from all IPs, before unknown clients are locked out.</summary>
    public const int MaxAccountAttempts = 50;

    /// <summary>How long a lock lasts; failures older than this are forgotten too.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>Successful-sign-in IPs remembered per account (most recent kept).</summary>
    private const int MaxKnownIpsPerUser = 16;

    /// <summary>Above this many tracked entries, stale ones are swept on the next failure.</summary>
    private const int SweepThreshold = 10_000;

    private sealed class Entry
    {
        public int Failures;
        public DateTime LastFailure;
        public DateTime? LockedUntil;
    }

    private readonly ConcurrentDictionary<string, Entry> _pairs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry> _accounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> _knownIps = new(StringComparer.Ordinal);

    private static string UserKey(string? username) => (username ?? string.Empty).Trim().ToLowerInvariant();
    private static string PairKey(string? username, string? ip) => UserKey(username) + "\n" + (ip ?? "unknown");

    /// <summary>
    /// Remaining lock for this username FROM THIS IP, or null. Applies to every
    /// client, known or not.
    /// </summary>
    public TimeSpan? GetLockRemaining(string? username, string? ip) => Remaining(_pairs, PairKey(username, ip));

    /// <summary>
    /// Remaining account-wide lock, or null. The caller lets a known client
    /// (see <see cref="IsKnownIp"/>) through regardless.
    /// </summary>
    public TimeSpan? GetAccountLockRemaining(string? username) => Remaining(_accounts, UserKey(username));

    /// <summary>True when this IP signed in to the account successfully since the last restart.</summary>
    public bool IsKnownIp(string? username, string? ip) =>
        ip != null && _knownIps.TryGetValue(UserKey(username), out var ips) && ips.ContainsKey(ip);

    /// <summary>Records a failed login against the (username, IP) pairing and the account.</summary>
    public void RecordFailure(string? username, string? ip)
    {
        DateTime now = DateTime.UtcNow;
        if (_pairs.Count + _accounts.Count > SweepThreshold)
            Sweep(now);
        Bump(_pairs, PairKey(username, ip), MaxAttempts, now);
        Bump(_accounts, UserKey(username), MaxAccountAttempts, now);
    }

    /// <summary>
    /// Clears this pairing's state after a successful login and remembers the
    /// IP as one the account really uses. The account-wide counter is cleared
    /// too: a correct password proves the owner is present.
    /// </summary>
    public void RecordSuccess(string? username, string? ip)
    {
        _pairs.TryRemove(PairKey(username, ip), out _);
        _accounts.TryRemove(UserKey(username), out _);
        if (ip == null)
            return;
        var ips = _knownIps.GetOrAdd(UserKey(username), _ => new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal));
        ips[ip] = DateTime.UtcNow;
        if (ips.Count > MaxKnownIpsPerUser)
        {
            foreach (var oldest in ips.OrderBy(kv => kv.Value).Take(ips.Count - MaxKnownIpsPerUser).ToList())
                ips.TryRemove(oldest.Key, out _);
        }
    }

    private static TimeSpan? Remaining(ConcurrentDictionary<string, Entry> map, string key)
    {
        if (!map.TryGetValue(key, out var e))
            return null;
        lock (e)
        {
            if (e.LockedUntil == null)
                return null;
            TimeSpan remaining = e.LockedUntil.Value - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
                return remaining;
        }
        // Expired — reset so the next attempt starts clean.
        map.TryRemove(key, out _);
        return null;
    }

    private static void Bump(ConcurrentDictionary<string, Entry> map, string key, int threshold, DateTime now)
    {
        var e = map.GetOrAdd(key, _ => new Entry());
        lock (e)
        {
            // Failures spread thinly over a long time are not an attack; only a
            // run inside one lockout window counts toward the threshold.
            if (now - e.LastFailure > LockoutDuration)
                e.Failures = 0;
            e.LastFailure = now;
            e.Failures++;
            if (e.Failures >= threshold)
            {
                e.LockedUntil = now.Add(LockoutDuration);
                e.Failures = 0; // reset the counter; the lock itself now gates attempts
            }
        }
    }

    /// <summary>Drops entries with no live lock and no recent failure, so junk usernames can't grow memory.</summary>
    private void Sweep(DateTime now)
    {
        foreach (var map in new[] { _pairs, _accounts })
        {
            foreach (var kv in map)
            {
                Entry e = kv.Value;
                bool stale;
                lock (e)
                    stale = (e.LockedUntil == null || e.LockedUntil <= now) && now - e.LastFailure > LockoutDuration;
                if (stale)
                    map.TryRemove(kv.Key, out _);
            }
        }
    }
}
