using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RenzoBackend.Controllers;
using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Auth;
using RenzoBackend.Services.Reader;
using RenzoBackend.Services.Users;
using RenzoOAuthProxy.Services;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// 2026-11-15 audit, sessions/auth/DoS cluster: a stolen pre-rotation refresh
/// token that worked forever, access tokens surviving a password change, an
/// Owner anyone could lock out, spoofable client IPs, an unbounded backup
/// inflate, unclamped page sizes, and an OAuth proxy that refreshed any token
/// for anyone.
/// </summary>
public class SessionsAuthDosTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly List<AppDbContext> _contexts = [];
    private readonly JwtTokenService _jwt = new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["JwtSecret"] = new string('k', 64) })
        .Build());

    public SessionsAuthDosTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        Db().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _contexts.ForEach(c => c.Dispose());
        _conn.Dispose();
    }

    private AppDbContext Db()
    {
        var db = new AppDbContext(_options);
        _contexts.Add(db);
        return db;
    }

    private RefreshSessionService Sessions(AppDbContext db) => new(db, _jwt, NullLogger<RefreshSessionService>.Instance);

    // ---- L15: refresh-token reuse detection ----

    private sealed record Tokens(string Raw0, string Raw1, string Raw2, string H0, string H1, string H2);

    private Tokens NewTokens()
    {
        var (r0, h0) = _jwt.GenerateRefreshToken();
        var (r1, h1) = _jwt.GenerateRefreshToken();
        var (r2, h2) = _jwt.GenerateRefreshToken();
        return new Tokens(r0, r1, r2, h0, h1, h2);
    }

    /// <summary>A session row in a given state. Each test gets a fresh session id,
    /// so the in-process 90s replay buffer (keyed by session) starts empty.</summary>
    private Guid SeedSession(Action<RefreshSessionEntity> shape)
    {
        var db = Db();
        var user = new UserEntity { Id = Guid.NewGuid(), Username = "u" + Guid.NewGuid().ToString("N")[..6], OpdsPath = Guid.NewGuid().ToString("N") };
        db.Users.Add(user);
        var s = new RefreshSessionEntity { UserId = user.Id, ExpiresAt = DateTime.UtcNow.AddDays(30), LastSeenAt = DateTime.UtcNow.AddHours(-4) };
        shape(s);
        db.RefreshSessions.Add(s);
        db.SaveChanges();
        return s.Id;
    }

    private RefreshSessionEntity Load(Guid id) => Db().RefreshSessions.AsNoTracking().Single(s => s.Id == id);

    [Fact]
    public async Task A_device_that_lost_its_refresh_response_is_forgiven_once_without_extending_the_grace()
    {
        Tokens t = NewTokens();
        DateTime validUntil = DateTime.UtcNow.AddHours(20);
        Guid id = SeedSession(s => { s.TokenHash = t.H1; s.PreviousTokenHash = t.H0; s.PreviousTokenValidUntil = validUntil; });

        var result = await Sessions(Db()).RotateAsync(t.Raw0);

        Assert.NotNull(result);
        RefreshSessionEntity row = Load(id);
        Assert.Null(row.RevokedAt);
        Assert.Equal(t.H0, row.PreviousTokenHash);          // still pinned to what the device holds
        Assert.Equal(validUntil, row.PreviousTokenValidUntil); // NOT re-extended
        Assert.NotNull(row.PreviousTokenReplayedAt);
        Assert.Contains(":" + t.H1, row.RetiredTokenHash);  // the replacement it disowned, pinned
        Assert.True(_jwt.ValidateRefreshToken(result.Value.newRawToken, row.TokenHash));
    }

    [Fact]
    public async Task The_previous_token_a_second_time_revokes_the_session()
    {
        Tokens t = NewTokens();
        Guid id = SeedSession(s =>
        {
            s.TokenHash = t.H2; s.PreviousTokenHash = t.H0; s.PreviousTokenValidUntil = DateTime.UtcNow.AddHours(10);
            s.PreviousTokenReplayedAt = DateTime.UtcNow.AddHours(-4);
        });

        Assert.Null(await Sessions(Db()).RotateAsync(t.Raw0));
        Assert.NotNull(Load(id).RevokedAt);
        // ...which ends the current token too.
        Assert.Null(await Sessions(Db()).RotateAsync(t.Raw2));
    }

    [Fact]
    public async Task A_token_the_session_moved_past_revokes_it()
    {
        Tokens t = NewTokens();
        Guid id = SeedSession(s =>
        {
            s.TokenHash = t.H2; s.PreviousTokenHash = t.H1; s.PreviousTokenValidUntil = DateTime.UtcNow.AddHours(20);
            s.RetiredTokenHash = t.H0;
        });

        Assert.Null(await Sessions(Db()).RotateAsync(t.Raw0));
        Assert.NotNull(Load(id).RevokedAt);
    }

    [Fact]
    public async Task A_superseded_token_revokes_even_seconds_after_a_refresh()
    {
        // A copier refreshing every minute used to keep LastSeenAt fresh, and the
        // "it's just a race" window then let every reuse through unpunished.
        Tokens t = NewTokens();
        Guid id = SeedSession(s =>
        {
            s.TokenHash = t.H2; s.PreviousTokenHash = t.H1; s.PreviousTokenValidUntil = DateTime.UtcNow.AddHours(20);
            s.RetiredTokenHash = t.H0; s.LastSeenAt = DateTime.UtcNow.AddSeconds(-5);
        });

        Assert.Null(await Sessions(Db()).RotateAsync(t.Raw0));
        Assert.NotNull(Load(id).RevokedAt);
        Assert.Null(await Sessions(Db()).RotateAsync(t.Raw2));
    }

    [Fact]
    public async Task A_copier_cannot_launder_a_stale_token_by_rotating_straight_away()
    {
        // Copier holds T0; the device rotated T0 -> T1 hours ago.
        Tokens t = NewTokens();
        Guid id = SeedSession(s => { s.TokenHash = t.H1; s.PreviousTokenHash = t.H0; s.PreviousTokenValidUntil = DateTime.UtcNow.AddHours(20); });

        var stolen = await Sessions(Db()).RotateAsync(t.Raw0);          // forgiven once: T2
        Assert.NotNull(stolen);
        string current = stolen.Value.newRawToken;
        for (int i = 0; i < 40; i++)                                     // rotate far past any rolling list
        {
            // A fresh service each time, past the in-memory replay of the last one.
            var next = await Sessions(Db()).RotateAsync(current);
            Assert.NotNull(next);
            current = next.Value.newRawToken;
        }

        // The real device comes back with T1: two parties hold this session.
        Assert.Null(await Sessions(Db()).RotateAsync(t.Raw1));
        Assert.NotNull(Load(id).RevokedAt);
        Assert.Null(await Sessions(Db()).RotateAsync(current));          // the copier's chain is dead too
    }

    private static string Pin(string hash, DateTime until) =>
        $"!{new DateTimeOffset(until, TimeSpan.Zero).ToUnixTimeSeconds()}:{hash}";

    [Fact]
    public async Task A_second_stranded_forgiveness_while_a_pin_is_live_revokes_instead_of_laundering()
    {
        // After one forgiveness (T1 pinned) the copier rotated normally, so the
        // replay flag was cleared; now it comes back "stranded" on T2 again.
        // Repeating that 17 times used to push T1's pin off the list.
        Tokens t = NewTokens();
        var (pinnedRaw, pinnedHash) = _jwt.GenerateRefreshToken();
        Guid id = SeedSession(s =>
        {
            s.TokenHash = t.H1; s.PreviousTokenHash = t.H0; s.PreviousTokenValidUntil = DateTime.UtcNow.AddHours(20);
            s.RetiredTokenHash = Pin(pinnedHash, DateTime.UtcNow.AddDays(20));
        });

        Assert.Null(await Sessions(Db()).RotateAsync(t.Raw0));
        Assert.NotNull(Load(id).RevokedAt);
        Assert.Null(await Sessions(Db()).RotateAsync(t.Raw1));
        Assert.Null(await Sessions(Db()).RotateAsync(pinnedRaw));
    }

    [Fact]
    public async Task A_stranded_device_is_still_forgiven_once_its_old_pin_has_run_out()
    {
        Tokens t = NewTokens();
        var (_, oldHash) = _jwt.GenerateRefreshToken();
        Guid id = SeedSession(s =>
        {
            s.TokenHash = t.H1; s.PreviousTokenHash = t.H0; s.PreviousTokenValidUntil = DateTime.UtcNow.AddHours(20);
            s.RetiredTokenHash = Pin(oldHash, DateTime.UtcNow.AddDays(-1));
        });

        Assert.NotNull(await Sessions(Db()).RotateAsync(t.Raw0));
        RefreshSessionEntity row = Load(id);
        Assert.Null(row.RevokedAt);
        Assert.Contains(":" + t.H1, row.RetiredTokenHash);
        Assert.DoesNotContain(oldHash, row.RetiredTokenHash);
    }

    [Fact]
    public async Task Every_rolled_off_token_stays_a_tripwire_not_just_the_last()
    {
        Tokens t = NewTokens();
        Guid id = SeedSession(s => { s.TokenHash = t.H1; s.PreviousTokenHash = t.H0; s.PreviousTokenValidUntil = DateTime.UtcNow.AddHours(5); });

        var a = await Sessions(Db()).RotateAsync(t.Raw1);    // T0 retired
        var b = await Sessions(Db()).RotateAsync(a!.Value.newRawToken);   // T1 retired too
        Assert.NotNull(b);

        Assert.Null(await Sessions(Db()).RotateAsync(t.Raw0));
        Assert.NotNull(Load(id).RevokedAt);
    }

    [Fact]
    public async Task Normal_rotation_retires_the_old_previous_and_a_prompt_retry_still_replays()
    {
        Tokens t = NewTokens();
        Guid id = SeedSession(s => { s.TokenHash = t.H1; s.PreviousTokenHash = t.H0; s.PreviousTokenValidUntil = DateTime.UtcNow.AddHours(5); });

        var first = await Sessions(Db()).RotateAsync(t.Raw1);
        var retry = await Sessions(Db()).RotateAsync(t.Raw1);   // the response was lost; same token again

        Assert.NotNull(first);
        Assert.NotNull(retry);
        Assert.Equal(first.Value.newRawToken, retry.Value.newRawToken);
        RefreshSessionEntity row = Load(id);
        Assert.Null(row.RevokedAt);
        Assert.Equal(t.H0, row.RetiredTokenHash);
        Assert.Equal(t.H1, row.PreviousTokenHash);
        Assert.Null(row.PreviousTokenReplayedAt);
    }

    [Fact]
    public async Task Signing_out_while_holding_the_previous_token_ends_the_session()
    {
        Tokens t = NewTokens();
        Guid id = SeedSession(s => { s.TokenHash = t.H1; s.PreviousTokenHash = t.H0; s.PreviousTokenValidUntil = DateTime.UtcNow.AddHours(5); });

        await Sessions(Db()).RevokeByTokenAsync(t.Raw0);

        Assert.NotNull(Load(id).RevokedAt);
    }

    // ---- L15: access tokens die with a password / level change ----

    [Fact]
    public void An_access_token_is_only_good_for_the_stamp_it_was_minted_under()
    {
        var user = new UserEntity { Id = Guid.NewGuid(), Username = "owner", OpdsPath = "p", SecurityStamp = 3 };
        ClaimsPrincipal principal = _jwt.ValidateToken(_jwt.GenerateAccessToken(user))!;

        Assert.True(JwtTokenService.MatchesSecurityStamp(principal, user));
        user.SecurityStamp++;
        Assert.False(JwtTokenService.MatchesSecurityStamp(principal, user));
    }

    [Fact]
    public void A_token_from_before_stamps_existed_still_works_until_the_first_bump()
    {
        var legacy = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())]));
        Assert.True(JwtTokenService.MatchesSecurityStamp(legacy, new UserEntity { SecurityStamp = 0 }));
        Assert.False(JwtTokenService.MatchesSecurityStamp(legacy, new UserEntity { SecurityStamp = 1 }));
    }

    [Fact]
    public async Task Password_change_and_level_change_bump_the_stamp()
    {
        var db = Db();
        var passwords = new PasswordService();
        var user = new UserEntity
        {
            Id = Guid.NewGuid(), Username = "reader", OpdsPath = "p", Level = UserLevel.User,
            PasswordHash = passwords.HashPassword("old-password", out string salt),
        };
        user.Salt = salt;
        db.Users.Add(user);
        db.SaveChanges();
        var detached = new UserEntity { Id = user.Id, Username = user.Username, OpdsPath = "p" };
        var commands = new UserCommandService(Db(), passwords, null!, null!);

        Assert.True(await commands.ChangePasswordAsync(detached, "old-password", "new-password"));
        Assert.Equal(1, detached.SecurityStamp);   // mirrored, so the re-minted token is current

        await new UserCommandService(Db(), passwords, null!, null!).UpdateUserAsync(detached, level: UserLevel.Manager);
        Assert.Equal(2, Db().Users.AsNoTracking().Single(u => u.Id == user.Id).SecurityStamp);
    }

    // ---- L14: a stranger cannot lock the Owner out ----

    [Fact]
    public void Failures_from_one_ip_lock_only_that_ip()
    {
        var throttle = new LoginThrottleService();
        for (int i = 0; i < LoginThrottleService.MaxAttempts; i++)
            throttle.RecordFailure("owner", "203.0.113.9");

        Assert.NotNull(throttle.GetLockRemaining("owner", "203.0.113.9"));
        Assert.Null(throttle.GetLockRemaining("Owner", "10.0.1.20"));
        Assert.Null(throttle.GetAccountLockRemaining("owner"));
    }

    [Fact]
    public void A_distributed_attack_locks_the_account_but_known_ips_are_recognised()
    {
        var throttle = new LoginThrottleService();
        throttle.RecordSuccess("owner", "10.0.1.20");
        for (int i = 0; i < LoginThrottleService.MaxAccountAttempts; i++)
            throttle.RecordFailure("owner", $"198.51.100.{i}");

        Assert.NotNull(throttle.GetAccountLockRemaining("owner"));
        Assert.True(throttle.IsKnownIp("owner", "10.0.1.20"));
        Assert.False(throttle.IsKnownIp("owner", "198.51.100.1"));
        Assert.Null(throttle.GetLockRemaining("owner", "10.0.1.20"));
    }

    // ---- L16: X-Forwarded-For only from loopback ----

    private static async Task<IPAddress?> ClientIpAfterForwarding(string peer)
    {
        var options = new ForwardedHeadersOptions();
        Startup.ConfigureForwardedHeaders(options, new ConfigurationBuilder().Build());
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        ctx.Request.Headers["X-Forwarded-For"] = "1.2.3.4";
        await middleware.Invoke(ctx);
        return ctx.Connection.RemoteIpAddress;
    }

    [Theory]
    [InlineData("127.0.0.1", "1.2.3.4")]
    [InlineData("::1", "1.2.3.4")]
    [InlineData("::ffff:127.0.0.1", "1.2.3.4")]
    [InlineData("10.0.1.50", "10.0.1.50")]
    [InlineData("192.168.1.7", "192.168.1.7")]
    public async Task Only_a_loopback_peer_may_set_the_client_ip(string peer, string expected)
    {
        IPAddress? seen = await ClientIpAfterForwarding(peer);
        Assert.NotNull(seen);
        Assert.Equal(IPAddress.Parse(expected).MapToIPv4(), seen!.MapToIPv4());
    }

    // ---- M11: backup import ----

    private static MemoryStream Gzip(byte[] payload)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(payload);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void A_backup_that_inflates_past_the_cap_is_refused()
    {
        using MemoryStream bomb = Gzip(new byte[64 * 1024]);
        Assert.Throws<BackupTooLargeException>(() => TachibkParser.Parse(bomb, maxDecompressedBytes: 16 * 1024));
    }

    [Fact]
    public void A_length_past_the_end_fails_instead_of_looping()
    {
        // field 1 / wire 2, then a length of 2^32-1 (used to cast to -1 and step backwards).
        using MemoryStream bad = Gzip([0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0x00]);
        Assert.Throws<InvalidDataException>(() => TachibkParser.Parse(bad));
    }

    [Fact]
    public void A_well_formed_backup_still_parses()
    {
        byte[] title = "Solo Leveling"u8.ToArray();
        byte[] chapter = [0x20, 0x01, 0x4D, .. BitConverter.GetBytes(12.5f)];   // read=1, number=12.5
        byte[] manga = [0x1A, (byte)title.Length, .. title, 0x82, 0x01, (byte)chapter.Length, .. chapter];
        using MemoryStream backup = Gzip([0x0A, (byte)manga.Length, .. manga]);

        TachibkManga parsed = Assert.Single(TachibkParser.Parse(backup));
        Assert.Equal("Solo Leveling", parsed.Title);
        TachibkChapter ch = Assert.Single(parsed.Chapters);
        Assert.True(ch.Read);
        Assert.Equal(12.5m, ch.Number);
    }

    private static byte[] Varint(int v)
    {
        var bytes = new List<byte>();
        uint u = (uint)v;
        while (u >= 0x80) { bytes.Add((byte)(u | 0x80)); u >>= 7; }
        bytes.Add((byte)u);
        return [.. bytes];
    }

    private static MemoryStream BackupWithChapters(IEnumerable<float> numbers)
    {
        var manga = new List<byte>();
        manga.AddRange([0x1A, 1, (byte)'x']);
        foreach (float n in numbers)
        {
            byte[] chapter = [0x20, 0x01, 0x4D, .. BitConverter.GetBytes(n)];   // read=1, number=n
            manga.AddRange([0x82, 0x01, (byte)chapter.Length, .. chapter]);
        }
        return Gzip([0x0A, .. Varint(manga.Count), .. manga]);
    }

    [Fact]
    public void Repeated_chapter_numbers_collapse_to_one_entry()
    {
        using MemoryStream backup = BackupWithChapters(Enumerable.Repeat(7f, 5000));
        TachibkManga parsed = Assert.Single(TachibkParser.Parse(backup));
        Assert.Equal(7m, Assert.Single(parsed.Chapters).Number);
    }

    [Fact]
    public void A_series_with_too_many_chapters_is_refused()
    {
        using MemoryStream backup = BackupWithChapters(Enumerable.Range(0, TachibkParser.MaxChaptersPerManga + 1).Select(i => (float)i));
        Assert.Throws<BackupTooLargeException>(() => TachibkParser.Parse(backup));
    }

    [Fact]
    public void An_import_only_touches_the_callers_own_library()
    {
        Guid me = Guid.NewGuid(), someoneElse = Guid.NewGuid();
        var series = new[]
        {
            new SeriesEntity { Title = "mine", OwnerId = me },
            new SeriesEntity { Title = "legacy", OwnerId = Guid.Empty },
            new SeriesEntity { Title = "theirs", OwnerId = someoneElse },
        }.AsQueryable();

        string[] titles = ReaderController.ImportableSeries(series, me).Select(s => s.Title).OrderBy(t => t).ToArray();
        Assert.Equal(["legacy", "mine"], titles);
    }

    // ---- M12: page sizes ----

    [Theory]
    [InlineData(int.MaxValue, PageLimits.Latest, PageLimits.Latest)]
    [InlineData(-5, PageLimits.Latest, 0)]
    [InlineData(40, PageLimits.Latest, 40)]
    [InlineData(1000, PageLimits.Updates, 1000)]   // what both clients ask for
    [InlineData(5000, PageLimits.Downloads, 5000)]
    public void Page_sizes_are_clamped_without_cutting_what_clients_ask_for(int requested, int max, int expected) =>
        Assert.Equal(expected, PageLimits.ClampCount(requested, max));

    // ---- L19: OAuth proxy ----

    private static InstanceGuardService Guard(Dictionary<string, string?> config) => new(
        new ConfigurationBuilder().AddInMemoryCollection(config).Build(),
        NullLogger<InstanceGuardService>.Instance);

    private static readonly Dictionary<string, string?> NoDataDir = new() { ["OAuth:BackendDataDir"] = "/nonexistent-renzo-test-dir" };

    [Fact]
    public void Only_the_real_instance_key_is_accepted()
    {
        var guard = Guard(new(NoDataDir) { ["Scrobbling:Proxy:InstanceKey"] = "the-real-key" });
        Assert.True(guard.IsValidInstanceKey("the-real-key"));
        Assert.False(guard.IsValidInstanceKey("anything-non-empty"));
        Assert.False(guard.IsValidInstanceKey(""));
        Assert.False(guard.IsValidInstanceKey(null));
    }

    [Fact]
    public void With_no_key_configured_the_proxy_fails_closed()
    {
        Assert.False(Guard(new(NoDataDir)).IsValidInstanceKey("anything"));
    }

    [Fact]
    public void Public_base_must_be_on_the_allow_list_once_one_exists()
    {
        var guard = Guard(new(NoDataDir) { ["OAuth:PublicBaseUrl"] = "https://shiori.example.com" });
        Assert.Equal("https://shiori.example.com", guard.ResolvePublicBase("https://shiori.example.com/", () => "http://127.0.0.1:5050"));
        Assert.Null(guard.ResolvePublicBase("https://evil.example", () => "http://127.0.0.1:5050"));
        Assert.Null(guard.ResolvePublicBase("javascript:alert(1)", () => "http://127.0.0.1:5050"));
        Assert.Equal("https://shiori.example.com", guard.ResolvePublicBase(null, () => "http://127.0.0.1:5050"));
    }

    [Fact]
    public void The_session_store_is_bounded()
    {
        var store = new TokenStoreService();
        for (int i = 0; i < TokenStoreService.MaxEntries * 3; i++)
            store.Store($"state{i}", "k", "anilist", "https://x/oauth/anilist/callback");
        Assert.True(store.DebugLiveCount() <= TokenStoreService.MaxEntries);
        Assert.NotNull(store.Retrieve($"state{TokenStoreService.MaxEntries * 3 - 1}"));   // newest kept
    }

    [Theory]
    [InlineData("anilist", true)]
    [InlineData("myanimelist", true)]
    [InlineData("..", false)]
    [InlineData("anilist%2F..", false)]
    [InlineData("", false)]
    public void The_public_forwarder_only_takes_a_bare_provider_name(string provider, bool ok) =>
        Assert.Equal(ok, OAuthProxyForwardController.IsProviderSegment(provider));
}
