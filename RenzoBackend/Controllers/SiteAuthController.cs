using RenzoBackend.Models.Database;
using RenzoBackend.Services.SiteAuth;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace RenzoBackend.Controllers;

public class SiteCredentialDto
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("statusDetail")] public string? StatusDetail { get; set; }
    [JsonPropertyName("lastLoginAt")] public DateTime? LastLoginAt { get; set; }
    [JsonPropertyName("supportsAutoLogin")] public bool SupportsAutoLogin { get; set; }

    public static SiteCredentialDto From(SiteCredentialEntity c) => new()
    {
        Id = c.Id, Provider = c.Provider, Username = c.Username,
        Status = c.Status, StatusDetail = c.StatusDetail, LastLoginAt = c.LastLoginAt,
        // Any discovered coin site can attempt auto-login; the paste path always exists too.
        SupportsAutoLogin = true,
    };
}

public class SiteInfoDto
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("domain")] public string Domain { get; set; } = "";
    [JsonPropertyName("supportsAutoLogin")] public bool SupportsAutoLogin { get; set; }
    /// <summary>True when the source advertises a coin/paid gate (recommended to log in); false = offered because you have series from it.</summary>
    [JsonPropertyName("coin")] public bool Coin { get; set; }
}

public class SaveCredentialRequest
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
}

public class SaveCookieRequest
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("cookie")] public string Cookie { get; set; } = "";
}

/// <summary>
/// Manages per-user logins to coin/paid scanlation sites. Credentials are
/// stored encrypted; on save (and on demand) Renzō logs in, harvests the
/// session cookies into the shared Mihon jar, and the source's extension then
/// serves the chapters the user owns. Sites that can't be automated accept a
/// pasted session cookie instead.
/// </summary>
[ApiController]
[Route("api/site-auth")]
[Produces("application/json")]
public class SiteAuthController : ControllerBase
{
    private readonly SiteAuthService _service;
    private readonly CoinSiteRegistry _registry;
    private readonly ILogger _logger;

    public SiteAuthController(SiteAuthService service, CoinSiteRegistry registry, ILogger<SiteAuthController> logger)
    {
        _service = service;
        _registry = registry;
        _logger = logger;
    }

    private Guid CurrentUserId => (HttpContext.Items["User"] as UserEntity)?.Id ?? Guid.Empty;

    /// <summary>
    /// Coin sites the user could add a login for — auto-detected from every
    /// installed source's own preferences, so newly installed coin sources
    /// appear here automatically.
    /// </summary>
    [HttpGet("sites")]
    [ProducesResponseType(typeof(List<SiteInfoDto>), 200)]
    public async Task<ActionResult<List<SiteInfoDto>>> GetSites(CancellationToken token = default)
    {
        var sites = await _registry.GetLoginableSitesAsync(token).ConfigureAwait(false);
        return Ok(sites.Select(s => new SiteInfoDto
        {
            Provider = s.def.Provider, Domain = s.def.Domain,
            SupportsAutoLogin = !string.IsNullOrEmpty(s.def.Domain),
            Coin = s.coin,
        }).ToList());
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<SiteCredentialDto>), 200)]
    public async Task<ActionResult<List<SiteCredentialDto>>> List(CancellationToken token = default)
    {
        var creds = await _service.ListAsync(CurrentUserId, token).ConfigureAwait(false);
        return Ok(creds.Select(SiteCredentialDto.From).ToList());
    }

    /// <summary>Saves username/password and logs in immediately.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(SiteCredentialDto), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult> Save([FromBody] SaveCredentialRequest req, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(req.Provider) || string.IsNullOrWhiteSpace(req.Username))
            return BadRequest(new { error = "Site and username are required." });

        var (cred, result) = await _service.SaveAndLoginAsync(
            CurrentUserId, req.Provider, req.Username.Trim(), req.Password, token).ConfigureAwait(false);
        return Ok(new { credential = SiteCredentialDto.From(cred), result });
    }

    /// <summary>Saves a pasted session cookie for sites that can't auto-login.</summary>
    [HttpPost("cookie")]
    [ProducesResponseType(typeof(SiteCredentialDto), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult> SaveCookie([FromBody] SaveCookieRequest req, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(req.Provider) || string.IsNullOrWhiteSpace(req.Cookie))
            return BadRequest(new { error = "Site and cookie value are required." });

        var (cred, result) = await _service.SaveCookieAsync(
            CurrentUserId, req.Provider, req.Username?.Trim() ?? "", req.Cookie.Trim(), token).ConfigureAwait(false);
        if (cred.Id == Guid.Empty)
            return BadRequest(new { error = result.Detail });
        return Ok(new { credential = SiteCredentialDto.From(cred), result });
    }

    /// <summary>Re-runs login for an existing credential (the "Test / Re-login" button).</summary>
    [HttpPost("{id:guid}/login")]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> Relogin(Guid id, CancellationToken token = default)
    {
        var (cred, result) = await _service.ReloginAsync(CurrentUserId, id, token).ConfigureAwait(false);
        if (cred == null)
            return NotFound();
        return Ok(new { credential = SiteCredentialDto.From(cred), result });
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(200)]
    public async Task<ActionResult> Delete(Guid id, CancellationToken token = default)
    {
        await _service.DeleteAsync(CurrentUserId, id, token).ConfigureAwait(false);
        return Ok(new { success = true });
    }
}
