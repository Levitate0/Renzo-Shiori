using Microsoft.AspNetCore.Mvc;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Auth;
using RenzoBackend.Services.Images;
using RenzoBackend.Services.Series;

namespace RenzoBackend.Controllers
{
    /// <summary>
    /// Source migration (SourceMigrationService): move the caller's series off a
    /// broken source onto others. Sources page › Migrate on the web
    /// (components/comp/sources/migrate-tab.tsx) and the Hub (ui/sources/MigrateTab.kt).
    /// Only ever the caller's own series.
    /// </summary>
    [ApiController]
    [Route("api/migrate")]
    [RequireUserLevel(UserLevel.Manager)]
    public class MigrateController : ControllerBase
    {
        private readonly SourceMigrationService _migration;
        private readonly ThumbCacheService _thumbs;

        public MigrateController(SourceMigrationService migration, ThumbCacheService thumbs)
        {
            _migration = migration;
            _thumbs = thumbs;
        }

        /// <summary>Covers through the server's image cache (/api/image/…), as everywhere else, not the source's own link.</summary>
        private async Task<MigrationRunDto> WithThumbsAsync(MigrationRunDto run, CancellationToken token)
        {
            await _thumbs.PopulateThumbsAsync(run.Items, "/api/image/", token).ConfigureAwait(false);
            return run;
        }

        private Guid CurrentUserId => (HttpContext.Items["User"] as UserEntity)?.Id ?? Guid.Empty;

        /// <summary>The sources the caller's series use, and the ones they can move to, with recent download speeds.</summary>
        [HttpGet("sources")]
        public async Task<ActionResult<MigrationSourcesDto>> SourcesAsync(CancellationToken token = default) =>
            Ok(await _migration.SourcesAsync(CurrentUserId, token).ConfigureAwait(false));

        /// <summary>The caller's latest migration, if any (204 when none).</summary>
        [HttpGet("current")]
        public async Task<ActionResult<MigrationRunDto>> CurrentAsync(CancellationToken token = default)
        {
            MigrationRunDto? run = _migration.Current(CurrentUserId);
            return run == null ? NoContent() : Ok(await WithThumbsAsync(run, token).ConfigureAwait(false));
        }

        /// <summary>Starts looking for matches; poll GET /api/migrate/{id}.</summary>
        [HttpPost]
        public async Task<ActionResult<MigrationRunDto>> StartAsync([FromBody] MigrationStartDto request, CancellationToken token = default)
        {
            var (run, error) = await _migration.StartAsync(CurrentUserId, request, token).ConfigureAwait(false);
            return run == null ? Conflict(new { error }) : Ok(await WithThumbsAsync(run, token).ConfigureAwait(false));
        }

        [HttpGet("{id:guid}")]
        public async Task<ActionResult<MigrationRunDto>> GetAsync(Guid id, CancellationToken token = default)
        {
            MigrationRunDto? run = _migration.Get(CurrentUserId, id);
            return run == null ? NotFound() : Ok(await WithThumbsAsync(run, token).ConfigureAwait(false));
        }

        /// <summary>Adds the chosen matches; poll GET /api/migrate/{id}.</summary>
        [HttpPost("{id:guid}/apply")]
        public async Task<ActionResult<MigrationRunDto>> ApplyAsync(Guid id, [FromBody] MigrationApplyDto request, CancellationToken token = default)
        {
            var (run, error) = _migration.Apply(CurrentUserId, id, request);
            return run == null ? Conflict(new { error }) : Ok(await WithThumbsAsync(run, token).ConfigureAwait(false));
        }

        /// <summary>Forgets a finished migration.</summary>
        [HttpDelete("{id:guid}")]
        public IActionResult Discard(Guid id) => _migration.Discard(CurrentUserId, id) ? NoContent() : NotFound();
    }
}
