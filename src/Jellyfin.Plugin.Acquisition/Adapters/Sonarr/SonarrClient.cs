using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;

namespace Jellyfin.Plugin.Acquisition.Adapters.Sonarr;

public class SonarrClient : ISonarrClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SonarrClient> _logger;

    public SonarrClient(HttpClient httpClient, ILogger<SonarrClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// A service without an API key is simply not configured yet: its failures are expected and logged at Debug,
    /// so an unused integration does not fill the server log with errors on every search.
    /// </summary>
    private void LogFailure(Exception ex, string message, params object?[] args)
    {
        if (string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration?.SonarrApiKey))
        {
            _logger.LogDebug(ex, message, args);
        }
        else
        {
            _logger.LogError(ex, message, args);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var config = Plugin.Instance?.Configuration;
        var rawUrl = !string.IsNullOrWhiteSpace(config?.SonarrUrl) ? config.SonarrUrl : "http://localhost:8989";
        if (!rawUrl.EndsWith('/')) rawUrl += "/";
        var uri = new Uri(new Uri(rawUrl), relativePath.TrimStart('/'));

        var req = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(config?.SonarrApiKey))
        {
            req.Headers.Add("X-Api-Key", config.SonarrApiKey);
        }
        return req;
    }

    public async Task<List<SonarrSeries>> LookupSeriesAsync(string term, CancellationToken ct = default)
    {
        var result = await LookupSeriesWithDiagnosticsAsync(term, ct).ConfigureAwait(false);
        return result.Items;
    }

    public async Task<SonarrLookupResult> LookupSeriesWithDiagnosticsAsync(string term, CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        var hasApiKey = !string.IsNullOrWhiteSpace(config?.SonarrApiKey);

        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"api/v3/series/lookup?term={Uri.EscapeDataString(term)}");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return new SonarrLookupResult
                {
                    // Without an API key a 401 means "not configured yet", not "credentials rejected"
                    Status = hasApiKey ? Jellyfin.Plugin.Acquisition.Models.ProviderStatus.Unauthorized : Jellyfin.Plugin.Acquisition.Models.ProviderStatus.NotConfigured,
                    ErrorMessage = hasApiKey ? $"Sonarr authentication failed (HTTP {(int)resp.StatusCode}: invalid API key)." : $"Sonarr API key is not configured or invalid (HTTP {(int)resp.StatusCode})."
                };
            }

            if ((int)resp.StatusCode == 429)
            {
                return new SonarrLookupResult
                {
                    Status = Jellyfin.Plugin.Acquisition.Models.ProviderStatus.RateLimited,
                    ErrorMessage = "Sonarr rate limit reached (HTTP 429)."
                };
            }

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Sonarr LookupSeries returned HTTP {Status} for {Term}", resp.StatusCode, term);
                return new SonarrLookupResult
                {
                    Status = hasApiKey ? Jellyfin.Plugin.Acquisition.Models.ProviderStatus.Unavailable : Jellyfin.Plugin.Acquisition.Models.ProviderStatus.NotConfigured,
                    ErrorMessage = $"Sonarr returned HTTP {resp.StatusCode}."
                };
            }

            var response = await resp.Content.ReadFromJsonAsync<List<SonarrSeries>>(cancellationToken: ct).ConfigureAwait(false);
            return new SonarrLookupResult
            {
                Status = Jellyfin.Plugin.Acquisition.Models.ProviderStatus.Ready,
                Items = response ?? []
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(ex, "Sonarr LookupSeries failed for term {Term}", term);
            return new SonarrLookupResult
            {
                Status = hasApiKey ? Jellyfin.Plugin.Acquisition.Models.ProviderStatus.Unavailable : Jellyfin.Plugin.Acquisition.Models.ProviderStatus.NotConfigured,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<SonarrSeries?> GetSeriesByIdAsync(int id, CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"api/v3/series/{id}");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<SonarrSeries>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sonarr GetSeriesById failed for ID {Id}", id);
            return null;
        }
    }

    public async Task<SonarrSeries?> FindSeriesByTvdbIdAsync(int tvdbId, CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"api/v3/series?tvdbId={tvdbId}");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // A failed lookup must not look like "not registered": callers would try to add a duplicate
                throw new HttpRequestException($"Sonarr returned HTTP {(int)resp.StatusCode} while checking existing items.", null, resp.StatusCode);
            }
            var list = await resp.Content.ReadFromJsonAsync<List<SonarrSeries>>(cancellationToken: ct).ConfigureAwait(false);
            return list?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sonarr FindSeriesByTvdbId failed for TVDB ID {TvdbId}", tvdbId);
            throw;
        }
    }

    public async Task<SonarrSeries> AddSeriesAsync(SonarrSeries series, CancellationToken ct = default)
    {
        // Sonarr must never search on its own: the plugin chooses the release (searchForMissingEpisodes=false, searchForCutoffUnmetEpisodes=false)
        series.AddOptions = new SonarrAddOptions
        {
            SearchForMissingEpisodes = false,
            SearchForCutoffUnmetEpisodes = false,
            Monitor = "none"
        };

        using var req = CreateRequest(HttpMethod.Post, "api/v3/series");
        req.Content = JsonContent.Create(series);

        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // Keep Arr's validation message (e.g. invalid root folder) so it can be shown to the user
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length > 300) body = body[..300];
            throw new HttpRequestException($"Sonarr returned HTTP {(int)resp.StatusCode}: {body}", null, resp.StatusCode);
        }

        var created = await resp.Content.ReadFromJsonAsync<SonarrSeries>(cancellationToken: ct).ConfigureAwait(false);
        return created ?? series;
    }

    public async Task<List<SonarrEpisode>?> GetEpisodesAsync(int seriesId, CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"api/v3/episode?seriesId={seriesId}");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Sonarr GetEpisodes returned HTTP {Status} for seriesId {SeriesId}", resp.StatusCode, seriesId);
                return null;
            }
            var response = await resp.Content.ReadFromJsonAsync<List<SonarrEpisode>>(cancellationToken: ct).ConfigureAwait(false);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sonarr GetEpisodes failed for seriesId {SeriesId}", seriesId);
            return null;
        }
    }

    public async Task<SonarrParseResult?> ParseAsync(string title, CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"api/v3/parse?title={Uri.EscapeDataString(title)}");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<SonarrParseResult>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sonarr Parse failed for title: {Title}", title);
            return null;
        }
    }

    public async Task<List<SonarrQueueItem>?> GetQueueAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, "api/v3/queue?page=1&pageSize=100");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct).ConfigureAwait(false);
            if (doc != null && doc.RootElement.TryGetProperty("records", out var recordsElement))
            {
                return JsonSerializer.Deserialize<List<SonarrQueueItem>>(recordsElement.GetRawText()) ?? [];
            }
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sonarr GetQueue failed");
            return null;
        }
    }

    public async Task<List<SonarrHistoryItem>> GetHistoryAsync(string? downloadId = null, CancellationToken ct = default)
    {
        try
        {
            var url = "api/v3/history?page=1&pageSize=100";
            if (!string.IsNullOrEmpty(downloadId))
            {
                url += $"&downloadId={Uri.EscapeDataString(downloadId)}";
            }
            using var req = CreateRequest(HttpMethod.Get, url);
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];

            var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct).ConfigureAwait(false);
            if (doc != null && doc.RootElement.TryGetProperty("records", out var recordsElement))
            {
                return JsonSerializer.Deserialize<List<SonarrHistoryItem>>(recordsElement.GetRawText()) ?? [];
            }
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sonarr GetHistory failed");
            return [];
        }
    }

    public async Task<List<SonarrManualImportCandidate>> GetManualImportCandidatesAsync(string folderPath, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v3/manualimport?folder={Uri.EscapeDataString(folderPath)}&filterExistingFiles=true";
            using var req = CreateRequest(HttpMethod.Get, url);
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];

            var response = await resp.Content.ReadFromJsonAsync<List<SonarrManualImportCandidate>>(cancellationToken: ct).ConfigureAwait(false);
            return response ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sonarr GetManualImportCandidates failed for path {Path}", folderPath);
            return [];
        }
    }

    public async Task<bool> ExecuteManualImportAsync(IEnumerable<SonarrManualImportCandidate> candidates, CancellationToken ct = default)
    {
        try
        {
            var files = candidates.Select(c => new
            {
                path = c.Path,
                seriesId = c.Series?.Id,
                episodeIds = c.Episodes.Select(e => e.Id).ToList(),
                quality = c.Quality,
                indexerFlags = 0
            }).ToList();

            var payload = new
            {
                name = "ManualImport",
                files,
                importMode = "Copy"
            };

            using var req = CreateRequest(HttpMethod.Post, "api/v3/command");
            req.Content = JsonContent.Create(payload);

            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sonarr ExecuteManualImport failed");
            return false;
        }
    }

    public async Task<List<SonarrRootFolder>> GetRootFoldersAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, "api/v3/rootfolder");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<List<SonarrRootFolder>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            LogFailure(ex, "Sonarr GetRootFolders failed");
            throw;
        }
    }

    public async Task<List<SonarrQualityProfile>> GetQualityProfilesAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, "api/v3/qualityprofile");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<List<SonarrQualityProfile>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            LogFailure(ex, "Sonarr GetQualityProfiles failed");
            throw;
        }
    }

    public async Task<List<ArrDownloadClient>> GetDownloadClientsAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, "api/v3/downloadclient");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<List<ArrDownloadClient>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            LogFailure(ex, "Sonarr GetDownloadClients failed");
            throw;
        }
    }
}
