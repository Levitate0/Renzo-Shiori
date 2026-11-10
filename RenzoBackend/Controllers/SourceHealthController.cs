using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Auth;
using RenzoBackend.Services.Downloads;

namespace RenzoBackend.Controllers
{
    /// <summary>How one source is doing across the caller's series (Sources › Health).</summary>
    public sealed class SourceHealthDto
    {
        [JsonPropertyName("provider")] public string Provider { get; set; } = "";
        [JsonPropertyName("seriesCount")] public int SeriesCount { get; set; }
        /// <summary>Series where this source is switched on.</summary>
        [JsonPropertyName("enabledCount")] public int EnabledCount { get; set; }
        /// <summary>Of those, the ones it is stepped down on right now (SourceHealthService).</summary>
        [JsonPropertyName("steppedDownCount")] public int SteppedDownCount { get; set; }
        [JsonPropertyName("reasons")] public List<string> Reasons { get; set; } = [];
        /// <summary>Reads and downloads that failed on it in the last day (since the server started).</summary>
        [JsonPropertyName("failuresLastDay")] public int FailuresLastDay { get; set; }
        /// <summary>Its last served chapter or completed download; null when none is known.</summary>
        [JsonPropertyName("lastSuccessUtc")] public DateTime? LastSuccessUtc { get; set; }
        [JsonPropertyName("speed")] public SourceSpeedDto Speed { get; set; } = new();
        /// <summary>"dead" (failing everywhere for a day), "failing", "ok", or "idle" (nothing known).</summary>
        [JsonPropertyName("status")] public string Status { get; set; } = "idle";
    }

    /// <summary>
    /// Sources › Health on the web (components/comp/sources/health-tab.tsx) and the
    /// Hub (ui/sources/HealthTab.kt), and the dead-source alert in the top bar.
    /// Only the caller's own series.
    /// </summary>
    [ApiController]
    [Route("api/sources/health")]
    [RequireUserLevel(UserLevel.Manager)]
    public class SourceHealthController : ControllerBase
    {
        /// <summary>Failures in a day, with nothing served for a day, before a source counts as dead.</summary>
        internal const int DeadAfterFailures = 3;
        private static readonly TimeSpan DeadAfter = TimeSpan.FromDays(1);

        private readonly AppDbContext _db;
        private readonly SourceHealthService _health;
        private readonly SourceSpeedStats _speed;

        public SourceHealthController(AppDbContext db, SourceHealthService health, SourceSpeedStats speed)
        {
            _db = db;
            _health = health;
            _speed = speed;
        }

        private Guid CurrentUserId => (HttpContext.Items["User"] as UserEntity)?.Id ?? Guid.Empty;

        /// <summary>Every source the caller's series use, worst first.</summary>
        [HttpGet]
        public async Task<ActionResult<List<SourceHealthDto>>> GetAsync(CancellationToken token) =>
            Ok(await BuildAsync(token).ConfigureAwait(false));

        /// <summary>Only the dead ones: the top bar's alert.</summary>
        [HttpGet("alerts")]
        public async Task<ActionResult<List<SourceHealthDto>>> AlertsAsync(CancellationToken token) =>
            Ok((await BuildAsync(token).ConfigureAwait(false)).Where(s => s.Status == "dead").ToList());

        private async Task<List<SourceHealthDto>> BuildAsync(CancellationToken token)
        {
            Guid userId = CurrentUserId;
            var rows = await _db.SeriesProviders.AsNoTracking()
                .Where(p => !p.IsUnknown && !p.IsLocal && _db.Series.Any(s => s.Id == p.SeriesId && s.OwnerId == userId))
                .Select(p => new { p.Id, p.SeriesId, p.Provider, p.MihonProviderId, p.IsDisabled })
                .ToListAsync(token).ConfigureAwait(false);
            DateTime now = DateTime.UtcNow;
            var list = rows.Where(r => !string.IsNullOrEmpty(r.Provider)).GroupBy(r => r.Provider!).Select(g =>
            {
                var enabled = g.Where(r => !r.IsDisabled).ToList();
                var down = enabled.Where(r => _health.IsSteppedDown(r.Id, r.MihonProviderId)).ToList();
                DateTime? served = _health.LastSuccess(g.Key);
                DateTime? delivered = _speed.LastDelivered(g.Key);
                DateTime? last = served == null ? delivered : delivered == null ? served : (served > delivered ? served : delivered);
                var dto = new SourceHealthDto
                {
                    Provider = g.Key,
                    SeriesCount = g.Select(r => r.SeriesId).Distinct().Count(),
                    EnabledCount = enabled.Select(r => r.SeriesId).Distinct().Count(),
                    SteppedDownCount = down.Select(r => r.SeriesId).Distinct().Count(),
                    Reasons = down.Select(r => _health.StepDownReason(r.Id, r.MihonProviderId)).OfType<string>().Distinct().ToList(),
                    FailuresLastDay = _health.FailuresLastDay(g.Key),
                    LastSuccessUtc = last,
                    Speed = _speed.For(g.Key),
                };
                bool quiet = last == null || now - last.Value > DeadAfter;
                dto.Status =
                    dto.EnabledCount > 0 && dto.FailuresLastDay >= DeadAfterFailures && quiet ? "dead"
                    : dto.SteppedDownCount > 0 || dto.FailuresLastDay >= DeadAfterFailures
                        || (dto.Speed.Attempts >= 5 && dto.Speed.SuccessRate < 0.5) ? "failing"
                    : last != null || dto.Speed.Attempts > 0 ? "ok"
                    : "idle";
                return dto;
            }).ToList();
            static int Rank(string s) => s switch { "dead" => 0, "failing" => 1, "ok" => 2, _ => 3 };
            return list.OrderBy(s => Rank(s.Status)).ThenByDescending(s => s.EnabledCount).ThenBy(s => s.Provider, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
