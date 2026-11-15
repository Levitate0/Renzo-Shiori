using System.Collections.Concurrent;

namespace RenzoOAuthProxy.Services;

/// <summary>
/// In-memory token store using a ConcurrentDictionary.
/// Tokens are stored in plaintext (ephemeral, in-memory only, 15-minute TTL).
/// </summary>
public class TokenStoreService
{
    private readonly ConcurrentDictionary<string, TokenStoreEntry> _store = new();
    // 15 min, not 5 — a real first-time login (typing credentials, a slow provider
    // page, maybe 2FA) can eat several minutes before the redirect even happens;
    // 5 was tight enough to expire mid-flow on an unhurried user.
    private readonly TimeSpan _entryTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Hard ceiling on live sessions. Expired entries were only dropped when
    /// their own state was looked up again, so every abandoned authorize call
    /// stayed in memory for the life of the process. One instance needs a
    /// handful at a time; the cap only bites on a flood.
    /// </summary>
    public const int MaxEntries = 256;

    public void Store(string state, string instanceKey, string provider, string redirectUri, string? codeVerifier = null, string? authUrl = null)
    {
        DateTime now = DateTime.UtcNow;
        foreach (var kv in _store)
        {
            if (now - kv.Value.CreatedAt >= _entryTtl)
                _store.TryRemove(kv.Key, out _);
        }
        int excess = _store.Count - (MaxEntries - 1);
        if (excess > 0)
        {
            foreach (var oldest in _store.OrderBy(kv => kv.Value.CreatedAt).Take(excess).ToList())
                _store.TryRemove(oldest.Key, out _);
        }

        _store[state] = new TokenStoreEntry
        {
            State = state,
            InstanceKey = instanceKey,
            Provider = provider,
            RedirectUri = redirectUri,
            CodeVerifier = codeVerifier,
            AuthUrl = authUrl,
            CreatedAt = now
        };
    }

    /// <summary>
    /// The newest still-live, not-yet-completed session for this instance+provider,
    /// if one exists. Two authorize calls close together for the same provider — a
    /// double click, a stray retry, another open tab/device — otherwise mint two
    /// independent states; if the user finishes login on whichever page ISN'T the
    /// most recent one, its state is technically still valid but "invalid state"
    /// looking to the app, since only the newest is what anything is waiting on.
    /// Reusing the existing session instead means either page completes correctly.
    /// </summary>
    public TokenStoreEntry? FindActive(string instanceKey, string provider)
    {
        return _store.Values
            .Where(e => e.InstanceKey == instanceKey
                        && string.Equals(e.Provider, provider, StringComparison.OrdinalIgnoreCase)
                        && e.AccessToken == null
                        && DateTime.UtcNow - e.CreatedAt < _entryTtl)
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefault();
    }

    public TokenStoreEntry? Retrieve(string state)
    {
        if (_store.TryGetValue(state, out var entry))
        {
            if (DateTime.UtcNow - entry.CreatedAt < _entryTtl)
                return entry;

            _store.TryRemove(state, out _);
        }
        return null;
    }

    public void SetTokens(string state, string accessToken, string? refreshToken, DateTime? expiresAt)
    {
        if (_store.TryGetValue(state, out var entry))
        {
            entry.AccessToken = accessToken;
            entry.RefreshToken = refreshToken;
            entry.ExpiresAt = expiresAt;
        }
    }

    public TokenStoreEntry? Remove(string state)
    {
        _store.TryRemove(state, out var entry);
        return entry;
    }

    /// <summary>Diagnostic only — count of entries currently in the store, live or not.</summary>
    public int DebugLiveCount() => _store.Count;
}

public class TokenStoreEntry
{
    public string State { get; set; } = string.Empty;
    public string InstanceKey { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    // PKCE — MyAnimeList requires a code_challenge on every authorize request
    // (code_challenge_method=plain, i.e. the challenge IS the verifier), so the
    // verifier generated at authorize-url time has to survive until token exchange.
    public string? CodeVerifier { get; set; }
    public string? AuthUrl { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}