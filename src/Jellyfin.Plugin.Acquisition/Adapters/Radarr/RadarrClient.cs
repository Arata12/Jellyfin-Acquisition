using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Adapters.Radarr;

public class RadarrClient : IRadarrClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<RadarrClient> _logger;

    public RadarrClient(HttpClient httpClient, ILogger<RadarrClient> logger)
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
        if (string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration?.RadarrApiKey))
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
        var rawUrl = !string.IsNullOrWhiteSpace(config?.RadarrUrl) ? config.RadarrUrl : "http://localhost:7878";
        if (!rawUrl.EndsWith('/')) rawUrl += "/";
        var uri = new Uri(new Uri(rawUrl), relativePath.TrimStart('/'));

        var req = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(config?.RadarrApiKey))
        {
            req.Headers.Add("X-Api-Key", config.RadarrApiKey);
        }
        return req;
    }

    public async Task<List<RadarrMovie>> LookupMovieAsync(string term, CancellationToken ct = default)
    {
        var result = await LookupMovieWithDiagnosticsAsync(term, ct).ConfigureAwait(false);
        return result.Items;
    }

    public async Task<RadarrLookupResult> LookupMovieWithDiagnosticsAsync(string term, CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        var hasApiKey = !string.IsNullOrWhiteSpace(config?.RadarrApiKey);

        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"api/v3/movie/lookup?term={Uri.EscapeDataString(term)}");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return new RadarrLookupResult
                {
                    // Without an API key a 401 means "not configured yet", not "credentials rejected"
                    Status = hasApiKey ? ProviderStatus.Unauthorized : ProviderStatus.NotConfigured,
                    ErrorMessage = hasApiKey ? $"Radarr authentication failed (HTTP {(int)resp.StatusCode}: invalid API key)." : $"Radarr API key is not configured or invalid (HTTP {(int)resp.StatusCode})."
                };
            }

            if ((int)resp.StatusCode == 429)
            {
                return new RadarrLookupResult
                {
                    Status = ProviderStatus.RateLimited,
                    ErrorMessage = "Radarr rate limit reached (HTTP 429)."
                };
            }

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Radarr LookupMovie returned HTTP {Status} for {Term}", resp.StatusCode, term);
                return new RadarrLookupResult
                {
                    Status = hasApiKey ? ProviderStatus.Unavailable : ProviderStatus.NotConfigured,
                    ErrorMessage = $"Radarr returned HTTP {resp.StatusCode}."
                };
            }

            var response = await resp.Content.ReadFromJsonAsync<List<RadarrMovie>>(cancellationToken: ct).ConfigureAwait(false);
            return new RadarrLookupResult
            {
                Status = ProviderStatus.Ready,
                Items = response ?? []
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(ex, "Radarr LookupMovie failed for term {Term}", term);
            return new RadarrLookupResult
            {
                Status = hasApiKey ? ProviderStatus.Unavailable : ProviderStatus.NotConfigured,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<RadarrMovie?> GetMovieByIdAsync(int id, CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"api/v3/movie/{id}");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<RadarrMovie>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Radarr GetMovieById failed for ID {Id}", id);
            return null;
        }
    }

    public async Task<RadarrMovie?> FindMovieByTmdbIdAsync(int tmdbId, CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"api/v3/movie?tmdbId={tmdbId}");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // A failed lookup must not look like "not registered": callers would try to add a duplicate
                throw new HttpRequestException($"Radarr returned HTTP {(int)resp.StatusCode} while checking existing items.", null, resp.StatusCode);
            }
            var list = await resp.Content.ReadFromJsonAsync<List<RadarrMovie>>(cancellationToken: ct).ConfigureAwait(false);
            return list?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Radarr FindMovieByTmdbId failed for TMDB ID {TmdbId}", tmdbId);
            throw;
        }
    }

    public async Task<RadarrMovie> AddMovieAsync(RadarrMovie movie, CancellationToken ct = default)
    {
        // Radarr must never search on its own: the plugin chooses the release (searchForMovie=false)
        movie.AddOptions = new RadarrAddOptions
        {
            SearchForMovie = false,
            Monitor = "none"
        };

        using var req = CreateRequest(HttpMethod.Post, "api/v3/movie");
        req.Content = JsonContent.Create(movie);

        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // Keep Arr's validation message (e.g. invalid root folder) so it can be shown to the user
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length > 300) body = body[..300];
            throw new HttpRequestException($"Radarr returned HTTP {(int)resp.StatusCode}: {body}", null, resp.StatusCode);
        }

        var created = await resp.Content.ReadFromJsonAsync<RadarrMovie>(cancellationToken: ct).ConfigureAwait(false);
        return created ?? movie;
    }

    public async Task<RadarrParseResult?> ParseAsync(string title, CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"api/v3/parse?title={Uri.EscapeDataString(title)}");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<RadarrParseResult>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Radarr Parse failed for title: {Title}", title);
            return null;
        }
    }

    public async Task<List<RadarrQueueItem>?> GetQueueAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, "api/v3/queue?page=1&pageSize=100");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct).ConfigureAwait(false);
            if (doc != null && doc.RootElement.TryGetProperty("records", out var recordsElement))
            {
                return JsonSerializer.Deserialize<List<RadarrQueueItem>>(recordsElement.GetRawText()) ?? [];
            }
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Radarr GetQueue failed");
            return null;
        }
    }

    public async Task<List<RadarrHistoryItem>> GetHistoryAsync(string? downloadId = null, CancellationToken ct = default)
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
                return JsonSerializer.Deserialize<List<RadarrHistoryItem>>(recordsElement.GetRawText()) ?? [];
            }
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Radarr GetHistory failed");
            return [];
        }
    }

    public async Task<List<RadarrManualImportCandidate>> GetManualImportCandidatesAsync(string folderPath, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v3/manualimport?folder={Uri.EscapeDataString(folderPath)}&filterExistingFiles=true";
            using var req = CreateRequest(HttpMethod.Get, url);
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];

            var response = await resp.Content.ReadFromJsonAsync<List<RadarrManualImportCandidate>>(cancellationToken: ct).ConfigureAwait(false);
            return response ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Radarr GetManualImportCandidates failed for path {Path}", folderPath);
            return [];
        }
    }

    public async Task<bool> ExecuteManualImportAsync(IEnumerable<RadarrManualImportCandidate> candidates, CancellationToken ct = default)
    {
        try
        {
            var files = candidates.Select(c => new
            {
                path = c.Path,
                movieId = c.Movie?.Id,
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
            _logger.LogError(ex, "Radarr ExecuteManualImport failed");
            return false;
        }
    }

    public async Task<List<RadarrRootFolder>> GetRootFoldersAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, "api/v3/rootfolder");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<List<RadarrRootFolder>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            LogFailure(ex, "Radarr GetRootFolders failed");
            throw;
        }
    }

    public async Task<List<RadarrQualityProfile>> GetQualityProfilesAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, "api/v3/qualityprofile");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<List<RadarrQualityProfile>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            LogFailure(ex, "Radarr GetQualityProfiles failed");
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
            LogFailure(ex, "Radarr GetDownloadClients failed");
            throw;
        }
    }
}
