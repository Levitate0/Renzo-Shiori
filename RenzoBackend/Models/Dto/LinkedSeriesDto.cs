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


}