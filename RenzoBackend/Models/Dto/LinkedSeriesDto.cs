using RenzoBackend.Models;
using RenzoBackend.Models.Abstractions;
using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Dto;
// [Schema] // Controller I/O Model
public class LinkedSeriesDto : SeriesSummaryBase
{
    public string ProviderId { get; set; } = "";
    [JsonPropertyName("linkedIds")]
    public List<string> LinkedIds { get; set; } = new List<string>();

    /// <summary>
    /// Found under one of the series' other names (AltTitles), not the name
    /// searched: the name it was found under. Its Title then shows the searched
    /// name, so it sits with the series it is, until added.
    /// </summary>
    [JsonPropertyName("foundAs")]
    public string? FoundAs { get; set; }

    /// <summary>The title the source itself gives, when Title shows the searched name instead (see FoundAs).</summary>
    [JsonPropertyName("sourceTitle")]
    public string? SourceTitle { get; set; }


}