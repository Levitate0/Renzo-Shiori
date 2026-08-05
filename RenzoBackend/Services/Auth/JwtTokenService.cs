using RenzoBackend.Models.Database;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Settings;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using JwtRegisteredClaimNames = System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames;

namespace RenzoBackend.Services.Auth;

/// <summary>
/// Service for generating and validating JWT access tokens and refresh tokens.
/// </summary>
public class JwtTokenService
{
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Distinct audience for short-lived image-access tokens (see
    /// <see cref="GenerateImageAccessToken"/>). Kept separate from the main
    /// "Renzo" API audience so an image token can never be used to call any
    /// other endpoint, and a main session token can never be used as an image
    /// token, even though both are signed with the same key.
    /// </summary>
    private const string ImageAudience = "RenzoImages";

    private readonly ILogger<JwtTokenService>? _logger;

    public JwtTokenService(IConfiguration configuration, ILogger<JwtTokenService>? logger = null)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Gets the JWT signing key from configuration.
    /// On first run, EnvironmentSetup.WriteToAppSettingsAsync auto-generates a
    /// cryptographically random 256-bit secret and persists it to
    /// appsettings.json before the app ever starts serving requests, so this
    /// should always be present by the time anything calls GetSigningKey().
    ///
    /// This deliberately fails closed (throws) rather than falling back to a
    /// hardcoded default if the secret is somehow still missing. This is
    /// open-source code - any hardcoded fallback secret is, by definition,
    /// public knowledge, and anyone who read it could forge valid tokens for
    /// any user. A missing secret here means something upstream is broken
    /// (unwritable config, failed config load, etc.) and the app should refuse
    /// to start with forgeable auth rather than silently doing so.
    /// </summary>
    private string GetJwtSecret()
    {
        string? secret = _configuration["JwtSecret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "JwtSecret is missing from configuration. This is normally auto-generated " +
                "on first run (see EnvironmentSetup.WriteToAppSettingsAsync). If you're seeing " +
                "this, appsettings.json may not be writable, or configuration failed to load - " +
                "refusing to start with forgeable authentication.");
        }
        return secret;
    }

    /// <summary>
    /// Builds the symmetric signing key with a stable KeyId derived from the secret.
    /// The KeyId is required so the JWT 'kid' header is emitted on signing and can be
    /// resolved on validation. Microsoft.IdentityModel.Tokens 8.x performs strict 'kid'
    /// matching and throws IDX10517 when both the token and the key lack an id.
    /// </summary>
    private SymmetricSecurityKey GetSigningKey()
    {
        string secret = GetJwtSecret();
        byte[] keyBytes = Encoding.UTF8.GetBytes(secret);

        // Short, stable, non-reversible identifier for this key.
        byte[] kidHash = SHA256.HashData(keyBytes);
        string kid = Convert.ToBase64String(kidHash, 0, 8);

        return new SymmetricSecurityKey(keyBytes)
        {
            KeyId = kid
        };
    }

