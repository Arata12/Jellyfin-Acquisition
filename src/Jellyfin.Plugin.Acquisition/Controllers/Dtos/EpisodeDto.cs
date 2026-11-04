using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Acquisition.Controllers.Dtos;

public class EpisodeDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("episodeNumber")]
    public int EpisodeNumber { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("hasFile")]
    public bool HasFile { get; set; }

    /// <summary>The episode exists in the Jellyfin library although Sonarr has no file for it (then <see cref="HasFile"/> is true too).</summary>
    [JsonPropertyName("inJellyfin")]
    public bool InJellyfin { get; set; }

    [JsonPropertyName("isSelected")]
    public bool IsSelected { get; set; }
}
