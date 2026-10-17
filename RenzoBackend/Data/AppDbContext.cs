using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Mihon.ExtensionsBridge.Models.Extensions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chapter = RenzoBackend.Models.Chapter;

namespace RenzoBackend.Data
{
    //Only used to do migrations, repoint to your test database if you're dev.
    public class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite("Data Source=C:\\users\\mpiva\\appdata\\local\\renzo\\renzo.db")
                .Options;
            return new AppDbContext(options);
        }
    }
    public static class GenericValueComparer
    {
        public static ValueComparer<T> Create<T>()
        {
            return typeof(T).IsGenericType &&
                   typeof(T).GetGenericTypeDefinition() == typeof(List<>)
                ? CreateListComparer<T>()
                : new ValueComparer<T>(
                    (a, b) => EqualityComparer<T>.Default.Equals(a, b),
                    a => a == null ? 0 : EqualityComparer<T>.Default.GetHashCode(a),
                    a => a
                );
        }

        private static ValueComparer<T> CreateListComparer<T>()
        {
            var elementType = typeof(T).GetGenericArguments()[0];

            return new ValueComparer<T>(
                (a, b) => SequenceEqual(a, b),
                a => GetSequenceHashCode(a),
                a => CloneList(a)
            );
        }

        private static bool SequenceEqual<T>(T? a, T? b)
        {
            if (a is IEnumerable<object> listA && b is IEnumerable<object> listB)
                return listA.SequenceEqual(listB);
            return EqualityComparer<T>.Default.Equals(a, b);
        }

        private static int GetSequenceHashCode<T>(T? list)
        {
            if (list is IEnumerable<object> sequence)
                return sequence.Aggregate(0, (hash, item) => HashCode.Combine(hash, item?.GetHashCode() ?? 0));
            return list?.GetHashCode() ?? 0;
        }

        private static T CloneList<T>(T? list)
        {
            if (list is IEnumerable<object> source && list is IList<object> original)
            {
                var cloned = Activator.CreateInstance(typeof(T)) as IList<object>;
                foreach (var item in original)
                    cloned?.Add(item);
                return (T)(object)cloned!;
            }

            return list!;
        }
    }

    /// <summary>
    /// Change tracking for a source's chapter list (one JSON column).
    ///
    /// The generic list comparer above never worked for it: its snapshot step
    /// tests `list is IList&lt;object&gt;`, which a List&lt;Chapter&gt; is not, so
    /// the "snapshot" was the live list itself, and equality compared element
    /// references. A chapter edited in place therefore always compared equal
    /// to its own snapshot, and the edit was saved only if the caller also
    /// remembered <c>db.Touch(provider, p =&gt; p.Chapters)</c>. Several did not:
    /// a storage download deleting the other sources' duplicate files cleared
    /// their filenames in memory only, leaving rows that pointed at files that
    /// no longer existed.
    ///
    /// This one snapshots copies and compares values. ADD ANY NEW Chapter
    /// PROPERTY TO <see cref="Same"/> and <see cref="Copy"/>.
    /// </summary>
    public static class ChapterListComparer
    {
        public static ValueComparer<List<Chapter>> Instance { get; } = new(
            (a, b) => ListsEqual(a, b),
            a => a == null ? 0 : a.Count,
            a => CopyList(a));

        private static bool ListsEqual(List<Chapter>? a, List<Chapter>? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!Same(a[i], b[i])) return false;
            return true;
        }

        private static bool Same(Chapter? x, Chapter? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null) return false;
            return x.ChapterNumber == y.ChapterNumber
                && x.Index == y.Index
                && x.Name == y.Name
                && x.ProviderUploadDate == y.ProviderUploadDate
                && x.DateFetched == y.DateFetched
                && x.Url == y.Url
                && x.ProviderIndex == y.ProviderIndex
                && x.DownloadDate == y.DownloadDate
                && x.ShouldDownload == y.ShouldDownload
                && x.IsDeleted == y.IsDeleted
                && x.IsLocked == y.IsLocked
                && x.LockedCheckedAt == y.LockedCheckedAt
                && x.PageCount == y.PageCount
                && x.Filename == y.Filename
                && (ReferenceEquals(x.Pages, y.Pages) || (x.Pages ?? []).SequenceEqual(y.Pages ?? []));
        }

        private static Chapter Copy(Chapter c) => new()
        {
            ChapterNumber = c.ChapterNumber,
            Index = c.Index,
            Name = c.Name,
            ProviderUploadDate = c.ProviderUploadDate,
            DateFetched = c.DateFetched,
            Url = c.Url,
            ProviderIndex = c.ProviderIndex,
            DownloadDate = c.DownloadDate,
            ShouldDownload = c.ShouldDownload,
            IsDeleted = c.IsDeleted,
            IsLocked = c.IsLocked,
            LockedCheckedAt = c.LockedCheckedAt,
            PageCount = c.PageCount,
            Filename = c.Filename,
            Pages = c.Pages == null ? [] : [.. c.Pages],
        };

        private static List<Chapter> CopyList(List<Chapter>? list) =>
            list == null ? null! : list.Select(c => c == null ? null! : Copy(c)).ToList();
    }

    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
         
        }

        public DbSet<SeriesEntity> Series { get; set; }
        public DbSet<SettingEntity> Settings { get; set; }
        public DbSet<SeriesProviderEntity> SeriesProviders { get; set; }
        public DbSet<ProviderStorageEntity> Providers { get; set; }
        public DbSet<ImportEntity> Imports { get; set; }
        public DbSet<EtagCacheEntity> ETagCache { get; set; }
        public DbSet<JobEntity> Jobs { get; set; }
        public DbSet<EnqueueEntity> Queues { get; set; }
        public DbSet<LatestSerieEntity> LatestSeries { get; set; }
        public DbSet<HealthStatusEntity> HealthStatuses { get; set; }
        public DbSet<UserEntity> Users { get; set; }
        public DbSet<UserScrobblerConfigEntity> UserScrobblerConfigs { get; set; }
        public DbSet<UserSeriesMappingEntity> UserSeriesMappings { get; set; }
        public DbSet<SeriesMappingEntity> SeriesMappings { get; set; }
        public DbSet<FavoriteListEntity> FavoriteLists { get; set; }
        public DbSet<FavoriteItemEntity> FavoriteItems { get; set; }
        public DbSet<UserProviderEntity> UserProviders { get; set; }
        public DbSet<UserProviderPreferenceEntity> UserProviderPreferences { get; set; }
        public DbSet<SiteCredentialEntity> SiteCredentials { get; set; }
        public DbSet<RefreshSessionEntity> RefreshSessions { get; set; }
        public DbSet<TvPairingRequestEntity> TvPairingRequests { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<SeriesEntity>(entity =>
            {
                entity.HasKey(s => s.Id);
                entity.Property(s => s.Title).UseCollation("BINARY").IsRequired();
                entity.Property(s => s.StoragePath).UseCollation("BINARY").IsRequired(false);
                entity.Property(s => s.Type).UseCollation("BINARY").IsRequired(false);
                entity.Property(s => s.ThumbnailUrl).UseCollation("BINARY").IsRequired();
                entity.Property(s => s.Artist).UseCollation("BINARY").IsRequired();
                entity.Property(s => s.Author).UseCollation("BINARY").IsRequired();
                entity.Property(s => s.Description).UseCollation("BINARY").IsRequired();
                entity.Property(s => s.Status).IsRequired();
                entity.Property(s => s.ChapterCount).IsRequired();
                entity.Property(s => s.PauseDownloads).IsRequired();
                entity.Property(s => s.LastChapterDate).IsRequired(false);
                entity.Property(s => s.OwnerId).HasDefaultValue(Guid.Empty).IsRequired();
                entity.HasIndex(s => s.OwnerId);
                entity.Property(s => s.Genre)
                    .HasConversion(
                        v => string.Join(',', v),
                        v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                    ).Metadata.SetValueComparer(GenericValueComparer.Create<List<string>>());
                entity.HasMany(s => s.Sources)
                    .WithOne()
                    .HasForeignKey(sp => sp.SeriesId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<SeriesProviderEntity>(entity =>
            {
                entity.HasKey(sp => sp.Id);
                entity.Property(sp => sp.SeriesId);
                entity.Property(e => e.MihonProviderId).UseCollation("BINARY").IsRequired(false);
                entity.Property(e => e.MihonId).UseCollation("BINARY").IsRequired(false);
                entity.Property(e => e.BridgeItemInfo).UseCollation("BINARY").IsRequired(false);
                entity.Property(sp => sp.Provider).UseCollation("BINARY").IsRequired();
                entity.Property(sp => sp.Scanlator).UseCollation("BINARY").IsRequired();
                entity.Property(sp => sp.Url).UseCollation("BINARY").IsRequired(false);
                entity.Property(sp => sp.Title).UseCollation("BINARY").IsRequired();
                entity.Property(sp => sp.Language).UseCollation("BINARY").IsRequired();
                entity.Property(sp => sp.ThumbnailUrl).UseCollation("BINARY").IsRequired(false);
                entity.Property(sp => sp.Artist).UseCollation("BINARY").IsRequired(false);
                entity.Property(sp => sp.Author).UseCollation("BINARY").IsRequired(false);
                entity.Property(sp => sp.Description).UseCollation("BINARY").IsRequired(false);
                entity.Property(sp => sp.Genre)
                    .HasConversion(
                        v => string.Join(',', v),
                        v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                    ).Metadata.SetValueComparer(GenericValueComparer.Create<List<string>>());
 
                entity.Property(sp => sp.FetchDate).IsRequired(false);
                entity.Property(sp => sp.ChapterCount).IsRequired(false);
                entity.Property(sp => sp.ContinueAfterChapter).IsRequired(false);
                entity.Property(sp => sp.IsTitle).IsRequired();
                entity.Property(sp => sp.IsCover).IsRequired();
                entity.Property(sp => sp.IsStatus).HasDefaultValue(false).IsRequired();
                entity.Property(sp => sp.IsUnknown).IsRequired();
                entity.Property(sp => sp.IsNSFW).HasDefaultValue(false).IsRequired();
                entity.Property(sp => sp.IsStorage).IsRequired();
                entity.Property(sp => sp.IsDisabled).IsRequired();
                entity.Property(sp => sp.IsUninstalled).IsRequired();
                entity.Property(sp => sp.Status).IsRequired();
                entity.Property(sp => sp.LastErrorDate).IsRequired(false);
                entity.Property(sp => sp.ConsecutiveErrorCount).HasDefaultValue(0).IsRequired();
                entity.Property(sp => sp.LastSuccessfulFetchDate).IsRequired(false);
                entity.Property(sp => sp.LastSeriesInfoRefreshDate).IsRequired(false);
                entity.Property(sp => sp.LastKnownStatus).IsRequired(false);
                entity.Property(sp => sp.Chapters)
                     .HasConversion(
                         v => JsonSerializer.Serialize(v, new JsonSerializerOptions { WriteIndented = false }),
                         v => JsonSerializer.Deserialize<List<Chapter>>(v, new JsonSerializerOptions { WriteIndented = false }) ?? new List<Chapter>()
                     ).Metadata.SetValueComparer(ChapterListComparer.Instance);
                entity.HasIndex(sp => sp.SeriesId).HasDatabaseName("IX_SeriesProvider_SeriesId");
                entity.HasIndex(sp => sp.MihonId).HasDatabaseName("IX_SeriesProvider_MihonId");
                entity.HasIndex(sp => sp.MihonProviderId).HasDatabaseName("IX_SeriesProvider_MihonProviderId");
                entity.HasIndex(sp => new { sp.Title, sp.Language })
                    .HasDatabaseName("IX_SeriesProvider_Title_Language");
                entity.HasIndex(sp => new { sp.Provider, sp.Language, sp.Scanlator })
                    .HasDatabaseName("IX_SeriesProvider_Provider_Language_Scanlator");
            });

            modelBuilder.Entity<LatestSerieEntity>(entity =>
            {
                entity.HasKey(e => e.MihonId);
                entity.Property(e => e.MihonId).UseCollation("BINARY").IsRequired(true);
                entity.Property(e => e.MihonProviderId).UseCollation("BINARY").IsRequired(false);
                entity.Property(e => e.BridgeItemInfo).UseCollation("BINARY").IsRequired(false);
                entity.Property(e => e.Provider).UseCollation("BINARY").IsRequired();
                entity.Property(e => e.Language).UseCollation("BINARY").IsRequired();
                entity.Property(e => e.Url).UseCollation("BINARY").IsRequired(false);
                entity.Property(e => e.Title).IsRequired();
                entity.Property(e => e.ThumbnailUrl).UseCollation("BINARY").IsRequired(false);
                entity.Property(e => e.Artist).UseCollation("BINARY").IsRequired(false);
                entity.Property(e => e.Author).UseCollation("BINARY").IsRequired(false);
                entity.Property(e => e.Description).UseCollation("BINARY").IsRequired(false);
                entity.Property(e => e.Genre)
                    .HasConversion(
                        v => string.Join(',', v),
                        v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                    ).Metadata.SetValueComparer(GenericValueComparer.Create<List<string>>());
                entity.Property(e => e.Chapters)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, new JsonSerializerOptions { WriteIndented = false }),
                        v => JsonSerializer.Deserialize<List<ParsedChapter>>(v, new JsonSerializerOptions { WriteIndented = false }) ?? new List<ParsedChapter>()
                    ).Metadata.SetValueComparer(GenericValueComparer.Create<List<ParsedChapter>>());
                entity.Property(e => e.FetchDate).IsRequired();
                entity.Property(e => e.ChapterCount).IsRequired(false);
                entity.Property(e => e.LatestChapter).IsRequired(false);
                entity.Property(e => e.LatestChapterTitle).UseCollation("BINARY").IsRequired();
                entity.Property(e => e.Status).IsRequired();
                entity.Property(e => e.InLibrary).IsRequired();
                entity.Property(e => e.SeriesId).IsRequired(false);
                entity.HasIndex(l => l.FetchDate).HasDatabaseName("IX_LatestSerie_FetchDate");
                entity.HasIndex(l => l.Title).HasDatabaseName("IX_LatestSerie_Title");
                entity.HasIndex(l => l.Provider).HasDatabaseName("IX_LatestSerie_Provider");
                entity.HasIndex(l => l.MihonProviderId).HasDatabaseName("IX_LatestSerie_MihonProviderId");
            });

            modelBuilder.Entity<ProviderStorageEntity>(entity =>
            {
                entity.HasKey(e => e.MihonProviderId);
                entity.Property(e => e.MihonProviderId).UseCollation("BINARY").IsRequired(true);
                entity.Property(e => e.SourcePackageName).UseCollation("BINARY").IsRequired(false);
                entity.Property(e => e.Name).UseCollation("BINARY").IsRequired();
                entity.Property(e => e.IsStorage).IsRequired();
            });

            modelBuilder.Entity<ImportEntity>(entity =>
            {
                entity.HasKey(i => i.Path);
                entity.Property(i => i.Path).UseCollation("BINARY").IsRequired();
                entity.Property(i => i.Title).UseCollation("BINARY").IsRequired();
                entity.Property(i => i.Status)
                    .IsRequired()
                    .HasDefaultValue(ImportStatus.Import)
                    .HasConversion<int>();
                entity.Property(i => i.Action)
                    .IsRequired()
                    .HasDefaultValue(Models.Action.Add)
                    .HasConversion<int>();
                entity.Property(i => i.Info)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, new JsonSerializerOptions { WriteIndented = false }),
                        v => JsonSerializer.Deserialize<ImportSeriesSnapshot>(v, new JsonSerializerOptions { WriteIndented = false }) ?? new ImportSeriesSnapshot()
                    ).Metadata.SetValueComparer(GenericValueComparer.Create<ImportSeriesSnapshot>());
                entity.Property(i => i.Series)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, new JsonSerializerOptions { WriteIndented = false }),
                        v => JsonSerializer.Deserialize<List<ProviderSeriesDetails>>(v, new JsonSerializerOptions { WriteIndented = false }) ?? new List<ProviderSeriesDetails>()
                    ).Metadata.SetValueComparer(GenericValueComparer.Create<List<ProviderSeriesDetails>>());
                entity.Property(i => i.IsTitleOnly)
                    .IsRequired()
                    .HasDefaultValue(false);
                entity.HasIndex(i => new { i.Status, i.Action })
                    .HasDatabaseName("IX_Import_Status_Action");
            });

            modelBuilder.Entity<EtagCacheEntity>(entity =>
            {
                entity.HasKey(i => i.Key);
                entity.Property(i => i.Key).UseCollation("BINARY").IsRequired();
                entity.Property(i => i.Etag).UseCollation("BINARY").IsRequired();
                entity.Property(i => i.Url).UseCollation("BINARY").IsRequired();
                entity.Property(i => i.ExternalEtag).UseCollation("BINARY").IsRequired();
                entity.Property(i => i.ContentType).UseCollation("BINARY").IsRequired();
                entity.Property(i => i.Extension).UseCollation("BINARY").IsRequired();
                entity.Property(e => e.NextUpdateUTC).IsRequired();
            });

            modelBuilder.Entity<SettingEntity>(entity =>
            {
                entity.HasKey(s => s.Name);
                entity.Property(s => s.Name).UseCollation("BINARY").IsRequired();
                entity.Property(s => s.Value).UseCollation("BINARY").IsRequired();
            });

            modelBuilder.Entity<JobEntity>(entity =>
            {
                entity.HasKey(j => j.Id);
                entity.Property(j => j.Key).UseCollation("BINARY").IsRequired();
                entity.Property(j => j.GroupKey).UseCollation("BINARY").IsRequired();
                entity.Property(j => j.JobParameters).UseCollation("BINARY").IsRequired();
                entity.Property(j => j.JobType)
                    .IsRequired()
                    .HasConversion<int>();
                entity.Property(j => j.Priority)
                    .IsRequired()
                    .HasDefaultValue(Priority.Low)
                    .HasConversion<int>();
                entity.Property(j => j.TimeBetweenJobs).IsRequired();
                entity.Property(j => j.MinutePlace).IsRequired();
                entity.Property(j => j.NextExecution).IsRequired();
                entity.Property(j => j.PreviousExecution).IsRequired(false);
                entity.HasIndex(j => j.Key);
                entity.HasIndex(j => j.NextExecution);
                entity.HasIndex(j => new { j.JobType, j.GroupKey })
                    .HasDatabaseName("IX_Job_JobType_GroupKey");
                entity.HasIndex(j => j.IsEnabled).HasDatabaseName("IX_Job_IsEnabled");
                entity.HasIndex(j => new { j.JobType, j.Key })
                    .HasDatabaseName("IX_Job_JobType_Key");
            });

            modelBuilder.Entity<EnqueueEntity>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Queue).UseCollation("BINARY").IsRequired();
                entity.Property(e => e.Key).UseCollation("BINARY").IsRequired();
                entity.Property(e => e.GroupKey).UseCollation("BINARY").IsRequired();
                entity.Property(e => e.ExtraKey).UseCollation("BINARY").IsRequired(false);
                entity.Property(e => e.JobParameters).UseCollation("BINARY").IsRequired(false);
                entity.Property(e => e.JobType)
                    .IsRequired()
                    .HasConversion<int>();
                entity.Property(e => e.Status)
                    .IsRequired()
                    .HasDefaultValue(QueueStatus.Waiting)
                    .HasConversion<int>();
                entity.Property(e => e.Priority)
                    .IsRequired()
                    .HasDefaultValue(Priority.Low)
                    .HasConversion<int>();
                entity.Property(e => e.EnqueuedDate).IsRequired();
                entity.Property(e => e.StartedDate).IsRequired(false);
                entity.Property(e => e.ScheduledDate).IsRequired();
                entity.Property(e => e.FinishedDate).IsRequired(false);
                entity.Property(e => e.RetryCount).IsRequired();
                entity.HasIndex(e => e.Queue);
                entity.HasIndex(e => e.Status);
                entity.HasIndex(e => e.Key);
                entity.HasIndex(e => e.ScheduledDate);
                entity.HasIndex(e => new { e.JobType, e.Status })
                    .HasDatabaseName("IX_Enqueue_JobType_Status");
                entity.HasIndex(e => new { e.JobType, e.ExtraKey })
                    .HasDatabaseName("IX_Enqueue_JobType_ExtraKey");
                entity.HasIndex(e => e.GroupKey).HasDatabaseName("IX_Enqueue_GroupKey");
                entity.HasIndex(e => e.FinishedDate).HasDatabaseName("IX_Enqueue_FinishedDate");
            });

            modelBuilder.Entity<HealthStatusEntity>(entity =>
            {
                entity.HasKey(h => h.Id);
                entity.Property(h => h.TargetType).IsRequired().HasConversion<int>();
                entity.Property(h => h.TargetId).IsRequired();
                entity.Property(h => h.Level).IsRequired().HasConversion<int>();
                entity.Property(h => h.Message).IsRequired();
                entity.Property(h => h.AffectedSeriesJson).IsRequired(false);
                entity.Property(h => h.CreatedAt).IsRequired();
                entity.Property(h => h.ResolvedAt).IsRequired(false);
                entity.Property(h => h.IsActive).IsRequired().HasDefaultValue(true);
                entity.HasIndex(h => new { h.TargetType, h.TargetId }).HasDatabaseName("IX_HealthStatus_Target");
                entity.HasIndex(h => h.IsActive).HasDatabaseName("IX_HealthStatus_IsActive");
                entity.HasIndex(h => h.Level).HasDatabaseName("IX_HealthStatus_Level");
            });

            modelBuilder.Entity<UserEntity>(entity =>
            {
                entity.HasKey(u => u.Id);
                entity.Property(u => u.Username).UseCollation("BINARY").IsRequired();
                entity.Property(u => u.AvatarBlob).IsRequired(false);
                entity.Property(u => u.AvatarContentType).IsRequired(false);
                entity.Property(u => u.PasswordHash).IsRequired(false);
                entity.Property(u => u.Salt).IsRequired(false);
                entity.Property(u => u.PasswordSetToken).IsRequired(false);
                entity.Property(u => u.RefreshTokenHash).IsRequired(false);
                entity.Property(u => u.RefreshTokenExpiresAt).IsRequired(false);
                entity.Property(u => u.Level).IsRequired().HasConversion<int>();
                entity.Property(u => u.OpdsPath).UseCollation("BINARY").IsRequired();
                entity.Property(u => u.CreatedAt).IsRequired();
                entity.Property(u => u.LastLoginAt).IsRequired(false);
                entity.Property(u => u.IsActive).IsRequired().HasDefaultValue(true);
                entity.HasIndex(u => u.Username).IsUnique().HasDatabaseName("IX_User_Username");
                entity.HasIndex(u => u.OpdsPath).IsUnique().HasDatabaseName("IX_User_OpdsPath");
            });

            modelBuilder.Entity<UserScrobblerConfigEntity>(entity =>
            {
                entity.HasKey(c => c.Id);
                entity.Property(c => c.UserId).IsRequired();
                entity.Property(c => c.Provider).IsRequired().HasConversion<int>();
                entity.Property(c => c.AccessToken).IsRequired(false);
                entity.Property(c => c.RefreshToken).IsRequired(false);
                entity.Property(c => c.TokenExpiresAt).IsRequired(false);
                entity.Property(c => c.IsEnabled).IsRequired().HasDefaultValue(true);
                entity.Property(c => c.AutoSync).IsRequired().HasDefaultValue(true);
                entity.Property(c => c.LastSyncAt).IsRequired(false);
                entity.Property(c => c.LastUploadAt).IsRequired(false);
                entity.Property(c => c.LastDownloadAt).IsRequired(false);
                entity.HasIndex(c => new { c.UserId, c.Provider }).IsUnique()
                    .HasDatabaseName("IX_UserScrobblerConfig_UserId_Provider");
            });

            modelBuilder.Entity<UserSeriesMappingEntity>(entity =>
            {
                entity.HasKey(m => m.Id);
                entity.Property(m => m.UserId).IsRequired();
                entity.Property(m => m.SeriesId).IsRequired();
                entity.Property(m => m.Provider).IsRequired().HasConversion<int>();
                entity.Property(m => m.ExternalSeriesId).UseCollation("BINARY").IsRequired();
                entity.Property(m => m.ExternalSeriesTitle).UseCollation("BINARY").IsRequired(false);
                entity.Property(m => m.MappingStatus).IsRequired().HasConversion<int>();
                entity.HasIndex(m => new { m.UserId, m.SeriesId, m.Provider }).IsUnique()
                    .HasDatabaseName("IX_UserSeriesMapping_UserId_SeriesId_Provider");
            });

            modelBuilder.Entity<SeriesMappingEntity>(entity =>
            {
                entity.HasKey(m => m.Id);
                entity.Property(m => m.SeriesId).IsRequired();
                entity.Property(m => m.Provider).IsRequired().HasConversion<int>();
                entity.Property(m => m.ExternalSeriesId).UseCollation("BINARY").IsRequired();
                entity.Property(m => m.ExternalSeriesTitle).UseCollation("BINARY").IsRequired(false);
                entity.Property(m => m.UserRole).IsRequired().HasConversion<int>();
                entity.Property(m => m.UpdateDate).IsRequired();
                entity.HasIndex(m => new { m.SeriesId, m.Provider }).IsUnique()
                    .HasDatabaseName("IX_SeriesMapping_SeriesId_Provider");
            });

            modelBuilder.Entity<FavoriteListEntity>(entity =>
            {
                entity.HasKey(f => f.Id);
                entity.Property(f => f.UserId).IsRequired();
                entity.Property(f => f.Name).UseCollation("BINARY").IsRequired();
                entity.HasIndex(f => f.UserId).HasDatabaseName("IX_FavoriteList_UserId");
            });

            modelBuilder.Entity<FavoriteItemEntity>(entity =>
            {
                entity.HasKey(f => f.Id);
                entity.Property(f => f.ListId).IsRequired();
                entity.Property(f => f.SeriesId).IsRequired();
                entity.HasIndex(f => new { f.ListId, f.SeriesId }).IsUnique()
                    .HasDatabaseName("IX_FavoriteItem_ListId_SeriesId");
            });

            // One row per remembered device. Replaces the single refresh-token
            // column on Users, which allowed only one remembered device per
            // account — a second sign-in silently evicted the first.
            modelBuilder.Entity<RefreshSessionEntity>(entity =>
            {
                entity.HasKey(s => s.Id);
                entity.Property(s => s.UserId).IsRequired();
                entity.Property(s => s.TokenHash).UseCollation("BINARY").IsRequired();
                entity.HasIndex(s => s.UserId).HasDatabaseName("IX_RefreshSessions_UserId");
                entity.HasOne(s => s.User)
                    .WithMany()
                    .HasForeignKey(s => s.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<TvPairingRequestEntity>(entity =>
            {
                entity.HasKey(r => r.Id);
                entity.Property(r => r.UserCode).UseCollation("BINARY").IsRequired();
                entity.Property(r => r.DeviceCodeHash).UseCollation("BINARY").IsRequired();
                entity.HasIndex(r => r.UserCode).IsUnique()
                    .HasDatabaseName("IX_TvPairingRequests_UserCode");
            });

            modelBuilder.Entity<SiteCredentialEntity>(entity =>
            {
                entity.HasKey(c => c.Id);
                entity.Property(c => c.UserId).IsRequired();
                entity.Property(c => c.Provider).UseCollation("BINARY").IsRequired();
                entity.Property(c => c.Username).UseCollation("BINARY").IsRequired();
                entity.Property(c => c.Status).UseCollation("BINARY").IsRequired();
                entity.HasIndex(c => new { c.UserId, c.Provider }).IsUnique()
                    .HasDatabaseName("IX_SiteCredential_UserId_Provider");
            });

            modelBuilder.Entity<UserProviderEntity>(entity =>
            {
                entity.HasKey(p => p.Id);
                entity.Property(p => p.UserId).IsRequired();
                entity.Property(p => p.MihonProviderId).UseCollation("BINARY").IsRequired();
                entity.HasIndex(p => new { p.UserId, p.MihonProviderId }).IsUnique()
                    .HasDatabaseName("IX_UserProvider_UserId_MihonProviderId");
            });

            modelBuilder.Entity<UserProviderPreferenceEntity>(entity =>
            {
                entity.HasKey(p => p.Id);
                entity.Property(p => p.UserId).IsRequired();
                entity.Property(p => p.PkgName).UseCollation("BINARY").IsRequired();
                entity.Property(p => p.ValueJson).IsRequired();
                entity.HasIndex(p => new { p.UserId, p.PkgName, p.PreferenceIndex }).IsUnique()
                    .HasDatabaseName("IX_UserProviderPreference_User_Pkg_Index");
            });
        }

        /// <summary>
        /// Change counters for the library-wide read paths (see
        /// <see cref="Services.Series.LibrarySnapshotCache"/>). A save that
        /// touches a series or one of its sources (chapter list included) bumps
        /// that series; a save that touches a setting bumps everything, since
        /// settings shape the derived data (categories come from the folder
        /// settings).
        /// </summary>
        public static long LibraryVersion => Interlocked.Read(ref _libraryVersion);
        private static long _libraryVersion;
        private static long _seriesCounter;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> SeriesVersions = new();

        public static long SeriesVersion(Guid seriesId) => SeriesVersions.TryGetValue(seriesId, out long v) ? v : 0;

        /// <summary>For writes that bypass the change tracker (raw SQL, ExecuteUpdate).</summary>
        public static void BumpLibraryVersion() => Interlocked.Increment(ref _libraryVersion);

        public static void BumpSeries(Guid seriesId) =>
            SeriesVersions[seriesId] = Interlocked.Increment(ref _seriesCounter);

        private (HashSet<Guid> Series, bool Global) PendingLibraryChanges()
        {
            var series = new HashSet<Guid>();
            bool global = false;
            foreach (var e in ChangeTracker.Entries())
            {
                if (e.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                    continue;
                switch (e.Entity)
                {
                    case SeriesEntity s: series.Add(s.Id); break;
                    case SeriesProviderEntity p:
                        series.Add(p.SeriesId);
                        // A source re-parented onto another series changes both.
                        if (e.State == EntityState.Modified && e.OriginalValues[nameof(SeriesProviderEntity.SeriesId)] is Guid was)
                            series.Add(was);
                        break;
                    case SettingEntity: global = true; break;
                }
            }
            return (series, global);
        }

        private static void ApplyLibraryChanges((HashSet<Guid> Series, bool Global) changes)
        {
            foreach (Guid id in changes.Series)
                BumpSeries(id);
            if (changes.Global)
                BumpLibraryVersion();
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            var changes = PendingLibraryChanges();
            int n = base.SaveChanges(acceptAllChangesOnSuccess);
            ApplyLibraryChanges(changes);
            return n;
        }

        public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            var changes = PendingLibraryChanges();
            int n = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
            ApplyLibraryChanges(changes);
            return n;
        }
    }
}
