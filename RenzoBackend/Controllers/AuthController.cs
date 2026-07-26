using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Auth;
using RenzoBackend.Services.Users;
using RenzoBackend.Services.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace RenzoBackend.Controllers;

[ApiController]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PasswordService _passwordService;
    private readonly JwtTokenService _jwtTokenService;
    private readonly UserInviteService _userInviteService;
    private readonly UserQueryService _userQueryService;
    private readonly UserCommandService _userCommandService;
    private readonly SettingsService _settingsService;
    private readonly EmailService _emailService;
    private readonly LoginThrottleService _loginThrottle;
    private readonly ILogger _logger;

    public AuthController(
        AppDbContext db,
        PasswordService passwordService,
        JwtTokenService jwtTokenService,
        UserInviteService userInviteService,
        UserQueryService userQueryService,
        UserCommandService userCommandService,
        SettingsService settingsService,
        EmailService emailService,
        LoginThrottleService loginThrottle,
        ILogger<AuthController> logger)
    {
        _db = db;
        _passwordService = passwordService;
        _jwtTokenService = jwtTokenService;
        _userInviteService = userInviteService;
        _userQueryService = userQueryService;
        _userCommandService = userCommandService;
        _settingsService = settingsService;
        _emailService = emailService;
        _loginThrottle = loginThrottle;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/auth/status - Returns authentication status and user list.
    /// Public endpoint.
    /// </summary>
    [HttpGet("/api/auth/status")]
    public async Task<ActionResult<AuthStatusDto>> GetStatus(CancellationToken token)
    {
        var settings = await _settingsService.GetSettingsAsync();
        bool authEnabled = settings.AuthenticationEnabled;
        bool hasUsers = await _userQueryService.AnyUsersExistAsync(token);

        var result = new AuthStatusDto
        {
            AuthenticationEnabled = authEnabled,
            HasUsers = hasUsers
        };

        // When auth is disabled, return user list for the user selector
        if (!authEnabled && hasUsers)
        {
            var users = await _userQueryService.ListUsersAsync(token);
            result.Users = users.Select(u => new UserDto
            {
                Id = u.Id,
                Username = u.Username,
                AvatarBase64 = u.AvatarBlob != null ? Convert.ToBase64String(u.AvatarBlob) : null,
                AvatarContentType = u.AvatarContentType,
                Level = u.Level,
                OpdsPath = u.OpdsPath,
                CreatedAt = u.CreatedAt,
                LastLoginAt = u.LastLoginAt,
                IsActive = true,
                HasPassword = !string.IsNullOrWhiteSpace(u.PasswordHash)
            }).ToList();
        }

        return Ok(result);
    }

    /// <summary>
    /// POST /api/auth/login - Authenticate user with username and password.
    /// Public endpoint, only works when auth is enabled.
    /// Rate limited (5 attempts/minute/IP) as brute-force mitigation.
    /// </summary>
    [HttpPost("/api/auth/login")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponseDto>> Login([FromBody] LoginRequestDto request, CancellationToken token)
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (!settings.AuthenticationEnabled)
            return BadRequest(new { error = "Authentication is not enabled" });

        // Per-account brute-force lockout (complements the per-IP rate limiter).
        // Checked by username BEFORE touching the DB or hashing, so a locked
        // account short-circuits regardless of whether the guess is right.
        TimeSpan? locked = _loginThrottle.GetLockRemaining(request.Username);
        if (locked != null)
        {
            int mins = Math.Max(1, (int)Math.Ceiling(locked.Value.TotalMinutes));
            Response.Headers.RetryAfter = ((int)locked.Value.TotalSeconds).ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { error = $"Too many failed attempts. Try again in about {mins} minute{(mins == 1 ? "" : "s")}." });
        }

        UserEntity? user = await _userQueryService.GetByUsernameAsync(request.Username, token);
        if (user == null || !user.IsActive)
        {
            _loginThrottle.RecordFailure(request.Username);
            return Unauthorized(new { error = "Invalid credentials" });
        }

        if (string.IsNullOrWhiteSpace(user.PasswordHash) || string.IsNullOrWhiteSpace(user.Salt))
            return Unauthorized(new { error = "User has no password set. Ask the admin to send you an invite." });

        if (!_passwordService.VerifyPassword(request.Password, user.PasswordHash, user.Salt))
        {
            _loginThrottle.RecordFailure(request.Username);
            return Unauthorized(new { error = "Invalid credentials" });
        }

        // Success — clear any accumulated failure/lock state for this account.
        _loginThrottle.RecordSuccess(request.Username);

        // Update last login
        await _userCommandService.UpdateLastLoginAsync(user, token);

        // Generate access token
        string accessToken = _jwtTokenService.GenerateAccessToken(user);

        // Handle Remember Me (refresh token)
        if (request.RememberMe)
        {
            var (rawRefreshToken, refreshHash) = _jwtTokenService.GenerateRefreshToken();
            int expirationDays = _jwtTokenService.GetRememberMeExpirationDays();
            DateTime expiresAt = DateTime.UtcNow.AddDays(expirationDays);

            await _userCommandService.StoreRefreshTokenAsync(user, refreshHash, expiresAt, token);

            // Set httpOnly cookie. Secure follows the request scheme: through the
            // Cloudflare Tunnel IsHttps is true (X-Forwarded-Proto is processed), so
            // public access keeps the Secure flag; plain-HTTP LAN access (private IP)
            // would silently never store a Secure cookie, which broke Remember Me on
            // the local network — and the flag protects nothing on a plaintext link.
            Response.Cookies.Append("refresh_token", rawRefreshToken, new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Expires = expiresAt,
                Path = "/api/auth/refresh"
            });
        }

        return Ok(new LoginResponseDto
        {
            Token = accessToken,
            User = UserDto.FromEntity(user)
        });
    }

    /// <summary>
    /// POST /api/auth/select-user - Select a user when auth is disabled.
    /// Public endpoint.
    /// </summary>
    [HttpPost("/api/auth/select-user")]
    public async Task<ActionResult<UserDto>> SelectUser([FromBody] SelectUserRequestDto request, CancellationToken token)
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (settings.AuthenticationEnabled)
            return BadRequest(new { error = "Authentication is enabled, use login instead" });

        UserEntity? user = await _userQueryService.GetByUsernameAsync(request.Username, token);
        if (user == null || !user.IsActive)
            return NotFound(new { error = "User not found" });

        return Ok(UserDto.FromEntity(user));
    }

    /// <summary>
    /// POST /api/auth/refresh - Refresh the access token using the refresh token cookie.
    /// Public endpoint.
    /// </summary>
    [HttpPost("/api/auth/refresh")]
    public async Task<ActionResult<LoginResponseDto>> Refresh(CancellationToken token)
    {
        string? rawRefreshToken = Request.Cookies["refresh_token"];
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
            return Unauthorized(new { error = "No refresh token" });

        // Find user by iterating refresh token hashes
        // (this is O(n) but user bases are small)
        var users = await _db.Users
            .Where(u => !string.IsNullOrWhiteSpace(u.RefreshTokenHash) && u.RefreshTokenExpiresAt > DateTime.UtcNow)
            .ToListAsync(token);

        UserEntity? matchedUser = null;
        foreach (var u in users)
        {
            if (_jwtTokenService.ValidateRefreshToken(rawRefreshToken, u.RefreshTokenHash!))
            {
                matchedUser = u;
                break;
            }
        }

        if (matchedUser == null)
        {
            Response.Cookies.Delete("refresh_token");
            return Unauthorized(new { error = "Invalid or expired refresh token" });
        }

        // Rotate: clear old refresh token
        await _userCommandService.ClearRefreshTokenAsync(matchedUser, token);

        // Generate new access token
        string accessToken = _jwtTokenService.GenerateAccessToken(matchedUser);

        // Generate new refresh token (auto-bump expiration)
        var (newRawRefreshToken, newRefreshHash) = _jwtTokenService.GenerateRefreshToken();
        int expirationDays = _jwtTokenService.GetRememberMeExpirationDays();
        DateTime newExpiresAt = DateTime.UtcNow.AddDays(expirationDays);

        await _userCommandService.StoreRefreshTokenAsync(matchedUser, newRefreshHash, newExpiresAt, token);

        // Secure follows the request scheme for the same LAN-vs-tunnel reason as in Login.
        Response.Cookies.Append("refresh_token", newRawRefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Expires = newExpiresAt,
            Path = "/api/auth/refresh"
        });

        return Ok(new LoginResponseDto
        {
            Token = accessToken,
            User = UserDto.FromEntity(matchedUser)
        });
    }

    /// <summary>
    /// POST /api/auth/logout - Clear the refresh token.
    /// Authenticated endpoint.
    /// </summary>
    [HttpPost("/api/auth/logout")]
    public async Task<ActionResult> Logout(CancellationToken token)
    {
        UserEntity? user = HttpContext.Items["User"] as UserEntity;
        if (user != null)
        {
            await _userCommandService.ClearRefreshTokenAsync(user, token);
        }

        Response.Cookies.Delete("refresh_token");
        return Ok(new { success = true });
    }

    /// <summary>
    /// PUT /api/auth/me - Update current user profile/avatar.
    /// Authenticated endpoint.
    /// </summary>
    [HttpPut("/api/auth/me")]
    public async Task<ActionResult<UserDto>> UpdateMe([FromBody] UpdateUserDto update, CancellationToken token)
    {
        UserEntity? user = HttpContext.Items["User"] as UserEntity;
        if (user == null)
            return Unauthorized();

        byte[]? avatarBlob = null;
        if (!string.IsNullOrWhiteSpace(update.AvatarBase64))
        {
            try
            {
                avatarBlob = Convert.FromBase64String(update.AvatarBase64);
                if (avatarBlob.Length > 2 * 1024 * 1024) // 2MB limit
                    return BadRequest(new { error = "Avatar image must be less than 2MB" });
            }
            catch
            {
                return BadRequest(new { error = "Invalid base64 image data" });
            }
        }

        if (update.Email != null && !string.IsNullOrWhiteSpace(update.Email) &&
            !System.Net.Mail.MailAddress.TryCreate(update.Email.Trim(), out _))
        {
            return BadRequest(new { error = "Invalid email address" });
        }

        await _userCommandService.UpdateUserAsync(user,
            avatarBlob: avatarBlob,
            avatarContentType: update.RemoveAvatar == true ? null : update.AvatarContentType,
            removeAvatar: update.RemoveAvatar,
            email: update.Email,
            preferences: update.Preferences,
            token: token);

        return Ok(UserDto.FromEntity(user));
    }

    /// <summary>
    /// GET /api/auth/me - Get current user info.
    /// Authenticated endpoint.
    /// </summary>
    [HttpGet("/api/auth/me")]
    public ActionResult<UserDto> GetMe()
    {
        UserEntity? user = HttpContext.Items["User"] as UserEntity;
        if (user == null)
            return Unauthorized();

        return Ok(UserDto.FromEntity(user));
    }

    /// <summary>
    /// GET /api/auth/image-token - Mints a short-lived (15 minute), narrowly-scoped
    /// token for authenticating &lt;img src&gt; requests via a `?token=` query
    /// parameter. Requires a valid full-scope session (Bearer header) to call -
    /// the token this returns is intentionally weaker/narrower than the one used
    /// to request it, and cannot be used to call any other endpoint.
    /// Authenticated endpoint.
    /// </summary>
    [HttpGet("/api/auth/image-token")]
    public ActionResult<ImageTokenResponseDto> GetImageToken()
    {
        UserEntity? user = HttpContext.Items["User"] as UserEntity;
        if (user == null)
            return Unauthorized();

        string imageToken = _jwtTokenService.GenerateImageAccessToken(user);

        return Ok(new ImageTokenResponseDto
        {
            Token = imageToken,
            ExpiresAt = DateTime.UtcNow.AddMinutes(15)
        });
    }

    /// <summary>
    /// POST /api/auth/set-password - Set password using invite token.
    /// Public endpoint.
    /// Rate limited (5 attempts/minute/IP) - this endpoint effectively verifies
    /// a token guess (like a password), so it gets the same brute-force
    /// protection as login.
    /// </summary>
    [HttpPost("/api/auth/set-password")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponseDto>> SetPassword([FromBody] SetPasswordRequestDto request, CancellationToken token)
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (!settings.AuthenticationEnabled)
            return BadRequest(new { error = "Authentication is not enabled" });

        if (PasswordPolicy.Validate(request.Password) is { } setErr)
            return BadRequest(new { error = setErr });

        UserEntity? user = await _userQueryService.GetByUsernameAsync(request.Username, token);
        if (user == null)
            return NotFound(new { error = "User not found" });

        if (!_userInviteService.ConsumePasswordSetToken(user, request.Token))
            return BadRequest(new { error = "Invalid or expired token" });

        await _userCommandService.SetPasswordAsync(user, request.Password, token);
        await _userCommandService.UpdateLastLoginAsync(user, token);

        string accessToken = _jwtTokenService.GenerateAccessToken(user);

        return Ok(new LoginResponseDto
        {
            Token = accessToken,
            User = UserDto.FromEntity(user)
        });
    }

    /// <summary>
    /// POST /api/auth/change-password - Change current user's password.
    /// Authenticated endpoint.
    /// Rate limited (5 attempts/minute/IP) - requires the current password, so
    /// the same brute-force protection as login applies here.
    /// </summary>
    [HttpPost("/api/auth/change-password")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult> ChangePassword([FromBody] ChangePasswordDto request, CancellationToken token)
    {
        UserEntity? user = HttpContext.Items["User"] as UserEntity;
        if (user == null)
            return Unauthorized();

        if (PasswordPolicy.Validate(request.NewPassword) is { } changeErr)
            return BadRequest(new { error = changeErr });

        bool success = await _userCommandService.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword, token);
        if (!success)
            return BadRequest(new { error = "Current password is incorrect" });

        return Ok(new { success = true });
    }

    /// <summary>
    /// POST /api/auth/forgot-password - Emails a one-hour password-reset link.
    /// Public endpoint. Keyed strictly on EMAIL address — never username, which
    /// is publicly listed on the user-select screen, so accepting it would let
    /// anyone trigger a reset for any account. ALWAYS returns the same generic
    /// response so it cannot be used to enumerate which emails exist.
    /// Rate limited (5 attempts/minute/IP).
    /// </summary>
    [HttpPost("/api/auth/forgot-password")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult> ForgotPassword([FromBody] ForgotPasswordRequestDto request, CancellationToken token)
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (!settings.AuthenticationEnabled)
            return BadRequest(new { error = "Authentication is not enabled" });

        // Deliberately identical response for every outcome below.
        var genericOk = Ok(new { success = true, message = "If that email address is on file, a reset link has been sent." });

        // Require a syntactically valid email — a bare username never resolves.
        string email = request.Email?.Trim() ?? string.Empty;
        if (email.Length == 0 || !System.Net.Mail.MailAddress.TryCreate(email, out _))
            return genericOk;

        UserEntity? user = await _userQueryService.GetByEmailAsync(email, token);
        if (user == null || !user.IsActive || string.IsNullOrWhiteSpace(user.Email))
            return genericOk;

        if (!await _emailService.IsConfiguredAsync(token))
        {
            _logger.LogWarning("Password reset requested for '{Username}' but SMTP is not configured.", user.Username);
            return genericOk;
        }

        string resetToken = _userInviteService.GeneratePasswordResetToken(user);
        await _db.SaveChangesAsync(token);

        string baseUrl = Services.Settings.InviteUrlResolver.ResolveBaseUrl(settings, Request);
        string link = $"{baseUrl}/auth/reset-password?token={resetToken}";

        string? error = await _emailService.SendAsync(
            user.Email,
            "Renzo Shiori password reset",
            $"Hello {user.Username},\n\n" +
            $"A password reset was requested for your Renzo Shiori account. Click the link below to choose a new password. " +
            $"The link expires in {(int)UserInviteService.PasswordResetTokenLifetime.TotalMinutes} minutes.\n\n" +
            $"{link}\n\n" +
            "If you did not request this, you can ignore this email — your password has not been changed.",
            token);

        if (error != null)
            _logger.LogWarning("Failed to send password-reset email for '{Username}': {Error}", user.Username, error);
        else
            _logger.LogInformation("Password-reset email sent for '{Username}'.", user.Username);

        return genericOk;
    }

    /// <summary>
    /// POST /api/auth/reset-password - Sets a new password using an emailed
    /// reset token. Public endpoint; the token is single-use and expires.
    /// Revokes the account's remember-me refresh token so stolen sessions
    /// don't survive a reset.
    /// Rate limited (5 attempts/minute/IP) - this endpoint verifies a token
    /// guess, so it gets the same brute-force protection as login.
    /// </summary>
    [HttpPost("/api/auth/reset-password")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult> ResetPassword([FromBody] ResetPasswordRequestDto request, CancellationToken token)
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (!settings.AuthenticationEnabled)
            return BadRequest(new { error = "Authentication is not enabled" });

        if (PasswordPolicy.Validate(request.NewPassword) is { } resetErr)
            return BadRequest(new { error = resetErr });

        // Identify the account from the token itself — no username is accepted,
        // so a publicly-known username can't be paired with a guessed/leaked
        // token for a different account. ConsumePasswordResetToken re-verifies
        // the hash + expiry (defense in depth) and clears it (single use).
        UserEntity? user = await _userQueryService.GetByPasswordResetTokenAsync(request.Token, token);
        if (user == null || !user.IsActive || !_userInviteService.ConsumePasswordResetToken(user, request.Token))
            return BadRequest(new { error = "Invalid or expired reset link. Request a new one." });

        user.RefreshTokenHash = null;
        user.RefreshTokenExpiresAt = null;
        await _userCommandService.SetPasswordAsync(user, request.NewPassword, token);

        _logger.LogInformation("Password reset completed for '{Username}'.", user.Username);
        return Ok(new { success = true });
    }
}