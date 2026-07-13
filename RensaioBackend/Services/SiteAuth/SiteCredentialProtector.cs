using Microsoft.AspNetCore.DataProtection;

namespace RensaioBackend.Services.SiteAuth;

/// <summary>
/// Encrypts site passwords and harvested cookies at rest with ASP.NET Core
/// Data Protection — the same mechanism used for scrobbler OAuth tokens.
/// </summary>
public class SiteCredentialProtector
{
    private readonly IDataProtector _protector;

    public SiteCredentialProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Rensaio.SiteAuth.Credentials");
    }

    public string Encrypt(string plainText) => _protector.Protect(plainText);

    public string? TryDecrypt(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
            return null;
        try { return _protector.Unprotect(protectedText); }
        catch { return null; } // key rotated / tampered — treat as absent
    }
}
