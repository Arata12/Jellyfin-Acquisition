using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Acquisition.Controllers.Dtos;

/// <summary>"You might like" row for the Jellyfin home page.</summary>
public class RecommendationsDto
{
    [JsonPropertyName("rows")]
    public List<RecommendationRowDto> Rows { get; set; } = [];
}

public class RecommendationRowDto
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Library item the row is based on; null for the general row.</summary>
    [JsonPropertyName("seedItemId")]
    public Guid? SeedItemId { get; set; }

    /// <summary>Titles in the library the user has not watched (<c>isLocal</c>) mixed with titles that can be requested.</summary>
    [JsonPropertyName("items")]
    public List<CatalogItemDto> Items { get; set; } = [];
}
