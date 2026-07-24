using System.Text.Json.Serialization;
using RenzoBackend.Models.Abstractions;
using RenzoBackend.Models.Enums;

namespace RenzoBackend.Models.Dto;

public class SeriesHealthDto : IThumb
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("thumbnailUrl")]
    public string? ThumbnailUrl { get; set; }

    [JsonPropertyName("level")]
    public HealthStatusLevel Level { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("lastChapterDate")]
    public DateTime? LastChapterDate { get; set; }

    [JsonPropertyName("daysWithoutRelease")]
    public int? DaysWithoutRelease { get; set; }

    /// <summary>
    /// Release cadence in days (absolute value, always positive).
    /// </summary>
    [JsonPropertyName("releaseCadenceDays")]
    public int? ReleaseCadenceDays { get; set; }

    [JsonPropertyName("providers")]
    public List<SmallProviderHealthDto> Providers { get; set; } = [];
}

public class ProviderHealthDto
{
    [JsonPropertyName("providerId")]
    public Guid ProviderId { get; set; }

    [JsonPropertyName("providerName")]
    public string ProviderName { get; set; } = string.Empty;

    [JsonPropertyName("scanlator")]
    public string Scanlator { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public HealthStatusLevel Level { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("lastErrorDate")]
    public DateTime? LastErrorDate { get; set; }

    [JsonPropertyName("consecutiveErrors")]
    public int ConsecutiveErrors { get; set; }

    [JsonPropertyName("isMihonInstalled")]
    public bool IsMihonInstalled { get; set; } = true;

    /// <summary>
    /// Only series that have their own active alerts (yellow/red) appear here.
    /// </summary>
    [JsonPropertyName("affectedSeries")]
    public List<SeriesHealthDto> AffectedSeries { get; set; } = [];
}

public class SmallProviderHealthDto
{
    [JsonPropertyName("providerId")]
    public Guid ProviderId { get; set; }

    [JsonPropertyName("providerName")]
    public string ProviderName { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public HealthStatusLevel Level { get; set; }
}

public class StatusSummaryDto
{
    [JsonPropertyName("totalYellowSeries")]
    public int TotalYellowSeries { get; set; }

    [JsonPropertyName("totalRedSeries")]
    public int TotalRedSeries { get; set; }

    [JsonPropertyName("totalYellowProviders")]
    public int TotalYellowProviders { get; set; }

    [JsonPropertyName("totalRedProviders")]
    public int TotalRedProviders { get; set; }
}

public class ClearAlertRequest
{
    [JsonPropertyName("targetType")]
    public HealthStatusTargetType TargetType { get; set; }

    [JsonPropertyName("targetId")]
    public Guid TargetId { get; set; }
}

public class SetCadenceRequest
{
    /// <summary>
    /// Release cadence in days. Null = clear user override and let system recalculate.
    /// The backend stores this as a negative value to mark it as user-set.
    /// </summary>
    [JsonPropertyName("cadenceDays")]
    public int? CadenceDays { get; set; }
}

public class DeleteDownloadsRequest
{
    /// <summary>
    /// Chapter numbers whose downloaded files should be deleted. Null or empty
    /// means "delete every downloaded chapter for the series".
    /// </summary>
    [JsonPropertyName("chapterNumbers")]
    public List<decimal>? ChapterNumbers { get; set; }
}