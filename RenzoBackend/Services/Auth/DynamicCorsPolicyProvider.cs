using Microsoft.AspNetCore.Cors.Infrastructure;
using RenzoBackend.Services.Settings;

namespace RenzoBackend.Services.Auth;

/// <summary>
/// Resolves the CORS policy per request from the current (WebUI-editable)
/// AllowedOrigins setting instead of a policy frozen at startup, so origin
/// changes apply without a restart. Preserves the two prior behaviors exactly:
/// configured list => strict allowlist with credentials; empty list =>
/// AllowAnyOrigin WITHOUT credentials (never a credentialed wildcard).
/// </summary>
public class DynamicCorsPolicyProvider : ICorsPolicyProvider
{
    private string[]? _cachedOrigins;
    private CorsPolicy? _cachedPolicy;

    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        string[] origins = RuntimeSecuritySettings.AllowedOrigins;

        // Rebuild only when the settings snapshot actually changed (reference
        // swap in RuntimeSecuritySettings); otherwise reuse the built policy.
        CorsPolicy? policy = _cachedPolicy;
        if (policy == null || !ReferenceEquals(_cachedOrigins, origins))
        {
            var builder = new CorsPolicyBuilder();
            if (origins.Length > 0)
            {
                builder.WithOrigins(origins)
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            }
            else
            {
                builder.AllowAnyOrigin()
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            }
            policy = builder.Build();
            _cachedOrigins = origins;
            _cachedPolicy = policy;
        }

        return Task.FromResult<CorsPolicy?>(policy);
    }
}
