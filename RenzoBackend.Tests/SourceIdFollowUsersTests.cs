using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Background;
using RenzoBackend.Services.Providers;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// Carrying users to a source's new id (SourceMoveService): once, so a user who
/// switches the new id off afterwards is not switched back on every pass.
/// </summary>
public class SourceIdFollowUsersTests : IDisposable
{
    private static readonly SourceIdChange Change =
        new("eu.kanade.tachiyomi.extension.en.asmotoon", "5258838449396844455", "2076371496779298312", "Asmodeus Scans", "en");
    private const string Other = "eu.kanade.tachiyomi.extension.en.mangafire|1";
    private static readonly Guid OnlyOld = Guid.NewGuid(), Both = Guid.NewGuid(), OnlyNew = Guid.NewGuid(), Neither = Guid.NewGuid();

    // A real SQLite schema, like the live server's, each call on a fresh context.
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly List<AppDbContext> _contexts = [];

    public SourceIdFollowUsersTests()
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

    private void Enable(Guid user, string providerId)
    {
        AppDbContext db = Db();
        db.UserProviders.Add(new UserProviderEntity { Id = Guid.NewGuid(), UserId = user, MihonProviderId = providerId });
        db.SaveChanges();
    }

    private void Disable(Guid user, string providerId)
    {
        AppDbContext db = Db();
        db.UserProviders.RemoveRange(db.UserProviders.Where(u => u.UserId == user && u.MihonProviderId == providerId));
        db.SaveChanges();
    }

    private List<string> Enabled(Guid user) =>
        Db().UserProviders.Where(u => u.UserId == user).Select(u => u.MihonProviderId).OrderBy(p => p).ToList();

    private Task<List<Guid>> ToCarry() => SourceMoveService.UsersToCarryAsync(Db(), Change, CancellationToken.None);

    [Fact]
    public async Task A_user_is_moved_to_the_new_id_once()
    {
        Enable(OnlyOld, Change.OldProviderId);
        Enable(OnlyOld, Other);
        Enable(Both, Change.OldProviderId);
        Enable(Both, Change.NewProviderId);
        Enable(OnlyNew, Change.NewProviderId);
        Enable(Neither, Other);

        List<Guid> users = await ToCarry();
        Assert.Equal(new[] { OnlyOld, Both }.OrderBy(u => u), users.OrderBy(u => u));
        await SourceMoveService.CarryUsersAsync(Db(), Change, users, CancellationToken.None);

        // The old-id row replaced, never a second new-id row, nobody else touched.
        Assert.Equal([Change.NewProviderId, Other], Enabled(OnlyOld));
        Assert.Equal([Change.NewProviderId], Enabled(Both));
        Assert.Equal([Change.NewProviderId], Enabled(OnlyNew));
        Assert.Equal([Other], Enabled(Neither));
        Assert.Empty(await ToCarry());

        // Switched off afterwards: the next pass leaves it off.
        Disable(OnlyOld, Change.NewProviderId);
        Assert.Empty(await ToCarry());
        Assert.Equal([Other], Enabled(OnlyOld));
    }
}
