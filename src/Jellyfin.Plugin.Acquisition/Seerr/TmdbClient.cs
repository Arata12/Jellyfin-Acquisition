using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Acquisition.Seerr;

public interface ITmdbClient
{
    /// <summary>Whether a TMDB key is configured; without it discover rows and recommendations stay empty.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// One page of a TMDB list endpoint mapped to Seerr's media shape. People are dropped and
    /// <paramref name="mediaType"/> fills in the type on single-type endpoints. Null when TMDB cannot be read.
    /// </summary>
    Task<SeerrPageDto?> GetPageAsync(string path, int page, string? language, string? mediaType, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default);
}

/// <summary>
/// Optional TMDB access for the parts of the Seerr API that Sonarr and Radarr cannot serve (popular, trending and
/// upcoming rows, recommendations).
/// </summary>
public class TmdbClient : ITmdbClient
{
    private const string BaseUrl = "https://api.themoviedb.org/3/";

    private readonly HttpClient _httpClient;
    private readonly ILogger<TmdbClient> _logger;

    public TmdbClient(HttpClient httpClient, ILogger<TmdbClient> logger)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
        _logger = logger;
    }

    private static string ApiKey => Plugin.Instance?.Configuration.TmdbApiKey?.Trim() ?? string.Empty;

    public bool IsConfigured => ApiKey.Length > 0;

    public async Task<SeerrPageDto?> GetPageAsync(string path, int page, string? language, string? mediaType, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default)
    {
        var key = ApiKey;
        if (key.Length == 0) return null;

        // v4 read access tokens are JWTs sent as bearer; v3 keys go in the query string
        var isBearer = key.StartsWith("eyJ", StringComparison.Ordinal);
        var parameters = new Dictionary<string, string> { ["page"] = Math.Clamp(page, 1, 500).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(language)) parameters["language"] = language.Trim();
        if (query != null)
        {
            foreach (var (k, v) in query) parameters[k] = v;
        }

        if (!isBearer) parameters["api_key"] = key;

        var url = BaseUrl + path.TrimStart('/') + "?" + string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (isBearer) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TMDB returned {Status} for {Path}", (int)response.StatusCode, path);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return MapPage(doc.RootElement, mediaType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "TMDB request failed for {Path}", path);
            return null;
        }
    }

    public static SeerrPageDto MapPage(JsonElement root, string? mediaType)
    {
        var page = new SeerrPageDto
        {
            Page = Int(root, "page") ?? 1,
            TotalPages = Int(root, "total_pages") ?? 1,
            TotalResults = Int(root, "total_results") ?? 0
        };

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                var type = Str(item, "media_type") ?? mediaType;
                if (type is not ("movie" or "tv")) continue;

                var id = Int(item, "id");
                if (id is null or <= 0) continue;

                page.Results.Add(new SeerrMediaDto
                {
                    Id = id.Value,
                    MediaType = type,
                    Title = Str(item, "title"),
                    Name = Str(item, "name"),
                    OriginalTitle = Str(item, "original_title"),
                    OriginalName = Str(item, "original_name"),
                    Overview = Str(item, "overview"),
                    PosterPath = Str(item, "poster_path"),
                    BackdropPath = Str(item, "backdrop_path"),
                    ReleaseDate = Str(item, "release_date"),
                    FirstAirDate = Str(item, "first_air_date"),
                    VoteAverage = Double(item, "vote_average"),
                    VoteCount = Int(item, "vote_count"),
                    Popularity = Double(item, "popularity"),
                    OriginalLanguage = Str(item, "original_language"),
                    Adult = item.TryGetProperty("adult", out var adult) && adult.ValueKind is JsonValueKind.True or JsonValueKind.False ? adult.GetBoolean() : null,
                    OriginCountry = item.TryGetProperty("origin_country", out var countries) && countries.ValueKind == JsonValueKind.Array
                        ? countries.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToList()
                        : null
                });
            }
        }

        return page;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()) ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static double? Double(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
