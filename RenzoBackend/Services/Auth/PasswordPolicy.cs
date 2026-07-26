namespace RenzoBackend.Services.Auth;

/// <summary>
/// Single source of truth for password strength rules, applied wherever a
/// password is set or changed (set-password, change-password, reset-password).
/// Kept intentionally simple: a minimum length with no forced-complexity rules
/// (which push users toward predictable patterns) — length is what matters most,
/// and the hash is PBKDF2 at 600k iterations regardless.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;

    /// <summary>Returns an error message if the password is unacceptable, else null.</summary>
    public static string? Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinLength)
            return $"Password must be at least {MinLength} characters.";
        return null;
    }
}
