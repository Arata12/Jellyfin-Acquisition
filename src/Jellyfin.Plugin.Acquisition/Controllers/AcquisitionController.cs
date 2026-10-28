using System.Net.Mime;
using System.Reflection;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Adapters.QBittorrent;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Catalog;
using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr.Models;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Registration;
using Jellyfin.Plugin.Acquisition.Releases;
using Jellyfin.Plugin.Acquisition.Security;
using Jellyfin.Plugin.Acquisition.Storage;
using Jellyfin.Plugin.Acquisition.Tracking;

namespace Jellyfin.Plugin.Acquisition.Controllers;

[ApiController]
[Route("Acquisition")]
[Produces(MediaTypeNames.Application.Json, "application/json; profile=\"CamelCase\"")]
public class AcquisitionController : ControllerBase
{
    private readonly ICatalogService _catalogService;
    private readonly IRequestRepository _requestRepo;
    private readonly ITrackerService _trackerService;
    private readonly IReleaseService _releaseService;
    private readonly ISonarrClient _sonarrClient;
    private readonly IRadarrClient? _radarrClient;
    private readonly IQBittorrentClient? _qbClient;
    private readonly IUserManager _userManager;
    private readonly ILogger<AcquisitionController> _logger;
    private readonly ArrRegistrar _registrar;

    /// <summary>Evaluated releases older than this are searched again when the user opens the list.</summary>
    private static readonly TimeSpan ReleaseListMaxAge = TimeSpan.FromMinutes(15);

    private static readonly System.Text.RegularExpressions.Regex OpaqueImageIdPattern =
        new("^[a-f0-9]{64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public AcquisitionController(
        ICatalogService catalogService,
        IRequestRepository requestRepo,
        ITrackerService trackerService,
        IReleaseService releaseService,
        ISonarrClient sonarrClient,
        IUserManager userManager,
        ILogger<AcquisitionController> logger,
        IRadarrClient? radarrClient = null,
        IQBittorrentClient? qbClient = null)
    {
        _catalogService = catalogService;
        _requestRepo = requestRepo;
        _trackerService = trackerService;
        _releaseService = releaseService;
        _sonarrClient = sonarrClient;
        _userManager = userManager;
        _logger = logger;
        _radarrClient = radarrClient;
        _qbClient = qbClient;
        _registrar = new ArrRegistrar(sonarrClient, radarrClient, logger);
    }

    private Guid? GetValidatedUserId()
    {
        return SecurityHelper.GetCurrentUserId(User);
    }

