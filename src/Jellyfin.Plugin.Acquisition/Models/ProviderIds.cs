using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Acquisition.Models;

/// <summary>
/// External provider identifiers.
/// </summary>
public class ProviderIds
{
    [JsonPropertyName("tvdb")]
    public string? Tvdb { get; set; }

    [JsonPropertyName("tmdb")]
    public string? Tmdb { get; set; }

    [JsonPropertyName("imdb")]
    public string? Imdb { get; set; }
}
