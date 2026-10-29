using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RenzoBackend.Services.Auth;

/// <summary>
/// Generates a user's OPDS path: the first URL segment of their OPDS feed and
/// MCP endpoint. Reader apps and AI tools cannot send a sign-in token, so this
/// path IS the credential, and it has to be unguessable. It used to be two words
/// from a 264-word list (~69,000 possibilities, no rate limit), which an
/// internet caller could enumerate in minutes and reach the owner's MCP tools,
/// including update_settings.
/// </summary>
public partial class OpdsPathGenerator
{
    public OpdsPathGenerator(IServiceProvider serviceProvider) { }

    /// <summary>32 hex characters = 128 random bits; collisions are not a concern.</summary>
    public Task<string> GenerateUniquePathAsync() =>
        Task.FromResult(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());

    /// <summary>
    /// True for a path this generator makes. AuthMiddleware lets a request skip
    /// sign-in only on such a path; an old two-word path keeps working only when
    /// sign-in is off, until the user regenerates it.
    /// </summary>
    public static bool IsStrong(string segment) => StrongPath().IsMatch(segment);

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex StrongPath();
}
