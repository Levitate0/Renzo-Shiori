using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Auth;
using RenzoBackend.Services.Users;
using RenzoBackend.Services.Settings;
using RenzoBackend.Services.Series;
using Microsoft.AspNetCore.Mvc;

namespace RenzoBackend.Controllers;

[ApiController]
[Route("/api/users")]
public class UserController : ControllerBase
{
    private readonly UserQueryService _userQueryService;
    private readonly UserCommandService _userCommandService;
    private readonly UserInviteService _userInviteService;
    private readonly SettingsService _settingsService;
    private readonly SeriesCommandService _seriesCommandService;

    public UserController(
        UserQueryService userQueryService,
        UserCommandService userCommandService,
        UserInviteService userInviteService,
        SettingsService settingsService,
        SeriesCommandService seriesCommandService)
    {
        _userQueryService = userQueryService;
        _userCommandService = userCommandService;
        _userInviteService = userInviteService;
        _settingsService = settingsService;
        _seriesCommandService = seriesCommandService;
    }

    /// <summary>
    /// Determines if a user is the owner.
    /// </summary>
    private static bool IsOwner(UserEntity user)
    {
        return user.Level == UserLevel.Owner;
    }

    /// <summary>
    /// A user's OPDS path signs an OPDS reader and the MCP endpoint in as that
    /// user, so it is a credential: only its own user ever gets it back (here or
    /// via /api/auth/me). For anyone else it goes out blank, not omitted, since
    /// Hub's UserDto requires the field.
    /// </summary>
    private UserDto ToDto(UserEntity user)
    {
        UserDto dto = UserDto.FromEntity(user);
        if ((HttpContext.Items["User"] as UserEntity)?.Id != user.Id)
            dto.OpdsPath = string.Empty;
        return dto;
    }

    /// <summary>
    /// GET /api/users - List all users. Admin only.
    /// </summary>
    [HttpGet]
    [RequireUserLevel(UserLevel.Admin)]
    public async Task<ActionResult<List<UserDto>>> ListUsers(CancellationToken token)
    {
        var users = await _userQueryService.ListUsersAsync(token);
        var result = users.Select(ToDto).ToList();
        return Ok(result);
    }

    /// <summary>
    /// GET /api/users/{id} - Get user details. Admin only.
    /// </summary>
    [HttpGet("{id:guid}")]
    [RequireUserLevel(UserLevel.Admin)]
    public async Task<ActionResult<UserDto>> GetUser(Guid id, CancellationToken token)
    {
        UserEntity? user = await _userQueryService.GetByIdAsync(id, token);
        if (user == null)
            return NotFound();

        return Ok(ToDto(user));
    }

    /// <summary>
    /// POST /api/users - Create a new user. Admin only.
    /// No password is set - user must be invited afterward.
    /// </summary>
    [HttpPost]
    [RequireUserLevel(UserLevel.Admin)]
    public async Task<ActionResult<UserDto>> CreateUser([FromBody] CreateUserDto dto, CancellationToken token)
    {
        // The name is the user's library folder: one plain folder name, unique
        // ignoring case (folders resolve case-insensitively).
        if (UserCommandService.UsernameProblem(dto.Username) is { } problem)
            return BadRequest(new { error = problem });

        if (await _userQueryService.UsernameTakenAsync(dto.Username, token))
            return Conflict(new { error = "Username already exists" });

        if (UserCommandService.LibraryFolderTwinProblem((await _settingsService.GetSettingsAsync(token)).StorageFolder, dto.Username) is { } twin)
            return Conflict(new { error = twin });

        // Only owner can create admin-level users; non-owner admins are restricted to user/manager
        UserEntity? currentUser = HttpContext.Items["User"] as UserEntity;
        if (dto.Level >= UserLevel.Admin && (currentUser == null || !IsOwner(currentUser)))
            return StatusCode(403, new { error = "Only the owner can create admin or owner users" });

        // Prevent creating additional owners
        if (dto.Level == UserLevel.Owner)
            return BadRequest(new { error = "Cannot create another owner. Only one owner is allowed." });

        UserEntity user = await _userCommandService.CreateUserAsync(dto.Username, dto.Level, token);
        return CreatedAtAction(nameof(GetUser), new { id = user.Id }, ToDto(user));
    }

