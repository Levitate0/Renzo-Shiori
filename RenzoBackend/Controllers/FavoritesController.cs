using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace RenzoBackend.Controllers;

public class FavoriteListDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("parentId")]
    public Guid? ParentId { get; set; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }

    [JsonPropertyName("seriesIds")]
    public List<Guid> SeriesIds { get; set; } = [];
}

public class CreateFavoriteListDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("parentId")]
    public Guid? ParentId { get; set; }
}

public class RenameFavoriteListDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class ReorderFavoriteListsDto
{
    /// <summary>Whose sub-lists are being ordered; null for the top-level tabs.</summary>
    [JsonPropertyName("parentId")]
    public Guid? ParentId { get; set; }

    /// <summary>Every sibling list's id, in the new order.</summary>
    [JsonPropertyName("ids")]
    public List<Guid> Ids { get; set; } = [];
}

public class FavoriteItemRequestDto
{
    [JsonPropertyName("seriesId")]
    public Guid SeriesId { get; set; }
}

/// <summary>
/// Per-user favorites: named top-level tabs ("Manhwa favourites",
/// "Manga favourites #2", …) with optional one-level sub-lists, each holding
/// library series. Lists belong to the authenticated user (or, when auth is
/// disabled with no profile selected, a shared bucket).
/// </summary>
[ApiController]
[Route("api/favorites")]
[Produces("application/json")]
public class FavoritesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger _logger;

    public FavoritesController(AppDbContext db, ILogger<FavoritesController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private Guid CurrentUserId => (HttpContext.Items["User"] as UserEntity)?.Id ?? Guid.Empty;

    /// <summary>All of the current user's lists, each with its member series ids.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<FavoriteListDto>), 200)]
    public async Task<ActionResult<List<FavoriteListDto>>> GetAllAsync(CancellationToken token = default)
    {
        Guid userId = CurrentUserId;
        List<FavoriteListEntity> lists = await _db.FavoriteLists
            .Where(l => l.UserId == userId)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.CreatedAt)
            .ToListAsync(token).ConfigureAwait(false);
        if (lists.Count == 0)
            return Ok(new List<FavoriteListDto>());

        List<Guid> listIds = lists.Select(l => l.Id).ToList();
        // Only ids of series that still exist — items for deleted series are
        // just skipped (harmless orphans) rather than eagerly pruned.
        var items = await _db.FavoriteItems
            .Where(i => listIds.Contains(i.ListId))
            .Join(_db.Series, i => i.SeriesId, s => s.Id, (i, s) => new { i.ListId, i.SeriesId })
            .ToListAsync(token).ConfigureAwait(false);

        var byList = items.GroupBy(i => i.ListId)
            .ToDictionary(g => g.Key, g => g.Select(i => i.SeriesId).ToList());

        return Ok(lists.Select(l => new FavoriteListDto
        {
            Id = l.Id,
            Name = l.Name,
            ParentId = l.ParentId,
            SortOrder = l.SortOrder,
            SeriesIds = byList.TryGetValue(l.Id, out var ids) ? ids : []
        }).ToList());
    }

    /// <summary>Creates a top-level tab, or a sub-list when parentId is set.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(FavoriteListDto), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<FavoriteListDto>> CreateAsync([FromBody] CreateFavoriteListDto dto, CancellationToken token = default)
    {
        string name = dto.Name?.Trim() ?? "";
        if (name.Length == 0)
            return BadRequest(new { error = "Name is required" });
        Guid userId = CurrentUserId;

        if (dto.ParentId != null)
        {
            FavoriteListEntity? parent = await _db.FavoriteLists
                .FirstOrDefaultAsync(l => l.Id == dto.ParentId && l.UserId == userId, token).ConfigureAwait(false);
            if (parent == null)
                return BadRequest(new { error = "Parent collection not found" });
            // One nesting level: sub-lists can't have children of their own.
            if (parent.ParentId != null)
                return BadRequest(new { error = "Sub-collections cannot contain further sub-collections" });
        }

        bool duplicate = await _db.FavoriteLists.AnyAsync(
            l => l.UserId == userId && l.ParentId == dto.ParentId && l.Name.ToLower() == name.ToLower(),
            token).ConfigureAwait(false);
        if (duplicate)
            return BadRequest(new { error = "A collection with that name already exists here" });

        int sortOrder = await _db.FavoriteLists
            .Where(l => l.UserId == userId && l.ParentId == dto.ParentId)
            .Select(l => (int?)l.SortOrder).MaxAsync(token).ConfigureAwait(false) + 1 ?? 0;

        var list = new FavoriteListEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ParentId = dto.ParentId,
            Name = name,
            SortOrder = sortOrder,
            CreatedAt = DateTime.UtcNow
        };
        _db.FavoriteLists.Add(list);
        await _db.SaveChangesAsync(token).ConfigureAwait(false);

        return Ok(new FavoriteListDto { Id = list.Id, Name = list.Name, ParentId = list.ParentId, SortOrder = list.SortOrder });
    }

    /// <summary>Renames a list.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> RenameAsync(Guid id, [FromBody] RenameFavoriteListDto dto, CancellationToken token = default)
    {
        string name = dto.Name?.Trim() ?? "";
        if (name.Length == 0)
            return BadRequest(new { error = "Name is required" });
        Guid userId = CurrentUserId;

        FavoriteListEntity? list = await _db.FavoriteLists
            .FirstOrDefaultAsync(l => l.Id == id && l.UserId == userId, token).ConfigureAwait(false);
        if (list == null)
            return NotFound();

        list.Name = name;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        return Ok(new { success = true });
    }

    /// <summary>
    /// Puts one set of siblings in a new order: the top-level tabs (parentId
    /// null), or one tab's sub-lists. Each list's SortOrder becomes its index
    /// in <c>ids</c>.
    /// </summary>
    /// <remarks>
    /// <c>ids</c> must be exactly that sibling set, no more and no less. A client
    /// whose copy is stale (a list created or deleted on another device since it
    /// last fetched) would otherwise write an order for only part of the set,
    /// and the lists it never saw would keep SortOrders that now collide with
    /// the new ones and sort in among them by accident. Any mismatch is
    /// therefore a 409 and nothing is written: the client refetches and moves
    /// again from what is really there. The same check refuses another user's
    /// ids, which can never be in this user's sibling set.
    /// The literal "order" can't reach RenameAsync: that route only matches a guid.
    /// </remarks>
    [HttpPut("order")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    public async Task<ActionResult> ReorderAsync([FromBody] ReorderFavoriteListsDto dto, CancellationToken token = default)
    {
        List<Guid> ids = dto.Ids ?? [];
        if (ids.Count == 0)
            return BadRequest(new { error = "At least one collection id is required" });
        if (ids.Distinct().Count() != ids.Count)
            return BadRequest(new { error = "Each collection id may appear only once" });
        Guid userId = CurrentUserId;

        if (dto.ParentId != null)
        {
            bool parentExists = await _db.FavoriteLists
                .AnyAsync(l => l.Id == dto.ParentId && l.UserId == userId, token).ConfigureAwait(false);
            if (!parentExists)
                return BadRequest(new { error = "Parent collection not found" });
        }

        Dictionary<Guid, FavoriteListEntity> siblings = await _db.FavoriteLists
            .Where(l => l.UserId == userId && l.ParentId == dto.ParentId)
            .ToDictionaryAsync(l => l.Id, token).ConfigureAwait(false);
        // ids has no duplicates, so equal counts plus every id being a sibling
        // means the two sets are the same.
        if (ids.Count != siblings.Count || !ids.All(siblings.ContainsKey))
            return Conflict(new { error = "The collections changed elsewhere — refresh and try again" });

        for (int i = 0; i < ids.Count; i++)
            siblings[ids[i]].SortOrder = i;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        return Ok(new { success = true });
    }

    /// <summary>Deletes a list, its sub-lists, and all their memberships.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> DeleteAsync(Guid id, CancellationToken token = default)
    {
        Guid userId = CurrentUserId;
        FavoriteListEntity? list = await _db.FavoriteLists
            .FirstOrDefaultAsync(l => l.Id == id && l.UserId == userId, token).ConfigureAwait(false);
        if (list == null)
            return NotFound();

        List<Guid> ids = [list.Id];
        List<FavoriteListEntity> children = await _db.FavoriteLists
            .Where(l => l.ParentId == id && l.UserId == userId)
            .ToListAsync(token).ConfigureAwait(false);
        ids.AddRange(children.Select(c => c.Id));

        _db.FavoriteItems.RemoveRange(_db.FavoriteItems.Where(i => ids.Contains(i.ListId)));
        _db.FavoriteLists.RemoveRange(children);
        _db.FavoriteLists.Remove(list);
        await _db.SaveChangesAsync(token).ConfigureAwait(false);

        _logger.LogInformation("Deleted favorites list '{Name}' ({Children} sub-lists)", list.Name, children.Count);
        return Ok(new { success = true });
    }

    /// <summary>Adds a series to a list (idempotent).</summary>
    [HttpPost("{id:guid}/items")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> AddItemAsync(Guid id, [FromBody] FavoriteItemRequestDto dto, CancellationToken token = default)
    {
        Guid userId = CurrentUserId;
        bool listExists = await _db.FavoriteLists.AnyAsync(l => l.Id == id && l.UserId == userId, token).ConfigureAwait(false);
        if (!listExists)
            return NotFound();
        bool seriesExists = await _db.Series.AnyAsync(s => s.Id == dto.SeriesId, token).ConfigureAwait(false);
        if (!seriesExists)
            return BadRequest(new { error = "Series not found" });

        bool already = await _db.FavoriteItems
            .AnyAsync(i => i.ListId == id && i.SeriesId == dto.SeriesId, token).ConfigureAwait(false);
        if (!already)
        {
            _db.FavoriteItems.Add(new FavoriteItemEntity
            {
                Id = Guid.NewGuid(),
                ListId = id,
                SeriesId = dto.SeriesId,
                AddedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(token).ConfigureAwait(false);
        }
        return Ok(new { success = true });
    }

    /// <summary>Removes a series from a list.</summary>
    [HttpDelete("{id:guid}/items/{seriesId:guid}")]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> RemoveItemAsync(Guid id, Guid seriesId, CancellationToken token = default)
    {
        Guid userId = CurrentUserId;
        bool listExists = await _db.FavoriteLists.AnyAsync(l => l.Id == id && l.UserId == userId, token).ConfigureAwait(false);
        if (!listExists)
            return NotFound();

        FavoriteItemEntity? item = await _db.FavoriteItems
            .FirstOrDefaultAsync(i => i.ListId == id && i.SeriesId == seriesId, token).ConfigureAwait(false);
        if (item != null)
        {
            _db.FavoriteItems.Remove(item);
            await _db.SaveChangesAsync(token).ConfigureAwait(false);
        }
        return Ok(new { success = true });
    }
}
