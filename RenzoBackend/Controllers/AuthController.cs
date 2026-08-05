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

    private readonly RefreshSessionService _refreshSessions;
    private readonly TvPairingService _tvPairing;

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
        RefreshSessionService refreshSessions,
        TvPairingService tvPairing,
        ILogger<AuthController> logger)
    {
        _refreshSessions = refreshSessions;
        _tvPairing = tvPairing;
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
            // One session row per device: signing in here must not evict the
            // user's other remembered devices (it used to — there was a single
            // refresh-token column on the user).
            var (rawRefreshToken, session) = await _refreshSessions
                .CreateAsync(user, DeviceNameFromRequest(), ClientIp(), isTvPairing: false, token)
                .ConfigureAwait(false);
            DateTime expiresAt = session.ExpiresAt;

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

        // Rotation happens in place on the device's own session row, so the
        // device keeps its identity (name, paired-at) and other devices are
        // untouched.
        var rotated = await _refreshSessions.RotateAsync(rawRefreshToken, token).ConfigureAwait(false);
        if (rotated == null)
        {
            Response.Cookies.Delete("refresh_token");
            return Unauthorized(new { error = "Invalid or expired refresh token" });
        }

        UserEntity matchedUser = rotated.Value.user;
        string newRawRefreshToken = rotated.Value.newRawToken;
        string accessToken = _jwtTokenService.GenerateAccessToken(matchedUser);
        DateTime newExpiresAt = DateTime.UtcNow.AddDays(_jwtTokenService.GetRememberMeExpirationDays());

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
        // Revoke THIS device's session only — signing out on a phone must not
        // sign the user out of their desktop and their TV as well.
        string? rawRefreshToken = Request.Cookies["refresh_token"];
        if (!string.IsNullOrWhiteSpace(rawRefreshToken))
            await _refreshSessions.RevokeByTokenAsync(rawRefreshToken, token).ConfigureAwait(false);

        Response.Cookies.Delete("refresh_token");
        return Ok(new { success = true });
    }


    // ── TV pairing ─────────────────────────────────────────────────────
    // Typing a password with a D-pad is miserable, and the users who most need
    // TV access often have no phone to "set it up on". The alternative today is
    // disabling authentication server-wide, which removes passwords for every
    // account on the instance. This is the OAuth device-authorisation flow, so
    // a television signs in without anyone typing a password into it.

    /// <summary>POST /api/auth/tv/code — device asks for a pairing code. Public.</summary>
    [HttpPost("/api/auth/tv/code")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult> TvCode([FromBody] TvCodeRequestDto? request, CancellationToken token)
    {
        var (pairing, rawDeviceCode) = await _tvPairing
            .CreateAsync(request?.DeviceName, ClientIp(), token).ConfigureAwait(false);

        return Ok(new
        {
            userCode = FormatUserCode(pairing.UserCode),
            deviceCode = rawDeviceCode,
            verificationUrl = BuildVerificationUrl(),
            expiresIn = (int)TvPairingService.Lifetime.TotalSeconds,
            interval = TvPairingService.PollIntervalSeconds,
        });
    }

    /// <summary>
    /// POST /api/auth/tv/poll — device polls for approval. Public.
    /// On approval this issues EXACTLY what a rememberMe login issues: an access
    /// token, the user, and the refresh cookie. A TV that only got a 24h token
    /// would need re-pairing daily, which defeats the feature.
    /// </summary>
    [HttpPost("/api/auth/tv/poll")]
    public async Task<ActionResult> TvPoll([FromBody] TvPollRequestDto request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request?.DeviceCode))
            return Ok(new { status = "expired" });

        var claimed = await _tvPairing.TryClaimAsync(request.DeviceCode, token).ConfigureAwait(false);
        if (claimed != null)
        {
            (TvPairingRequestEntity pairing, UserEntity user) = claimed.Value;
            await _userCommandService.UpdateLastLoginAsync(user, token);

            // Pairing is unconditionally "remember me" — a TV is the one place
            // where not remembering makes no sense.
            var (rawRefreshToken, session) = await _refreshSessions
                .CreateAsync(user, pairing.DeviceName ?? "TV", pairing.RequestIp, isTvPairing: true, token)
                .ConfigureAwait(false);

            Response.Cookies.Append("refresh_token", rawRefreshToken, new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Expires = session.ExpiresAt,
                Path = "/api/auth/refresh",
            });

            return Ok(new
            {
                status = "approved",
                token = _jwtTokenService.GenerateAccessToken(user),
                user = UserDto.FromEntity(user),
            });
        }

        TvPairingRequestEntity? state = await _tvPairing
            .FindByDeviceCodeAsync(request.DeviceCode, token).ConfigureAwait(false);
        return state switch
        {
            null => Ok(new { status = "expired" }),
            { Status: TvPairingStatus.Denied } => Ok(new { status = "denied" }),
            // Approved but already claimed — a replay of a single-use code.
            { Status: TvPairingStatus.Approved, Claimed: true } => Ok(new { status = "expired" }),
            _ => Ok(new { status = "pending" }),
        };
    }

    /// <summary>
    /// POST /api/auth/tv/approve — the approval page grants the request.
    /// Authenticated: the approver's identity is what the device receives, so a
    /// username is never read from the body.
    /// </summary>
    [HttpPost("/api/auth/tv/approve")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult> TvApprove([FromBody] TvApproveRequestDto request, CancellationToken token)
    {
        UserEntity? user = HttpContext.Items["User"] as UserEntity;
        if (user == null)
            return Unauthorized(new { error = "Sign in first" });
        if (string.IsNullOrWhiteSpace(request?.UserCode))
            return BadRequest(new { error = "Enter the code shown on your TV" });

        TvPairingResult result = await _tvPairing.ApproveAsync(request.UserCode, user.Id, token).ConfigureAwait(false);
        if (result == TvPairingResult.NotFound)
        {
            await _tvPairing.RecordFailedApprovalAsync(request.UserCode, token).ConfigureAwait(false);
            return NotFound(new { error = "That code isn't valid — check it and try again, or restart pairing on the TV." });
        }
        return result switch
        {
            TvPairingResult.Locked => StatusCode(429, new { error = "Too many attempts for that code. Restart pairing on the TV." }),
            TvPairingResult.AlreadyResolved => Conflict(new { error = "That code was already used." }),
            _ => Ok(new { success = true }),
        };
    }

    /// <summary>POST /api/auth/tv/deny — refuse a request so the TV stops polling.</summary>
    [HttpPost("/api/auth/tv/deny")]
    public async Task<ActionResult> TvDeny([FromBody] TvApproveRequestDto request, CancellationToken token)
    {
        if (HttpContext.Items["User"] is not UserEntity)
            return Unauthorized(new { error = "Sign in first" });
        if (string.IsNullOrWhiteSpace(request?.UserCode))
            return BadRequest(new { error = "No code supplied" });
        await _tvPairing.DenyAsync(request.UserCode, token).ConfigureAwait(false);
        return Ok(new { success = true });
    }

    /// <summary>
    /// GET /api/auth/tv/pending — what the approval page shows before granting:
    /// the device name and requesting IP, so "Living Room TV from my own LAN"
    /// can be told apart from something that looks wrong.
    /// </summary>
    [HttpGet("/api/auth/tv/pending")]
    public async Task<ActionResult> TvPending([FromQuery] string userCode, CancellationToken token)
    {
        if (HttpContext.Items["User"] is not UserEntity)
            return Unauthorized(new { error = "Sign in first" });
        TvPairingRequestEntity? request = await _tvPairing
            .FindPendingByUserCodeAsync(userCode, token).ConfigureAwait(false);
        if (request == null || request.Status != TvPairingStatus.Pending)
            return NotFound(new { error = "That code isn't valid — check it and try again." });
        return Ok(new
        {
            deviceName = request.DeviceName ?? "Unnamed device",
            requestIp = request.RequestIp,
            expiresAt = request.ExpiresAt,
        });
    }

    // ── Remembered devices ─────────────────────────────────────────────

    /// <summary>GET /api/auth/devices — this account's remembered sign-ins.</summary>
    [HttpGet("/api/auth/devices")]
    public async Task<ActionResult> Devices(CancellationToken token)
    {
        UserEntity? user = HttpContext.Items["User"] as UserEntity;
        if (user == null)
            return Unauthorized();

        string? currentRaw = Request.Cookies["refresh_token"];
        List<RefreshSessionEntity> sessions = await _refreshSessions.ListAsync(user.Id, token).ConfigureAwait(false);
        return Ok(sessions.Select(s => new
        {
            id = s.Id,
            deviceName = s.DeviceName ?? "Existing session",
            createdAt = s.CreatedAt,
            lastSeenAt = s.LastSeenAt,
            expiresAt = s.ExpiresAt,
            createdIp = s.CreatedIp,
            isTvPairing = s.IsTvPairing,
            isCurrent = currentRaw != null && _jwtTokenService.ValidateRefreshToken(currentRaw, s.TokenHash),
        }));
    }

    /// <summary>
    /// DELETE /api/auth/devices/{id} — revoke ONE device. Retiring a TV must not
    /// sign the user out everywhere else.
    /// </summary>
    [HttpDelete("/api/auth/devices/{id:guid}")]
    public async Task<ActionResult> RevokeDevice(Guid id, CancellationToken token)
    {
        UserEntity? user = HttpContext.Items["User"] as UserEntity;
        if (user == null)
            return Unauthorized();
        bool revoked = await _refreshSessions.RevokeAsync(user.Id, id, token).ConfigureAwait(false);
        return revoked ? Ok(new { success = true }) : NotFound(new { error = "Unknown device" });
    }

    // ── helpers ────────────────────────────────────────────────────────

    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>A best-effort label so the device list isn't a row of blanks.</summary>
    private string? DeviceNameFromRequest()
    {
        string ua = Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(ua))
            return null;
        if (ua.Contains("Renzo", StringComparison.OrdinalIgnoreCase)) return "Renzo app";
        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "Android";
        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
            ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "iOS";
        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
        if (ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)) return "Mac";
        if (ua.Contains("Linux", StringComparison.OrdinalIgnoreCase)) return "Linux";
        return "Browser";
    }

    /// <summary>Grouped for readability on a TV screen: ABCD-2345.</summary>
    private static string FormatUserCode(string code) =>
        code.Length == 8 ? $"{code[..4]}-{code[4..]}" : code;

    /// <summary>
    /// Absolute, so the TV can print it verbatim. Prefers the configured
    /// external domain; falls back to the request's own origin, which is what a
    /// LAN-only install needs.
    /// </summary>
    private string BuildVerificationUrl()
    {
        string configured = _settingsService.GetSettingsAsync().GetAwaiter().GetResult().ExternalDomain;
        string root = string.IsNullOrWhiteSpace(configured)
            ? $"{Request.Scheme}://{Request.Host}"
            : configured.TrimEnd('/');
        return $"{root}/tv";
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