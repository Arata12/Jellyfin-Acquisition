using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Acquisition.Seerr;

public class SkyHookShow
{
    [JsonPropertyName("tvdbId")] public int TvdbId { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("runtime")] public int? Runtime { get; set; }
    [JsonPropertyName("network")] public string? Network { get; set; }
    [JsonPropertyName("genres")] public List<string> Genres { get; set; } = [];
    [JsonPropertyName("firstAired")] public string? FirstAired { get; set; }
    [JsonPropertyName("lastAired")] public string? LastAired { get; set; }
    [JsonPropertyName("contentRating")] public string? ContentRating { get; set; }
    [JsonPropertyName("rating")] public SkyHookRating? Rating { get; set; }
    [JsonPropertyName("images")] public List<SkyHookImage> Images { get; set; } = [];
    [JsonPropertyName("actors")] public List<SkyHookActor> Actors { get; set; } = [];
    [JsonPropertyName("episodes")] public List<SkyHookEpisode> Episodes { get; set; } = [];
}

public class SkyHookRating
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
}

public class SkyHookImage
{
    [JsonPropertyName("coverType")] public string? CoverType { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public class SkyHookActor
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("character")] public string? Character { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
}

public class SkyHookEpisode
{
    [JsonPropertyName("seasonNumber")] public int SeasonNumber { get; set; }
    [JsonPropertyName("episodeNumber")] public int EpisodeNumber { get; set; }
    [JsonPropertyName("airDate")] public string? AirDate { get; set; }
}

public interface ISkyHookClient
{
    /// <summary>Full TVDB show (with episodes) from Sonarr's metadata service; null when it cannot be read.</summary>
    Task<SkyHookShow?> GetShowAsync(int tvdbId, CancellationToken ct = default);
}

/// <summary>
/// Reads Sonarr's own metadata service (SkyHook) for what Sonarr's lookup API leaves out of series that are not in
/// Sonarr yet: the episodes of each TVDB season, cast and genres. Same numbering Sonarr uses for the request.
/// </summary>
public class SkyHookClient : ISkyHookClient
{
    private const string BaseUrl = "https://skyhook.sonarr.tv/v1/tvdb/shows/en/";

    private readonly HttpClient _httpClient;
    private readonly ILogger<SkyHookClient> _logger;

    public SkyHookClient(HttpClient httpClient, ILogger<SkyHookClient> logger)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
        _logger = logger;
    }

    public async Task<SkyHookShow?> GetShowAsync(int tvdbId, CancellationToken ct = default)
    {
        if (tvdbId <= 0) return null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + tvdbId);
            req.Headers.UserAgent.ParseAdd($"Jellyfin-Acquisition/{typeof(SkyHookClient).Assembly.GetName().Version}");
            using var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("SkyHook returned {Status} for TVDB {TvdbId}", (int)response.StatusCode, tvdbId);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SkyHookShow>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "SkyHook lookup failed for TVDB {TvdbId}", tvdbId);
            return null;
        }
    }
}
