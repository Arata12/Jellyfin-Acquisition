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

    /// <summary>Original title, only when it differs from <see cref="Title"/>.</summary>
    [JsonPropertyName("originalTitle")]
    public string? OriginalTitle { get; set; }

    [JsonPropertyName("network")]
    public string? Network { get; set; }

    [JsonPropertyName("studio")]
    public string? Studio { get; set; }

    [JsonPropertyName("originalLanguage")]
    public string? OriginalLanguage { get; set; }

    /// <summary>Seasons excluding specials (series).</summary>
    [JsonPropertyName("seasonCount")]
    public int? SeasonCount { get; set; }

    /// <summary>Aired episodes, when Sonarr reports them (series).</summary>
    [JsonPropertyName("episodeCount")]
    public int? EpisodeCount { get; set; }

    /// <summary>"continuing", "ended" or "upcoming" (series).</summary>
    [JsonPropertyName("seriesStatus")]
    public string? SeriesStatus { get; set; }

    [JsonPropertyName("genres")]
    public List<string> Genres { get; set; } = [];

    [JsonPropertyName("runtimeMinutes")]
    public int? RuntimeMinutes { get; set; }

    [JsonPropertyName("externalLinks")]
    public List<ExternalLinkDto> ExternalLinks { get; set; } = [];

    /// <summary>Aired episodes Sonarr does not have yet, for series Sonarr already manages.</summary>
    [JsonPropertyName("missingEpisodes")]
    public int? MissingEpisodes { get; set; }

    /// <summary>True when genres/language identify the title as anime (used to suggest a destination).</summary>
    [JsonPropertyName("isAnime")]
    public bool IsAnime { get; set; }

    /// <summary>Alternate titles the lookup provides; only used to rank search results.</summary>
    [JsonIgnore]
    public List<string> AlternateTitles { get; set; } = [];

    public CatalogItemDto Clone() => (CatalogItemDto)MemberwiseClone();
}

public class ExternalLinkDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}
