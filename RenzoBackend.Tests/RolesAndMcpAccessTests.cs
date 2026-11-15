using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RenzoBackend.Controllers;
using RenzoBackend.Data;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Auth;
using RenzoBackend.Services.Mcp;
using RenzoBackend.Services.Reader.AutoBuy;
using RenzoBackend.Services.Users;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// 2026-11-15 audit, roles: MCP tools that were looser than their REST twins
/// (update_settings at Admin = takeover) and ignored library isolation, other
/// users' OPDS paths in the user list and invite, an Admin able to reset a peer
/// Admin, the setup wizard open to every level, and auto-buy on an ownerless
/// series spending any user's site login.
/// </summary>
public class RolesAndMcpAccessTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly List<AppDbContext> _contexts = [];

    public RolesAndMcpAccessTests()
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

    private UserEntity AddUser(UserLevel level, string? passwordHash = null)
    {
        var u = new UserEntity
        {
            Id = Guid.NewGuid(), Username = $"{level}-{Guid.NewGuid():N}"[..20], Level = level,
            OpdsPath = Guid.NewGuid().ToString("N"), PasswordHash = passwordHash, IsActive = true,
        };
        var db = Db();
        db.Users.Add(u);
        db.SaveChanges();
        return u;
    }

    private (Guid series, Guid provider) AddSeries(Guid ownerId, bool autoBuy = false, string site = "SiteA")
    {
        var db = Db();
        var s = new SeriesEntity
        {
            Id = Guid.NewGuid(), Title = "S", StoragePath = Guid.NewGuid().ToString("N"), OwnerId = ownerId,
            AutoBuyPaidChapters = autoBuy,
        };
        var p = new SeriesProviderEntity
        {
            Id = Guid.NewGuid(), SeriesId = s.Id, Provider = site, Title = "S",
            Chapters = [new Chapter { Number = 5, Name = "Chapter 5", Url = "https://sitea.example/ch5", IsLocked = true }],
        };
        db.Series.Add(s);
        db.SeriesProviders.Add(p);
        db.SaveChanges();
        return (s.Id, p.Id);
    }

    // ---- MCP tool levels are never looser than their REST twins ----

    [Theory]
    [InlineData("update_settings", UserLevel.Owner)]            // PUT /api/settings
    [InlineData("set_extension_preferences", UserLevel.Owner)]  // POST /api/provider/preferences
    [InlineData("remove_series", UserLevel.Admin)]              // DELETE /api/series
    [InlineData("uninstall_extension", UserLevel.Admin)]        // POST /api/provider/uninstall
    [InlineData("install_extension", UserLevel.Manager)]        // POST /api/provider/install
    [InlineData("add_series", UserLevel.Manager)]               // POST /api/series
    [InlineData("update_all_series", UserLevel.Manager)]        // POST /api/series/update-all
    public void Mcp_tool_level_matches_its_rest_twin(string tool, UserLevel rest)
    {
        Assert.True(new McpPermissionService().GetRequiredLevel(tool) >= rest);
    }

    [Fact]
    public async Task Admin_cannot_update_settings_over_mcp()
    {
        JsonElement r = await Call(AddUser(UserLevel.Admin), "update_settings", new { authenticationEnabled = false });
        Assert.Equal(-32003, r.GetProperty("code").GetInt32());
    }

    // ---- MCP write and read tools respect library isolation ----

    private McpToolService Mcp(AppDbContext db) => new(new McpPermissionService(), db,
        NullLogger<McpToolService>.Instance, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    /// <summary>Runs one tools/call; returns the tool's own JSON result, or the JSON-RPC error.</summary>
    private async Task<JsonElement> Call(UserEntity user, string tool, object args)
    {
        JsonElement req = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args },
        });
        JsonElement resp = (await Mcp(Db()).HandleJsonRpcAsync(req, user, default))!.Value;
        if (resp.TryGetProperty("error", out JsonElement err))
            return err.Clone();
        string text = resp.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static bool Ok(JsonElement r) => r.TryGetProperty("success", out var s) && s.GetBoolean();

    [Fact]
    public async Task Admin_cannot_remove_another_users_series()
    {
        UserEntity admin = AddUser(UserLevel.Admin);
        var (theirs, _) = AddSeries(AddUser(UserLevel.User).Id);

        Assert.False(Ok(await Call(admin, "remove_series", new { id = theirs.ToString(), alsoPhysical = true })));
        Assert.True(await Db().Series.AnyAsync(s => s.Id == theirs));
    }

    [Theory]
    [InlineData("update_series")]
    [InlineData("verify_series")]
    [InlineData("get_series_health")]
    public async Task Manager_cannot_touch_another_users_series_by_id(string tool)
    {
        UserEntity manager = AddUser(UserLevel.Manager);
        var (theirs, _) = AddSeries(AddUser(UserLevel.User).Id);
        JsonElement r = await Call(manager, tool, new { id = theirs.ToString() });
        Assert.False(Ok(r));
        Assert.Contains("another user", r.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Pause_downloads_only_on_own_series()
    {
        UserEntity manager = AddUser(UserLevel.Manager);
        var (theirs, _) = AddSeries(AddUser(UserLevel.User).Id);
        var (mine, _) = AddSeries(manager.Id);

        Assert.False(Ok(await Call(manager, "pause_series_downloads", new { seriesId = theirs.ToString() })));
        Assert.True(Ok(await Call(manager, "pause_series_downloads", new { seriesId = mine.ToString() })));
        Assert.False((await Db().Series.SingleAsync(s => s.Id == theirs)).PauseDownloads);
        Assert.True((await Db().Series.SingleAsync(s => s.Id == mine)).PauseDownloads);
    }

    [Theory]
    [InlineData("ScanLocalFiles")]
    [InlineData("Download")]
    [InlineData("ImportSeries")]
    [InlineData("UpdateExtensions")]
    [InlineData("InstallAdditionalExtensions")]
    public async Task Manager_cannot_enqueue_server_jobs_with_raw_parameters(string jobType)
    {
        JsonElement r = await Call(AddUser(UserLevel.Manager), "enqueue_job",
            new { jobType, parameters = new { Path = "/" } });
        Assert.False(Ok(r));
        Assert.Contains("Only the owner", r.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Manager_cannot_enqueue_a_chapter_check_for_another_users_source()
    {
        var (_, theirSource) = AddSeries(AddUser(UserLevel.User).Id);
        JsonElement r = await Call(AddUser(UserLevel.Manager), "enqueue_job",
            new { jobType = "GetChapters", parameters = theirSource.ToString() });
        Assert.False(Ok(r));
    }

    private Guid AddJob(JobType type, string key)
    {
        var job = new JobEntity { Id = Guid.NewGuid(), JobType = type, Key = key, GroupKey = type.ToString(), NextExecution = DateTime.UtcNow };
        var db = Db();
        db.Jobs.Add(job);
        db.SaveChanges();
        return job.Id;
    }

    [Fact]
    public async Task Jobs_below_owner_are_only_the_callers_own_chapter_checks()
    {
        UserEntity manager = AddUser(UserLevel.Manager);
        var (_, mySource) = AddSeries(manager.Id);
        var (_, theirSource) = AddSeries(AddUser(UserLevel.User).Id);
        Guid daily = AddJob(JobType.DailyUpdate, "DailyUpdate");
        Guid mine = AddJob(JobType.GetChapters, mySource.ToString());
        Guid theirs = AddJob(JobType.GetChapters, theirSource.ToString());

        JsonElement list = await Call(manager, "list_jobs", new { });
        Assert.Equal(new[] { mine }, list.GetProperty("data").EnumerateArray().Select(j => j.GetProperty("id").GetGuid()).ToArray());

        Assert.False(Ok(await Call(manager, "get_job_status", new { jobId = theirs.ToString() })));
        Assert.False(Ok(await Call(manager, "cancel_job", new { jobId = daily.ToString() })));
        Assert.False(Ok(await Call(manager, "cancel_job", new { jobId = theirs.ToString() })));
        Assert.True(Ok(await Call(manager, "cancel_job", new { jobId = mine.ToString() })));

        var left = await Db().Jobs.Select(j => j.Id).ToListAsync();
        Assert.Contains(daily, left);
        Assert.Contains(theirs, left);
        Assert.DoesNotContain(mine, left);

        // The Owner still sees every job.
        JsonElement all = await Call(AddUser(UserLevel.Owner), "list_jobs", new { });
        Assert.Equal(2, all.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Health_alerts_are_scoped_to_the_callers_library()
    {
        UserEntity user = AddUser(UserLevel.User);
        var (mine, mySource) = AddSeries(user.Id);
        var (theirs, theirSource) = AddSeries(AddUser(UserLevel.User).Id);
        var db = Db();
        db.HealthStatuses.AddRange(
            new HealthStatusEntity { Id = Guid.NewGuid(), TargetType = HealthStatusTargetType.Series, TargetId = mine, Message = "mine" },
            new HealthStatusEntity { Id = Guid.NewGuid(), TargetType = HealthStatusTargetType.Series, TargetId = theirs, Message = "theirs" },
            new HealthStatusEntity { Id = Guid.NewGuid(), TargetType = HealthStatusTargetType.Provider, TargetId = mySource, Message = "my source" },
            new HealthStatusEntity { Id = Guid.NewGuid(), TargetType = HealthStatusTargetType.Provider, TargetId = theirSource, Message = "their source" });
        db.SaveChanges();

        static string[] Messages(JsonElement r) =>
            r.GetProperty("data").EnumerateArray().Select(a => a.GetProperty("message").GetString()!).OrderBy(m => m).ToArray();

        Assert.Equal(new[] { "mine", "my source" }, Messages(await Call(user, "get_health_alerts", new { })));
        Assert.Equal(new[] { "my source" }, Messages(await Call(user, "get_provider_health", new { })));
        Assert.Equal(4, Messages(await Call(AddUser(UserLevel.Owner), "get_health_alerts", new { })).Length);
    }

    // ---- users: OPDS paths, admin hierarchy, invites ----

    private UserController Users(UserEntity? current)
    {
        AppDbContext db = Db();
        var query = new UserQueryService(db);
        var c = new UserController(query, new UserCommandService(db, null!, null!, query), new UserInviteService(), null!, null!);
        var http = new DefaultHttpContext();
        if (current != null) http.Items["User"] = current;
        c.ControllerContext = new ControllerContext { HttpContext = http };
        return c;
    }

    [Fact]
    public async Task User_list_carries_only_the_callers_own_opds_path()
    {
        UserEntity admin = AddUser(UserLevel.Admin);
        UserEntity other = AddUser(UserLevel.User);

        var list = (List<UserDto>)((OkObjectResult)(await Users(admin).ListUsers(default)).Result!).Value!;
        Assert.Equal(admin.OpdsPath, list.Single(u => u.Id == admin.Id).OpdsPath);
        Assert.Equal(string.Empty, list.Single(u => u.Id == other.Id).OpdsPath);   // blank, not null: Hub requires it

        var one = (UserDto)((OkObjectResult)(await Users(admin).GetUser(other.Id, default)).Result!).Value!;
        Assert.Equal(string.Empty, one.OpdsPath);
    }

    [Fact]
    public async Task Admin_cannot_invite_or_reset_a_peer_admin()
    {
        UserEntity admin = AddUser(UserLevel.Admin, "hash");
        UserEntity peer = AddUser(UserLevel.Admin, "hash");

        var r = await Users(admin).GenerateInvite(peer.Id, default);
        Assert.Equal(403, ((ObjectResult)r.Result!).StatusCode);
        Assert.Null((await Db().Users.SingleAsync(u => u.Id == peer.Id)).PasswordSetToken);
    }

    [Fact]
    public async Task Nobody_resets_their_own_password_through_an_invite()
    {
        UserEntity admin = AddUser(UserLevel.Admin, "hash");
        var r = await Users(admin).GenerateInvite(admin.Id, default);
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Theory]
    [InlineData(UserLevel.Admin)]
    [InlineData(UserLevel.Owner)]
    public async Task Only_the_owner_grants_admin(UserLevel to)
    {
        UserEntity admin = AddUser(UserLevel.Admin);
        UserEntity manager = AddUser(UserLevel.Manager);

        var r = await Users(admin).UpdateUser(manager.Id, new UpdateUserDto { Level = to }, default);
        Assert.Equal(403, ((ObjectResult)r.Result!).StatusCode);
        Assert.Equal(UserLevel.Manager, (await Db().Users.SingleAsync(u => u.Id == manager.Id)).Level);
    }

    [Fact]
    public async Task Owner_cannot_make_a_second_owner_by_edit()
    {
        UserEntity owner = AddUser(UserLevel.Owner);
        UserEntity admin = AddUser(UserLevel.Admin);
        var r = await Users(owner).UpdateUser(admin.Id, new UpdateUserDto { Level = UserLevel.Owner }, default);
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Fact]
    public void An_invite_token_sets_only_a_first_password()
    {
        var invites = new UserInviteService();
        var fresh = new UserEntity { Username = "new" };
        Assert.True(invites.ConsumePasswordSetToken(fresh, invites.GeneratePasswordSetToken(fresh)));

        // The account got a password some other way after the invite went out:
        // the old link (which the inviting admin saw) no longer opens it.
        var since = new UserEntity { Username = "since" };
        string old = invites.GeneratePasswordSetToken(since);
        since.PasswordHash = "hash";
        Assert.False(invites.ConsumePasswordSetToken(since, old));

        // An explicit reset (issued after the hierarchy check) does.
        string reset = invites.GeneratePasswordSetToken(since, isReset: true);
        Assert.True(invites.ConsumePasswordSetToken(since, reset));
        Assert.Null(since.PasswordSetToken);
    }

    [Fact]
    public async Task A_level_change_voids_a_pending_invite_or_reset()
    {
        UserEntity manager = AddUser(UserLevel.Manager, "hash");
        var db = Db();
        UserEntity tracked = await db.Users.SingleAsync(u => u.Id == manager.Id);
        new UserInviteService().GeneratePasswordSetToken(tracked, isReset: true);
        await db.SaveChangesAsync();

        await new UserCommandService(db, null!, null!, new UserQueryService(db)).UpdateUserAsync(tracked, level: UserLevel.Admin);

        UserEntity after = await Db().Users.SingleAsync(u => u.Id == manager.Id);
        Assert.Null(after.PasswordSetToken);
        Assert.Null(after.PasswordSetTokenExpiresAt);
    }

    [Fact]
    public void Invite_message_has_no_opds_path()
    {
        var user = new UserEntity { Username = "new", OpdsPath = "0123456789abcdef0123456789abcdef" };
        string message = new UserInviteService().GetInviteMessage(user, "https://example.test", authEnabled: true, rawSetToken: "T");
        Assert.DoesNotContain(user.OpdsPath, message);
        Assert.Contains("token=T", message);
    }

    // ---- setup wizard is Owner only (guest mode on first run still passes) ----

    private static IActionResult? Authorize(Type controller, UserEntity? user, bool authEnabled)
    {
        var attr = controller.GetCustomAttribute<RequireUserLevelAttribute>()!;
        var http = new DefaultHttpContext();
        http.Items["AuthEnabled"] = authEnabled;
        if (user != null) http.Items["User"] = user;
        var ctx = new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), []);
        attr.OnAuthorization(ctx);
        return ctx.Result;
    }

    [Theory]
    [InlineData(UserLevel.User)]
    [InlineData(UserLevel.Manager)]
    [InlineData(UserLevel.Admin)]
    public void Setup_wizard_refuses_below_owner(UserLevel level)
    {
        var r = Authorize(typeof(SetupWizardController), new UserEntity { Level = level }, authEnabled: true);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(r).StatusCode);
    }

    [Fact]
    public void Setup_wizard_admits_the_owner_and_first_run_guest_mode()
    {
        Assert.Null(Authorize(typeof(SetupWizardController), new UserEntity { Level = UserLevel.Owner }, authEnabled: true));
        Assert.Null(Authorize(typeof(SetupWizardController), null, authEnabled: false));
        Assert.IsType<UnauthorizedResult>(Authorize(typeof(SetupWizardController), null, authEnabled: true));
    }

    // ---- auto-buy on an ownerless series: Owner's logins, Owner's request ----

    private ChapterAutoBuyService AutoBuy() => new(Db(), null!, null!, null!,
        new ServiceCollection().BuildServiceProvider(), NullLogger<ChapterAutoBuyService>.Instance);

    private void AddCred(Guid userId, string site = "SiteA")
    {
        var db = Db();
        db.SiteCredentials.Add(new SiteCredentialEntity { Id = Guid.NewGuid(), UserId = userId, Provider = site, Username = "x", Status = "ok" });
        db.SaveChanges();
    }

    [Theory]
    [InlineData(UserLevel.User)]
    [InlineData(UserLevel.Admin)]
    public async Task Ownerless_series_auto_buy_is_not_started_below_owner(UserLevel level)
    {
        UserEntity requester = AddUser(level);
        AddCred(requester.Id);
        var (series, _) = AddSeries(Guid.Empty, autoBuy: true);

        AutoBuyResultDto? r = await AutoBuy().PrecheckAsync(series, 5, requester, default);
        Assert.Equal("not-owner", r?.Reason);
    }

    [Fact]
    public async Task Ownerless_series_auto_buy_never_uses_another_users_login()
    {
        UserEntity owner = AddUser(UserLevel.Owner);
        AddCred(AddUser(UserLevel.User).Id);       // someone else's login for the site
        var (series, _) = AddSeries(Guid.Empty, autoBuy: true);

        AutoBuyResultDto? r = await AutoBuy().PrecheckAsync(series, 5, owner, default);
        Assert.Equal("no-login", r?.Reason);
    }

    [Fact]
    public async Task Ownerless_series_auto_buy_uses_the_owners_own_login()
    {
        UserEntity owner = AddUser(UserLevel.Owner);
        AddCred(owner.Id);
        var (series, _) = AddSeries(Guid.Empty, autoBuy: true);

        // Past the login pick; it stops only because no browser runs in a test.
        AutoBuyResultDto? r = await AutoBuy().PrecheckAsync(series, 5, owner, default);
        Assert.Equal("unavailable", r?.Reason);
        Assert.Contains("browser", r?.Message);
    }

    // ---- second pass: peer-Admin takeover by email, invite level race ----

    [Fact]
    public async Task Admin_cannot_change_a_peer_admins_email_or_avatar()
    {
        UserEntity admin = AddUser(UserLevel.Admin, "hash");
        UserEntity peer = AddUser(UserLevel.Admin, "hash");

        var r = await Users(admin).UpdateUser(peer.Id, new UpdateUserDto { Email = "attacker@example.test" }, default);
        Assert.Equal(403, ((ObjectResult)r.Result!).StatusCode);
        Assert.Null((await Db().Users.SingleAsync(u => u.Id == peer.Id)).Email);

        r = await Users(admin).UpdateUser(peer.Id, new UpdateUserDto { RemoveAvatar = true }, default);
        Assert.Equal(403, ((ObjectResult)r.Result!).StatusCode);
    }

    [Fact]
    public async Task Admin_still_sets_a_users_email_and_owner_an_admins()
    {
        UserEntity admin = AddUser(UserLevel.Admin, "hash");
        UserEntity user = AddUser(UserLevel.User);
        Assert.IsType<OkObjectResult>((await Users(admin).UpdateUser(user.Id, new UpdateUserDto { Email = "u@example.test" }, default)).Result);

        UserEntity owner = AddUser(UserLevel.Owner, "hash");
        Assert.IsType<OkObjectResult>((await Users(owner).UpdateUser(admin.Id, new UpdateUserDto { Email = "a@example.test" }, default)).Result);
    }

    [Fact]
    public void An_invite_issued_before_a_promotion_is_void_after_it()
    {
        // generate-invite checks the level, then saves only the token columns; a
        // promotion committed in between must not leave the new Admin holding it.
        var invites = new UserInviteService();
        var manager = new UserEntity { Username = "m", Level = UserLevel.Manager };
        string raw = invites.GeneratePasswordSetToken(manager);
        manager.Level = UserLevel.Admin;
        Assert.False(invites.ConsumePasswordSetToken(manager, raw));
    }

    [Fact]
    public void An_untagged_invite_from_before_the_level_tag_still_works()
    {
        var user = new UserEntity { Username = "old", Level = UserLevel.User };
        user.PasswordSetToken = UserInviteService.HashResetToken("ABCDEF");
        user.PasswordSetTokenExpiresAt = DateTime.UtcNow.AddDays(1);
        Assert.True(new UserInviteService().ConsumePasswordSetToken(user, "ABCDEF"));
    }

    // ---- second pass: usernames are library folders ----

    [Theory]
    [InlineData("Levitate/Series")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData("back\\slash")]
    [InlineData(" padded")]
    [InlineData("dot.")]
    [InlineData("co:lon")]
    [InlineData("ab")]
    public void Usernames_that_are_not_one_plain_folder_are_refused(string name) =>
        Assert.NotNull(UserCommandService.UsernameProblem(name));

    [Theory]
    [InlineData("Levitate")]
    [InlineData("reader_2")]
    [InlineData("Jo Smith")]
    [InlineData("j.smith")]
    public void Ordinary_usernames_are_accepted(string name) =>
        Assert.Null(UserCommandService.UsernameProblem(name));

    [Fact]
    public async Task A_username_that_differs_only_in_case_is_taken()
    {
        UserEntity owner = AddUser(UserLevel.Owner);
        var r = await Users(owner).CreateUser(new CreateUserDto { Username = owner.Username.ToUpperInvariant(), Level = UserLevel.User }, default);
        Assert.IsType<ConflictObjectResult>(r.Result);
    }

    [Fact]
    public async Task A_new_series_may_not_sit_in_another_users_folder()
    {
        UserEntity owner = AddUser(UserLevel.Owner);
        UserEntity manager = AddUser(UserLevel.Manager);
        var db = Db();
        var theirs = new SeriesEntity { Id = Guid.NewGuid(), Title = "T", StoragePath = owner.Username + "/Manga/Theirs", OwnerId = owner.Id };
        db.Series.Add(theirs);
        db.SaveChanges();

        // The Owner's library root (any case), their series folder, inside it, or above it.
        Assert.True(await Services.Series.SeriesCommandService.FolderBelongsToAnotherUserAsync(Db(), owner.Username.ToLowerInvariant() + "/New", manager.Id, default));
        Assert.True(await Services.Series.SeriesCommandService.FolderBelongsToAnotherUserAsync(Db(), theirs.StoragePath.ToUpperInvariant(), manager.Id, default));
        // Their own folder is fine; the Owner is not limited by it.
        Assert.False(await Services.Series.SeriesCommandService.FolderBelongsToAnotherUserAsync(Db(), manager.Username + "/Manga/New", manager.Id, default));
        Assert.False(await Services.Series.SeriesCommandService.FolderBelongsToAnotherUserAsync(Db(), manager.Username + "/Manga/New", owner.Id, default));
    }

    [Fact]
    public async Task A_series_nested_in_another_users_series_folder_is_refused()
    {
        UserEntity a = AddUser(UserLevel.User);
        UserEntity b = AddUser(UserLevel.User);
        var db = Db();
        db.Series.Add(new SeriesEntity { Id = Guid.NewGuid(), Title = "T", StoragePath = "Shared/Theirs", OwnerId = a.Id });
        db.SaveChanges();

        Assert.True(await Services.Series.SeriesCommandService.FolderBelongsToAnotherUserAsync(Db(), "shared/theirs/inner", b.Id, default));
        Assert.True(await Services.Series.SeriesCommandService.FolderBelongsToAnotherUserAsync(Db(), "Shared", b.Id, default));
    }

    // ---- second pass: auto-buy runs on the shared browser session = Owner only ----

    [Theory]
    [InlineData(UserLevel.User)]
    [InlineData(UserLevel.Admin)]
    public async Task Auto_buy_on_a_users_own_series_is_refused_below_owner(UserLevel level)
    {
        UserEntity requester = AddUser(level);
        AddCred(requester.Id);
        var (series, _) = AddSeries(requester.Id, autoBuy: true);

        AutoBuyResultDto? r = await AutoBuy().PrecheckAsync(series, 5, requester, default);
        Assert.Equal("not-owner", r?.Reason);
    }
}
