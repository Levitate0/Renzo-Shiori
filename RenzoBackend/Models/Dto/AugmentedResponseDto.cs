using System.Text.Json.Serialization;
using RenzoBackend.Models.Enums;
using RenzoBackend.Models;
using Action = RenzoBackend.Models.Action;

namespace RenzoBackend.Models.Dto
{
    public class AugmentedResponseDto : ImportSummaryBase
    {
        [JsonPropertyName("storageFolderPath")]
        public string StorageFolderPath
        {
            get => NormalizedPath;
            set => NormalizedPath = value;
        }

        [JsonPropertyName("useCategoriesForPath")]
        public bool UseCategoriesForPath { get; set; }

        [JsonPropertyName("existingSeries")]
        public bool ExistingSeries { get; set; }

        [JsonPropertyName("existingSeriesId")]
        public Guid? ExistingSeriesId { get; set; }
        [JsonPropertyName("categories")]
        public List<string> Categories { get; set; } = [];
        [JsonPropertyName("series")]
        public List<ProviderSeriesDetails> Series { get; set; } = [];
        [JsonPropertyName("preferredLanguages")]
        public List<string> PreferredLanguages { get; set; } = [];

        /// <summary>
        /// Selected sources that were dropped during augment (couldn't contribute a
        /// usable series), with the reason — so the UI can explain *why* "Next" has
        /// nothing to show instead of guessing.
        /// </summary>
        [JsonPropertyName("droppedSeries")]
        public List<DroppedSeriesDto> DroppedSeries { get; set; } = [];

        [JsonPropertyName("disableJobs")]
        public bool DisableJobs { get; set; } = false;

        [JsonPropertyName("startChapter")]
        public decimal? StartChapter { get; set; } = null;

        [JsonIgnore] 
        public ImportSeriesSnapshot LocalInfo { get; set; } = new ImportSeriesSnapshot();
        [JsonIgnore]
        public ImportStatus Status { get; set; }
        [JsonIgnore]
        public Action Action { get; set; }

    }

    /// <summary>A source that was dropped during augment, with a machine-readable reason.</summary>
    public class DroppedSeriesDto
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;
        /// <summary>"no-chapters" = details loaded but 0 chapters (usually not translated in the
        /// enabled languages); "unreachable" = source down / rate-limited / timed out.</summary>
        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;
    }
}
