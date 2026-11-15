namespace RenzoOAuthProxy.Models;

public class TokenRetrieveRequestDto
{
    public string State { get; set; } = string.Empty;

    /// <summary>The backend's instance key, which it sends in the body for this call.</summary>
    public string? InstanceKey { get; set; }
}
