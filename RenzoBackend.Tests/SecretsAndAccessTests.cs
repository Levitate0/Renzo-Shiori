using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RenzoBackend.Controllers;
using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Auth;
using RenzoBackend.Services.Series;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// 2026-11-12 audit: things the server handed to people who should not have
/// had them — stored passwords on GET /api/settings, the image token opening
/// the whole API, OPDS paths on the public user picker, and Apply to All
/// adopting every ownerless series for any user who pressed it.
/// </summary>
public class SecretsAndAccessTests : IDisposable
{
    // ---- GET /api/settings never carries a stored password ----

    private static SettingsDto Stored() => new()
    {
        SmtpUsername = "mailer", SmtpPassword = "smtp-secret",
        SocksProxyUsername = "proxy", SocksProxyPassword = "socks-secret",
    };

    [Fact]
    public void Settings_sent_to_a_client_have_no_passwords_only_flags()
    {
        SettingsDto stored = Stored();
        string json = JsonSerializer.Serialize(stored.WithoutSecrets());

        Assert.DoesNotContain("smtp-secret", json);
        Assert.DoesNotContain("socks-secret", json);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("smtpPasswordSet").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("socksProxyPasswordSet").GetBoolean());
        // The cached instance the rest of the server reads is untouched.
        Assert.Equal("smtp-secret", stored.SmtpPassword);
        Assert.Equal("socks-secret", stored.SocksProxyPassword);
    }

    [Fact]
    public void Nothing_stored_reads_as_not_set()
    {
        SettingsDto c = new SettingsDto().WithoutSecrets();
        Assert.False(c.SmtpPasswordSet);
        Assert.False(c.SocksProxyPasswordSet);
    }

    [Fact]
    public void A_save_that_sends_blank_passwords_keeps_the_stored_ones()
    {
        SettingsDto sent = Stored().WithoutSecrets();   // what a client round-trips
        sent.KeepStoredSecrets(Stored());
        Assert.Equal("smtp-secret", sent.SmtpPassword);
        Assert.Equal("socks-secret", sent.SocksProxyPassword);
    }

    [Fact]
    public void A_typed_password_replaces_and_a_cleared_username_removes()
    {
        SettingsDto sent = Stored().WithoutSecrets();
        sent.SmtpPassword = "new-one";
        sent.SocksProxyUsername = "";
        sent.KeepStoredSecrets(Stored());
        Assert.Equal("new-one", sent.SmtpPassword);
        Assert.Equal("", sent.SocksProxyPassword);
    }

    [Fact]
    public void Auto_download_goes_out_camelCase_like_the_clients_read_it()
    {
        // No naming policy on the server: without a pinned name this was
        // "AutoDownloadNewChapters" and both clients read it as missing (= on).
        string json = JsonSerializer.Serialize(new SettingsDto { AutoDownloadNewChapters = false });
        Assert.Contains("\"autoDownloadNewChapters\":false", json);
    }

    // ---- the ?token= image token only opens image routes ----

    [Theory]
    [InlineData("GET", "/api/image/abc123", true)]
    [InlineData("GET", "/api/image", true)]
    [InlineData("HEAD", "/api/image/abc123", true)]
    [InlineData("GET", "/api/reader/page", true)]
    [InlineData("GET", "/api/reader/stream/page/", true)]
    [InlineData("GET", "/api/reader/preview/page", true)]
    [InlineData("POST", "/api/image/abc123", false)]
    [InlineData("GET", "/api/imagex", false)]
    [InlineData("GET", "/api/settings", false)]
    [InlineData("GET", "/api/users", false)]
    [InlineData("GET", "/api/reader/chapters", false)]
    [InlineData("GET", "/api/reader/stream/pages", false)]
    [InlineData("PUT", "/api/reader/page", false)]
    [InlineData("DELETE", "/api/serie", false)]
    public void Image_token_is_accepted_only_on_image_routes(string method, string path, bool accepted)
    {
        Assert.Equal(accepted, AuthMiddleware.AcceptsImageToken(new PathString(path), method));
    }

    // ---- the public user picker carries no credential ----

    [Fact]
    public void Public_user_picker_has_no_opds_path()
    {
        var u = new UserEntity
        {
            Id = Guid.NewGuid(), Username = "reader", Level = UserLevel.Owner,
            OpdsPath = "0123456789abcdef0123456789abcdef", Email = "a@b.c",
        };
        UserDto d = AuthController.PickerEntry(u);
        Assert.Equal("reader", d.Username);
        Assert.Equal(string.Empty, d.OpdsPath);   // blank, not null: Hub requires the field
        Assert.Null(d.Email);
    }

    // ---- Apply to All: only the Owner adopts ownerless series ----

    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly List<AppDbContext> _contexts = [];

    public SecretsAndAccessTests()
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

    /// <summary>Only the database and logger are reached on this path (no series
    /// has two sources, so nothing is re-ranked or queued).</summary>
    private SeriesCommandService Commands(AppDbContext db) => new(db, null!, null!, null!,
        NullLogger<SeriesCommandService>.Instance, null!, null!, null!, null!, null!, null!, null!,
        null!, null!, null!, null!, null!);

    private (Guid user, Guid ownerless, Guid theirs) Seed(UserLevel level)
    {
        var db = Db();
        var user = new UserEntity { Id = Guid.NewGuid(), Username = "u", Level = level, OpdsPath = "p" };
        user.Preferences = user.SetPriorityPrefs(order: ["A", "B"]);
        var ownerless = new SeriesEntity { Id = Guid.NewGuid(), Title = "Ownerless", StoragePath = "o", OwnerId = Guid.Empty };
        var theirs = new SeriesEntity { Id = Guid.NewGuid(), Title = "Theirs", StoragePath = "t", OwnerId = user.Id };
        db.Users.Add(user);
        db.Series.AddRange(ownerless, theirs);
        db.SaveChanges();
        return (user.Id, ownerless.Id, theirs.Id);
    }

    [Theory]
    [InlineData(UserLevel.User)]
    [InlineData(UserLevel.Manager)]
    [InlineData(UserLevel.Admin)]
    public async Task Apply_to_all_below_owner_adopts_nothing(UserLevel level)
    {
        var (user, ownerless, _) = Seed(level);
        ApplyPriorityToAllResult r = await Commands(Db()).ApplyDefaultPriorityToAllSeriesAsync(user);

        Assert.True(r.Applied);
        Assert.Equal(0, r.SeriesAdopted);
        Assert.Equal(1, r.SeriesConsidered);   // still covers their own series
        Assert.Equal(Guid.Empty, (await Db().Series.SingleAsync(s => s.Id == ownerless)).OwnerId);
    }

    [Fact]
    public async Task Apply_to_all_as_owner_adopts_ownerless_series()
    {
        var (user, ownerless, _) = Seed(UserLevel.Owner);
        ApplyPriorityToAllResult r = await Commands(Db()).ApplyDefaultPriorityToAllSeriesAsync(user);

        Assert.Equal(1, r.SeriesAdopted);
        Assert.Equal(2, r.SeriesConsidered);
        Assert.Equal(user, (await Db().Series.SingleAsync(s => s.Id == ownerless)).OwnerId);
    }
}
