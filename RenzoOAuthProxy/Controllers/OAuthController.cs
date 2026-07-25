using RenzoOAuthProxy.Models;
using RenzoOAuthProxy.Services;
using Microsoft.AspNetCore.Mvc;

namespace RenzoOAuthProxy.Controllers;

[ApiController]
[Route("api/oauth")]
public class OAuthController : ControllerBase
{
    private readonly ProviderApiService _providerApi;
    private readonly TokenStoreService _tokenStore;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OAuthController> _logger;

    public OAuthController(
        ProviderApiService providerApi,
        TokenStoreService tokenStore,
        IConfiguration configuration,
        ILogger<OAuthController> logger)
    {
        _providerApi = providerApi;
        _tokenStore = tokenStore;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the public base URL (scheme://host) this proxy is reachable at, so the
    /// OAuth redirect_uri matches the admin's registered provider app. Priority:
    /// the caller-supplied X-Public-Base header (the user's real request host, threaded
    /// through by the backend), then OAuth:PublicBaseUrl config, then the request host.
    /// </summary>
    private string ResolvePublicBase(string? publicBaseHeader)
        => (publicBaseHeader ?? _configuration["OAuth:PublicBaseUrl"])?.TrimEnd('/')
           ?? $"{Request.Scheme}://{Request.Host}";

    [HttpPost("{provider}/url")]
    public async Task<ActionResult<OAuthUrlResponseDto>> GetAuthUrl(
        string provider,
        [FromHeader(Name = "X-Instance-Key")] string instanceKey,
        [FromHeader(Name = "X-Public-Base")] string? publicBase = null)
    {
        if (string.IsNullOrWhiteSpace(instanceKey))
            return Unauthorized(new ErrorResponseDto { Error = "X-Instance-Key header required" });

        try
        {
            // Reuse an already-live, not-yet-completed session for this instance+
            // provider instead of minting a competing one — see FindActive's comment.
            var existing = _tokenStore.FindActive(instanceKey, provider);
            _logger.LogInformation(
                "GetAuthUrl: instanceKey={InstanceKey} provider={Provider} existingFound={Found} existingState={ExistingState} liveEntryCount={Count}",
                instanceKey, provider, existing != null, existing?.State ?? "(none)", _tokenStore.DebugLiveCount());
            if (existing?.AuthUrl != null)
                return Ok(new OAuthUrlResponseDto { AuthUrl = existing.AuthUrl, State = existing.State });

            var state = Guid.NewGuid().ToString("N");
            // Public callback lives at /oauth/... on the Renzo domain (the backend
            // forwards /oauth/* to this proxy's /api/oauth/*). This is the URL the admin
            // registers as the provider redirect_uri.
            var redirectUri = $"{ResolvePublicBase(publicBase)}/oauth/{provider}/callback";
            var (authUrl, codeVerifier) = await _providerApi.GenerateAuthUrlAsync(provider, redirectUri, state);

            _tokenStore.Store(state, instanceKey, provider, redirectUri, codeVerifier, authUrl);

            return Ok(new OAuthUrlResponseDto { AuthUrl = authUrl, State = state });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Error = ex.Message });
        }
    }

    [HttpGet("{provider}/callback")]
    public async Task<IActionResult> Callback(
        string provider,
        [FromQuery] string code,
        [FromQuery] string state,
        [FromQuery] string? redirectUri = null)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            return BadRequest(new ErrorResponseDto { Error = "Missing code or state parameter" });

        var tokenEntry = _tokenStore.Retrieve(state);
        if (tokenEntry == null)
            return BadRequest(new ErrorResponseDto { Error = "Invalid state — authorization session not found" });