    /// <summary>
    /// PUT /api/users/{id} - Update user. Admin only.
    /// Enforces admin hierarchy rules:
    /// - Owner cannot change own level/isActive
    /// - Only owner can change another admin's level/isActive
    /// - Admin users cannot edit the owner at all
    /// </summary>
    [HttpPut("{id:guid}")]
    [RequireUserLevel(UserLevel.Admin)]
    public async Task<ActionResult<UserDto>> UpdateUser(Guid id, [FromBody] UpdateUserDto dto, CancellationToken token)
    {
        UserEntity? user = await _userQueryService.GetByIdAsync(id, token);
        if (user == null)
            return NotFound();

        // Get current user from context
        UserEntity? currentUser = HttpContext.Items["User"] as UserEntity;
        bool isTargetOwner = IsOwner(user);
        bool isCurrentUserOwner = currentUser != null && IsOwner(currentUser);
        bool isSelf = currentUser?.Id == user.Id;

        // Admin users cannot edit the owner
        if (isTargetOwner && !isCurrentUserOwner)
            return StatusCode(403, new { error = "Admin users cannot edit the owner" });

        // Authorization checks for level/isActive changes
        bool changingLevelOrActive = dto.Level.HasValue || dto.IsActive.HasValue;

        if (changingLevelOrActive)
        {
            // Owner cannot change own level/isActive
            if (isSelf && isCurrentUserOwner)
                return BadRequest(new { error = "The owner cannot change their own level or active status" });

            // Only owner can change another admin's level/isActive
            if (user.Level == UserLevel.Admin && !isCurrentUserOwner)
                return StatusCode(403, new { error = "Only the owner can change another admin's level or active status" });

            // Same rule as CreateUser: only the owner grants Admin. An Admin
            // promoting a Manager to a peer Admin could then reset that peer.
            if (dto.Level >= UserLevel.Admin && !isCurrentUserOwner)
                return StatusCode(403, new { error = "Only the owner can make a user an admin" });

            // There is one owner; ownership is never handed over through an edit.
            if (dto.Level == UserLevel.Owner)
                return BadRequest(new { error = "Cannot make another owner. Only one owner is allowed." });
        }

        // The email is where the public forgot-password flow mails a reset link,
        // so setting a peer Admin's email to your own address is a takeover. Below
        // Owner, an Admin's email and avatar are theirs alone to change.
        bool changingProfile = dto.Email != null || !string.IsNullOrWhiteSpace(dto.AvatarBase64) || dto.RemoveAvatar == true;
        if (changingProfile && user.Level >= UserLevel.Admin && !isSelf && !isCurrentUserOwner)
            return StatusCode(403, new { error = "Only the owner can change another admin's email or avatar" });

        byte[]? avatarBlob = null;
        if (!string.IsNullOrWhiteSpace(dto.AvatarBase64))
        {
            try
            {
                avatarBlob = Convert.FromBase64String(dto.AvatarBase64);
                if (avatarBlob.Length > 2 * 1024 * 1024)
                    return BadRequest(new { error = "Avatar image must be less than 2MB" });
            }
            catch
            {
                return BadRequest(new { error = "Invalid base64 image data" });
            }
        }

        if (dto.Email != null && !string.IsNullOrWhiteSpace(dto.Email) &&
            !System.Net.Mail.MailAddress.TryCreate(dto.Email.Trim(), out _))
        {
            return BadRequest(new { error = "Invalid email address" });
        }

        await _userCommandService.UpdateUserAsync(user,
            level: dto.Level,
            isActive: dto.IsActive,
            avatarBlob: avatarBlob,
            avatarContentType: dto.RemoveAvatar == true ? null : dto.AvatarContentType,
            removeAvatar: dto.RemoveAvatar,
            email: dto.Email,
            token: token);

        return Ok(ToDto(user));
    }

