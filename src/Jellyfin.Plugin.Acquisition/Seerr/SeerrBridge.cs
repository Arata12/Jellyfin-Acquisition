using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;
using Jellyfin.Plugin.Acquisition.Catalog;
using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Registration;
using Jellyfin.Plugin.Acquisition.Storage;

namespace Jellyfin.Plugin.Acquisition.Seerr;

/// <summary>An answer the Seerr API returns as <c>{message}</c> with this status code.</summary>
public class SeerrApiException : Exception
{
    public SeerrApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

public interface ISeerrBridge
{
    Task<SeerrPageDto> SearchAsync(Guid userId, string query, int page, CancellationToken ct = default);
    Task<SeerrDetailsDto?> GetMovieAsync(Guid userId, int tmdbId, CancellationToken ct = default);
    Task<SeerrDetailsDto?> GetTvAsync(Guid userId, int tmdbId, CancellationToken ct = default);

    /// <summary>Fills <see cref="SeerrMediaDto.MediaInfo"/> from the library and the plugin's requests.</summary>
    Task AttachStatusAsync(Guid userId, SeerrPageDto page, CancellationToken ct = default);

    /// <summary>Destinations offered as Seerr "servers"; empty when there is nothing to choose.</summary>
    Task<List<SeerrServiceInstanceDto>> ListServersAsync(MediaKind kind, CancellationToken ct = default);