    /// <summary>
    /// Search external catalog (Sonarr and Radarr lookup) with provider diagnostics.
    /// </summary>
    [HttpGet("catalog")]
    [Authorize]
    [ProducesResponseType(typeof(CatalogSearchResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CatalogSearchResultDto>> SearchCatalog([FromQuery] string query, CancellationToken ct)
    {
        var userId = GetValidatedUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var results = await _catalogService.SearchCatalogWithDiagnosticsAsync(userId.Value, query, ct).ConfigureAwait(false);
        return Ok(results);
    }

    /// <summary>
    /// Get details of an external item.
    /// </summary>
    [HttpGet("catalog/{kind}/{providerId}")]
    [Authorize]
    [ProducesResponseType(typeof(CatalogItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CatalogItemDto>> GetCatalogDetails([FromRoute] MediaKind kind, [FromRoute] string providerId, CancellationToken ct)
    {
        var userId = GetValidatedUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var item = await _catalogService.GetDetailsAsync(userId.Value, kind, providerId, ct).ConfigureAwait(false);
        if (item == null)
        {
            return NotFound();
        }
        return Ok(item);
    }

    /// <summary>
    /// Resolve a series from the local library to its Sonarr catalog entry using its external IDs (TVDB, TMDB or IMDb).
    /// </summary>
    [HttpGet("catalog/local-series")]
    [Authorize]
    [ProducesResponseType(typeof(CatalogItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CatalogItemDto>> ResolveLocalSeries(
        [FromQuery] string? title,
        [FromQuery] int? year,
        [FromQuery] string? tvdb,
        [FromQuery] string? tmdb,
        [FromQuery] string? imdb,
        CancellationToken ct)
    {
        var userId = GetValidatedUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var ids = new ProviderIds { Tvdb = tvdb, Tmdb = tmdb, Imdb = imdb };
        var item = await _catalogService.ResolveLocalSeriesAsync(userId.Value, title ?? string.Empty, year, ids, ct).ConfigureAwait(false);
        if (item == null)
        {
            return NotFound(new ErrorResponseDto { Message = "Sonarr no tiene una serie con los mismos identificadores (TVDB, TMDB o IMDb)." });
        }

        return Ok(item);
    }

    /// <summary>
    /// Create a new acquisition request.
    /// </summary>
    [HttpPost("requests")]
    [Authorize]
    [ProducesResponseType(typeof(AcquisitionRequestDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AcquisitionRequestDto>> CreateRequest([FromBody] CreateRequestDto dto, CancellationToken ct)
    {
        var userId = GetValidatedUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var config = Plugin.Instance?.Configuration;
        if (!SecurityHelper.CanAcquire(userId.Value, _userManager, config))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(dto.Title))
        {
            return BadRequest(new ErrorResponseDto { Message = "Falta el título de la solicitud." });
        }

        dto.ProviderIds ??= new ProviderIds();

        // Arr registers series by TVDB and movies by TMDB; without them the request could never be imported
        var (requiredProvider, requiredId) = dto.Kind == MediaKind.Series ? ("tvdb", dto.ProviderIds.Tvdb) : ("tmdb", dto.ProviderIds.Tmdb);
        if (!int.TryParse(requiredId, out var parsedId) || parsedId <= 0)
        {
            return BadRequest(new ErrorResponseDto
            {
                Message = dto.Kind == MediaKind.Series
                    ? "La serie no tiene identificador TVDB; Sonarr lo necesita. Búscala desde el catálogo externo."
                    : "La película no tiene identificador TMDB; Radarr lo necesita. Búscala desde el catálogo externo."
            });
        }

        // Avoid duplicates by external identity, not by title (different works can share a title)
        var existing = await _requestRepo.FindActiveByExternalIdAsync(dto.Kind, requiredProvider, requiredId!, ct).ConfigureAwait(false);
        if (existing == null && !string.IsNullOrWhiteSpace(dto.ProviderIds.Imdb))
        {
            existing = await _requestRepo.FindActiveByExternalIdAsync(dto.Kind, "imdb", dto.ProviderIds.Imdb, ct).ConfigureAwait(false);
        }

        if (existing != null)
        {
            return Conflict(new ConflictResponseDto { Message = "An active request already exists for this title.", RequestId = existing.Id });
        }

        // The destination is resolved on the server from a stable identifier; browser-sent paths are never used
        var resolution = await DestinationResolver.ResolveAsync(dto.Kind, dto.DestinationId, dto.DestinationName, config, _sonarrClient, _radarrClient, ct).ConfigureAwait(false);
        if (resolution.Destination == null)
        {
            return StatusCode(resolution.StatusCode, new ErrorResponseDto { Message = resolution.ErrorMessage ?? "Destino no válido." });
        }

        var destination = resolution.Destination;
        var request = new AcquisitionRequest
        {
            UserId = userId.Value,
            Kind = dto.Kind,
            Title = dto.Title.Trim(),
            Year = dto.Year,
            ProviderIds = dto.ProviderIds,
            Scope = dto.Scope ?? new RequestScope { All = dto.Kind == MediaKind.Movie },
            // "Serie completa" keeps following the series while it airs (user decision)
            FollowAiring = dto.Kind == MediaKind.Series && (dto.Scope?.All ?? false),
            DestinationId = destination.Id,
            DestinationName = destination.Name,
            RootFolderPath = destination.RootFolderPath,
            QualityProfileId = destination.QualityProfileId > 0 ? destination.QualityProfileId : null,
            SeriesType = destination.SeriesType
        };

        request.AddHistory(RequestStatus.Pending, "Solicitud creada por el usuario.");
        await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);

        return CreatedAtAction(nameof(GetRequest), new { id = request.Id }, MapToDto(request));
    }

    /// <summary>
    /// Explicitly prepare a series in Sonarr (POST only, authorized).
    /// </summary>
    [HttpPost("requests/{id}/prepare")]
    [Authorize]
    [ProducesResponseType(typeof(AcquisitionRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AcquisitionRequestDto>> PrepareSeries([FromRoute] string id, CancellationToken ct)
    {
        var userId = GetValidatedUserId();
        if (!userId.HasValue) return Unauthorized();

        using (await _trackerService.AcquireLockAsync(id, ct).ConfigureAwait(false))
        {
            var request = await _requestRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (request == null || request.Kind != MediaKind.Series) return NotFound();

            var config = Plugin.Instance?.Configuration;
            if (!SecurityHelper.CanManageRequest(userId.Value, request, _userManager, config))
            {
                return Forbid();
            }

            if (request.Status != RequestStatus.Pending)
            {
                return Conflict("Series registration can only be prepared while request is in Pending status.");
            }

            var registration = await _registrar.EnsureRegisteredAsync(request, config, ct).ConfigureAwait(false);
            if (registration.Outcome == RegistrationOutcome.Transient)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponseDto { Message = registration.Message ?? "Sonarr no está disponible." });
            }

            if (registration.Outcome == RegistrationOutcome.NeedsAttention)
            {
                return BadRequest(new ErrorResponseDto { Message = registration.Message ?? "No se pudo registrar la serie en Sonarr." });
            }

            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
            return Ok(MapToDto(request));
        }
    }

    /// <summary>
    /// Load episodes for series request scope selection. Read-only GET without side-effects (R02).
    /// </summary>
    [HttpGet("requests/{id}/episodes")]
    [Authorize]
    [ProducesResponseType(typeof(List<EpisodeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<EpisodeDto>>> GetEpisodes([FromRoute] string id, CancellationToken ct)
    {
        var userId = GetValidatedUserId();
        if (!userId.HasValue) return Unauthorized();

        var request = await _requestRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (request == null || request.Kind != MediaKind.Series) return NotFound();

        var config = Plugin.Instance?.Configuration;
        if (!SecurityHelper.CanViewRequest(userId.Value, request, _userManager, config))
        {
            return Forbid();
        }

        if (!request.ArrId.HasValue)
        {
            return BadRequest("Series must be prepared before retrieving episodes.");
        }

        var sonarrEpisodes = await _sonarrClient.GetEpisodesAsync(request.ArrId.Value, ct).ConfigureAwait(false);
        if (sonarrEpisodes == null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, "Unable to communicate with Sonarr to fetch episodes.");
        }

        var dtos = sonarrEpisodes.Select(e => new EpisodeDto
        {
            Id = e.Id,
            SeasonNumber = e.SeasonNumber,
            EpisodeNumber = e.EpisodeNumber,
            Title = e.Title ?? $"Episode {e.EpisodeNumber}",
            Overview = e.Overview,
            HasFile = e.HasFile,
            IsSelected = request.Scope.All || request.Scope.SeasonNumbers.Contains(e.SeasonNumber) || request.Scope.EpisodeIds.Contains(e.Id)
        }).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Confirm request scope (seasons / episodes).
    /// </summary>
    [HttpPost("requests/{id}/confirm")]
    [Authorize]
    [ProducesResponseType(typeof(AcquisitionRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AcquisitionRequestDto>> ConfirmScope([FromRoute] string id, [FromBody] ScopeConfirmationDto confirmation, CancellationToken ct)
    {
        var userId = GetValidatedUserId();
        if (!userId.HasValue) return Unauthorized();

        using (await _trackerService.AcquireLockAsync(id, ct).ConfigureAwait(false))
        {
            var request = await _requestRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (request == null) return NotFound();

            var config = Plugin.Instance?.Configuration;
            if (!SecurityHelper.CanManageRequest(userId.Value, request, _userManager, config))
            {
                return Forbid();
            }

            if (request.Status != RequestStatus.Pending)
            {
                return Conflict("Scope can only be confirmed while request is in Pending status.");
            }

            request.Scope.All = confirmation.All;
            request.FollowAiring = request.Kind == MediaKind.Series && confirmation.All;
            request.Scope.SeasonNumbers = confirmation.SeasonNumbers ?? [];
            request.Scope.EpisodeNumbers = confirmation.EpisodeNumbers ?? [];
            request.Scope.EpisodeIds = confirmation.EpisodeIds ?? [];

            // Build target episodes if series
            if (request.Kind == MediaKind.Series && request.ArrId.HasValue)
            {
                var allEpisodes = await _sonarrClient.GetEpisodesAsync(request.ArrId.Value, ct).ConfigureAwait(false);
                if (allEpisodes != null)
                {
                    if (request.Scope.All)
                    {
                        var now = DateTime.UtcNow;
                        request.Scope.TargetEpisodes = allEpisodes
                            .Where(e => e.SeasonNumber > 0 && !e.HasFile && (!e.AirDateUtc.HasValue || e.AirDateUtc.Value <= now))
                            .Select(e => new EpisodeTarget
                            {
                                SeriesId = request.ArrId.Value,
                                EpisodeId = e.Id,
                                SeasonNumber = e.SeasonNumber,
                                EpisodeNumber = e.EpisodeNumber,
                                Title = e.Title,
                                AirDateUtc = e.AirDateUtc,
                                AbsoluteEpisodeNumber = e.AbsoluteEpisodeNumber
                            }).ToList();
                    }
                    else
                    {
                        var selectedIds = new HashSet<int>(request.Scope.EpisodeIds);
                        if (confirmation.TargetEpisodes != null && confirmation.TargetEpisodes.Count > 0)
                        {
                            foreach (var te in confirmation.TargetEpisodes) selectedIds.Add(te.EpisodeId);
                        }

                        var selectedSeasons = new HashSet<int>(request.Scope.SeasonNumbers);

                        request.Scope.TargetEpisodes = allEpisodes
                            .Where(e => !e.HasFile && (selectedIds.Contains(e.Id) || selectedSeasons.Contains(e.SeasonNumber)))
                            .Select(e => new EpisodeTarget
                            {
                                SeriesId = request.ArrId.Value,
                                EpisodeId = e.Id,
                                SeasonNumber = e.SeasonNumber,
                                EpisodeNumber = e.EpisodeNumber,
                                Title = e.Title,
                                AirDateUtc = e.AirDateUtc,
                                AbsoluteEpisodeNumber = e.AbsoluteEpisodeNumber
                            }).ToList();
                    }
                }
            }

            request.AddHistory(RequestStatus.Pending, "Alcance de episodios/temporadas confirmado por el usuario.");
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);

            return Ok(MapToDto(request));
        }
    }

    /// <summary>
    /// Get status of an acquisition request.
    /// </summary>
    [HttpGet("requests/{id}")]
    [Authorize]
    [ProducesResponseType(typeof(AcquisitionRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AcquisitionRequestDto>> GetRequest([FromRoute] string id, CancellationToken ct)
    {
        var userId = GetValidatedUserId();
        if (!userId.HasValue) return Unauthorized();

        var request = await _requestRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (request == null) return NotFound();

        var config = Plugin.Instance?.Configuration;
        if (!SecurityHelper.CanViewRequest(userId.Value, request, _userManager, config))
        {
            return Forbid();
        }

        return Ok(MapToDto(request));
    }

    /// <summary>
    /// Search and view releases for a request. Never returns private URLs or magnets (R03).
    /// </summary>
    [HttpGet("requests/{id}/releases")]
    [Authorize]
    [ProducesResponseType(typeof(List<ReleaseCandidateDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<ReleaseCandidateDto>>> GetReleases([FromRoute] string id, [FromQuery] bool refresh = false, CancellationToken ct = default)
    {
        var userId = GetValidatedUserId();
        if (!userId.HasValue) return Unauthorized();

        var request = await _requestRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (request == null) return NotFound();

        // Searching hits Prowlarr and every indexer: only users who can act on the result may trigger it
        var config = Plugin.Instance?.Configuration;
        if (!SecurityHelper.CanManageRequest(userId.Value, request, _userManager, config))
        {
            return Forbid();
        }

        var activeUnit = request.GetActiveUnit();
        var expectedUnitId = activeUnit?.Id;
        var hasFreshList = request.AvailableReleasesUnitId == expectedUnitId &&
                           request.AvailableReleasesUpdatedAt.HasValue &&
                           DateTimeOffset.UtcNow - request.AvailableReleasesUpdatedAt.Value < ReleaseListMaxAge;

        var canSearch = request.Status is RequestStatus.Searching or RequestStatus.Pending or RequestStatus.RequiresAttention;
        if (!canSearch || (hasFreshList && !refresh))
        {
            // Outside the selection phase the stored list is shown as-is; no new search is started
            return Ok(request.AvailableReleases.Select(ReleaseCandidateDto.FromModel).ToList());
        }

        List<ReleaseCandidate> releases;
        try
        {
            releases = activeUnit != null
                ? await _releaseService.SearchAndEvaluateReleasesForUnitAsync(request, activeUnit, ct).ConfigureAwait(false)
                : await _releaseService.SearchAndEvaluateReleasesAsync(request, ct).ConfigureAwait(false);
        }
        catch (ProwlarrUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponseDto { Message = ex.Message });
        }

        using (await _trackerService.AcquireLockAsync(id, ct).ConfigureAwait(false))
        {
            var fresh = await _requestRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (fresh != null && fresh.Status is RequestStatus.Searching or RequestStatus.Pending or RequestStatus.RequiresAttention)
            {
                var freshActiveUnit = fresh.GetActiveUnit();
                if (freshActiveUnit?.Id == expectedUnitId)
                {
                    fresh.AvailableReleases = releases;
                    fresh.AvailableReleasesUnitId = expectedUnitId;
                    fresh.AvailableReleasesUpdatedAt = DateTimeOffset.UtcNow;
                    await _requestRepo.SaveOrUpdateAsync(fresh, ct).ConfigureAwait(false);
                }
            }
        }

        var dtos = releases.Select(ReleaseCandidateDto.FromModel).ToList();
        return Ok(dtos);
    }

    /// <summary>
    /// Manually select a release candidate. Validates identity (R05/N05).
    /// </summary>
    [HttpPost("requests/{id}/release")]
    [Authorize]
    [ProducesResponseType(typeof(AcquisitionRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AcquisitionRequestDto>> SelectRelease([FromRoute] string id, [FromBody] SelectReleaseDto dto, CancellationToken ct)
    {
        var userId = GetValidatedUserId();
        if (!userId.HasValue) return Unauthorized();

        using (await _trackerService.AcquireLockAsync(id, ct).ConfigureAwait(false))
        {
            var request = await _requestRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (request == null) return NotFound();

            var config = Plugin.Instance?.Configuration;
            if (!SecurityHelper.CanManageRequest(userId.Value, request, _userManager, config))
            {
                return Forbid();
            }

            var wanted = request.AvailableReleases.FirstOrDefault(r => r.Id.Equals(dto.ReleaseId, StringComparison.OrdinalIgnoreCase));

            if (request.Status is not (RequestStatus.Searching or RequestStatus.RequiresAttention))
            {
                // The tracker picks the best release automatically; the user may click the same one a moment later
                var current = request.GetActiveUnit()?.SelectedRelease ?? request.SelectedRelease;
                if (current != null && wanted != null && IsSameRelease(current, wanted))
                {
                    return Ok(MapToDto(request));
                }

                return Conflict(new ErrorResponseDto
                {
                    Message = current != null
                        ? $"Ya se está descargando «{current.Title}». Para cambiarla, cancela la solicitud y vuelve a pedirla."
                        : "Las releases solo se pueden elegir mientras la solicitud está buscando o requiere atención."
                });
            }

            var activeUnit = request.GetActiveUnit();
            if (activeUnit != null && request.AvailableReleasesUnitId != activeUnit.Id)
            {
                return Conflict(new ErrorResponseDto { Message = "La lista de releases es de una descarga anterior. Vuelve a abrir la lista para actualizarla." });
            }

            var selected = wanted;
            if (selected == null)
            {
                return NotFound(new ErrorResponseDto { Message = "Esa release ya no está en la lista evaluada. Vuelve a abrir la lista para actualizarla." });
            }

            // Validate identity, year, scope, protocol cannot be bypassed (N05)
            if (!selected.IsTorrent)
            {
                return BadRequest("Selected release is not a torrent.");
            }
            if (selected.RejectionReasons.Any(r => r.Contains("mismatch", StringComparison.OrdinalIgnoreCase) || r.Contains("unresolved", StringComparison.OrdinalIgnoreCase)))
            {
                return BadRequest("Cannot select release with incompatible identity, year, or scope.");
            }

            // Re-evaluate mandatory coverage and scope conditions against active unit (H06)
            if (activeUnit != null)
            {
                if (selected.SeasonNumber > 0 && activeUnit.SeasonNumber > 0 && selected.SeasonNumber != activeUnit.SeasonNumber)
                {
                    return BadRequest($"Selected release season (S{selected.SeasonNumber}) does not match active unit season (S{activeUnit.SeasonNumber}).");
                }
                if (activeUnit.IsFullSeasonPack && !selected.IsSeasonPack)
                {
                    return BadRequest("Selected release is not a season pack for full season request.");
                }
                if (!activeUnit.IsFullSeasonPack && selected.EpisodeNumber > 0 && !activeUnit.EpisodeNumbers.Contains(selected.EpisodeNumber))
                {
                    return BadRequest("Selected release does not cover the requested episode.");
                }
            }

            request.SelectedRelease = selected;
            request.NextAttemptUtc = null; // a manual choice is processed on the next poll, not after a search backoff
            if (activeUnit != null)
            {
                activeUnit.FetchFailures = 0;
                activeUnit.SearchAttempts = 0;
                if (activeUnit.Status == RequestStatus.RequiresAttention)
                {
                    // A manual choice for a download set aside puts it back in the queue
                    activeUnit.Status = RequestStatus.Pending;
                    activeUnit.ErrorMessage = null;
                }

                activeUnit.SelectedRelease = selected;
                // Invalidate active torrent hash and assign a new correlation tag for this new selection (J01 / K01)
                activeUnit.TorrentHash = null;
                request.TorrentHash = null;
                activeUnit.CorrelationTag = $"acq-{request.Id}-{activeUnit.Id}-{Guid.NewGuid():N}";
                request.CorrelationTag = activeUnit.CorrelationTag;
            }
            else
            {
                request.TorrentHash = null;
                request.CorrelationTag = $"acq-{request.Id}-{Guid.NewGuid():N}";
            }

            request.AddHistory(RequestStatus.Searching, $"Selección manual de release: {selected.Title}");
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);

            return Ok(MapToDto(request));
        }
    }

    /// <summary>
    /// Retry a failed or attention-required request.
    /// </summary>
    [HttpPost("requests/{id}/retry")]
    [Authorize]
    [ProducesResponseType(typeof(AcquisitionRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AcquisitionRequestDto>> RetryRequest([FromRoute] string id, CancellationToken ct)
    {
        var userId = GetValidatedUserId();
        if (!userId.HasValue) return Unauthorized();

        var config = Plugin.Instance?.Configuration;
        var result = await _trackerService.RetryRequestAsync(
            id,
            req => SecurityHelper.CanManageRequest(userId.Value, req, _userManager, config),
            ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            RetryOutcome.NotFound => NotFound(),
            RetryOutcome.Forbidden => Forbid(),
            RetryOutcome.InvalidStatus => Conflict(result.ErrorMessage),
            RetryOutcome.Success => Ok(MapToDto(result.Request!)),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>
    /// Cancel a request that is not yet available. Stops tracking only: a torrent already sent to qBittorrent is not removed.
    /// </summary>
    [HttpDelete("requests/{id}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelRequest([FromRoute] string id, CancellationToken ct)
    {
        var userId = GetValidatedUserId();
        if (!userId.HasValue) return Unauthorized();

        var config = Plugin.Instance?.Configuration;
        var result = await _trackerService.CancelRequestAsync(
            id,
            req => SecurityHelper.CanManageRequest(userId.Value, req, _userManager, config),
            ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            RetryOutcome.NotFound => NotFound(),
            RetryOutcome.Forbidden => Forbid(),
            RetryOutcome.InvalidStatus => Conflict(new ErrorResponseDto { Message = result.ErrorMessage ?? "La solicitud no se puede cancelar." }),
            RetryOutcome.Success => NoContent(),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>
    /// Serve cached poster without exposing external API keys or remote URLs to the browser.
    /// </summary>
    [HttpGet("images/{opaqueId}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetImage([FromRoute] string opaqueId, CancellationToken ct)
    {
        // Opaque IDs are SHA-256 hex digests; anything else is rejected before touching the file system
        if (string.IsNullOrEmpty(opaqueId) || !OpaqueImageIdPattern.IsMatch(opaqueId))
        {
            return NotFound();
        }

        var img = await _catalogService.GetImageAsync(opaqueId, ct).ConfigureAwait(false);
        if (img == null)
        {
            return NotFound();
        }
        return File(img.Value.Bytes, img.Value.ContentType);
    }

    /// <summary>
    /// Serve embedded client script.
    /// </summary>
    [HttpGet("web/acquisition.js")]
    [AllowAnonymous]
    public IActionResult GetClientScript()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.Acquisition.Web.acquisition.js");
        if (stream == null) return NotFound();
        Response.Headers.CacheControl = "no-cache"; // never run a stale client after a plugin update
        return File(stream, "text/javascript; charset=utf-8");
    }

    /// <summary>
    /// Serve embedded client styles.
    /// </summary>
    [HttpGet("web/acquisition.css")]
    [AllowAnonymous]
    public IActionResult GetClientStyles()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.Acquisition.Web.acquisition.css");
        if (stream == null) return NotFound();
        Response.Headers.CacheControl = "no-cache";
        return File(stream, "text/css; charset=utf-8");
    }

    /// <summary>
    /// Retrieve discovered setup options (root folders, quality profiles, clients, categories) - Admin only.
    /// </summary>
    [HttpGet("setup/options")]
    [Authorize]
    [ProducesResponseType(typeof(SetupOptionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SetupOptionsDto>> GetSetupOptions(CancellationToken ct)
    {
        var userId = GetValidatedUserId();
        if (!userId.HasValue) return Unauthorized();

        var config = Plugin.Instance?.Configuration;
        if (!SecurityHelper.IsAdmin(userId.Value, _userManager))
        {
            return Forbid();
        }

        var options = new SetupOptionsDto();

        // 1. Sonarr options
        try
        {
            var sonarrRoots = await _sonarrClient.GetRootFoldersAsync(ct).ConfigureAwait(false);
            var sonarrProfiles = await _sonarrClient.GetQualityProfilesAsync(ct).ConfigureAwait(false);
            var sonarrClients = await _sonarrClient.GetDownloadClientsAsync(ct).ConfigureAwait(false);

            options.Sonarr.IsConnected = true;
            options.Sonarr.RootFolders = sonarrRoots.Select(r => new NamedOptionDto
            {
                Id = r.Id.ToString(),
                Name = r.Path ?? string.Empty,
                Details = r.FreeSpace.HasValue ? $"Libre: {r.FreeSpace.Value / (1024 * 1024 * 1024)} GB" : null
            }).ToList();

            options.Sonarr.QualityProfiles = sonarrProfiles.Select(p => new NamedOptionDto
            {
                Id = p.Id.ToString(),
                Name = p.Name ?? string.Empty
            }).ToList();

            options.Sonarr.DownloadClients = sonarrClients.Select(c => new DownloadClientSummaryDto
            {
                Id = c.Id,
                Name = c.Name ?? string.Empty,
                Implementation = c.Implementation ?? string.Empty,
                Category = c.GetCategory(),
                Host = c.GetHost(),
                Port = c.GetPort(),
                Enabled = c.Enable
            }).ToList();
        }
        catch (Exception ex)
        {
            options.Sonarr.IsConnected = false;
            options.Sonarr.ErrorMessage = ex.Message;
        }

        // 2. Radarr options
        if (_radarrClient != null)
        {
            try
            {
                var radarrRoots = await _radarrClient.GetRootFoldersAsync(ct).ConfigureAwait(false);
                var radarrProfiles = await _radarrClient.GetQualityProfilesAsync(ct).ConfigureAwait(false);
                var radarrClients = await _radarrClient.GetDownloadClientsAsync(ct).ConfigureAwait(false);

                options.Radarr.IsConnected = true;
                options.Radarr.RootFolders = radarrRoots.Select(r => new NamedOptionDto
                {
                    Id = r.Id.ToString(),
                    Name = r.Path ?? string.Empty,
                    Details = r.FreeSpace.HasValue ? $"Libre: {r.FreeSpace.Value / (1024 * 1024 * 1024)} GB" : null
                }).ToList();

                options.Radarr.QualityProfiles = radarrProfiles.Select(p => new NamedOptionDto
                {
                    Id = p.Id.ToString(),
                    Name = p.Name ?? string.Empty
                }).ToList();

                options.Radarr.DownloadClients = radarrClients.Select(c => new DownloadClientSummaryDto
                {
                    Id = c.Id,
                    Name = c.Name ?? string.Empty,
                    Implementation = c.Implementation ?? string.Empty,
                    Category = c.GetCategory(),
                    Host = c.GetHost(),
                    Port = c.GetPort(),
                    Enabled = c.Enable
                }).ToList();
            }
            catch (Exception ex)
            {
                options.Radarr.IsConnected = false;
                options.Radarr.ErrorMessage = ex.Message;
            }
        }

        // 3. QBittorrent categories
        if (_qbClient != null)
        {
            try
            {
                var qbCategories = await _qbClient.GetCategoriesAsync(ct).ConfigureAwait(false);
                options.QBittorrent.IsConnected = true;
                options.QBittorrent.Categories = qbCategories.Select(c => new NamedOptionDto
                {
                    Id = c.Name,
                    Name = c.Name,
                    Details = c.SavePath
                }).ToList();
            }
            catch (Exception ex)
            {
                options.QBittorrent.IsConnected = false;
                options.QBittorrent.ErrorMessage = ex.Message;
            }
        }

        options.ConfiguredDestinations = config?.Destinations.Select(d => new DestinationConfigDto
        {
            Id = d.Id,
            Name = d.Name,
            Kind = d.Kind,
            RootFolderPath = d.RootFolderPath,
            QualityProfileId = d.QualityProfileId,
            SeriesType = d.SeriesType,
            IsDefault = d.IsDefault
        }).ToList() ?? [];

        return Ok(options);
    }

    /// <summary>
    /// Get allowed request options (destinations, root folders, profiles) for a media kind.
    /// </summary>
    [HttpGet("request-options")]
    [Authorize]
    [ProducesResponseType(typeof(RequestOptionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RequestOptionsDto>> GetRequestOptions([FromQuery] MediaKind kind, CancellationToken ct)
    {
        var userId = GetValidatedUserId();
        if (!userId.HasValue) return Unauthorized();

        var config = Plugin.Instance?.Configuration;
        if (!SecurityHelper.CanAcquire(userId.Value, _userManager, config))
        {
            return Forbid();
        }

        // Requesters only receive the authorized destinations; quality profiles are an integration detail
        var list = await DestinationResolver.ListAsync(kind, config, _sonarrClient, _radarrClient, ct).ConfigureAwait(false);
        var result = new RequestOptionsDto
        {
            Kind = kind,
            ErrorMessage = list.ErrorMessage,
            Destinations = list.Options.Select(o => new RequestOptionDestinationDto
            {
                Id = o.Id,
                Name = o.Name,
                RootFolderPath = o.RootFolderPath,
                QualityProfileId = o.QualityProfileId,
                SeriesType = o.SeriesType,
                IsDefault = o.IsDefault
            }).ToList()
        };

        var defaults = result.Destinations.Where(d => d.IsDefault).ToList();
        result.DefaultDestinationId = result.Destinations.Count == 1 ? result.Destinations[0].Id
            : defaults.Count == 1 ? defaults[0].Id
            : null;

        return Ok(result);
    }

    /// <summary>
    /// Same release across searches (candidate IDs change on every search): same info hash, or same title from the same indexer.
    /// </summary>
    private static bool IsSameRelease(ReleaseCandidate a, ReleaseCandidate b)
    {
        if (!string.IsNullOrEmpty(a.InfoHash) && !string.IsNullOrEmpty(b.InfoHash))
        {
            return string.Equals(a.InfoHash, b.InfoHash, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(a.Title, b.Title, StringComparison.Ordinal) &&
               string.Equals(a.Indexer, b.Indexer, StringComparison.OrdinalIgnoreCase);
    }

    private static AcquisitionRequestDto MapToDto(AcquisitionRequest r)
    {
        return new AcquisitionRequestDto
        {
            Id = r.Id,
            Kind = r.Kind,
            Title = r.Title,
            Year = r.Year,
            ProviderIds = r.ProviderIds,
            Scope = r.Scope,
            Status = r.Status,
            StatusText = r.Status.ToString(),
            TorrentName = r.TorrentName,
            DestinationName = r.DestinationName,
            RootFolderPath = r.RootFolderPath,
            QualityProfileId = r.QualityProfileId,
            SeriesType = r.SeriesType,
            AttentionReason = r.AttentionReason,
            ErrorMessage = r.ErrorMessage,
            LocalItemId = r.LocalItemId,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
            CompletedAt = r.CompletedAt,
            History = r.History,
            Progress = BuildProgress(r)
        };
    }

    /// <summary>
    /// Summary of the request's downloads (episodes or season packs) for the status view.
    /// </summary>
    private static RequestProgressDto BuildProgress(AcquisitionRequest r)
    {
        var active = r.GetActiveUnit();
        var manual = Plugin.Instance?.Configuration.IsManualReleaseSelection() == true;
        return new RequestProgressDto
        {
            Total = r.Units.Count,
            Available = r.Units.Count(u => u.Status == RequestStatus.Available),
            InProgress = r.Units.Count(u => u.Status is RequestStatus.Searching or RequestStatus.Downloading or RequestStatus.Importing),
            Pending = r.Units.Count(u => u.Status == RequestStatus.Pending),
            Attention = r.Units.Count(u => u.Status is RequestStatus.RequiresAttention or RequestStatus.Failed),
            Current = active != null && active.Status is not (RequestStatus.RequiresAttention or RequestStatus.Failed) ? active.Describe(r.Kind) : null,
            AttentionItems = r.Units
                .Where(u => u.Status is RequestStatus.RequiresAttention or RequestStatus.Failed)
                .Select(u => new UnitIssueDto { Label = u.Describe(r.Kind), Reason = u.ErrorMessage })
                .ToList(),
            Following = r.FollowAiring,
            WaitingForAir = active is { Status: RequestStatus.Pending, SelectedRelease: null } && active.SearchableFromUtc > DateTime.UtcNow
                ? $"{active.Describe(r.Kind)} se buscará a partir del {active.SearchableFromUtc:dd/MM HH:mm} UTC"
                : null,
            AwaitingSelection = manual &&
                r.Status is RequestStatus.Searching or RequestStatus.Pending &&
                (active?.SelectedRelease ?? r.SelectedRelease) == null &&
                r.AvailableReleasesUnitId == active?.Id &&
                r.AvailableReleases.Any(c => c.IsEligible)
        };
    }
}
