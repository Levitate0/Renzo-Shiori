using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace RenzoBackend.Services.Auth;

/// <summary>
/// Service for querying user data.
/// </summary>
public class UserQueryService
{
    private readonly AppDbContext _db;

    public UserQueryService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<UserEntity>> ListUsersAsync(CancellationToken token = default)
    {
        return await _db.Users.OrderBy(u => u.Username).ToListAsync(token);
    }

    public async Task<UserEntity?> GetByIdAsync(Guid id, CancellationToken token = default)
    {
        return await _db.Users.FindAsync(new object[] { id }, token);
    }

    public async Task<UserEntity?> GetByUsernameAsync(string username, CancellationToken token = default)
    {
        return await _db.Users.FirstOrDefaultAsync(u => u.Username == username, token);
    }

    public async Task<UserEntity?> GetByOpdsPathAsync(string opdsPath, CancellationToken token = default)
    {
        return await _db.Users.FirstOrDefaultAsync(u => u.OpdsPath == opdsPath, token);
    }

    /// <summary>
    /// Finds an active user by (case-insensitive) email address. Account recovery
    /// is keyed strictly on email — never username, which is publicly visible on
    /// the user-select screen.
    /// </summary>
    public async Task<UserEntity?> GetByEmailAsync(string email, CancellationToken token = default)
    {
        string lowered = email.Trim().ToLowerInvariant();
        if (lowered.Length == 0)
            return null;
        return await _db.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == lowered, token);
    }

    /// <summary>
    /// Finds the user holding a given (unexpired) password-reset token, by its
    /// stored hash — so the reset flow identifies the account from the token
    /// itself rather than a caller-supplied username.
    /// </summary>
    public async Task<UserEntity?> GetByPasswordResetTokenAsync(string rawToken, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return null;
        string hash = UserInviteService.HashResetToken(rawToken);
        return await _db.Users.FirstOrDefaultAsync(
            u => u.PasswordResetTokenHash == hash
                 && u.PasswordResetExpiresAt != null
                 && u.PasswordResetExpiresAt > DateTime.UtcNow,
            token);
    }

    public async Task<bool> AnyUsersExistAsync(CancellationToken token = default)
    {
        return await _db.Users.AnyAsync(token);
    }

    public async Task<bool> AnyUserHasPasswordAsync(CancellationToken token = default)
    {
        return await _db.Users.AnyAsync(u => !string.IsNullOrWhiteSpace(u.PasswordHash), token);
    }

    public async Task<int> GetUserCountAsync(CancellationToken token = default)
    {
        return await _db.Users.CountAsync(token);
    }

    /// <summary>
    /// Gets the owner user. Used for authorization checks.
    /// </summary>
    public async Task<UserEntity?> GetOwnerAsync(CancellationToken token = default)
    {
        return await _db.Users
            .Where(u => u.Level == UserLevel.Owner)
            .FirstOrDefaultAsync(token);
    }

    /// <summary>
    /// Checks if an owner user already exists.
    /// </summary>
    public async Task<bool> OwnerExistsAsync(CancellationToken token = default)
    {
        return await _db.Users.AnyAsync(u => u.Level == UserLevel.Owner, token);
    }
}