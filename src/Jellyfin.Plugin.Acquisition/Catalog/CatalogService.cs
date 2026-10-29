using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;
using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Storage;

namespace Jellyfin.Plugin.Acquisition.Catalog;

public class CatalogService : ICatalogService
{
    private readonly ISonarrClient _sonarrClient;
    private readonly IRadarrClient _radarrClient;
    private readonly ILocalMatcherService _localMatcher;
    private readonly IRequestRepository _requestRepo;
    private readonly HttpClient _imageHttpClient;
    private readonly string _imageStoragePath;
    private readonly ILogger<CatalogService> _logger;

    private class CachedSearchResult
    {
        public DateTimeOffset Expiry { get; set; }
        public List<CatalogItemDto> Items { get; set; } = [];
        public ProviderDiagnosticsDto Sonarr { get; set; } = new();
        public ProviderDiagnosticsDto Radarr { get; set; } = new();
        public bool IsPartial { get; set; }
        public string? DiagnosticMessage { get; set; }
        public string ConnectionFingerprint { get; set; } = string.Empty;
    }

    private const int MaxCachedSearches = 200;
    private const int MaxRegisteredImages = 5000;
    private const long MaxImageBytes = 10 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, CachedSearchResult> _searchCache = new();
    private readonly ConcurrentDictionary<string, string> _opaqueImageMap = new();

    public CatalogService(
        ISonarrClient sonarrClient,
        IRadarrClient radarrClient,
        ILocalMatcherService localMatcher,
        IRequestRepository requestRepo,
        IHttpClientFactory httpClientFactory,
        IApplicationPaths appPaths,
        ILogger<CatalogService> logger)
    {
        _sonarrClient = sonarrClient;
        _radarrClient = radarrClient;
        _localMatcher = localMatcher;
        _requestRepo = requestRepo;
        _imageHttpClient = httpClientFactory.CreateClient();
        _logger = logger;

        _imageStoragePath = Path.Combine(appPaths.PluginConfigurationsPath, "Acquisition", "images");
        Directory.CreateDirectory(_imageStoragePath);
    }

    public async Task<List<CatalogItemDto>> SearchCatalogAsync(Guid userId, string query, CancellationToken ct = default)
    {
        var result = await SearchCatalogWithDiagnosticsAsync(userId, query, ct).ConfigureAwait(false);
        return result.Items;
    }

