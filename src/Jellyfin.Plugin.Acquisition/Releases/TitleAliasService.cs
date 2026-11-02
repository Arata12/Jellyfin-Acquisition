using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Seerr;

namespace Jellyfin.Plugin.Acquisition.Releases;

public interface ITitleAliasService
{
    /// <summary>
    /// Other names of the requested title (romaji, scene names, translations), looked up by its IDs and never by its
    /// name, so a different show that merely shares words can never be among them. Empty when none can be read.
    /// </summary>
    Task<IReadOnlyList<string>> GetAliasesAsync(AcquisitionRequest request, CancellationToken ct = default);
}

/// <summary>
/// Releases are often named in another language than the requested title (anime fansubs use the romaji name). The
/// names come from Sonarr's scene mappings, SkyHook (TVDB), AniList through the MyAnimeList ids SkyHook gives for the
/// TVDB show, Radarr's original title and, when a key is configured, TMDB.
/// </summary>
public class TitleAliasService : ITitleAliasService
{
    private const string AniListUrl = "https://graphql.anilist.co";
    private const string AniListQuery = "query($ids:[Int]){Page(perPage:25){media(idMal_in:$ids,type:ANIME){title{romaji english native} synonyms}}}";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    private readonly HttpClient _httpClient;
    private readonly ISonarrClient _sonarrClient;
    private readonly IRadarrClient _radarrClient;
    private readonly ISkyHookClient _skyHookClient;
    private readonly ITmdbClient _tmdbClient;
    private readonly ILogger<TitleAliasService> _logger;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<string> Aliases)> _cache = new();

    public TitleAliasService(
        HttpClient httpClient,
        ISonarrClient sonarrClient,
        IRadarrClient radarrClient,
        ISkyHookClient skyHookClient,
        ITmdbClient tmdbClient,
        ILogger<TitleAliasService> logger)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
        _sonarrClient = sonarrClient;
        _radarrClient = radarrClient;
        _skyHookClient = skyHookClient;
        _tmdbClient = tmdbClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetAliasesAsync(AcquisitionRequest request, CancellationToken ct = default)
    {
        var ids = request.ProviderIds ?? new ProviderIds();
        var key = $"{request.Kind}:{request.ArrId}:{ids.Tvdb}:{ids.Tmdb}:{ids.Imdb}";
        if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheTtl)
        {
            return cached.Aliases;
        }

        var titles = new List<string>();
        try
        {
            if (request.Kind == MediaKind.Series)
            {
                await AddSeriesAliasesAsync(request, ids, titles, ct).ConfigureAwait(false);
            }
            else
            {
                await AddMovieAliasesAsync(request, ids, titles, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not read the alternative titles of {Title}", request.Title);
        }

        var aliases = titles
            .Select(t => t.Trim())
            .Where(t => t.Length > 0 && !string.Equals(t, request.Title.Trim(), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // An empty answer is usually a source being down: ask again on the next search instead of caching it
        if (aliases.Count > 0)
        {
            _cache[key] = (DateTimeOffset.UtcNow, aliases);
        }

        return aliases;
    }

    private async Task AddSeriesAliasesAsync(AcquisitionRequest request, ProviderIds ids, List<string> titles, CancellationToken ct)
    {
        var sonarrTask = request.ArrId.HasValue ? _sonarrClient.GetSeriesByIdAsync(request.ArrId.Value, ct) : Task.FromResult<Adapters.Sonarr.Models.SonarrSeries?>(null);
        var showTask = int.TryParse(ids.Tvdb, out var tvdbId) && tvdbId > 0 ? _skyHookClient.GetShowAsync(tvdbId, ct) : Task.FromResult<SkyHookShow?>(null);
        var tmdbTask = int.TryParse(ids.Tmdb, out var tmdbId) && tmdbId > 0 ? _tmdbClient.GetAlternativeTitlesAsync(MediaKind.Series, tmdbId, ct) : Task.FromResult(new List<string>());

        // Scene names first: they are the names releases are published with
        var series = await sonarrTask.ConfigureAwait(false);
        if (series?.AlternateTitles != null)
        {
            titles.AddRange(series.AlternateTitles.Select(a => a.Title).OfType<string>());
        }

        var show = await showTask.ConfigureAwait(false);
        if (show?.MalIds is { Count: > 0 })
        {
            titles.AddRange(await GetAniListTitlesAsync(show.MalIds, ct).ConfigureAwait(false));
        }

        if (show?.AlternativeTitles != null)
        {
            titles.AddRange(show.AlternativeTitles.Select(a => a.Title).OfType<string>());
        }

        titles.AddRange(await tmdbTask.ConfigureAwait(false));
    }

    private async Task AddMovieAliasesAsync(AcquisitionRequest request, ProviderIds ids, List<string> titles, CancellationToken ct)
    {
        var tmdbTask = int.TryParse(ids.Tmdb, out var tmdbId) && tmdbId > 0 ? _tmdbClient.GetAlternativeTitlesAsync(MediaKind.Movie, tmdbId, ct) : Task.FromResult(new List<string>());

        if (request.ArrId.HasValue)
        {
            var movie = await _radarrClient.GetMovieByIdAsync(request.ArrId.Value, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(movie?.OriginalTitle))
            {
                titles.Add(movie.OriginalTitle);
            }
        }

        titles.AddRange(await tmdbTask.ConfigureAwait(false));
    }

    private async Task<List<string>> GetAniListTitlesAsync(List<int> malIds, CancellationToken ct)
    {
        try
        {
            var body = new { query = AniListQuery, variables = new { ids = malIds.Take(25).ToArray() } };
            using var response = await _httpClient.PostAsJsonAsync(AniListUrl, body, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("AniList returned {Status}", (int)response.StatusCode);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("Page", out var page) ||
                !page.TryGetProperty("media", out var media) ||
                media.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<string>();
            foreach (var item in media.EnumerateArray())
            {
                if (item.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in new[] { "romaji", "english", "native" })
                    {
                        if (title.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                        {
                            result.Add(value.GetString()!);
                        }
                    }
                }

                if (item.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
                {
                    result.AddRange(synonyms.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!));
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "AniList lookup failed for MAL ids {Ids}", string.Join(',', malIds));
            return [];
        }
    }
}