    Task<SeerrRequestDto> CreateRequestAsync(Guid userId, SeerrCreateRequestDto body, CancellationToken ct = default);
}

/// <summary>
/// Serves the Seerr API from the plugin's own sources: Sonarr/Radarr lookups for identity and metadata (series in
/// TVDB numbering, the one Sonarr downloads with), the Jellyfin library for availability and the request store for
/// request state. Requests are created exactly like the web UI's and processed by the same tracker.
/// </summary>
public class SeerrBridge : ISeerrBridge
{
    private static readonly Regex TmdbImageUrl = new(
        @"^https?://image\.tmdb\.org/t/p/[^/]+(/[A-Za-z0-9_\-]+\.(?:jpg|jpeg|png|webp|svg))$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ISonarrClient _sonarrClient;
    private readonly IRadarrClient _radarrClient;
    private readonly ICatalogService _catalogService;
    private readonly ILocalMatcherService _localMatcher;
    private readonly IRequestRepository _requestRepo;
    private readonly ISkyHookClient _skyHook;
    private readonly ILogger<SeerrBridge> _logger;

    public SeerrBridge(
        ISonarrClient sonarrClient,
        IRadarrClient radarrClient,
        ICatalogService catalogService,
        ILocalMatcherService localMatcher,
        IRequestRepository requestRepo,
        ISkyHookClient skyHook,
        ILogger<SeerrBridge> logger)
    {
        _sonarrClient = sonarrClient;
        _radarrClient = radarrClient;
        _catalogService = catalogService;
        _localMatcher = localMatcher;
        _requestRepo = requestRepo;
        _skyHook = skyHook;
        _logger = logger;
    }

    /// <summary>Stable positive integer for an ID Seerr models as a number (users, requests).</summary>
    public static int StableId(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        var id = (int)(BitConverter.ToUInt32(hash, 0) & 0x7FFFFFFF);
        return id == 0 ? 1 : id;
    }

    public async Task<SeerrPageDto> SearchAsync(Guid userId, string query, int page, CancellationToken ct = default)
    {
        // The Arr lookups return a single list: everything is on the first page
        if (page > 1)
        {
            return new SeerrPageDto { Page = page, TotalPages = page };
        }

        // Unlike the web search, titles already in the library stay listed (marked available) so missing seasons can be requested
        var result = await _catalogService.SearchCatalogWithDiagnosticsAsync(userId, query, ct, includeLocal: true).ConfigureAwait(false);
        if (result.Items.Count == 0 && !IsReady(result.Sonarr) && !IsReady(result.Radarr) && (result.Sonarr.IsConfigured || result.Radarr.IsConfigured))
        {
            throw new SeerrApiException(StatusCodes.Status503ServiceUnavailable, result.DiagnosticMessage ?? "El catálogo externo no está disponible.");
        }

        var dto = new SeerrPageDto();
        foreach (var item in result.Items)
        {
            if (!int.TryParse(item.ProviderIds.Tmdb, out var tmdbId) || tmdbId <= 0)
            {
                // Seerr clients address everything by TMDB id
                continue;
            }

            var isMovie = item.Kind == MediaKind.Movie;
            var year = item.Year is > 0 ? item.Year.Value.ToString(CultureInfo.InvariantCulture) : null;
            dto.Results.Add(new SeerrMediaDto
            {
                Id = tmdbId,
                MediaType = isMovie ? "movie" : "tv",
                Title = isMovie ? item.Title : null,
                Name = isMovie ? null : item.Title,
                Overview = item.Overview,
                PosterPath = OpaquePath(item.PosterUrl),
                ReleaseDate = isMovie ? year : null,
                FirstAirDate = isMovie ? null : year
            });
        }

        dto.TotalResults = dto.Results.Count;
        await AttachStatusAsync(userId, dto, ct).ConfigureAwait(false);
        return dto;
    }

    public async Task AttachStatusAsync(Guid userId, SeerrPageDto page, CancellationToken ct = default)
    {
        if (page.Results.Count == 0) return;

        var requests = await _requestRepo.GetAllAsync(ct).ConfigureAwait(false) ?? [];
        var items = page.Results.Select(m => new CatalogItemDto
        {
            Kind = m.MediaType == "movie" ? MediaKind.Movie : MediaKind.Series,
            Title = m.Title ?? m.Name ?? string.Empty,
            Year = YearOf(m.ReleaseDate ?? m.FirstAirDate),
            ProviderIds = new ProviderIds { Tmdb = m.Id.ToString(CultureInfo.InvariantCulture) }
        }).ToList();

        try
        {
            _localMatcher.MatchBatch(userId, items);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Library match for Seerr results failed");
        }

        for (var i = 0; i < page.Results.Count; i++)
        {
            var media = page.Results[i];
            var kind = items[i].Kind;
            var related = RequestsFor(requests, kind, items[i].ProviderIds);
            var info = BuildMediaInfo(media.Id, null, kind, related, seasons: null);
            if (items[i].IsLocal)
            {
                info.Status = SeerrMediaStatus.Available;
            }

            media.MediaInfo = info;
        }
    }

    public async Task<SeerrDetailsDto?> GetMovieAsync(Guid userId, int tmdbId, CancellationToken ct = default)
    {
        var lookup = await _radarrClient.LookupMovieWithDiagnosticsAsync($"tmdb:{tmdbId}", ct).ConfigureAwait(false);
        if (lookup.Status != ProviderStatus.Ready)
        {
            throw new SeerrApiException(StatusCodes.Status503ServiceUnavailable, lookup.ErrorMessage ?? "Radarr no está disponible.");
        }

        var movie = lookup.Items.FirstOrDefault(m => m.TmdbId == tmdbId) ?? lookup.Items.FirstOrDefault();
        if (movie == null) return null;

        var ids = new ProviderIds { Tmdb = tmdbId.ToString(CultureInfo.InvariantCulture), Imdb = movie.ImdbId };
        var requests = await _requestRepo.GetAllAsync(ct).ConfigureAwait(false) ?? [];
        var info = BuildMediaInfo(tmdbId, null, MediaKind.Movie, RequestsFor(requests, MediaKind.Movie, ids), seasons: null);
        if (movie.HasFile || FindLocal(userId, MediaKind.Movie, movie.Title, movie.Year, ids))
        {
            info.Status = SeerrMediaStatus.Available;
        }

        return new SeerrDetailsDto
        {
            Id = tmdbId,
            ImdbId = movie.ImdbId,
            Title = movie.Title,
            Overview = movie.Overview,
            PosterPath = ImagePath(movie.Images.FirstOrDefault(i => Is(i.CoverType, "poster"))?.RemoteUrl),
            BackdropPath = ImagePath(movie.Images.FirstOrDefault(i => Is(i.CoverType, "fanart"))?.RemoteUrl),
            ReleaseDate = movie.InCinemas?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                          ?? (movie.Year > 0 ? movie.Year.ToString(CultureInfo.InvariantCulture) : null),
            Runtime = movie.Runtime is > 0 ? movie.Runtime : null,
            Genres = Named(movie.Genres),
            ProductionCompanies = string.IsNullOrWhiteSpace(movie.Studio) ? null : Named([movie.Studio]),
            Releases = string.IsNullOrWhiteSpace(movie.Certification)
                ? null
                : new SeerrCountryRatingsDto { Results = [new SeerrCountryRatingDto { ReleaseDates = [new SeerrCertificationDto { Certification = movie.Certification }] }] },
            ExternalIds = new SeerrExternalIdsDto { ImdbId = movie.ImdbId },
            MediaInfo = info
        };
    }

    public async Task<SeerrDetailsDto?> GetTvAsync(Guid userId, int tmdbId, CancellationToken ct = default)
    {
        // Sonarr resolves the TMDB id to its TVDB series (SkyHook understands "tmdb:")
        var lookup = await _sonarrClient.LookupSeriesWithDiagnosticsAsync($"tmdb:{tmdbId}", ct).ConfigureAwait(false);
        if (lookup.Status != ProviderStatus.Ready)
        {
            throw new SeerrApiException(StatusCodes.Status503ServiceUnavailable, lookup.ErrorMessage ?? "Sonarr no está disponible.");
        }

        var series = lookup.Items.FirstOrDefault(s => s.TmdbId == tmdbId) ?? lookup.Items.FirstOrDefault();
        if (series == null || series.TvdbId <= 0) return null;

        var showTask = _skyHook.GetShowAsync(series.TvdbId, ct);
        var sonarrEpisodes = series.Id > 0 ? await _sonarrClient.GetEpisodesAsync(series.Id, ct).ConfigureAwait(false) : null;
        var show = await showTask.ConfigureAwait(false);

        // Seasons in TVDB numbering, the one the request is resolved with in Sonarr
        var seasons = BuildSeasons(sonarrEpisodes, show);
        if (seasons == null)
        {
            throw new SeerrApiException(StatusCodes.Status503ServiceUnavailable, "No se pudo obtener la lista de episodios de la serie.");
        }

        var ids = new ProviderIds
        {
            Tvdb = series.TvdbId.ToString(CultureInfo.InvariantCulture),
            Tmdb = tmdbId.ToString(CultureInfo.InvariantCulture),
            Imdb = series.ImdbId
        };
        var requests = await _requestRepo.GetAllAsync(ct).ConfigureAwait(false) ?? [];
        var related = RequestsFor(requests, MediaKind.Series, ids);
        var seasonStatus = SeasonAvailability(sonarrEpisodes, seasons);
        var info = BuildMediaInfo(tmdbId, series.TvdbId, MediaKind.Series, related, seasonStatus);

        var isLocal = FindLocal(userId, MediaKind.Series, series.Title, series.Year, ids);
        info.Status = MediaStatus(seasonStatus, isLocal || sonarrEpisodes?.Any(e => e.HasFile) == true, info.Status);

        var rating = double.TryParse(show?.Rating?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : (double?)null;
        var posterUrl = series.Images.FirstOrDefault(i => Is(i.CoverType, "poster"))?.RemoteUrl
                        ?? show?.Images.FirstOrDefault(i => Is(i.CoverType, "poster"))?.Url;
        var fanartUrl = series.Images.FirstOrDefault(i => Is(i.CoverType, "fanart"))?.RemoteUrl
                        ?? show?.Images.FirstOrDefault(i => Is(i.CoverType, "fanart"))?.Url;

        return new SeerrDetailsDto
        {
            Id = tmdbId,
            Name = series.Title,
            Overview = series.Overview ?? show?.Overview,
            PosterPath = ImagePath(posterUrl),
            BackdropPath = ImagePath(fanartUrl),
            FirstAirDate = show?.FirstAired ?? (series.Year > 0 ? series.Year.ToString(CultureInfo.InvariantCulture) : null),
            LastAirDate = show?.LastAired,
            EpisodeRunTime = show?.Runtime is > 0 ? [show.Runtime.Value] : null,
            VoteAverage = rating,
            VoteCount = show?.Rating?.Count,
            Status = AirStatus(series.Status ?? show?.Status),
            NumberOfEpisodes = seasons.Sum(s => s.EpisodeCount),
            Genres = Named(show?.Genres),
            Networks = string.IsNullOrWhiteSpace(show?.Network) ? null : Named([show.Network]),
            ContentRatings = string.IsNullOrWhiteSpace(show?.ContentRating)
                ? null
                : new SeerrCountryRatingsDto { Results = [new SeerrCountryRatingDto { Rating = show.ContentRating }] },
            ExternalIds = new SeerrExternalIdsDto { ImdbId = series.ImdbId, TvdbId = series.TvdbId },
            Seasons = seasons,
            Credits = show == null
                ? null
                : new SeerrCreditsDto
                {
                    Cast = show.Actors
                        .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                        .Take(30)
                        .Select(a => new SeerrCastMemberDto { Name = a.Name!, Character = a.Character, ProfilePath = ImagePath(a.Image) })
                        .ToList()
                },
            MediaInfo = info
        };
    }

    public async Task<List<SeerrServiceInstanceDto>> ListServersAsync(MediaKind kind, CancellationToken ct = default)
    {
        var list = await DestinationResolver.ListAsync(kind, Plugin.Instance?.Configuration, _sonarrClient, _radarrClient, ct).ConfigureAwait(false);
        if (list.Options.Count <= 1)
        {
            return [];
        }

        var defaults = list.Options.Count(o => o.IsDefault);
        return list.Options.Select((o, index) => new SeerrServiceInstanceDto
        {
            Id = index,
            Name = o.Name,
            IsDefault = defaults == 1 ? o.IsDefault : index == 0
        }).ToList();
    }

    public async Task<SeerrRequestDto> CreateRequestAsync(Guid userId, SeerrCreateRequestDto body, CancellationToken ct = default)
    {
        if (body.Is4k)
        {
            throw new SeerrApiException(StatusCodes.Status400BadRequest, "Las solicitudes en 4K no están disponibles.");
        }

        var kind = body.MediaType switch
        {
            "movie" => MediaKind.Movie,
            "tv" => MediaKind.Series,
            _ => throw new SeerrApiException(StatusCodes.Status400BadRequest, "Tipo de contenido no válido.")
        };

        if (body.MediaId <= 0)
        {
            throw new SeerrApiException(StatusCodes.Status400BadRequest, "Falta el identificador TMDB.");
        }

        var config = Plugin.Instance?.Configuration;
        var scope = new RequestScope { All = kind == MediaKind.Movie };
        string title;
        int year;
        ProviderIds ids;

        if (kind == MediaKind.Movie)
        {
            var lookup = await _radarrClient.LookupMovieWithDiagnosticsAsync($"tmdb:{body.MediaId}", ct).ConfigureAwait(false);
            if (lookup.Status != ProviderStatus.Ready)
            {
                throw new SeerrApiException(StatusCodes.Status503ServiceUnavailable, lookup.ErrorMessage ?? "Radarr no está disponible.");
            }

            var movie = lookup.Items.FirstOrDefault(m => m.TmdbId == body.MediaId)
                        ?? throw new SeerrApiException(StatusCodes.Status404NotFound, "Radarr no encontró la película.");
            title = movie.Title;
            year = movie.Year;
            ids = new ProviderIds { Tmdb = movie.TmdbId.ToString(CultureInfo.InvariantCulture), Imdb = movie.ImdbId };
        }
        else
        {
            var lookup = await _sonarrClient.LookupSeriesWithDiagnosticsAsync($"tmdb:{body.MediaId}", ct).ConfigureAwait(false);
            if (lookup.Status != ProviderStatus.Ready)
            {
                throw new SeerrApiException(StatusCodes.Status503ServiceUnavailable, lookup.ErrorMessage ?? "Sonarr no está disponible.");
            }

            var series = lookup.Items.FirstOrDefault(s => s.TmdbId == body.MediaId) ?? lookup.Items.FirstOrDefault();
            if (series == null || series.TvdbId <= 0)
            {
                throw new SeerrApiException(StatusCodes.Status404NotFound, "Sonarr no encontró la serie.");
            }

            title = series.Title;
            year = series.Year;
            ids = new ProviderIds
            {
                Tvdb = series.TvdbId.ToString(CultureInfo.InvariantCulture),
                Tmdb = body.MediaId.ToString(CultureInfo.InvariantCulture),
                Imdb = series.ImdbId
            };

            var seasons = ParseSeasons(body.Seasons);
            if (seasons == null)
            {
                // "all": the whole series, following it while it airs, as "Serie completa" in the web UI
                scope.All = true;
            }
            else if (seasons.Count == 0)
            {
                throw new SeerrApiException(StatusCodes.Status400BadRequest, "Elige al menos una temporada.");
            }
            else
            {
                scope.SeasonNumbers = seasons;
            }
        }

        var (provider, providerId) = kind == MediaKind.Series ? ("tvdb", ids.Tvdb!) : ("tmdb", ids.Tmdb!);
        var existing = await _requestRepo.FindActiveByExternalIdAsync(kind, provider, providerId, ct).ConfigureAwait(false);
        if (existing == null && !string.IsNullOrWhiteSpace(ids.Imdb))
        {
            existing = await _requestRepo.FindActiveByExternalIdAsync(kind, "imdb", ids.Imdb, ct).ConfigureAwait(false);
        }

        if (existing != null)
        {
            throw new SeerrApiException(StatusCodes.Status409Conflict, "Ya hay una solicitud activa para este título. Puedes seguirla desde Jellyfin.");
        }

        string? destinationId = null;
        if (body.ServerId.HasValue)
        {
            var list = await DestinationResolver.ListAsync(kind, config, _sonarrClient, _radarrClient, ct).ConfigureAwait(false);
            destinationId = body.ServerId.Value >= 0 && body.ServerId.Value < list.Options.Count
                ? list.Options[body.ServerId.Value].Id
                : throw new SeerrApiException(StatusCodes.Status400BadRequest, "El destino seleccionado ya no existe; elige otro.");
        }

        var resolution = await DestinationResolver.ResolveAsync(kind, destinationId, null, config, _sonarrClient, _radarrClient, ct).ConfigureAwait(false);
        var destination = resolution.Destination
                          ?? throw new SeerrApiException(resolution.StatusCode, resolution.ErrorMessage ?? "Destino no válido.");

        var request = new AcquisitionRequest
        {
            UserId = userId,
            Kind = kind,
            Title = title,
            Year = year > 0 ? year : null,
            ProviderIds = ids,
            Scope = scope,
            FollowAiring = kind == MediaKind.Series && scope.All,
            AutoReleaseSelection = true,
            DestinationId = destination.Id,
            DestinationName = destination.Name,
            RootFolderPath = destination.RootFolderPath,
            QualityProfileId = destination.QualityProfileId > 0 ? destination.QualityProfileId : null,
            SeriesType = destination.SeriesType
        };

        request.AddHistory(RequestStatus.Pending, "Solicitud creada desde un cliente Seerr (plezy).");
        await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);

        return new SeerrRequestDto
        {
            Id = StableId(request.Id),
            Status = SeerrRequestStatus.Approved,
            Media = new SeerrMediaInfoDto
            {
                Id = body.MediaId,
                TmdbId = body.MediaId,
                TvdbId = int.TryParse(ids.Tvdb, out var tvdb) ? tvdb : null,
                Status = SeerrMediaStatus.Processing
            },
            Seasons = kind == MediaKind.Series ? scope.SeasonNumbers.Select(n => new SeerrRequestSeasonDto { SeasonNumber = n }).ToList() : null
        };
    }

    /// <summary>Season numbers of a <c>seasons</c> field; null for <c>"all"</c> (or when absent).</summary>
    public static List<int>? ParseSeasons(JsonElement seasons)
    {
        if (seasons.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return seasons.EnumerateArray()
            .Where(s => s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) && n > 0)
            .Select(s => s.GetInt32())
            .Distinct()
            .Order()
            .ToList();
    }

    /// <summary>Seasons (specials left out) with their episode counts, from Sonarr when it has the series, else SkyHook.</summary>
    public static List<SeerrSeasonDto>? BuildSeasons(List<SonarrEpisode>? sonarrEpisodes, SkyHookShow? show)
    {
        if (sonarrEpisodes is { Count: > 0 })
        {
            return sonarrEpisodes
                .Where(e => e.SeasonNumber > 0)
                .GroupBy(e => e.SeasonNumber)
                .OrderBy(g => g.Key)
                .Select(g => new SeerrSeasonDto
                {
                    Id = g.Key,
                    SeasonNumber = g.Key,
                    EpisodeCount = g.Count(),
                    AirDate = g.Min(e => e.AirDateUtc)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                })
                .ToList();
        }

        if (show?.Episodes is { Count: > 0 })
        {
            return show.Episodes
                .Where(e => e.SeasonNumber > 0)
                .GroupBy(e => e.SeasonNumber)
                .OrderBy(g => g.Key)
                .Select(g => new SeerrSeasonDto
                {
                    Id = g.Key,
                    SeasonNumber = g.Key,
                    EpisodeCount = g.Count(),
                    AirDate = g.Select(e => e.AirDate).Where(d => !string.IsNullOrEmpty(d)).Order(StringComparer.Ordinal).FirstOrDefault()
                })
                .ToList();
        }

        return null;
    }

    /// <summary>
    /// Per-season availability: only a season whose episodes all have files is "available"; anything else stays
    /// requestable, since the plugin downloads just the missing episodes of a requested season.
    /// </summary>
    public static Dictionary<int, int> SeasonAvailability(List<SonarrEpisode>? sonarrEpisodes, List<SeerrSeasonDto> seasons)
    {
        var result = seasons.ToDictionary(s => s.SeasonNumber, _ => SeerrMediaStatus.Unknown);
        if (sonarrEpisodes == null) return result;

        foreach (var group in sonarrEpisodes.Where(e => e.SeasonNumber > 0).GroupBy(e => e.SeasonNumber))
        {
            if (group.All(e => e.HasFile))
            {
                result[group.Key] = SeerrMediaStatus.Available;
            }
        }

        return result;
    }

    public static int MediaStatus(Dictionary<int, int> seasonStatus, bool hasSomething, int requestStatus)
    {
        if (seasonStatus.Count > 0 && seasonStatus.Values.All(s => s == SeerrMediaStatus.Available))
        {
            return SeerrMediaStatus.Available;
        }

        if (hasSomething || seasonStatus.Values.Any(s => s == SeerrMediaStatus.Available))
        {
            return SeerrMediaStatus.PartiallyAvailable;
        }

        return requestStatus;
    }

    /// <summary>
    /// Seerr media info: requests mapped to Seerr request states, and "processing" while a request is active.
    /// Seasons covered by an active request that are not available yet are reported as processing too.
    /// </summary>
    public static SeerrMediaInfoDto BuildMediaInfo(int tmdbId, int? tvdbId, MediaKind kind, List<AcquisitionRequest> related, Dictionary<int, int>? seasons)
    {
        var allSeasons = seasons?.Keys.ToList() ?? [];
        var requestDtos = related.Select(r => new SeerrRequestDto
        {
            Id = StableId(r.Id),
            Status = r.Status switch
            {
                RequestStatus.Failed => SeerrRequestStatus.Failed,
                RequestStatus.Available when !r.FollowAiring => SeerrRequestStatus.Completed,
                _ => SeerrRequestStatus.Approved
            },
            Seasons = kind == MediaKind.Series
                ? CoveredSeasons(r, allSeasons).Select(n => new SeerrRequestSeasonDto { SeasonNumber = n }).ToList()
                : null
        }).ToList();

        var active = related.Where(r => r.IsActive && r.Status != RequestStatus.Failed).ToList();
        var info = new SeerrMediaInfoDto
        {
            Id = tmdbId,
            TmdbId = tmdbId,
            TvdbId = tvdbId,
            Status = active.Count > 0 ? SeerrMediaStatus.Processing : SeerrMediaStatus.Unknown,
            Requests = requestDtos
        };

        if (seasons != null)
        {
            var covered = active.SelectMany(r => CoveredSeasons(r, allSeasons)).ToHashSet();
            info.Seasons = seasons
                .OrderBy(s => s.Key)
                .Select(s => new SeerrSeasonInfoDto
                {
                    SeasonNumber = s.Key,
                    Status = s.Value == SeerrMediaStatus.Available ? s.Value : covered.Contains(s.Key) ? SeerrMediaStatus.Processing : s.Value
                })
                .ToList();
        }

        return info;
    }

    private static IEnumerable<int> CoveredSeasons(AcquisitionRequest request, List<int> allSeasons)
    {
        if (request.Scope.All) return allSeasons;

        return request.Scope.SeasonNumbers
            .Concat(request.Scope.TargetEpisodes.Select(t => t.SeasonNumber))
            .Concat(request.Units.Select(u => u.SeasonNumber))
            .Where(n => n > 0)
            .Distinct()
            .Order();
    }

    private static List<AcquisitionRequest> RequestsFor(IReadOnlyList<AcquisitionRequest> requests, MediaKind kind, ProviderIds ids)
    {
        static bool Same(string? a, string? b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // Most recent first, one failed or completed request at most so old history does not pile up
        var matching = requests
            .Where(r => r.Kind == kind && r.ProviderIds != null &&
                        (Same(r.ProviderIds.Tvdb, ids.Tvdb) || Same(r.ProviderIds.Tmdb, ids.Tmdb) || Same(r.ProviderIds.Imdb, ids.Imdb)))
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
        var active = matching.Where(r => r.IsActive && r.Status != RequestStatus.Failed);
        var latestClosed = matching.FirstOrDefault(r => !r.IsActive || r.Status == RequestStatus.Failed);
        return latestClosed == null ? active.ToList() : active.Append(latestClosed).ToList();
    }

    private bool FindLocal(Guid userId, MediaKind kind, string title, int year, ProviderIds ids)
    {
        try
        {
            return _localMatcher.FindLocalMatch(userId, kind, title, year > 0 ? year : null, ids).HasValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Library match failed for {Title}", title);
            return false;
        }
    }

    /// <summary>
    /// Image path for Seerr's <c>/imageproxy/tmdb/t/p/{size}{path}</c>: TMDB artwork keeps its own path (so the
    /// client picks the size), anything else goes through the plugin's opaque image cache.
    /// </summary>
    private string? ImagePath(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return null;

        var match = TmdbImageUrl.Match(remoteUrl);
        if (match.Success) return match.Groups[1].Value;

        var opaqueId = _catalogService.RegisterImage(remoteUrl);
        return opaqueId == null ? null : "/" + opaqueId;
    }

    /// <summary>"/Acquisition/images/{id}" of a catalog result as an image-proxy path.</summary>
    private static string? OpaquePath(string? posterUrl)
    {
        if (string.IsNullOrEmpty(posterUrl)) return null;
        var id = posterUrl[(posterUrl.LastIndexOf('/') + 1)..];
        return id.Length == 64 ? "/" + id : null;
    }

    private static string? AirStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "continuing" => "Returning Series",
        "ended" => "Ended",
        "upcoming" => "Planned",
        _ => null
    };

    private static List<SeerrNamedValueDto>? Named(IEnumerable<string>? names)
    {
        var list = names?.Where(n => !string.IsNullOrWhiteSpace(n)).Select((n, i) => new SeerrNamedValueDto { Id = i + 1, Name = n }).ToList();
        return list is { Count: > 0 } ? list : null;
    }

    private static int? YearOf(string? date) =>
        date != null && date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;

    private static bool IsReady(ProviderDiagnosticsDto diagnostics) =>
        string.Equals(diagnostics.Status, nameof(ProviderStatus.Ready), StringComparison.Ordinal);

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