    /// <summary>
    /// DELETE /api/users/{id} - Delete user. Admin only.
    /// Enforces admin hierarchy rules:
    /// - Cannot delete yourself
    /// - Cannot delete the owner
    /// - Only owner can delete other admins
    /// - Admin users cannot delete the owner
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RequireUserLevel(UserLevel.Admin)]
    public async Task<ActionResult> DeleteUser(Guid id, CancellationToken token)
    {
        UserEntity? user = await _userQueryService.GetByIdAsync(id, token);
        if (user == null)
            return NotFound();

        // Get current user from context
        UserEntity? currentUser = HttpContext.Items["User"] as UserEntity;

        // Cannot delete yourself
        if (currentUser?.Id == user.Id)
            return BadRequest(new { error = "Cannot delete yourself" });

        bool isTargetOwner = IsOwner(user);
        bool isCurrentUserOwner = currentUser != null && IsOwner(currentUser);

        // Admin users cannot delete the owner
        if (isTargetOwner && !isCurrentUserOwner)
            return StatusCode(403, new { error = "Admin users cannot delete the owner" });

        // Cannot delete the owner
        if (isTargetOwner)
            return BadRequest(new { error = "Cannot delete the owner" });

        // Only owner can delete other admins
        if (user.Level == UserLevel.Admin && !isCurrentUserOwner)
            return StatusCode(403, new { error = "Only the owner can delete other admins" });

        await _userCommandService.DeleteUserAsync(user, token);
        return NoContent();
    }

    /// <summary>
    /// POST /api/users/first - Create the first owner user.
    /// Public endpoint, only works when no users exist.
    /// </summary>
    [HttpPost("/api/users/first")]
    public async Task<ActionResult<UserDto>> CreateFirstUser([FromBody] CreateUserDto dto, CancellationToken token)
    {
        if (await _userQueryService.AnyUsersExistAsync(token))
            return BadRequest(new { error = "Users already exist" });

        if (UserCommandService.UsernameProblem(dto.Username) is { } problem)
            return BadRequest(new { error = problem });

        // Force owner level for first user
        UserEntity user = await _userCommandService.CreateUserAsync(dto.Username, UserLevel.Owner, token);

        // Update any SeriesMappings with Guid.Empty to the real owner ID
        await _seriesCommandService.UpdateSeriesMappingsOwnerAsync(user.Id, token).ConfigureAwait(false);

        return Ok(UserDto.FromEntity(user));
    }

    /// <summary>
    /// PUT /api/users/{id}/claim - Claim an auto-created user as owner.
    /// Public endpoint, only works when no owner exists yet.
    /// </summary>
    [HttpPut("/api/users/{id:guid}/claim")]
    public async Task<ActionResult<UserDto>> ClaimUser(Guid id, CancellationToken token)
    {
        // Check if any owner already exists
        if (await _userQueryService.OwnerExistsAsync(token))
            return BadRequest(new { error = "An owner already exists" });

        UserEntity? user = await _userQueryService.GetByIdAsync(id, token);
        if (user == null)
            return NotFound();

        await _userCommandService.PromoteToOwnerAsync(user, token);

        // Update any SeriesMappings with Guid.Empty to the real owner ID
        await _seriesCommandService.UpdateSeriesMappingsOwnerAsync(user.Id, token).ConfigureAwait(false);

        return Ok(UserDto.FromEntity(user));
    }

    /// <summary>
    /// POST /api/users/{id}/generate-invite - Generate an invite token for a user.
    /// Admin only. The link sets the account's password, so for an account that
    /// already has one it is a password RESET (a takeover of that account):
    /// - only the owner may invite/reset an Admin (or the owner) — an Admin can
    ///   only invite/reset the Users and Managers below them;
    /// - nobody resets their own password this way (change-password asks for the
    ///   current one, so a stolen session can't turn into a permanent takeover);
    /// - the token is marked as a reset only when the account has a password now,
    ///   and set-password refuses an unmarked (invite) token for an account that
    ///   has a password by then.
    /// </summary>
    [HttpPost("{id:guid}/generate-invite")]
    [RequireUserLevel(UserLevel.Admin)]
    public async Task<ActionResult<InviteMessageDto>> GenerateInvite(Guid id, CancellationToken token)
    {
        UserEntity? user = await _userQueryService.GetByIdAsync(id, token);
        if (user == null)
            return NotFound();

        // Admin users cannot invite the owner or a peer admin. No user at all is
        // guest mode (auth off), which RequireUserLevel treats as the owner.
        UserEntity? currentUser = HttpContext.Items["User"] as UserEntity;
        bool isCurrentUserOwner = currentUser == null || IsOwner(currentUser);
        bool isReset = !string.IsNullOrWhiteSpace(user.PasswordHash);
        if (isReset && currentUser?.Id == user.Id)
            return BadRequest(new { error = "Change your own password under Account instead" });
        if (IsOwner(user) && !isCurrentUserOwner)
            return StatusCode(403, new { error = "Admin users cannot invite the owner" });
        if (user.Level == UserLevel.Admin && !isCurrentUserOwner)
            return StatusCode(403, new { error = "Only the owner can invite or reset an admin" });

        // Generate new token (invalidates any previous one). The raw token is only
        // available here — the entity stores just its hash — so thread it through.
        string rawSetToken = _userInviteService.GeneratePasswordSetToken(user, isReset);
        await _dbSaveChangesAsync(user, token);

        var settings = await _settingsService.GetSettingsAsync();
        string externalDomain = Services.Settings.InviteUrlResolver.ResolveBaseUrl(settings, Request);

        string message = _userInviteService.GetInviteMessage(user, externalDomain, settings.AuthenticationEnabled, rawSetToken);

        // No OpdsPath: it is the invited user's credential, not the inviter's.
        // They get it from /api/auth/me once signed in. Blank, not omitted, for Hub.
        return Ok(new InviteMessageDto
        {
            Message = message,
            Token = rawSetToken,
            OpdsPath = string.Empty
        });
    }

    /// <summary>
    /// POST /api/users/{id}/regenerate-opds - Regenerate OPDS path. Admin only.
    /// Admin users cannot regenerate the OPDS path of the owner.
    /// </summary>
    [HttpPost("{id:guid}/regenerate-opds")]
    [RequireUserLevel(UserLevel.Admin)]
    public async Task<ActionResult<RegenerateOpdsResponseDto>> RegenerateOpdsPath(Guid id, CancellationToken token)
    {
        UserEntity? user = await _userQueryService.GetByIdAsync(id, token);
        if (user == null)
            return NotFound();

        // Admin users cannot regenerate OPDS path of the owner
        UserEntity? currentUser = HttpContext.Items["User"] as UserEntity;
        if (IsOwner(user) && (currentUser == null || !IsOwner(currentUser)))
            return StatusCode(403, new { error = "Admin users cannot regenerate the OPDS path for the owner" });

        // Only Users/Managers below an Admin, as with invites and edits.
        if (user.Level == UserLevel.Admin && currentUser != null && !IsOwner(currentUser) && currentUser.Id != user.Id)
            return StatusCode(403, new { error = "Only the owner can regenerate an admin's OPDS path" });

        // Regenerating revokes the old path; the new one is shown only to its own
        // user (see ToDto), so an admin can revoke a path without learning it.
        string newPath = await _userCommandService.RegenerateOpdsPathAsync(user, token);
        return Ok(new RegenerateOpdsResponseDto { OpdsPath = currentUser == null || currentUser.Id == user.Id ? newPath : string.Empty });
    }

    private async Task _dbSaveChangesAsync(UserEntity user, CancellationToken token)
    {
        var db = HttpContext.RequestServices.GetRequiredService<Data.AppDbContext>();
        await db.SaveChangesAsync(token);
    }
}