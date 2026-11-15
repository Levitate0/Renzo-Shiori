using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RenzoBackend.Controllers;

/// <summary>
/// Reverse-proxies the browser OAuth callback (<c>/oauth/{provider}/callback</c>,
/// redirected here by MAL/AniList/Kitsu/MangaDex) to the OAuth proxy bundled inside
/// this same container on loopback. This keeps a single public port: TLS terminates at
/// the admin's reverse proxy → the Renzo backend (9833) → this forwarder → the local
/// proxy (127.0.0.1:5050). The server-to-server <c>/url</c> and <c>/token</c> calls the
/// backend makes go straight to the loopback proxy and never pass through here.
/// Anonymous by design: the provider's browser redirect carries no Renzo JWT; the
/// tokens are later retrieved by the authenticated user via the scrobbler callback.
/// </summary>
[ApiController]
[Route("oauth")]
[AllowAnonymous]
public class OAuthProxyForwardController : ControllerBase
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _target;

    public OAuthProxyForwardController(IHttpClientFactory httpFactory, IConfiguration cfg)
    {
        _httpFactory = httpFactory;
        _target = (cfg["Scrobbling:LocalProxyUrl"] ?? "http://127.0.0.1:5050").TrimEnd('/');
    }

    /// <summary>
    /// Only the provider's browser redirect — <c>GET /oauth/{provider}/callback</c> —
    /// is forwarded. Everything else the proxy serves (<c>/url</c>, <c>/token</c>,
    /// <c>/refresh</c>) is server-to-server: the backend calls the proxy on
    /// loopback directly, so exposing them here only handed the internet an
    /// unauthenticated way to spend the app's client secret.
    /// </summary>
    [HttpGet("{provider}/callback")]
    public async Task Forward(string provider)
    {
        if (!IsProviderSegment(provider))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var url = $"{_target}/api/oauth/{provider}/callback{Request.QueryString}";
        using var client = _httpFactory.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);

        // Only Accept. The proxy's instance key and public base are the backend's
        // to assert; a browser must not be able to supply either.
        if (Request.Headers.TryGetValue("Accept", out var accept))
            msg.Headers.TryAddWithoutValidation("Accept", accept.ToString());

        using var resp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
        Response.StatusCode = (int)resp.StatusCode;
        if (resp.Content.Headers.ContentType is { } ct)
            Response.ContentType = ct.ToString();
        await resp.Content.CopyToAsync(Response.Body);
    }

    /// <summary>A bare provider name ("anilist", "myanimelist"): no dots, slashes or escapes.</summary>
    internal static bool IsProviderSegment(string? provider) =>
        !string.IsNullOrEmpty(provider) && provider.Length <= 32 && provider.All(char.IsAsciiLetterOrDigit);
}
