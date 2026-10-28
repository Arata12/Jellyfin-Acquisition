using System.Text.Json.Serialization;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Controllers.Dtos;

public class CatalogItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MediaKind Kind { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("posterUrl")]
    public string? PosterUrl { get; set; }

    [JsonPropertyName("providerIds")]
    public ProviderIds ProviderIds { get; set; } = new();

    [JsonPropertyName("isLocal")]
    public bool IsLocal { get; set; }

    [JsonPropertyName("localItemId")]
    public Guid? LocalItemId { get; set; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("requestStatus")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RequestStatus? RequestStatus { get; set; }

    [JsonPropertyName("statusText")]
    public string? StatusText { get; set; }
}