    public void InvalidateSearchCache(string? query = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _searchCache.Clear();
        }
        else
        {
            _searchCache.TryRemove(query.Trim().ToLowerInvariant(), out _);
        }
    }

    public async Task<CatalogSearchResultDto> SearchCatalogWithDiagnosticsAsync(Guid userId, string query, CancellationToken ct = default, bool includeLocal = false)
    {
        var searchResult = new CatalogSearchResultDto();

        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 3)
        {
            return searchResult;
        }

        var normalizedQuery = query.Trim().ToLowerInvariant();

        // 1. Check cache (5 minutes, preserving provider diagnostics and connection fingerprint)
        var config = Plugin.Instance?.Configuration;
        var currentFingerprint = $"{config?.SonarrUrl}|{config?.SonarrApiKey}|{config?.RadarrUrl}|{config?.RadarrApiKey}";

        if (_searchCache.TryGetValue(normalizedQuery, out var cached))
        {
            if (cached.Expiry > DateTimeOffset.UtcNow && cached.ConnectionFingerprint == currentFingerprint)
            {
                var enrichedCached = await EnrichItemsWithLocalAndRequestStatusAsync(userId, cached.Items, filterOutLocal: !includeLocal, ct).ConfigureAwait(false);
                searchResult.Items = enrichedCached;
                searchResult.TotalRemoteFound = cached.Items.Count;
                searchResult.LocalMatchesCount = cached.Items.Count - enrichedCached.Count;
                searchResult.Sonarr = new ProviderDiagnosticsDto
                {
                    Status = cached.Sonarr.Status,
                    Message = cached.Sonarr.Message,
                    IsConfigured = cached.Sonarr.IsConfigured
                };
                searchResult.Radarr = new ProviderDiagnosticsDto
                {
                    Status = cached.Radarr.Status,
                    Message = cached.Radarr.Message,
                    IsConfigured = cached.Radarr.IsConfigured
                };
                searchResult.IsPartial = cached.IsPartial;
                if (cached.Items.Count > 0 && enrichedCached.Count == 0)
                {
                    searchResult.DiagnosticMessage = "Todos los títulos encontrados ya están en tu biblioteca.";
                }
                else if (cached.Items.Count == 0)
                {
                    searchResult.DiagnosticMessage = cached.DiagnosticMessage ?? "Sin coincidencias externas.";
                }
                else
                {
                    searchResult.DiagnosticMessage = cached.DiagnosticMessage;
                }
                return searchResult;
            }
            else
            {
                _searchCache.TryRemove(normalizedQuery, out _);
            }
        }

        // 2. Query Sonarr and Radarr with diagnostics in parallel
        var sonarrTask = _sonarrClient.LookupSeriesWithDiagnosticsAsync(normalizedQuery, ct);
        var radarrTask = _radarrClient.LookupMovieWithDiagnosticsAsync(normalizedQuery, ct);

        try
        {
            await Task.WhenAll(sonarrTask, radarrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "One or more catalog lookups failed for {Query}", query);
        }

        SonarrLookupResult sonarrDiag;
        if (sonarrTask.Status == TaskStatus.RanToCompletion && sonarrTask.Result != null)
        {
            sonarrDiag = sonarrTask.Result;
        }
        else
        {
            try
            {
                var fallbackSeries = await _sonarrClient.LookupSeriesAsync(normalizedQuery, ct).ConfigureAwait(false);
                sonarrDiag = new SonarrLookupResult
                {
                    Status = ProviderStatus.Ready,
                    Items = fallbackSeries ?? []
                };
            }
            catch (Exception ex)
            {
                sonarrDiag = new SonarrLookupResult
                {
                    Status = ProviderStatus.Unavailable,
                    ErrorMessage = ex.Message
                };
            }
        }

        RadarrLookupResult radarrDiag;
        if (radarrTask.Status == TaskStatus.RanToCompletion && radarrTask.Result != null)
        {
            radarrDiag = radarrTask.Result;
        }
        else
        {
            try
            {
                var fallbackMovies = await _radarrClient.LookupMovieAsync(normalizedQuery, ct).ConfigureAwait(false);
                radarrDiag = new RadarrLookupResult
                {
                    Status = ProviderStatus.Ready,
                    Items = fallbackMovies ?? []
                };
            }
            catch (Exception ex)
            {
                radarrDiag = new RadarrLookupResult
                {
                    Status = ProviderStatus.Unavailable,
                    ErrorMessage = ex.Message
                };
            }
        }

        searchResult.Sonarr = new ProviderDiagnosticsDto
        {
            Status = sonarrDiag.Status.ToString(),
            Message = sonarrDiag.ErrorMessage,
            IsConfigured = sonarrDiag.Status != ProviderStatus.NotConfigured
        };

        searchResult.Radarr = new ProviderDiagnosticsDto
        {
            Status = radarrDiag.Status.ToString(),
            Message = radarrDiag.ErrorMessage,
            IsConfigured = radarrDiag.Status != ProviderStatus.NotConfigured
        };

        var rawResults = new List<CatalogItemDto>();

        // Process Sonarr series
        if (sonarrDiag.Status == ProviderStatus.Ready && sonarrDiag.Items != null)
        {
            foreach (var series in sonarrDiag.Items)
            {
                var posterUrl = series.Images.FirstOrDefault(i => string.Equals(i.CoverType, "poster", StringComparison.OrdinalIgnoreCase))?.RemoteUrl
                                ?? series.Images.FirstOrDefault()?.RemoteUrl;
                var opaqueId = RegisterImage(posterUrl);

                rawResults.Add(new CatalogItemDto
                {
                    Id = $"series-{series.TvdbId}",
                    Kind = MediaKind.Series,
                    Title = series.Title,
                    Year = series.Year,
                    Overview = series.Overview,
                    PosterUrl = opaqueId != null ? $"/Acquisition/images/{opaqueId}" : null,
                    ProviderIds = new ProviderIds
                    {
                        Tvdb = series.TvdbId.ToString(),
                        Tmdb = series.TmdbId > 0 ? series.TmdbId.ToString() : null,
                        Imdb = series.ImdbId
                    }
                });
            }
        }

        // Process Radarr movies
        if (radarrDiag.Status == ProviderStatus.Ready && radarrDiag.Items != null)
        {
            foreach (var movie in radarrDiag.Items)
            {
                var posterUrl = movie.Images.FirstOrDefault(i => string.Equals(i.CoverType, "poster", StringComparison.OrdinalIgnoreCase))?.RemoteUrl
                                ?? movie.Images.FirstOrDefault()?.RemoteUrl;
                var opaqueId = RegisterImage(posterUrl);

                rawResults.Add(new CatalogItemDto
                {
                    Id = $"movie-{movie.TmdbId}",
                    Kind = MediaKind.Movie,
                    Title = movie.Title,
                    Year = movie.Year,
                    Overview = movie.Overview,
                    PosterUrl = opaqueId != null ? $"/Acquisition/images/{opaqueId}" : null,
                    ProviderIds = new ProviderIds
                    {
                        Tmdb = movie.TmdbId.ToString(),
                        Imdb = movie.ImdbId
                    }
                });
            }
        }

        // Determine if response is partial
        var sonarrOk = sonarrDiag.Status == ProviderStatus.Ready;
        var radarrOk = radarrDiag.Status == ProviderStatus.Ready;

        if (sonarrOk != radarrOk)
        {
            searchResult.IsPartial = true;
        }

        var enriched = await EnrichItemsWithLocalAndRequestStatusAsync(userId, rawResults, filterOutLocal: !includeLocal, ct).ConfigureAwait(false);
        searchResult.Items = enriched;
        searchResult.TotalRemoteFound = rawResults.Count;
        searchResult.LocalMatchesCount = rawResults.Count - enriched.Count;

        // Compute user-friendly diagnostic message
        if (sonarrDiag.Status == ProviderStatus.NotConfigured && radarrDiag.Status == ProviderStatus.NotConfigured)
        {
            searchResult.DiagnosticMessage = "El catálogo externo aún no está configurado.";
        }
        else if (!sonarrOk && !radarrOk)
        {
            if (sonarrDiag.Status == ProviderStatus.Unauthorized || radarrDiag.Status == ProviderStatus.Unauthorized)
            {
                searchResult.DiagnosticMessage = "Error de autenticación con el proveedor externo.";
            }
            else
            {
                searchResult.DiagnosticMessage = "El catálogo externo no se encuentra disponible temporalmente.";
            }
        }
        else if (searchResult.IsPartial)
        {
            if (sonarrOk && radarrDiag.Status == ProviderStatus.NotConfigured)
            {
                searchResult.DiagnosticMessage = "Solo series habilitadas; películas no configuradas.";
            }
            else if (radarrOk && sonarrDiag.Status == ProviderStatus.NotConfigured)
            {
                searchResult.DiagnosticMessage = "Solo películas habilitadas; series no configuradas.";
            }
            else
            {
                var failedName = !sonarrOk ? "Sonarr" : "Radarr";
                searchResult.DiagnosticMessage = $"Respuesta parcial: no se pudo consultar {failedName}.";
            }
        }
        else if (rawResults.Count > 0 && enriched.Count == 0)
        {
            searchResult.DiagnosticMessage = "Todos los títulos encontrados ya están en tu biblioteca.";
        }
        else if (enriched.Count == 0)
        {
            searchResult.DiagnosticMessage = "Sin coincidencias externas.";
        }

        // Only cache if AT LEAST ONE provider succeeded and neither provider failed with an error
        // Do NOT cache failures as valid empties
        var hasFailures = sonarrDiag.Status is ProviderStatus.Unavailable or ProviderStatus.Unauthorized or ProviderStatus.RateLimited
                       || radarrDiag.Status is ProviderStatus.Unavailable or ProviderStatus.Unauthorized or ProviderStatus.RateLimited;

        if (!hasFailures && (sonarrOk || radarrOk))
        {
            PruneSearchCache();
            _searchCache[normalizedQuery] = new CachedSearchResult
            {
                Expiry = DateTimeOffset.UtcNow.AddMinutes(5),
                Items = rawResults,
                Sonarr = searchResult.Sonarr,
                Radarr = searchResult.Radarr,
                IsPartial = searchResult.IsPartial,
                DiagnosticMessage = searchResult.DiagnosticMessage,
                ConnectionFingerprint = currentFingerprint
            };
        }

        return searchResult;
    }

    private async Task<List<CatalogItemDto>> EnrichItemsWithLocalAndRequestStatusAsync(Guid userId, List<CatalogItemDto> source, bool filterOutLocal, CancellationToken ct)
    {
        var activeRequests = await _requestRepo.GetAllAsync(ct).ConfigureAwait(false) ?? [];

        // Clone items for user enrichment
        var copies = source.Select(item => new CatalogItemDto
        {
            Id = item.Id,
            Kind = item.Kind,
            Title = item.Title,
            Year = item.Year,
            Overview = item.Overview,
            PosterUrl = item.PosterUrl,
            ProviderIds = item.ProviderIds
        }).ToList();

        // Batch match with local library (R15)
        _localMatcher.MatchBatch(userId, copies);

        var enriched = new List<CatalogItemDto>();
        foreach (var copy in copies)
        {
            // Omit items already in library from external search results (R11)
            if (filterOutLocal && copy.IsLocal)
            {
                continue;
            }

            // Check existing request by external identity; the title alone is shared by different works
            var active = activeRequests.FirstOrDefault(r =>
                r.Kind == copy.Kind &&
                r.IsActive &&
                IsSameWork(r, copy));

            if (active != null)
            {
                copy.RequestId = active.Id;
                copy.RequestStatus = active.Status;
                copy.StatusText = active.Status.ToString();
            }

            enriched.Add(copy);
        }

        return enriched;
    }

    private static bool IsSameWork(AcquisitionRequest request, CatalogItemDto item)
    {
        static bool Same(string? a, string? b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        var ids = request.ProviderIds ?? new ProviderIds();
        if (Same(ids.Tvdb, item.ProviderIds.Tvdb) || Same(ids.Tmdb, item.ProviderIds.Tmdb) || Same(ids.Imdb, item.ProviderIds.Imdb))
        {
            return true;
        }

        // Legacy requests without IDs: require the same title AND year
        var requestHasIds = !string.IsNullOrEmpty(ids.Tvdb) || !string.IsNullOrEmpty(ids.Tmdb) || !string.IsNullOrEmpty(ids.Imdb);
        return !requestHasIds &&
               request.Year.HasValue && request.Year == item.Year &&
               string.Equals(request.Title, item.Title, StringComparison.OrdinalIgnoreCase);
    }

    private void PruneSearchCache()
    {
        if (_searchCache.Count < MaxCachedSearches) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _searchCache.Where(e => e.Value.Expiry <= now).ToList())
        {
            _searchCache.TryRemove(entry.Key, out _);
        }

        if (_searchCache.Count >= MaxCachedSearches)
        {
            foreach (var entry in _searchCache.OrderBy(e => e.Value.Expiry).Take(_searchCache.Count - MaxCachedSearches + 1).ToList())
            {
                _searchCache.TryRemove(entry.Key, out _);
            }
        }
    }

    public async Task<CatalogItemDto?> ResolveLocalSeriesAsync(Guid userId, string title, int? year, ProviderIds ids, CancellationToken ct = default)
    {
        if (int.TryParse(ids.Tvdb, out var tvdbId) && tvdbId > 0)
        {
            return await GetDetailsAsync(userId, MediaKind.Series, ids.Tvdb!, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // Jellyfin libraries often only carry TMDB/IMDb IDs; map them to Sonarr's TVDB entry by identity, never by title alone
        var lookup = await _sonarrClient.LookupSeriesAsync(title.Trim(), ct).ConfigureAwait(false) ?? [];
        var match = lookup.FirstOrDefault(series =>
            (!string.IsNullOrEmpty(ids.Imdb) && string.Equals(series.ImdbId, ids.Imdb, StringComparison.OrdinalIgnoreCase)) ||
            (int.TryParse(ids.Tmdb, out var tmdbId) && tmdbId > 0 && series.TmdbId == tmdbId));

        return match == null || match.TvdbId <= 0
            ? null
            : await GetDetailsAsync(userId, MediaKind.Series, match.TvdbId.ToString(), ct).ConfigureAwait(false);
    }

    public async Task<CatalogItemDto?> GetDetailsAsync(Guid userId, MediaKind kind, string providerId, CancellationToken ct = default)
    {
        if (kind == MediaKind.Series && int.TryParse(providerId, out var tvdbId))
        {
            var seriesList = await _sonarrClient.LookupSeriesAsync($"tvdb:{tvdbId}", ct).ConfigureAwait(false);
            var series = seriesList.FirstOrDefault();
            if (series != null)
            {
                var posterUrl = series.Images.FirstOrDefault(i => string.Equals(i.CoverType, "poster", StringComparison.OrdinalIgnoreCase))?.RemoteUrl;
                var opaqueId = RegisterImage(posterUrl);

                var item = new CatalogItemDto
                {
                    Id = $"series-{series.TvdbId}",
                    Kind = MediaKind.Series,
                    Title = series.Title,
                    Year = series.Year,
                    Overview = series.Overview,
                    PosterUrl = opaqueId != null ? $"/Acquisition/images/{opaqueId}" : null,
                    ProviderIds = new ProviderIds
                    {
                        Tvdb = series.TvdbId.ToString(),
                        Tmdb = series.TmdbId > 0 ? series.TmdbId.ToString() : null,
                        Imdb = series.ImdbId
                    }
                };

                var enriched = await EnrichItemsWithLocalAndRequestStatusAsync(userId, [item], filterOutLocal: false, ct).ConfigureAwait(false);
                return enriched.FirstOrDefault() ?? item;
            }
        }
        else if (kind == MediaKind.Movie && int.TryParse(providerId, out var tmdbId))
        {
            var movieList = await _radarrClient.LookupMovieAsync($"tmdb:{tmdbId}", ct).ConfigureAwait(false);
            var movie = movieList.FirstOrDefault();
            if (movie != null)
            {
                var posterUrl = movie.Images.FirstOrDefault(i => string.Equals(i.CoverType, "poster", StringComparison.OrdinalIgnoreCase))?.RemoteUrl;
                var opaqueId = RegisterImage(posterUrl);

                var item = new CatalogItemDto
                {
                    Id = $"movie-{movie.TmdbId}",
                    Kind = MediaKind.Movie,
                    Title = movie.Title,
                    Year = movie.Year,
                    Overview = movie.Overview,
                    PosterUrl = opaqueId != null ? $"/Acquisition/images/{opaqueId}" : null,
                    ProviderIds = new ProviderIds
                    {
                        Tmdb = movie.TmdbId.ToString(),
                        Imdb = movie.ImdbId
                    }
                };

                var enriched = await EnrichItemsWithLocalAndRequestStatusAsync(userId, [item], filterOutLocal: false, ct).ConfigureAwait(false);
                return enriched.FirstOrDefault() ?? item;
            }
        }

        return null;
    }

    public string? RegisterImage(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return null;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(remoteUrl))).ToLowerInvariant();
        if (_opaqueImageMap.Count >= MaxRegisteredImages)
        {
            // Bounded memory: images already downloaded stay served from the disk cache by their hash
            _opaqueImageMap.Clear();
        }

        _opaqueImageMap[hash] = remoteUrl;
        return hash;
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetImageAsync(string opaqueId, CancellationToken ct = default)
    {
        // Only SHA-256 hex IDs produced by RegisterImage are valid; nothing else may reach the file system
        if (opaqueId.Length != 64 || !opaqueId.All(Uri.IsHexDigit))
        {
            return null;
        }

        var localFilePath = Path.Combine(_imageStoragePath, opaqueId + ".img");
        if (File.Exists(localFilePath))
        {
            var bytes = await File.ReadAllBytesAsync(localFilePath, ct).ConfigureAwait(false);
            var cachedType = DetectImageType(bytes);
            return cachedType != null ? (bytes, cachedType) : null;
        }

        if (_opaqueImageMap.TryGetValue(opaqueId, out var remoteUrl))
        {
            try
            {
                using var response = await _imageHttpClient.GetAsync(remoteUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && (response.Content.Headers.ContentLength ?? 0) <= MaxImageBytes)
                {
                    var bytes = await ReadLimitedAsync(response.Content, ct).ConfigureAwait(false);
                    var contentType = bytes != null ? DetectImageType(bytes) : null;
                    if (bytes != null && contentType != null)
                    {
                        await File.WriteAllBytesAsync(localFilePath, bytes, ct).ConfigureAwait(false);
                        return (bytes, contentType);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Failed to download image from {Url}", Security.SecurityHelper.RedactSecrets(remoteUrl));
            }
        }

        return null;
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxImageBytes) return null;
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Content type from the file signature, so cached files are served with their real type and non-images are refused.
    /// </summary>
    private static string? DetectImageType(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return "image/gif";
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return "image/webp";
        return null;
    }
}
