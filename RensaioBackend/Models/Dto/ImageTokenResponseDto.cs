namespace RensaioBackend.Models.Dto;

/// <summary>
/// Response for GET /api/auth/image-token: a short-lived, narrowly-scoped
/// token for authenticating &lt;img src&gt; requests via a `?token=` query
/// parameter. See JwtTokenService.GenerateImageAccessToken.
/// </summary>
public class ImageTokenResponseDto
{
    public required string Token { get; set; }
    public DateTime ExpiresAt { get; set; }
}
