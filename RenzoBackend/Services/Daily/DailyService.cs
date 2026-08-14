using RenzoBackend.Data;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Jobs.Models;
using RenzoBackend.Services.Settings;
using Microsoft.EntityFrameworkCore;

namespace RenzoBackend.Services.Daily
{
    public class DailyService
    {
        private readonly AppDbContext _db;
        private readonly ILogger _logger;
        private readonly IConfiguration _configuration;

        public DailyService(AppDbContext db, ILogger<DailyService> logger, IConfiguration configuration)
        {
            _db = db;
            _logger = logger;
            _configuration = configuration;
  
        }

        public async Task<JobResult> ExecuteAsync(JobInfo _, CancellationToken token = default)
        {
            _logger.LogInformation("Starting daily maintenance tasks...");
            await CreateBackupAsync(token).ConfigureAwait(false);
            await CleanupOldCompletedEnqueueAsync(token).ConfigureAwait(false);
            _logger.LogInformation("Daily maintenance tasks completed.");
            return JobResult.Success;

        }

        public async Task CreateBackupAsync(CancellationToken token = default)
        {
            string backupDirectory = Path.Combine(_configuration["runtimeDirectory"]!, "Backups");
            if (!Directory.Exists(backupDirectory))
            {
                try
                {
                    Directory.CreateDirectory(backupDirectory);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to create backup directory at {BackupDirectory}", backupDirectory);
                    return;
                }
            }
            // Weekly, not daily. Each backup is a full VACUUM copy of the whole
            // database — this one grew 2.1GB -> 4.3GB in three weeks — so a daily
            // cadence multiplied by the old 31-file retention trended toward ~130GB
            // of backups for a library that is itself a fraction of that.
            //
            // The gate lives here rather than on the job because the daily run also
            // does queue cleanup, which should keep running every day.
            var existing = Directory.GetFiles(backupDirectory, "backup-*.db")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            FileInfo? newest = existing.FirstOrDefault();
            if (newest != null && DateTime.UtcNow - newest.LastWriteTimeUtc < TimeSpan.FromDays(7))
            {
                _logger.LogInformation(
                    "Skipping backup: the most recent one ({Name}) is {Age:F1} day(s) old; backups run weekly.",
                    newest.Name, (DateTime.UtcNow - newest.LastWriteTimeUtc).TotalDays);
                return;
            }

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var backupPath = Path.Combine(backupDirectory, $"backup-{timestamp}.db");
            string sqlCommand = $"VACUUM INTO '{backupPath.Replace("'", "''")}'";
            await _db.Database.ExecuteSqlRawAsync(sqlCommand, token).ConfigureAwait(false);
            _logger.LogInformation("SQLite backup created at {backupPath}", backupPath);

            // Keep only the newest backup — the previous week's is purged once this
            // week's exists.
            //
            // Deliberately AFTER the VACUUM above has completed: purging first would
            // leave a window with no backup at all, and if the new one then failed
            // there would be nothing to fall back on. The write is verified below
            // before anything is deleted, for the same reason.
            try
            {
                var written = new FileInfo(backupPath);
                if (!written.Exists || written.Length == 0)
                {
                    _logger.LogWarning(
                        "Backup at {Path} is missing or empty — keeping older backups rather than purging.", backupPath);
                    return;
                }

                var backupFiles = Directory.GetFiles(backupDirectory, "backup-*.db")
                    .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                    .ToList();

                if (backupFiles.Count > 1)
                {
                    var toDelete = backupFiles.Skip(1);
                    foreach (var file in toDelete)
                    {
                        try
                        {
                            File.Delete(file);
                            _logger.LogInformation("Deleted old backup file: {BackupFile}", file);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete old backup file: {BackupFile}", file);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old backup files in {BackupDirectory}", backupDirectory);
            }
        }
        public async Task<int> CleanupOldCompletedEnqueueAsync(CancellationToken token = default)
        {
            // Step 1: Get all completed items ordered by FinishedDate descending
            var completedItems = await _db.Queues
                .Where(q => q.Status == QueueStatus.Completed && q.FinishedDate != null)
                .OrderByDescending(q => q.FinishedDate)
                .ToListAsync(token)
                .ConfigureAwait(false);

            // Step 2: If there are <= 1000, do nothing
            if (completedItems.Count <= 1000)
                return 0;

            // Step 3: Find the cutoff date (6 months ago)
            var sixMonthsAgo = DateTime.UtcNow.AddMonths(-6);

            // Step 4: Find the 1000th most recent completed item
            var minKeepDate = completedItems.Count > 1000
                ? completedItems[999].FinishedDate!.Value
                : sixMonthsAgo;

            // Step 5: The cutoff is the later of sixMonthsAgo or the 1000th item's FinishedDate
            var cutoffDate = minKeepDate > sixMonthsAgo ? minKeepDate : sixMonthsAgo;

            // Step 6: Find items to delete (older than cutoff)
            var toDelete = completedItems
                .Where(q => q.FinishedDate < cutoffDate)
                .ToList();

            if (toDelete.Count == 0)
                return 0;

            _db.Queues.RemoveRange(toDelete);
            await _db.SaveChangesAsync(token).ConfigureAwait(false);
            _logger.LogInformation("Deleted {Count} old completed Enqueue items (cutoff: {Cutoff})", toDelete.Count, cutoffDate);
            return toDelete.Count;
        }
    }
}