        try
        {
            // The redirect_uri sent to the provider on token-exchange MUST byte-match the
            // one used at authorize time. Reuse the stored value (the public URL), not the
            // forwarded request host (which is 127.0.0.1 when the backend forwards here).
            var callbackUri = redirectUri
                ?? (string.IsNullOrEmpty(tokenEntry.RedirectUri) ? null : tokenEntry.RedirectUri)
                ?? $"{Request.Scheme}://{Request.Host}/api/oauth/{provider}/callback";
            var tokenResult = await _providerApi.ExchangeCodeAsync(provider, code, callbackUri, tokenEntry.CodeVerifier);

            // Store plaintext in memory (ephemeral, 5-min TTL, never persisted)
            _tokenStore.SetTokens(state, tokenResult.AccessToken, tokenResult.RefreshToken, tokenResult.ExpiresAt);

            var providerName = provider.ToLowerInvariant() switch
            {
                "anilist" => "AniList",
                "myanimelist" or "mal" => "MyAnimeList",
                "kitsu" => "Kitsu",
                "mangadex" => "MangaDex",
                _ => provider
            };

            return Content($@"<!DOCTYPE html>
<html lang=""en"">
<head><meta charset=""utf-8""><title>Renzō — Complete</title>
<link rel=""icon"" href=""/favicon.ico?v=2"" sizes=""any"">
<link rel=""icon"" type=""image/png"" sizes=""32x32"" href=""/favicon-32x32.png?v=2"">
<style>
*{{margin:0;padding:0;box-sizing:border-box}}
:root{{--accent-h:346.8;--accent-s:77.2%;--accent-l:49.8%;--accent:hsl(var(--accent-h) var(--accent-s) var(--accent-l))}}
body{{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;display:flex;align-items:center;justify-content:center;min-height:100vh;background:hsl(20 14.3% 4.1%);color:hsl(0 0% 95%);transition:none}}
html.light body{{background:hsl(0 0% 100%);color:hsl(240 10% 3.9%)}}
html.light .card{{background:hsl(180 8.2% 90.2%)}}
.card{{background:hsl(24 9.8% 10%);border-radius:12px;padding:2.5rem 3rem;text-align:center;max-width:380px;box-shadow:0 4px 24px rgba(0,0,0,0.3)}}
.logo{{width:56px;height:56px;margin:0 auto 1.25rem;display:block}}
.check{{width:44px;height:44px;border-radius:50%;background:var(--accent);display:inline-flex;align-items:center;justify-content:center;margin-bottom:1rem}}
.check svg{{width:22px;height:22px;stroke:white;stroke-width:3;fill:none;stroke-linecap:round;stroke-linejoin:round}}
h1{{font-size:1.125rem;font-weight:600;margin-bottom:0.5rem}}
p{{font-size:0.875rem;opacity:0.7;margin-bottom:1.5rem}}
.pill{{display:inline-block;background:var(--accent);color:hsl(355.7 100% 97.3%);font-size:0.75rem;font-weight:600;padding:0.25rem 0.75rem;border-radius:999px;text-transform:uppercase;letter-spacing:0.04em}}
.hint{{font-size:0.75rem;opacity:0.4;margin-top:1.5rem}}
</style></head><body>
<div class=""card"">
<img class=""logo"" id=""logoImg"" src=""/renzo-icon-dark.png"" alt=""Renzō"">
<div class=""check""><svg viewBox=""0 0 24 24""><polyline points=""20 6 9 17 4 12""/></svg></div>
<h1>Authentication Complete</h1>
<p>Your {providerName} account has been connected to Renzō.</p>
<div class=""pill"">Connected</div>
<p class=""hint"">You may close this window.</p></div>
<script>
(function(){{
  // Match whichever light/dark/accent the user actually has set in the app
  // (falls back to system preference), instead of a fixed dark-only look.
  try {{
    var theme = localStorage.getItem('renzo-theme');
    var systemDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
    var isDark = theme === 'dark' || (theme !== 'light' && systemDark);
    document.documentElement.classList.toggle('light', !isDark);
    document.getElementById('logoImg').src = isDark ? '/renzo-icon-dark.png' : '/renzo-icon-light.png';
  }} catch (e) {{}}
  try {{
    var accents = {{
      blue: ['217.2', '91.2%', '59.8%'], green: ['142.1', '70.6%', '45.3%'],
      purple: ['262.1', '83.3%', '57.8%'], orange: ['24.6', '95%', '53.1%'],
      slate: ['215', '16%', '46.9%']
    }};
    var a = accents[localStorage.getItem('renzo-accent')];
    if (a) {{
      document.documentElement.style.setProperty('--accent-h', a[0]);
      document.documentElement.style.setProperty('--accent-s', a[1]);
      document.documentElement.style.setProperty('--accent-l', a[2]);
    }}
  }} catch (e) {{}}
  try {{ if (window.opener) window.opener.postMessage({{type:'oauth-success',provider:'{provider}',state:'{state}'}},'*'); }} catch (e) {{}}
}})();
</script>
</body></html>", "text/html");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OAuth callback failed for provider {Provider}", provider);
            return StatusCode(500, new ErrorResponseDto { Error = "Token exchange failed" });
        }
    }

    [HttpPost("{provider}/token")]
    public ActionResult<TokenRetrieveResponseDto> GetToken(
        string provider,
        [FromBody] TokenRetrieveRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.State))
            return BadRequest(new ErrorResponseDto { Error = "State is required" });

        // Peek, don't consume, until tokens actually exist. The frontend starts
        // polling this endpoint ~2s after redirecting to the provider — long before
        // a real login can finish — so unconditionally Remove()-ing on the first
        // poll destroyed the session before the user's actual callback ever arrived,
        // which then found nothing and failed with "Invalid state." Only consume
        // the entry once /callback has actually populated AccessToken.
        var tokenEntry = _tokenStore.Retrieve(request.State);
        if (tokenEntry?.AccessToken == null)
            return NotFound(new ErrorResponseDto { Error = "No tokens found for this state" });

        _tokenStore.Remove(request.State);

        return Ok(new TokenRetrieveResponseDto
        {
            AccessToken = tokenEntry.AccessToken ?? string.Empty,
            RefreshToken = tokenEntry.RefreshToken,
            ExpiresAt = tokenEntry.ExpiresAt
        });
    }

    [HttpPost("{provider}/refresh")]
    public async Task<ActionResult<TokenRefreshResponseDto>> RefreshToken(
        string provider,
        [FromBody] TokenRefreshRequestDto request,
        [FromHeader(Name = "X-Instance-Key")] string instanceKey)
    {
        if (string.IsNullOrWhiteSpace(instanceKey))
            return Unauthorized(new ErrorResponseDto { Error = "X-Instance-Key header required" });

        try
        {
            var tokenResult = await _providerApi.RefreshTokenAsync(provider, request.RefreshToken);
            return Ok(new TokenRefreshResponseDto
            {
                AccessToken = tokenResult.AccessToken,
                RefreshToken = tokenResult.RefreshToken,
                ExpiresAt = tokenResult.ExpiresAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token refresh failed for provider {Provider}", provider);
            return StatusCode(500, new ErrorResponseDto { Error = "Token refresh failed" });
        }
    }
}