using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RenzoBackend.Controllers;

/// <summary>
/// Reverse-proxies inbound <c>/api/oauth/*</c> requests — the browser OAuth callback
/// redirected here by MAL/AniList/Kitsu/MangaDex — to the OAuth proxy bundled inside
/// this same container on loopback. This keeps a single public port: TLS terminates at
/// the admin's reverse proxy → the Renzo backend (9833) → this forwarder → the local
/// proxy (127.0.0.1:5050). The server-to-server <c>/url</c> and <c>/token</c> calls the
/// backend makes go straight to the loopback proxy and never pass through here.
/// Anonymous by design: the provider's browser redirect carries no Renzo JWT; the
/// tokens are later retrieved by the authenticated user via the scrobbler callback.
/// </summary>
[ApiController]
[Route("api/oauth")]
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

    [AcceptVerbs("GET", "POST")]
    [Route("{**path}")]
    public async Task Forward(string path)
    {
        var url = $"{_target}/api/oauth/{path}{Request.QueryString}";
        using var client = _httpFactory.CreateClient();
        using var msg = new HttpRequestMessage(new HttpMethod(Request.Method), url);

        if (!HttpMethods.IsGet(Request.Method) && Request.ContentLength is > 0)
        {
            var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            ms.Position = 0;
            msg.Content = new StreamContent(ms);
            if (!string.IsNullOrEmpty(Request.ContentType))
                msg.Content.Headers.TryAddWithoutValidation("Content-Type", Request.ContentType);
        }

        // Forward the handful of headers the proxy cares about.
        foreach (var h in new[] { "X-Public-Base", "X-Instance-Key", "Accept" })
            if (Request.Headers.TryGetValue(h, out var v))
                msg.Headers.TryAddWithoutValidation(h, v.ToString());

        // If the caller didn't supply a public base, derive it from THIS public request
        // so the proxy's redirect_uri still matches what the browser actually hit.
        if (!Request.Headers.ContainsKey("X-Public-Base"))
            msg.Headers.TryAddWithoutValidation("X-Public-Base", $"{Request.Scheme}://{Request.Host}");

        using var resp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
        Response.StatusCode = (int)resp.StatusCode;
        if (resp.Content.Headers.ContentType is { } ct)
            Response.ContentType = ct.ToString();
        await resp.Content.CopyToAsync(Response.Body);
    }
}
