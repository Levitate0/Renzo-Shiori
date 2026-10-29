using Microsoft.AspNetCore.Http;
using RenzoBackend.Services.Auth;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// Which URLs may skip sign-in. A loose rule here once let any "/x/mcp" reach
/// the owner's MCP tools, so every change to these lists should fail a row here.
/// </summary>
public class AuthRoutesTests
{
    const string Strong = "0123456789abcdef0123456789abcdef";

    [Theory]
    // Fixed public endpoints, exact match only.
    [InlineData("POST", "/api/auth/login", true)]
    [InlineData("GET", "/api/auth/status/", true)]
    [InlineData("GET", "/api/auth/statusX", false)]
    [InlineData("GET", "/api/auth/login/../users", false)]
    [InlineData("POST", "/api/users/first", true)]
    [InlineData("GET", "/api/users/first", false)]
    [InlineData("PUT", "/api/users/3f2504e0-4f89-11d3-9a0c-0305e82c3301/claim", true)]
    [InlineData("PUT", "/api/users/x/claim/more", false)]
    [InlineData("GET", "/api/series", false)]
    // OPDS / MCP: only an unguessable path skips sign-in.
    [InlineData("POST", "/" + Strong + "/mcp", true)]
    [InlineData("GET", "/" + Strong, true)]
    [InlineData("POST", "/amber-willow/mcp", false)]
    [InlineData("POST", "/api/mcp", false)]
    [InlineData("GET", "/amber-willow", false)]
    [InlineData("GET", "/" + "0123456789ABCDEF0123456789ABCDEF", false)]
    // Container-hosted OAuth callback.
    [InlineData("GET", "/oauth/anilist/callback", true)]
    public void Skips_sign_in_only_where_intended(string method, string path, bool open)
    {
        var p = new PathString(path);
        Assert.Equal(open, AuthMiddleware.IsBypassRoute(p) || AuthMiddleware.IsPublicRoute(p, method));
    }

    [Fact]
    public async Task Generated_paths_are_strong()
    {
        var path = await new OpdsPathGenerator(null!).GenerateUniquePathAsync();
        Assert.True(OpdsPathGenerator.IsStrong(path));
    }
}
