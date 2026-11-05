using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RenzoBackend.Controllers;
using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// PUT /api/favorites/order (2026-11-04): collections reorder one sibling set at
/// a time, and only when the client names the whole set, so a stale copy can't
/// write half an order.
/// </summary>
public class FavoritesOrderTests : IDisposable
{
    private static readonly Guid Me = Guid.NewGuid(), Them = Guid.NewGuid();
    private const string Changed = "The collections changed elsewhere — refresh and try again";

    // A real SQLite schema (in memory, kept alive by the open connection) rather
    // than EF's in-memory provider, so the null ParentId comparison is the SQL
    // the live server runs. Each call gets a fresh context, like a request does.
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly List<AppDbContext> _contexts = [];
    private DateTime _clock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public FavoritesOrderTests()
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

    private FavoritesController As(Guid user) => new(Db(), NullLogger<FavoritesController>.Instance)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Items = { ["User"] = new UserEntity { Id = user } } }
        }
    };

    /// <summary>Lists created in call order, a minute apart, so CreatedAt never ties.</summary>
    private Guid List(Guid user, int sortOrder, Guid? parent = null)
    {
        var db = Db();
        var l = new FavoriteListEntity
        {
            Id = Guid.NewGuid(), UserId = user, ParentId = parent, Name = $"List {sortOrder}",
            SortOrder = sortOrder, CreatedAt = _clock = _clock.AddMinutes(1)
        };
        db.FavoriteLists.Add(l);
        db.SaveChanges();
        return l.Id;
    }

    private Task<ActionResult> Reorder(Guid user, Guid? parent, params Guid[] ids) =>
        As(user).ReorderAsync(new ReorderFavoriteListsDto { ParentId = parent, Ids = [.. ids] });

    /// <summary>What GET hands a client: one sibling set, in display order.</summary>
    private async Task<List<Guid>> Shown(Guid user, Guid? parent)
    {
        var all = Assert.IsType<List<FavoriteListDto>>(Assert.IsType<OkObjectResult>((await As(user).GetAllAsync()).Result).Value);
        return all.Where(l => l.ParentId == parent).Select(l => l.Id).ToList();
    }

    private Dictionary<Guid, int> SortOrders() => Db().FavoriteLists.ToDictionary(l => l.Id, l => l.SortOrder);

    private static (int? Status, string? Error) Outcome(ActionResult r)
    {
        var o = Assert.IsAssignableFrom<ObjectResult>(r);
        return (o.StatusCode, o.Value?.GetType().GetProperty("error")?.GetValue(o.Value) as string);
    }

    [Fact]
    public async Task Top_level_collections_take_the_order_they_are_sent_in()
    {
        Guid a = List(Me, 0), b = List(Me, 1), c = List(Me, 2);
        Guid sub = List(Me, 0, parent: a);

        Assert.Equal((200, null), Outcome(await Reorder(Me, null, c, a, b)));

        Assert.Equal([c, a, b], await Shown(Me, null));
        var orders = SortOrders();
        Assert.Equal((0, 1, 2), (orders[c], orders[a], orders[b]));
        Assert.Equal([sub], await Shown(Me, a));
    }

    [Fact]
    public async Task Sub_collections_reorder_under_their_parent_and_the_top_level_stays_put()
    {
        Guid a = List(Me, 0), b = List(Me, 1);
        Guid a1 = List(Me, 0, a), a2 = List(Me, 1, a), a3 = List(Me, 2, a);
        Guid b1 = List(Me, 0, b), b2 = List(Me, 1, b);
        var before = SortOrders();

        Assert.Equal((200, null), Outcome(await Reorder(Me, a, a3, a1, a2)));

        Assert.Equal([a3, a1, a2], await Shown(Me, a));
        Assert.Equal([a, b], await Shown(Me, null));
        Assert.Equal([b1, b2], await Shown(Me, b));
        var after = SortOrders();
        Assert.All(new[] { a, b, b1, b2 }, id => Assert.Equal(before[id], after[id]));
    }

    [Fact]
    public async Task A_stale_or_partial_set_is_refused_and_nothing_moves()
    {
        Guid a = List(Me, 0), b = List(Me, 1), c = List(Me, 2);
        Guid sub = List(Me, 0, a);
        var before = SortOrders();

        // Missing one created elsewhere; carrying one deleted elsewhere; a
        // sub-collection mixed in with the top level; the right ids for the
        // wrong parent.
        Assert.Equal((409, Changed), Outcome(await Reorder(Me, null, b, a)));
        Assert.Equal((409, Changed), Outcome(await Reorder(Me, null, c, b, a, Guid.NewGuid())));
        Assert.Equal((409, Changed), Outcome(await Reorder(Me, null, c, b, Guid.NewGuid())));
        Assert.Equal((409, Changed), Outcome(await Reorder(Me, null, c, b, a, sub)));
        Assert.Equal((409, Changed), Outcome(await Reorder(Me, a, c, b, a)));

        Assert.Equal(before, SortOrders());
    }

    [Fact]
    public async Task Duplicate_ids_are_a_bad_request()
    {
        Guid a = List(Me, 0), b = List(Me, 1);
        var before = SortOrders();

        var (status, error) = Outcome(await Reorder(Me, null, b, a, b));
        Assert.Equal(400, status);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Equal(before, SortOrders());
    }

    [Fact]
    public async Task No_ids_is_a_bad_request()
    {
        List(Me, 0);

        var (status, error) = Outcome(await Reorder(Me, null));
        Assert.Equal(400, status);
        Assert.False(string.IsNullOrEmpty(error));
        // { "ids": null } deserializes to null, not an empty list: still a 400, not a crash.
        Assert.Equal(400, Outcome(await As(Me).ReorderAsync(new ReorderFavoriteListsDto { Ids = null! })).Status);
    }

    [Fact]
    public async Task Another_users_collections_are_refused()
    {
        Guid mine = List(Me, 0);
        Guid x = List(Them, 0), y = List(Them, 1);
        Guid x1 = List(Them, 0, x), x2 = List(Them, 1, x);
        var before = SortOrders();

        // Their whole top level, mine plus theirs, their sub-collections under their tab.
        Assert.Equal((409, Changed), Outcome(await Reorder(Me, null, y, x)));
        Assert.Equal((409, Changed), Outcome(await Reorder(Me, null, mine, y, x)));
        Assert.Equal((400, "Parent collection not found"), Outcome(await Reorder(Me, x, x2, x1)));

        Assert.Equal(before, SortOrders());
        Assert.Equal([x, y], await Shown(Them, null));
    }

    [Fact]
    public async Task An_unknown_parent_is_a_bad_request()
    {
        Guid a = List(Me, 0);

        Assert.Equal((400, "Parent collection not found"), Outcome(await Reorder(Me, Guid.NewGuid(), a)));
    }

    [Fact]
    public async Task Get_shows_the_new_order_after_moving_up_and_back_down()
    {
        Guid a = List(Me, 0), b = List(Me, 1), c = List(Me, 2);
        Assert.Equal([a, b, c], await Shown(Me, null));

        // What a client's Move up on c sends, then Move down on c.
        Assert.Equal(200, Outcome(await Reorder(Me, null, a, c, b)).Status);
        Assert.Equal([a, c, b], await Shown(Me, null));
        Assert.Equal(200, Outcome(await Reorder(Me, null, a, b, c)).Status);
        Assert.Equal([a, b, c], await Shown(Me, null));
    }
}