    /// <summary>
    /// Generates a JWT access token for the given user.
    /// </summary>
    public string GenerateAccessToken(UserEntity user)
    {
        SymmetricSecurityKey key = GetSigningKey();
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        int expirationHours = RuntimeSecuritySettings.SessionExpirationHours;

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
            new Claim("level", ((int)user.Level).ToString()),
            new Claim("opdsPath", user.OpdsPath),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: "Renzo",
            audience: "Renzo",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(expirationHours),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Generates a short-lived (15 minute), narrowly-scoped JWT for authenticating
    /// image/thumbnail requests via a `?token=` query-string parameter, since
    /// &lt;img src="..."&gt; tags are loaded natively by the browser and cannot
    /// attach an Authorization header the way apiClient's fetch() calls do.
    ///
    /// This is deliberately weaker than the main access token in two ways:
    ///   1. Short lifetime (minutes, not hours) - even if it leaks via browser
    ///      history, server access logs, or an intermediate proxy/CDN log
    ///      (relevant once this is exposed publicly via Cloudflare Tunnel), the
    ///      exposure window is small.
    ///   2. Distinct audience ("RenzoImages" vs "Renzo") - a stolen image
    ///      token cannot be replayed against any other endpoint, and conversely
    ///      the main session token can never be used as an image token even if
    ///      someone mistakenly (or maliciously) puts it in a URL, since
    ///      ValidateImageToken only accepts the "RenzoImages" audience.
    /// </summary>
    public string GenerateImageAccessToken(UserEntity user)
    {
        SymmetricSecurityKey key = GetSigningKey();
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: "Renzo",
            audience: ImageAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Generates a cryptographically random refresh token.
    /// Returns both the raw token (to give to client) and its SHA-256 hash (to store in DB).
    /// </summary>
    public (string rawToken, string hash) GenerateRefreshToken()
    {
        byte[] tokenBytes = RandomNumberGenerator.GetBytes(32); // 256-bit
        string rawToken = Convert.ToBase64String(tokenBytes);

        byte[] hashBytes = SHA256.HashData(tokenBytes);
        string hash = Convert.ToBase64String(hashBytes);

        return (rawToken, hash);
    }

    /// <summary>
    /// Validates a refresh token raw value against the stored hash.
    /// </summary>
    public bool ValidateRefreshToken(string rawToken, string storedHash)
    {
        // A token that isn't even base64 is simply invalid — it must not throw.
        // Convert.FromBase64String does, and this runs on values supplied by the
        // caller: a corrupted refresh cookie (or any junk posted to a pairing
        // poll) turned into an unhandled FormatException and a 500 instead of a
        // clean "not authorised".
        if (string.IsNullOrEmpty(rawToken) || string.IsNullOrEmpty(storedHash))
            return false;

        Span<byte> tokenBytes = stackalloc byte[((rawToken.Length * 3) / 4) + 3];
        if (!Convert.TryFromBase64String(rawToken, tokenBytes, out int written))
            return false;

        byte[] computedHash = SHA256.HashData(tokenBytes[..written]);
        string computedHashString = Convert.ToBase64String(computedHash);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHashString),
            Encoding.UTF8.GetBytes(storedHash)
        );
    }

    /// <summary>
    /// Validates a JWT token and returns the ClaimsPrincipal.
    /// Returns null if the token is invalid or expired.
    /// </summary>
    public ClaimsPrincipal? ValidateToken(string token)
    {
        SymmetricSecurityKey key = GetSigningKey();

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var result = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = true,
                ValidIssuer = "Renzo",
                ValidateAudience = true,
                ValidAudience = "Renzo",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            }, out _);

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("ValidateToken (session) FAILED: {Type}: {Msg}", ex.GetType().Name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Validates a short-lived image-access token (see
    /// <see cref="GenerateImageAccessToken"/>). Only accepts tokens with the
    /// "RenzoImages" audience - a normal full-scope access token, despite
    /// being signed with the same key, is rejected here because its audience
    /// is "Renzo" instead.
    /// </summary>
    public ClaimsPrincipal? ValidateImageToken(string token)
    {
        SymmetricSecurityKey key = GetSigningKey();

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var result = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = true,
                ValidIssuer = "Renzo",
                ValidateAudience = true,
                ValidAudience = ImageAudience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            }, out _);

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("ValidateImageToken FAILED: {Type}: {Msg}", ex.GetType().Name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Extracts the user ID (sub claim) from a valid principal.
    /// </summary>
    public Guid? GetUserIdFromPrincipal(ClaimsPrincipal principal)
    {
        string? subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrWhiteSpace(subClaim))
        {
            subClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (subClaim==null)
                return null;
        }
        if (Guid.TryParse(subClaim, out Guid userId))
            return userId;

        return null;
    }

    /// <summary>
    /// Gets the remember-me expiration days from the live (WebUI-editable) settings.
    /// </summary>
    public int GetRememberMeExpirationDays()
    {
        return RuntimeSecuritySettings.RememberMeExpirationDays;
    }
}