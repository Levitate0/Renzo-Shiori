using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using RenzoBackend.Extensions;
using RenzoBackend.Models.Enums;

namespace RenzoBackend.Models.Database
{
    public class SeriesEntity
    {
        private string _storagePath = string.Empty;
        private int _chapterCount;
        [JsonPropertyName("id")]
        [Key]
        public Guid Id { get; set; }
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;
        [JsonPropertyName("thumbnailUrl")]
        public string ThumbnailUrl { get; set; } = string.Empty;
        [JsonPropertyName("artist")]
        public string Artist { get; set; } = string.Empty;

        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
        [JsonPropertyName("genre")]
        public List<string> Genre { get; set; } = new List<string>();
        [JsonPropertyName("status")]
        public SeriesStatus Status { get; set; } = SeriesStatus.UNKNOWN;

        [JsonPropertyName("storagePath")]
        public string StoragePath
        {
            get => _storagePath;
            set => _storagePath = SeriesModelExtensions.NormalizeStoragePath(value);
        }
        [JsonPropertyName("type")]
        public string? Type { get; set; }
        [JsonPropertyName("chapterCount")]
        public int ChapterCount
        {
            get => _chapterCount;
            set => _chapterCount = SeriesModelExtensions.ClampChapterCount(value);
        }
        [JsonPropertyName("pauseDownloads")]
        public bool PauseDownloads { get; set; } = false;

        [JsonPropertyName("startFromChapter")]
        public decimal? StartFromChapter { get; set; }

        [JsonPropertyName("lastChapterDate")]
        public DateTime? LastChapterDate { get; set; }

        /// <summary>
        /// When the series entered the library. Null on rows that predate the
        /// column; the updates feed falls back to the earliest chapter
        /// download date for those.
        /// </summary>
        [JsonPropertyName("dateAdded")]
        public DateTime? DateAdded { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Computed release cadence in days. Null = not yet determined.
        /// Mapped values: 7 (1 week), 15 (half month), 30 (1 month).
        /// Recalculated after each download or chapter fetch.
        /// </summary>
        [JsonPropertyName("releaseCadenceDays")]
        public int? ReleaseCadenceDays { get; set; }

        /// <summary>
        /// Manual 18+ override set by the user (series edit). Complements the
        /// tag-based detection for content whose sources ship no adult tags.
        /// </summary>
        [JsonPropertyName("nsfw")]
        public bool Nsfw { get; set; }

        /// <summary>
        /// Hide sub-chapters (a fractional number like 101.5) from this series'
        /// chapter list and downloads. Some sites let uploaders inject ".5"
        /// chapters as self-promo/announcement pages that duplicate or pad the
        /// real numbering; this lets the user suppress them per series.
        /// </summary>
        [JsonPropertyName("hideDecimalChapters")]
        public bool HideDecimalChapters { get; set; }

        /// <summary>
        /// The raw media-type reported by the external scrobbler this series is
        /// matched to (e.g. MAL <c>media_type</c>: manga / manhwa / manhua / novel,
        /// or Kitsu manga_type). Populated on auto-/confirmed match. This is an
        /// ID-based signal — far more reliable than a title guess — so it is the
        /// TOP authority for category resolution. Null until matched.
        /// </summary>
        [JsonPropertyName("scrobblerType")]
        public string? ScrobblerType { get; set; }

        /// <summary>
        /// The user who owns this series — each user's library is fully isolated.
        /// <see cref="Guid.Empty"/> means no owner has been assigned yet (should
        /// only exist transiently during the one-time startup backfill).
        /// </summary>
        [JsonPropertyName("ownerId")]
        public Guid OwnerId { get; set; } = Guid.Empty;

        public virtual ICollection<SeriesProviderEntity> Sources { get; set; } = [];
    }
}
