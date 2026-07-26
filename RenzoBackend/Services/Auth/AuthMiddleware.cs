using java.nio.file;
using Microsoft.EntityFrameworkCore;
using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using System.Security.Claims;

namespace RenzoBackend.Services.Auth;

/// <summary>
/// Authentication middleware that handles both JWT-based auth and header-based user selection.
/// </summary>
public class AuthMiddleware
{
    private readonly RequestDelegate _next;

    public AuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IServiceProvider serviceProvider)
    {
        //string pathStr = context.Request.Path.Value?.TrimEnd('/') ?? "";

        // Always allow routes that use path-based user resolution (OPDS, MCP)
        if (IsBypassRoute(context.Request.Path))
        {
            await _next(context);
            return;
        }

        // Skip auth for public endpoints
        if (IsPublicRoute(context.Request.Path, context.Request.Method))
        {
            await _next(context);
            return;
        }
        /*
        if (!pathStr.StartsWith("/api/"))
        {
            await _next(context);
            return;
        }*/
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<Services.Settings.SettingsService>();
        var jwtService = scope.ServiceProvider.GetRequiredService<JwtTokenService>();

        bool authEnabled = await IsAuthenticationEnabled(settings);

        // SignalR hub connections (WebSocket upgrades) can't carry the
        // X-Renzo-User header the no-auth mode otherwise relies on — browsers
        // don't allow custom headers on a raw WS handshake. The frontend already
        // mints a short-lived image-scoped JWT for this (same one used for <img>
        // tags) and passes it as `?access_token=` regardless of whether full
        // password auth is toggled on, since image tokens are validated purely by
        // JWT signature and don't depend on that setting. Check it FIRST, before
        // branching on authEnabled, so hub connections resolve to a real user
        // (and therefore only see their own library's activity) in both auth
        // modes — previously this only worked when authEnabled was true, so every
        // hub connection in the (default) no-auth deployment was unattributed and
        // every user's download progress broadcast to everyone.
        if (IsHubRoute(context.Request.Path))
        {
            string? hubToken = context.Request.Query["access_token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(hubToken))
            {
                string? hubAuthHeader = context.Request.Headers["Authorization"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(hubAuthHeader) && hubAuthHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    hubToken = hubAuthHeader["Bearer ".Length..].Trim();
            }
            if (!string.IsNullOrWhiteSpace(hubToken))
            {
                ClaimsPrincipal? hubPrincipal = jwtService.ValidateImageToken(hubToken);
                Guid? hubUserId = hubPrincipal == null ? null : jwtService.GetUserIdFromPrincipal(hubPrincipal);
                UserEntity? hubUser = hubUserId == null ? null : await db.Users.FindAsync(hubUserId.Value);
                if (hubUser != null && hubUser.IsActive)
                {
                    context.Items["User"] = hubUser;
                    context.Items["AuthEnabled"] = authEnabled;
                    await _next(context);
                    return;
                }
            }
            // No valid hub token — fall through to the normal auth-mode branches
            // below (covers the negotiate POST, which CAN carry the
            // X-Renzo-User header, and the authEnabled JWT path).
        }

        if (authEnabled)
        {
            ClaimsPrincipal? principal = null;

            // Primary path: full-scope session token via the Authorization header.
            // This is what every apiClient.ts fetch() call uses.
            string? authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string headerToken = authHeader["Bearer ".Length..].Trim();
                principal = jwtService.ValidateToken(headerToken);
            }
            else
            {
                // Fallback path: a short-lived, image-scoped token via `?token=`
                // query string, for requests that can't set custom headers
                // (<img src="...">, etc.). Deliberately validated with
                // ValidateImageToken (audience "RenzoImages"), NOT the main
                // ValidateToken (audience "Renzo") - a full-power session
                // token dropped into a URL by mistake or by an attacker is
                // rejected here, since only tokens minted by
                // GenerateImageAccessToken carry the right audience. This
                // keeps the powerful, long-lived token out of URLs (and
                // therefore out of browser history / server access logs /
                // any intermediate proxy or CDN logs) entirely.
                string? queryToken = context.Request.Query["token"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(queryToken))
                {
                    principal = jwtService.ValidateImageToken(queryToken);
                }
            }

            // SignalR hub connections: browsers cannot set an Authorization header
            // on a WebSocket upgrade, so the client supplies the same short-lived,
            // narrowly-scoped token used for <img> requests — as `access_token`
            // query on the socket, or as a Bearer header on the negotiate POST
            // (where the full-token validation above already rejected it, since
            // it carries the image audience). Accepted for hub routes ONLY: an
            // image-scoped token must never authorize regular API calls, and hub
            // traffic (job progress events) matches the image token's low-value-
            // if-leaked threat model.
            if (principal == null && IsHubRoute(context.Request.Path))
            {
                string? hubToken = context.Request.Query["access_token"].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(hubToken) &&
                    !string.IsNullOrWhiteSpace(authHeader) &&
                    authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    hubToken = authHeader["Bearer ".Length..].Trim();
                }
                if (!string.IsNullOrWhiteSpace(hubToken))
                {
                    principal = jwtService.ValidateImageToken(hubToken);
                }
            }

            var authLogger = scope.ServiceProvider.GetRequiredService<ILogger<AuthMiddleware>>();
            bool hadHeader = !string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
            bool hadQueryToken = !string.IsNullOrWhiteSpace(context.Request.Query["token"].FirstOrDefault());

            if (principal == null)
            {
                authLogger.LogWarning("AUTH 401 (no principal) path={Path} bearer={Bearer} queryToken={QT}",
                    context.Request.Path, hadHeader, hadQueryToken);
                context.Response.StatusCode = 401;
                return;
            }

            Guid? userId = jwtService.GetUserIdFromPrincipal(principal);
            if (userId == null)
            {
                authLogger.LogWarning("AUTH 401 (no userId claim) path={Path}", context.Request.Path);
                context.Response.StatusCode = 401;
                return;
            }

            UserEntity? user = await db.Users.FindAsync(userId.Value);
            if (user == null || !user.IsActive)
            {
                authLogger.LogWarning("AUTH 401 (user {UserId} null/inactive) path={Path}", userId, context.Request.Path);
                context.Response.StatusCode = 401;
                return;
            }

            context.Items["User"] = user;
            context.Items["AuthEnabled"] = true;
        }
        else
        {
            // Header-based user selection (auth disabled)
            string? username = context.Request.Headers["X-Renzo-User"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(username))
            {
                UserEntity? user = await db.Users.FirstOrDefaultAsync(u => u.Username == username && u.IsActive);
                if (user != null)
                {
                    context.Items["User"] = user;
                }
            }

            context.Items["AuthEnabled"] = false;
        }

        await _next(context);
    }

    private static bool IsHubRoute(PathString path) =>
        path.StartsWithSegments("/progress");

    private static bool IsBypassRoute(PathString path)
    {
        // Routes that use path-based user resolution (OPDS, MCP) bypass JWT auth.
        // The user is resolved from the {opdsPath} segment by the controller itself.
        // We can't easily distinguish OPDS routes by prefix, but MCP routes have a
        // recognizable /mcp/ segment.
        var pathStr = path.Value?.Trim('/') ?? "";
        var segments = pathStr.Split('/');
        // MCP: /{opdsPath}/mcp/sse or /{opdsPath}/mcp/message
        if (segments.Length >= 2 && segments[1].Equals("mcp", StringComparison.OrdinalIgnoreCase))
            return true;
        // Container-hosted OAuth: the provider redirects the user's browser to
        // /oauth/{provider}/callback with no Renzo JWT — it must be reachable
        // unauthenticated. The forwarder + bundled proxy validate state/instance-key.
        if (path.StartsWithSegments("/oauth"))
            return true;
        // OPDS: anything that doesn't start with /api/ is treated as OPDS
        // Since we can't distinguish easily, we handle OPDS routes in the middleware below
        // by allowing all routes through if auth is disabled or if the route doesn't start with /api/
        return false;
    }

    private static bool IsPublicRoute(PathString path, string method)
    {
        // EXACT match (after trimming a trailing slash), not prefix match: a loose
        // StartsWith let a crafted path like "/api/auth/statusX" or
        // "/api/auth/login/../users" slip past auth. These are all single, fixed
        // endpoints with no children, so exact matching loses nothing.
        string pathStr = (path.Value?.TrimEnd('/') ?? "").ToLowerInvariant();

        switch (pathStr)
        {
            // Public auth endpoints
            case "/api/auth/login":
            case "/api/auth/status":
            case "/api/auth/select-user":
            case "/api/auth/refresh":
            case "/api/auth/set-password":
            // Self-service password reset: rate-limited, non-enumerating, and
            // only act on a valid emailed token.
            case "/api/auth/forgot-password":
            case "/api/auth/reset-password":
            // Server discovery — lets clients validate an entered server address
            // (Jellyfin-style) before any credentials exist. Nothing sensitive.
            case "/api/system/info/public":
                return true;
        }

        // First-user creation (only when no users exist)
        if (method == "POST" && pathStr == "/api/users/first") return true;

        // PUT /api/users/{id}/claim — exact 5-segment shape ["", api, users, {id}, claim]
        if (method == "PUT")
        {
            string[] seg = pathStr.Split('/');
            if (seg.Length == 5 && seg[1] == "api" && seg[2] == "users" && seg[4] == "claim")
                return true;
        }

        return false;
    }

    private static async Task<bool> IsAuthenticationEnabled(Services.Settings.SettingsService settings)
    {
        try
        {
            var editableSettings = await settings.GetSettingsAsync();
            return editableSettings.AuthenticationEnabled;
        }
        catch
        {
            return false;
        }
    }
}

public static class AuthMiddlewareExtensions
{
    public static IApplicationBuilder UseAuthMiddleware(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<AuthMiddleware>();
    }
}