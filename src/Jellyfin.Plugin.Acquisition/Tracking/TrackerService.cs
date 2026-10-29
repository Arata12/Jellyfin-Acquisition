using System.Collections.Concurrent;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr;
using Jellyfin.Plugin.Acquisition.Adapters.QBittorrent;
using Jellyfin.Plugin.Acquisition.Adapters.QBittorrent.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;
using Jellyfin.Plugin.Acquisition.Catalog;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr.Models;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Registration;
using Jellyfin.Plugin.Acquisition.Releases;
using Jellyfin.Plugin.Acquisition.Storage;

namespace Jellyfin.Plugin.Acquisition.Tracking;

public class TrackerService : ITrackerService
{
    private static readonly TimeSpan LibraryScanInterval = TimeSpan.FromMinutes(10);

    // Jellyfin debounces change reports per path and restarts its timer on every new report: reporting on each
    // 15 s poll would keep postponing the refresh forever, so a path is reported at most once per interval.
    private static readonly TimeSpan PathReportInterval = TimeSpan.FromMinutes(3);

    // A targeted refresh does nothing when Jellyfin has no item for any parent folder (e.g. a library created empty)
    private static readonly TimeSpan FullScanAfter = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _reportedPaths = new(StringComparer.Ordinal);

    private DateTimeOffset _lastLibraryValidationRequested = DateTimeOffset.MinValue;

    /// <summary>Prefix of the plugin's correlation tags in qBittorrent; tags without it are never touched.</summary>
    private const string CorrelationTagPrefix = "acq-";

    private static readonly TimeSpan TagCleanupInterval = TimeSpan.FromMinutes(10);
    private DateTimeOffset _lastTagCleanup = DateTimeOffset.MinValue;
    private readonly object _libraryScanLock = new();

    private readonly IRequestRepository _requestRepo;
    private readonly ISonarrClient _sonarrClient;
    private readonly IRadarrClient _radarrClient;
    private readonly IProwlarrClient _prowlarrClient;
    private readonly IQBittorrentClient _qbClient;
    private readonly IReleaseService _releaseService;
    private readonly ILocalMatcherService _localMatcher;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor? _libraryMonitor;
    private readonly ILogger<TrackerService> _logger;
    private readonly ArrRegistrar _registrar;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cancelledRequests = new();

    /// <summary>Consecutive torrent fetch failures tolerated before asking the user.</summary>
    private const int MaxFetchFailures = 3;

    /// <summary>Searches without eligible releases before an episode is set aside while others are waiting.</summary>
    private const int MaxEmptySearches = 3;

    /// <summary>In manual selection mode, how long a ready release list waits before it is searched again.</summary>
    private static readonly TimeSpan ManualListRefresh = TimeSpan.FromMinutes(30);

    /// <summary>Time to wait after a download completes before asking Arr for a manual import.</summary>
    private static readonly TimeSpan ManualImportDelay = TimeSpan.FromMinutes(10);

    /// <summary>Time without any Arr import before the request needs attention.</summary>
    private static readonly TimeSpan ArrImportTimeout = TimeSpan.FromMinutes(60);

    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _requestLocks = new();

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _semaphore.Release();
            }
        }
    }

    public TrackerService(
        IRequestRepository requestRepo,
        ISonarrClient sonarrClient,
        IRadarrClient radarrClient,
        IProwlarrClient prowlarrClient,
        IQBittorrentClient qbClient,
        IReleaseService releaseService,
        ILocalMatcherService localMatcher,
        ILibraryManager libraryManager,
        ILogger<TrackerService> logger,
        ILibraryMonitor? libraryMonitor = null)
    {
        _requestRepo = requestRepo;
        _sonarrClient = sonarrClient;
        _radarrClient = radarrClient;
        _prowlarrClient = prowlarrClient;
        _qbClient = qbClient;
        _releaseService = releaseService;
        _localMatcher = localMatcher;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
        _registrar = new ArrRegistrar(sonarrClient, radarrClient, logger);
    }

    public async Task<IDisposable> AcquireLockAsync(string requestId, CancellationToken ct = default)
    {
        var sem = _requestLocks.GetOrAdd(requestId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(sem);
    }

    public async Task PollActiveRequestsAsync(CancellationToken ct = default)
    {
        if (!await _pollLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var allRequests = await _requestRepo.GetAllAsync(ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var activeRequests = allRequests.Where(r =>
                (r.Status != RequestStatus.Available &&
                 r.Status != RequestStatus.Failed &&
                 r.Status != RequestStatus.RequiresAttention) ||
                (r.Status == RequestStatus.Available && r.FollowAiring && (!r.NextFollowCheckUtc.HasValue || r.NextFollowCheckUtc.Value <= now))).ToList();

            foreach (var req in activeRequests)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await ProcessRequestAsync(req, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing acquisition request {Id} for {Title}", req.Id, req.Title);
                }
            }

            try
            {
                await CleanupCorrelationTagsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not clean up qBittorrent tags");
            }
        }
        finally
        {
            _pollLock.Release();
        }
    }

    public async Task ProcessRequestAsync(AcquisitionRequest initialRequest, CancellationToken ct = default)
    {
        var reqLock = _requestLocks.GetOrAdd(initialRequest.Id, _ => new SemaphoreSlim(1, 1));
        if (!await reqLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            // Another worker is actively executing this request (R07)
            return;
        }

        try
        {
            if (_cancelledRequests.ContainsKey(initialRequest.Id))
            {
                return;
            }

            // Reload fresh request inside the lock (N03)
            var request = await _requestRepo.GetByIdAsync(initialRequest.Id, ct).ConfigureAwait(false) ?? initialRequest;

            switch (request.Status)
            {
                case RequestStatus.Pending:
                    await HandlePendingAsync(request, ct).ConfigureAwait(false);
                    break;

                case RequestStatus.Searching:
                    await HandleSearchingAsync(request, ct).ConfigureAwait(false);
                    break;

                case RequestStatus.Downloading:
                    await HandleDownloadingAsync(request, ct).ConfigureAwait(false);
                    break;

                case RequestStatus.Importing:
                    await HandleImportingAsync(request, ct).ConfigureAwait(false);
                    break;

                case RequestStatus.Available when request.FollowAiring:
                    await HandleFollowAiringAsync(request, ct).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            reqLock.Release();
        }
    }

    private async Task HandlePendingAsync(AcquisitionRequest request, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;

        // Register in Arr first: without an ArrId nothing downloaded could ever be imported
        var hadArrId = request.ArrId.HasValue;
        var registration = await _registrar.EnsureRegisteredAsync(request, config, ct).ConfigureAwait(false);
        if (registration.Outcome == RegistrationOutcome.Transient)
        {
            _logger.LogWarning("Arr registration for request {Id} deferred: {Reason}", request.Id, registration.Message);
            return;
        }

        if (registration.Outcome == RegistrationOutcome.NeedsAttention)
        {
            request.AttentionReason = registration.Message;
            request.AddHistory(RequestStatus.RequiresAttention, "No se pudo registrar el título en Arr.", registration.Message);
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
            return;
        }

        if (request.Kind == MediaKind.Series)
        {
            var allEpisodes = await _sonarrClient.GetEpisodesAsync(request.ArrId!.Value, ct).ConfigureAwait(false);
            if (allEpisodes == null)
            {
                _logger.LogWarning("Sonarr unavailable when fetching episodes for Series {ArrId}", request.ArrId.Value);
                await SaveIfRegisteredAsync(request, hadArrId, ct).ConfigureAwait(false);
                return;
            }

            if (allEpisodes.Count == 0)
            {
                _logger.LogInformation("Sonarr returned 0 episodes for Series {ArrId}; catalog still populating, waiting.", request.ArrId.Value);
                await SaveIfRegisteredAsync(request, hadArrId, ct).ConfigureAwait(false);
                return;
            }

            var now = DateTime.UtcNow;

            if (request.Scope.All)
            {
                // Resolve All to explicit aired missing episodes (N01)
                var searchCutoff = now - DownloadUnit.ReleaseGrace;
                var missingAired = allEpisodes
                    .Where(e => e.SeasonNumber > 0 && !e.HasFile && HasAired(e, allEpisodes, searchCutoff))
                    .Select(e => ToTarget(request.ArrId.Value, e))
                    .ToList();

                var airedWithFiles = allEpisodes.Count(e => e.SeasonNumber > 0 && e.HasFile && HasAired(e, allEpisodes, now));
                if (missingAired.Count == 0 && airedWithFiles == 0 && request.FollowAiring)
                {
                    // Nothing has aired yet: wait for the first episodes instead of searching the whole series
                    var message = "Aún no se ha emitido ningún episodio; se descargarán según se emitan.";
                    if (request.History.LastOrDefault()?.Message != message)
                    {
                        request.AddHistory(RequestStatus.Searching, message);
                    }

                    request.NextFollowCheckUtc = NextFollowCheck(allEpisodes, now);
                    await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
                    return;
                }

                if (missingAired.Count == 0)
                {
                    // All aired episodes are reported with files in Sonarr!
                    // Distinguish catalog, Arr import, and Jellyfin availability (H03).
                    request.Scope.TargetEpisodes = allEpisodes
                        .Where(e => e.SeasonNumber > 0 && HasAired(e, allEpisodes, now))
                        .Select(e => ToTarget(request.ArrId.Value, e))
                        .ToList();

                    request.AddHistory(RequestStatus.Importing, "Todos los episodios emitidos están en Sonarr. Verificando disponibilidad en Jellyfin.");
                    await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
                    await HandleImportingAsync(request, ct).ConfigureAwait(false);
                    return;
                }

                request.Scope.TargetEpisodes = missingAired;
            }
            else if (request.Scope.TargetEpisodes.Count == 0 && (request.Scope.EpisodeIds.Count > 0 || request.Scope.SeasonNumbers.Count > 0))
            {
                // Scope confirmed while Sonarr was unreachable: resolve the selection now instead of searching the whole series
                var selectedIds = request.Scope.EpisodeIds.ToHashSet();
                var selectedSeasons = request.Scope.SeasonNumbers.ToHashSet();
                request.Scope.TargetEpisodes = allEpisodes
                    .Where(e => !e.HasFile && (selectedIds.Contains(e.Id) || selectedSeasons.Contains(e.SeasonNumber)))
                    .Select(e => ToTarget(request.ArrId.Value, e))
                    .ToList();
            }

            // If scope not confirmed and not All, wait for user confirmation
            if (!request.Scope.All && request.Scope.TargetEpisodes.Count == 0)
            {
                var hasSelection = request.Scope.EpisodeIds.Count > 0 || request.Scope.SeasonNumbers.Count > 0;
                if (hasSelection)
                {
                    // Everything selected already has a file in Sonarr: only Jellyfin availability is left to verify
                    var selectedIds = request.Scope.EpisodeIds.ToHashSet();
                    var selectedSeasons = request.Scope.SeasonNumbers.ToHashSet();
                    request.Scope.TargetEpisodes = allEpisodes
                        .Where(e => selectedIds.Contains(e.Id) || selectedSeasons.Contains(e.SeasonNumber))
                        .Select(e => ToTarget(request.ArrId.Value, e))
                        .ToList();
                    request.AddHistory(RequestStatus.Importing, "Los episodios seleccionados ya están en Sonarr. Verificando disponibilidad en Jellyfin.");
                    await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
                    await HandleImportingAsync(request, ct).ConfigureAwait(false);
                    return;
                }

                await SaveIfRegisteredAsync(request, hadArrId, ct).ConfigureAwait(false);
                return;
            }

            // Build units for series
            if (request.Units.Count == 0 && request.Scope.TargetEpisodes.Count > 0)
            {
                var bySeason = request.Scope.TargetEpisodes.GroupBy(t => t.SeasonNumber).OrderBy(g => g.Key);
                foreach (var seasonGroup in bySeason)
                {
                    var isSeasonSelected = request.Scope.All || request.Scope.SeasonNumbers.Contains(seasonGroup.Key);
                    var isAiring = allEpisodes.Any(e => e.SeasonNumber == seasonGroup.Key && (!e.AirDateUtc.HasValue || e.AirDateUtc.Value > now));

                    if (isSeasonSelected && seasonGroup.Count() > 1 && !isAiring)
                    {
                        var ordered = seasonGroup.OrderBy(e => e.EpisodeNumber).ToList();
                        request.Units.Add(new DownloadUnit
                        {
                            SeasonNumber = seasonGroup.Key,
                            EpisodeNumbers = ordered.Select(e => e.EpisodeNumber).ToList(),
                            EpisodeIds = ordered.Select(e => e.EpisodeId).ToList(),
                            AbsoluteEpisodeNumbers = ordered.Where(e => e.AbsoluteEpisodeNumber.HasValue).Select(e => e.AbsoluteEpisodeNumber!.Value).ToList(),
                            IsFullSeasonPack = true,
                            Status = RequestStatus.Pending
                        });
                    }
                    else
                    {
                        foreach (var ep in seasonGroup.OrderBy(e => e.EpisodeNumber))
                        {
                            request.Units.Add(new DownloadUnit
                            {
                                SeasonNumber = ep.SeasonNumber,
                                EpisodeNumbers = [ep.EpisodeNumber],
                                EpisodeIds = [ep.EpisodeId],
                                AbsoluteEpisodeNumbers = ep.AbsoluteEpisodeNumber.HasValue ? [ep.AbsoluteEpisodeNumber.Value] : [],
                                AirDateUtc = ep.AirDateUtc,
                                IsFullSeasonPack = false,
                                Status = RequestStatus.Pending
                            });
                        }
                    }
                }
            }
        }
        else if (request.Kind == MediaKind.Movie)
        {
            if (request.Units.Count == 0)
            {
                request.Units.Add(new DownloadUnit
                {
                    SeasonNumber = 0,
                    IsFullSeasonPack = false,
                    Status = RequestStatus.Pending
                });
            }
        }

        request.AddHistory(RequestStatus.Searching, "Registrado en Arr con búsqueda deshabilitada. Iniciando búsqueda de releases.");
        await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);

        await HandleSearchingAsync(request, ct).ConfigureAwait(false);
    }

    private static EpisodeTarget ToTarget(int seriesId, SonarrEpisode e) => new()
    {
        SeriesId = seriesId,
        EpisodeId = e.Id,
        SeasonNumber = e.SeasonNumber,
        EpisodeNumber = e.EpisodeNumber,
        AbsoluteEpisodeNumber = e.AbsoluteEpisodeNumber,
        Title = e.Title,
        AirDateUtc = e.AirDateUtc
    };

    private async Task SaveIfRegisteredAsync(AcquisitionRequest request, bool hadArrId, CancellationToken ct)
    {
        // Keep a fresh registration even while waiting, so the next poll does not look it up again
        if (!hadArrId && request.ArrId.HasValue)
        {
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleSearchingAsync(AcquisitionRequest request, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;

        // Respect backoff (R12)
        if (request.NextAttemptUtc.HasValue && request.NextAttemptUtc.Value > DateTimeOffset.UtcNow)
        {
            return;
        }

        if (request.Kind == MediaKind.Series && request.FollowAiring && request.Units.Count == 0)
        {
            if (!request.NextFollowCheckUtc.HasValue || request.NextFollowCheckUtc.Value <= DateTimeOffset.UtcNow)
            {
                await HandleFollowAiringAsync(request, ct).ConfigureAwait(false);
            }

            return;
        }

        var activeUnit = request.GetActiveUnit();
        if (activeUnit == null && request.Units.Count > 0)
        {
            // All units completed
            request.AddHistory(RequestStatus.Importing, "Todas las unidades de descarga completadas. Verificando disponibilidad.");
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
            await HandleImportingAsync(request, ct).ConfigureAwait(false);
            return;
        }

        // Only units set aside for review are left: nothing can progress without the user
        if (activeUnit != null && activeUnit.Status == RequestStatus.RequiresAttention)
        {
            await StopWithParkedUnitsAsync(request, ct).ConfigureAwait(false);
            return;
        }

        // An episode that has not aired yet waits for its air date plus the release margin (units are in airing order)
        if (activeUnit != null && activeUnit.Status == RequestStatus.Pending && activeUnit.SelectedRelease == null &&
            activeUnit.SearchableFromUtc > DateTime.UtcNow)
        {
            var searchableFrom = new DateTimeOffset(DateTime.SpecifyKind(activeUnit.SearchableFromUtc, DateTimeKind.Utc));
            request.NextAttemptUtc = searchableFrom;
            var waitMessage = $"Esperando la emisión de {activeUnit.Describe(request.Kind)}: se buscará a partir del {searchableFrom:dd/MM HH:mm} UTC.";
            if (request.History.LastOrDefault()?.Message != waitMessage)
            {
                request.AddHistory(RequestStatus.Searching, waitMessage);
            }

            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
            return;
        }

        // Re-validate existing unit selection before proceeding (H02)
        if (activeUnit != null && activeUnit.SelectedRelease != null)
        {
            var isComp = true;
            if (activeUnit.SeasonNumber > 0 && activeUnit.SelectedRelease.SeasonNumber > 0 && activeUnit.SelectedRelease.SeasonNumber != activeUnit.SeasonNumber)
            {
                isComp = false;
            }
            else if (activeUnit.IsFullSeasonPack && !activeUnit.SelectedRelease.IsSeasonPack)
            {
                isComp = false;
            }
            else if (!activeUnit.IsFullSeasonPack && activeUnit.SelectedRelease.EpisodeNumber > 0 && !activeUnit.EpisodeNumbers.Contains(activeUnit.SelectedRelease.EpisodeNumber))
            {
                isComp = false;
            }

            if (!isComp)
            {
                activeUnit.SelectedRelease = null;
                request.SelectedRelease = null;
            }
        }

        // 1. Find releases if none selected
        var manual = config?.IsManualReleaseSelection() == true && !request.AutoReleaseSelection;
        if (activeUnit != null && activeUnit.SelectedRelease == null)
        {
            // Manual mode: a recent list with eligible releases is already waiting for the user's choice
            if (manual && request.AvailableReleasesUnitId == activeUnit.Id && IsReleaseListFresh(request) && request.AvailableReleases.Any(r => r.IsEligible))
            {
                return;
            }

            var candidates = await SearchOrBackOffAsync(request, () => _releaseService.SearchAndEvaluateReleasesForUnitAsync(request, activeUnit, ct), ct).ConfigureAwait(false);
            if (candidates == null) return;

            // Stored once on the request; per-unit copies only duplicated the persisted list
            activeUnit.AvailableReleases.Clear();
            request.AvailableReleases = candidates;
            request.AvailableReleasesUnitId = activeUnit.Id;
            request.AvailableReleasesUpdatedAt = DateTimeOffset.UtcNow;

            var best = _releaseService.PickBestRelease(candidates);
            if (best == null)
            {
                // If season pack search yields no eligible pack, split into individual episode units (H04)
                if (activeUnit.IsFullSeasonPack && activeUnit.EpisodeNumbers.Count > 1)
                {
                    var unitIndex = request.Units.IndexOf(activeUnit);
                    request.Units.RemoveAt(unitIndex);
                    var newUnits = activeUnit.EpisodeNumbers.OrderBy(epNum => epNum).Select(epNum =>
                    {
                        var epTarget = request.Scope.TargetEpisodes.FirstOrDefault(t => t.SeasonNumber == activeUnit.SeasonNumber && t.EpisodeNumber == epNum);
                        return new DownloadUnit
                        {
                            SeasonNumber = activeUnit.SeasonNumber,
                            EpisodeNumbers = [epNum],
                            EpisodeIds = epTarget != null ? [epTarget.EpisodeId] : [],
                            IsFullSeasonPack = false,
                            Status = RequestStatus.Pending
                        };
                    }).ToList();
                    request.Units.InsertRange(unitIndex, newUnits);
                    request.AddHistory(RequestStatus.Searching, $"No se encontró pack de temporada para T{activeUnit.SeasonNumber}. Dividiendo en descargas de episodios individuales.");
                    await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
                    await HandleSearchingAsync(request, ct).ConfigureAwait(false);
                    return;
                }

                var unitDesc = activeUnit.IsFullSeasonPack ? $"Temporada {activeUnit.SeasonNumber}" : $"S{activeUnit.SeasonNumber:D2}E{(activeUnit.EpisodeNumbers.FirstOrDefault()):D2}";

                // Set the episode aside after a few empty searches when others are waiting; the last one keeps retrying
                activeUnit.SearchAttempts++;
                if (activeUnit.SearchAttempts >= MaxEmptySearches && HasOtherPendingUnit(request, activeUnit))
                {
                    await ParkUnitAsync(
                        request,
                        activeUnit,
                        $"No se encontraron releases válidas tras {MaxEmptySearches} búsquedas.",
                        null,
                        $"No se encontraron releases válidas para {unitDesc}.",
                        clearSelection: true,
                        ct).ConfigureAwait(false);
                    return;
                }

                request.NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                var lastMsg = request.History.LastOrDefault()?.Message;
                var msg = $"No se encontraron releases candidatas válidas para {unitDesc}.";
                if (lastMsg != msg)
                {
                    request.AddHistory(RequestStatus.Searching, msg);
                }
                await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
                return;
            }

            activeUnit.SearchAttempts = 0;
            if (manual)
            {
                await WaitForUserSelectionAsync(request, activeUnit.Describe(request.Kind), ct).ConfigureAwait(false);
                return;
            }

            activeUnit.SelectedRelease = best;
            request.SelectedRelease = best;
            request.AddHistory(RequestStatus.Searching, $"Seleccionada release óptima: {best.Title} (Score: {best.Score})");
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
        }
        else if (request.SelectedRelease == null && activeUnit == null)
        {
            if (manual && request.AvailableReleasesUnitId == null && IsReleaseListFresh(request) && request.AvailableReleases.Any(r => r.IsEligible))
            {
                return;
            }

            var candidates = await SearchOrBackOffAsync(request, () => _releaseService.SearchAndEvaluateReleasesAsync(request, ct), ct).ConfigureAwait(false);
            if (candidates == null) return;

            request.AvailableReleases = candidates;
            request.AvailableReleasesUnitId = null;
            request.AvailableReleasesUpdatedAt = DateTimeOffset.UtcNow;
            var best = _releaseService.PickBestRelease(candidates);
            if (best == null)
            {
                request.NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
                return;
            }

            if (manual)
            {
                await WaitForUserSelectionAsync(request, request.Kind == MediaKind.Movie ? "la película" : "la serie", ct).ConfigureAwait(false);
                return;
            }

            request.SelectedRelease = best;
            request.AddHistory(RequestStatus.Searching, $"Seleccionada release óptima: {best.Title} (Score: {best.Score})");
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
        }

        var selected = activeUnit?.SelectedRelease ?? request.SelectedRelease;
        if (selected == null) return;

        // Re-validate against fatal rejection reasons (N05)
        if (!selected.IsTorrent || selected.RejectionReasons.Any(r => r.Contains("mismatch", StringComparison.OrdinalIgnoreCase)))
        {
            await ParkUnitAsync(
                request,
                activeUnit,
                "Release seleccionada contiene incompatibilidad de identidad o alcance.",
                null,
                "Selected release identity or scope mismatch",
                clearSelection: true,
                ct).ConfigureAwait(false);
            return;
        }

        // 2. Prepare correlation tag and check qBittorrent before adding (R07 & H02)
        request.CorrelationTag = activeUnit?.CorrelationTag ??
            ((activeUnit != null && request.Units.Count > 1)
                ? $"acq-{request.Id}-{activeUnit.Id}"
                : (!string.IsNullOrEmpty(request.CorrelationTag) ? request.CorrelationTag : $"acq-{request.Id}"));

        if (activeUnit != null && string.IsNullOrEmpty(activeUnit.CorrelationTag))
        {
            activeUnit.CorrelationTag = request.CorrelationTag;
        }

        request.TorrentName = selected.Title;

        // Reconcile existing torrents: query qBittorrent by the current unit's active tag
        var existingTorrents = await _qbClient.GetTorrentsInfoAsync(tag: request.CorrelationTag, ct: ct).ConfigureAwait(false)
            ?? new List<QBittorrentTorrentInfo>();
        // Fallback to legacy root tag is ONLY allowed if the active unit inherited that legacy tag or single unit request (J01)
        if (existingTorrents.Count == 0 && (activeUnit?.CorrelationTag == $"acq-{request.Id}" || request.Units.Count <= 1))
        {
            existingTorrents = await _qbClient.GetTorrentsInfoAsync(tag: $"acq-{request.Id}", ct: ct).ConfigureAwait(false)
                ?? new List<QBittorrentTorrentInfo>();
        }

        var knownHash = activeUnit?.TorrentHash ?? request.TorrentHash;
        var selectedHash = selected.InfoHash;
        QBittorrentTorrentInfo? matchedExisting = null;

        if (!string.IsNullOrEmpty(knownHash))
        {
            matchedExisting = existingTorrents.FirstOrDefault(t => string.Equals(t.Hash, knownHash, StringComparison.OrdinalIgnoreCase));
            if (matchedExisting == null)
            {
                var hashTorrents = await _qbClient.GetTorrentsInfoAsync(hash: knownHash, ct: ct).ConfigureAwait(false);
                matchedExisting = hashTorrents?.FirstOrDefault();
            }
        }
        else if (!string.IsNullOrEmpty(selectedHash))
        {
            matchedExisting = existingTorrents.FirstOrDefault(t => string.Equals(t.Hash, selectedHash, StringComparison.OrdinalIgnoreCase));
            if (matchedExisting == null)
            {
                var hashTorrents = await _qbClient.GetTorrentsInfoAsync(hash: selectedHash, ct: ct).ConfigureAwait(false);
                matchedExisting = hashTorrents?.FirstOrDefault();
            }
        }
        else if (existingTorrents.Count > 0)
        {
            var nameMatches = existingTorrents.Where(t => string.Equals(t.Name, selected.Title, StringComparison.OrdinalIgnoreCase)).ToList();
            if (nameMatches.Count == 1)
            {
                matchedExisting = nameMatches[0];
            }
        }

        if (matchedExisting != null)
        {
            request.TorrentHash = matchedExisting.Hash;
            if (activeUnit != null)
            {
                activeUnit.TorrentHash = matchedExisting.Hash;
                activeUnit.Status = RequestStatus.Downloading;
            }
            request.AddHistory(RequestStatus.Downloading, "Descarga previamente enviada a qBittorrent detectada y adoptada.");
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
            return;
        }

        // Resolved only when a new torrent is sent; adopting an existing one does not need it
        if (request.Category == null)
        {
            var category = await ResolveCategoryAsync(request, config, ct).ConfigureAwait(false);
            if (category.Error != null)
            {
                // Without the category Arr monitors, Arr would never see (or import) the download
                request.AttentionReason = category.Error;
                request.AddHistory(RequestStatus.RequiresAttention, "No se pudo determinar la categoría de qBittorrent que supervisa Arr.", category.Error);
                await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
                return;
            }

            request.Category = category.Name;
        }

        // 3. Fetch torrent bytes or magnet via Prowlarr
        var fetchResult = await _prowlarrClient.FetchTorrentAsync(selected.DownloadUrl ?? selected.MagnetUri ?? string.Empty, ct).ConfigureAwait(false);
        if (!fetchResult.IsSuccess)
        {
            // Transient indexer errors are retried with backoff; only repeated failures need the user
            var failures = activeUnit != null ? ++activeUnit.FetchFailures : MaxFetchFailures;
            if (failures < MaxFetchFailures)
            {
                request.NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(2 * failures);
                request.AddHistory(RequestStatus.Searching, $"Error al descargar el torrent desde Prowlarr (intento {failures} de {MaxFetchFailures}). Se reintentará.", fetchResult.ErrorMessage);
            }
            else
            {
                if (activeUnit != null) activeUnit.FetchFailures = 0;
                await ParkUnitAsync(request, activeUnit, "Error al descargar el torrent desde Prowlarr.", fetchResult.ErrorMessage, fetchResult.ErrorMessage, clearSelection: true, ct).ConfigureAwait(false);
                return;
            }

            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
            return;
        }

        if (activeUnit != null) activeUnit.FetchFailures = 0;

        var addOptions = new QBittorrentAddOptions
        {
            TorrentBytes = fetchResult.TorrentBytes,
            MagnetUri = fetchResult.MagnetUri ?? selected.MagnetUri,
            Category = request.Category,
            Tag = request.CorrelationTag,
            Rename = selected.Title
        };

        // 4. Send to qBittorrent
        var added = await _qbClient.AddTorrentAsync(addOptions, ct).ConfigureAwait(false);
        if (!added)
        {
            request.AddHistory(RequestStatus.RequiresAttention, "Fallo al enviar el torrent a qBittorrent.");
            request.AttentionReason = "qBittorrent rejected or failed to add torrent";
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
            return;
        }

        request.TorrentHash = fetchResult.InfoHash ?? selected.InfoHash;
        request.NextAttemptUtc = null;
        if (activeUnit != null)
        {
            activeUnit.TorrentHash = request.TorrentHash;
            activeUnit.Status = RequestStatus.Downloading;
        }
        request.AddHistory(RequestStatus.Downloading, $"Torrent enviado a qBittorrent con categoría '{request.Category}' y etiqueta '{request.CorrelationTag}'.");
        await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<List<ReleaseCandidate>?> SearchOrBackOffAsync(AcquisitionRequest request, Func<Task<List<ReleaseCandidate>>> search, CancellationToken ct)
    {
        try
        {
            return await search().ConfigureAwait(false);
        }
        catch (ProwlarrUnavailableException ex)
        {
            // A Prowlarr failure is not "no releases": keep searching later and say why
            request.NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(5);
            var msg = "No se pudo consultar Prowlarr; se reintentará en 5 minutos.";
            if (request.History.LastOrDefault()?.Message != msg)
            {
                request.AddHistory(RequestStatus.Searching, msg, ex.Message);
            }

            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// The qBittorrent category is the one Arr monitors: the admin override, or the category of the enabled
    /// qBittorrent client in Arr that points to the plugin's qBittorrent. No default name is invented.
    /// </summary>
    private async Task<(string? Name, string? Error)> ResolveCategoryAsync(AcquisitionRequest request, Configuration.PluginConfiguration? config, CancellationToken ct)
    {
        var service = request.Kind == MediaKind.Series ? "Sonarr" : "Radarr";
        var manual = request.Kind == MediaKind.Series ? config?.SonarrCategory : config?.RadarrCategory;
        if (!string.IsNullOrWhiteSpace(manual))
        {
            return (manual.Trim(), null);
        }

        List<ArrDownloadClient> clients;
        try
        {
            clients = request.Kind == MediaKind.Series
                ? await _sonarrClient.GetDownloadClientsAsync(ct).ConfigureAwait(false) ?? []
                : await _radarrClient.GetDownloadClientsAsync(ct).ConfigureAwait(false) ?? [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"No se pudieron leer los clientes de descarga de {service}: {Security.SecurityHelper.RedactSecrets(ex.Message)}");
        }

        var qbClients = clients.Where(c => c.IsEnabledQBittorrent).ToList();
        if (qbClients.Count > 1 && Uri.TryCreate(config?.QBittorrentUrl, UriKind.Absolute, out var qbUri))
        {
            // Several qBittorrent clients: only the one pointing to the plugin's qBittorrent is unambiguous
            qbClients = qbClients.Where(c => c.PointsTo(qbUri)).ToList();
        }

        return qbClients.Count switch
        {
            1 => (qbClients[0].GetCategory() ?? string.Empty, null),
            0 => (null, $"{service} no tiene un cliente qBittorrent habilitado que coincida con la conexión del plugin. Configura la categoría manualmente en el plugin."),
            _ => (null, $"{service} tiene varios clientes qBittorrent y no se puede saber cuál usa el plugin. Configura la categoría manualmente en el plugin.")
        };
    }

    /// <summary>Longest interval between checks of a followed series when no air date is known.</summary>
    private static readonly TimeSpan FollowCheckInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// Whether an episode has aired by <paramref name="cutoff"/>. An episode without a date counts as "to be announced"
    /// only when the series has dates and no later episode has aired; series without any dates are treated as aired.
    /// </summary>
    private static bool HasAired(SonarrEpisode e, IReadOnlyList<SonarrEpisode> all, DateTime cutoff)
    {
        if (e.AirDateUtc.HasValue) return e.AirDateUtc.Value <= cutoff;
        if (!all.Any(x => x.AirDateUtc.HasValue)) return true;
        return all.Any(x => x.AirDateUtc.HasValue && x.AirDateUtc.Value <= cutoff &&
                            (x.SeasonNumber > e.SeasonNumber || (x.SeasonNumber == e.SeasonNumber && x.EpisodeNumber > e.EpisodeNumber)));
    }

    /// <summary>Next check of a followed series: when the next episode becomes searchable, at most every 6 hours.</summary>
    private static DateTimeOffset NextFollowCheck(IReadOnlyList<SonarrEpisode> episodes, DateTime now)
    {
        var latest = new DateTimeOffset(now, TimeSpan.Zero) + FollowCheckInterval;
        var upcoming = episodes
            .Where(e => e.SeasonNumber > 0 && !e.HasFile && e.AirDateUtc.HasValue && e.AirDateUtc.Value + DownloadUnit.ReleaseGrace > now)
            .Select(e => new DateTimeOffset(DateTime.SpecifyKind(e.AirDateUtc!.Value + DownloadUnit.ReleaseGrace, DateTimeKind.Utc)))
            .DefaultIfEmpty(latest)
            .Min();
        var earliest = new DateTimeOffset(now, TimeSpan.Zero) + TimeSpan.FromMinutes(15);
        return upcoming < earliest ? earliest : upcoming > latest ? latest : upcoming;
    }

    /// <summary>
    /// "Serie completa" of an airing series: adds newly aired missing episodes as new downloads, and stops following
    /// once Sonarr reports the series as ended with nothing left to air.
    /// </summary>
    private async Task HandleFollowAiringAsync(AcquisitionRequest request, CancellationToken ct)
    {
        if (!request.ArrId.HasValue) return;

        var now = DateTime.UtcNow;
        var episodes = await _sonarrClient.GetEpisodesAsync(request.ArrId.Value, ct).ConfigureAwait(false);
        if (episodes == null)
        {
            request.NextFollowCheckUtc = DateTimeOffset.UtcNow.AddMinutes(30);
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
            return;
        }

        var known = request.Units.SelectMany(u => u.EpisodeIds).Concat(request.Scope.TargetEpisodes.Select(t => t.EpisodeId)).ToHashSet();
        var cutoff = now - DownloadUnit.ReleaseGrace;
        var newlyAired = episodes
            .Where(e => e.SeasonNumber > 0 && !e.HasFile && !known.Contains(e.Id) && HasAired(e, episodes, cutoff))
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
            .ToList();

        if (newlyAired.Count > 0)
        {
            foreach (var e in newlyAired)
            {
                var target = ToTarget(request.ArrId.Value, e);
                request.Scope.TargetEpisodes.Add(target);
                request.Units.Add(new DownloadUnit
                {
                    SeasonNumber = e.SeasonNumber,
                    EpisodeNumbers = [e.EpisodeNumber],
                    EpisodeIds = [e.Id],
                    AbsoluteEpisodeNumbers = e.AbsoluteEpisodeNumber.HasValue ? [e.AbsoluteEpisodeNumber.Value] : [],
                    AirDateUtc = e.AirDateUtc,
                    Status = RequestStatus.Pending
                });
            }

            request.ImportingStartedAt = null;
            request.CompletedAt = null;
            request.NextFollowCheckUtc = null;
            request.AddHistory(
                RequestStatus.Searching,
                $"Nuevos episodios emitidos: {string.Join(", ", newlyAired.Select(e => $"S{e.SeasonNumber:D2}E{e.EpisodeNumber:D2}"))}. Iniciando búsqueda.");
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
            return;
        }

        var stillToAir = episodes.Any(e => e.SeasonNumber > 0 && !e.HasFile && !HasAired(e, episodes, cutoff));
        if (!stillToAir)
        {
            var series = await _sonarrClient.GetSeriesByIdAsync(request.ArrId.Value, ct).ConfigureAwait(false);
            if (string.Equals(series?.Status, "ended", StringComparison.OrdinalIgnoreCase))
            {
                request.FollowAiring = false;
                request.NextFollowCheckUtc = null;
                request.AddHistory(request.Status, "La serie ha terminado de emitirse; se deja de seguir.");
                await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
                return;
            }
        }

        request.NextFollowCheckUtc = NextFollowCheck(episodes, now);
        await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the plugin's correlation tags that no download needs any more: downloads already imported, and requests
    /// that were cancelled (no longer stored). Tags of downloads still tracked — including ones set aside for review,
    /// which need them to adopt their torrent on retry — are kept. Torrents themselves are never removed.
    /// </summary>
    private async Task CleanupCorrelationTagsAsync(CancellationToken ct)
    {
        if (Plugin.Instance?.Configuration.RemoveTagsAfterImport != true) return;
        if (DateTimeOffset.UtcNow - _lastTagCleanup < TagCleanupInterval) return;
        _lastTagCleanup = DateTimeOffset.UtcNow;

        var tags = await _qbClient.GetTagsAsync(ct).ConfigureAwait(false);
        if (tags == null || tags.Count == 0) return;

        // Read the store again: this poll may have just sent a torrent under a new tag
        var requests = await _requestRepo.GetAllAsync(ct).ConfigureAwait(false) ?? [];
        var inUse = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            var requestDone = request.Status == RequestStatus.Available && request.Units.All(u => u.Status == RequestStatus.Available);
            if (!requestDone)
            {
                // Request-level tags (legacy root tag, last tag used) still matter unless they belong to an imported download
                var importedTags = request.Units.Where(u => u.Status == RequestStatus.Available && !string.IsNullOrEmpty(u.CorrelationTag))
                    .Select(u => u.CorrelationTag!).ToHashSet(StringComparer.Ordinal);
                foreach (var tag in new[] { $"{CorrelationTagPrefix}{request.Id}", request.CorrelationTag })
                {
                    if (!string.IsNullOrEmpty(tag) && !importedTags.Contains(tag)) inUse.Add(tag);
                }
            }

            foreach (var unit in request.Units.Where(u => u.Status != RequestStatus.Available && !string.IsNullOrEmpty(u.CorrelationTag)))
            {
                inUse.Add(unit.CorrelationTag!);
            }
        }

        var obsolete = tags.Where(t => t.StartsWith(CorrelationTagPrefix, StringComparison.Ordinal) && !inUse.Contains(t)).ToList();
        if (obsolete.Count > 0 && await _qbClient.DeleteTagsAsync(obsolete, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Deleted {Count} qBittorrent tags of imported or cancelled downloads", obsolete.Count);
        }
    }

    private static bool HasOtherPendingUnit(AcquisitionRequest request, DownloadUnit unit) =>
        request.Units.Any(u => !ReferenceEquals(u, unit) && u.Status == RequestStatus.Pending);

    private static bool IsReleaseListFresh(AcquisitionRequest request) =>
        request.AvailableReleasesUpdatedAt.HasValue && DateTimeOffset.UtcNow - request.AvailableReleasesUpdatedAt.Value < ManualListRefresh;

    /// <summary>
    /// A failure specific to one download: with another download waiting, this one is set aside for review and the
    /// request moves on (user decision: sequential, non-blocking). Without one, the request needs attention as before.
    /// </summary>
    private async Task ParkUnitAsync(
        AcquisitionRequest request,
        DownloadUnit? unit,
        string message,
        string? details,
        string? attentionReason,
        bool clearSelection,
        CancellationToken ct)
    {
        var reason = attentionReason ?? details ?? message;
        var next = unit == null ? null : request.Units.FirstOrDefault(u => !ReferenceEquals(u, unit) && u.Status == RequestStatus.Pending);
        if (unit != null)
        {
            unit.Status = RequestStatus.RequiresAttention;
            unit.ErrorMessage = reason;
            if (clearSelection) unit.SelectedRelease = null;
        }

        if (next == null)
        {
            if (clearSelection) request.SelectedRelease = null;
            request.AttentionReason = reason;
            request.AddHistory(RequestStatus.RequiresAttention, message, details);
        }
        else
        {
            request.SelectedRelease = null;
            request.TorrentHash = null;
            request.AvailableReleases = [];
            request.AvailableReleasesUnitId = null;
            request.NextAttemptUtc = null;
            request.AttentionReason = null;
            request.AddHistory(
                RequestStatus.Searching,
                $"{unit!.Describe(request.Kind)} apartado para revisión: {message} Se continúa con {next.Describe(request.Kind)}.",
                details);
        }

        await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
    }

    private async Task StopWithParkedUnitsAsync(AcquisitionRequest request, CancellationToken ct)
    {
        var parked = request.Units.Where(u => u.Status == RequestStatus.RequiresAttention).ToList();
        request.AttentionReason = string.Join("; ", parked.Select(u => $"{u.Describe(request.Kind)}: {u.ErrorMessage}"));
        request.AddHistory(
            RequestStatus.RequiresAttention,
            $"{parked.Count} de {request.Units.Count} descargas requieren atención; el resto ha terminado. Usa «Reintentar» para volver a intentarlas.");
        await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Manual selection mode: the evaluated list is stored and the tracker waits for the user's choice.</summary>
    private async Task WaitForUserSelectionAsync(AcquisitionRequest request, string what, CancellationToken ct)
    {
        var message = $"Releases listas para {what}: elige una en «Seleccionar release».";
        if (request.History.LastOrDefault()?.Message != message)
        {
            request.AddHistory(RequestStatus.Searching, message);
        }

        await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<bool> CheckQueueDiagnosticsAsync(AcquisitionRequest request, DownloadUnit? activeUnit, CancellationToken ct)
    {
        var targetHash = activeUnit?.TorrentHash ?? request.TorrentHash;
        var targetTag = activeUnit?.CorrelationTag ?? request.CorrelationTag;
        var rootTag = $"acq-{request.Id}";

        if (request.Kind == MediaKind.Series)
        {
            List<SonarrQueueItem>? queue = null;
            try
            {
                queue = await _sonarrClient.GetQueueAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get Sonarr queue for request {Id}", request.Id);
                return false;
            }

            if (queue == null) return false;

            var queueItem = queue.FirstOrDefault(q =>
                (!string.IsNullOrEmpty(targetHash) && string.Equals(q.DownloadId, targetHash, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(q.Title) && !string.IsNullOrEmpty(targetTag) && q.Title.Contains(targetTag, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(q.Title) && q.Title.Contains(rootTag, StringComparison.OrdinalIgnoreCase)));

            if (queueItem != null)
            {
                if (queueItem.TrackedDownloadStatus is "warning" or "error" ||
                    queueItem.TrackedDownloadState is "importBlocked" or "failed" ||
                    queueItem.StatusMessages.Count > 0)
                {
                    var msg = queueItem.StatusMessages.FirstOrDefault()?.Messages?.FirstOrDefault()
                        ?? queueItem.ErrorMessage
                        ?? "Arr import blocked";
                    await ParkUnitAsync(request, activeUnit, "Descarga detenida o con advertencia en Sonarr.", msg, msg, clearSelection: false, ct).ConfigureAwait(false);
                    return true;
                }

                if (request.ArrId.HasValue && queueItem.SeriesId.HasValue && queueItem.SeriesId.Value != 0 && queueItem.SeriesId.Value != request.ArrId.Value)
                {
                    await ParkUnitAsync(request, activeUnit, "La descarga fue asociada a una serie diferente en Sonarr.", $"Series ID inesperado: {queueItem.SeriesId}", "Arr queue association mismatch", clearSelection: false, ct).ConfigureAwait(false);
                    return true;
                }
            }
        }
        else if (request.Kind == MediaKind.Movie)
        {
            List<RadarrQueueItem>? queue = null;
            try
            {
                queue = await _radarrClient.GetQueueAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get Radarr queue for request {Id}", request.Id);
                return false;
            }

            if (queue == null) return false;

            var queueItem = queue.FirstOrDefault(q =>
                (!string.IsNullOrEmpty(targetHash) && string.Equals(q.DownloadId, targetHash, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(q.Title) && !string.IsNullOrEmpty(targetTag) && q.Title.Contains(targetTag, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(q.Title) && q.Title.Contains(rootTag, StringComparison.OrdinalIgnoreCase)));

            if (queueItem != null)
            {
                if (queueItem.TrackedDownloadStatus is "warning" or "error" ||
                    queueItem.TrackedDownloadState is "importBlocked" or "failed" ||
                    queueItem.StatusMessages.Count > 0)
                {
                    var msg = queueItem.StatusMessages.FirstOrDefault()?.Messages?.FirstOrDefault()
                        ?? queueItem.ErrorMessage
                        ?? "Arr import blocked";
                    await ParkUnitAsync(request, activeUnit, "Descarga detenida o con advertencia en Radarr.", msg, msg, clearSelection: false, ct).ConfigureAwait(false);
                    return true;
                }

                if (request.ArrId.HasValue && queueItem.MovieId.HasValue && queueItem.MovieId.Value != 0 && queueItem.MovieId.Value != request.ArrId.Value)
                {
                    await ParkUnitAsync(request, activeUnit, "La descarga fue asociada a una película diferente en Radarr.", $"Movie ID inesperado: {queueItem.MovieId}", "Arr queue association mismatch", clearSelection: false, ct).ConfigureAwait(false);
                    return true;
                }
            }
        }

        return false;
    }

    private async Task HandleDownloadingAsync(AcquisitionRequest request, CancellationToken ct)
    {
        var activeUnit = request.GetActiveUnit();
        var targetHash = activeUnit?.TorrentHash ?? request.TorrentHash;
        var targetTag = activeUnit?.CorrelationTag ?? request.CorrelationTag;

        // 1. Reconcile CDH queue diagnostics (H05)
        if (await CheckQueueDiagnosticsAsync(request, activeUnit, ct).ConfigureAwait(false))
        {
            return;
        }

        // 2. Check torrent in qBittorrent
        var torrents = await _qbClient.GetTorrentsInfoAsync(
            category: request.Category,
            tag: targetTag,
            hash: targetHash,
            ct: ct).ConfigureAwait(false) ?? new List<QBittorrentTorrentInfo>();

        if (torrents.Count == 0 && !string.IsNullOrEmpty(targetTag))
        {
            torrents = await _qbClient.GetTorrentsInfoAsync(tag: targetTag, ct: ct).ConfigureAwait(false) ?? new List<QBittorrentTorrentInfo>();
        }

        QBittorrentTorrentInfo? torrent = null;
        if (!string.IsNullOrEmpty(targetHash))
        {
            torrent = torrents.FirstOrDefault(t => string.Equals(t.Hash, targetHash, StringComparison.OrdinalIgnoreCase));
        }
        else if (torrents.Count == 1)
        {
            torrent = torrents[0];
        }
        else if (torrents.Count > 1)
        {
            // Avoid blindly adopting the first torrent when multiple torrents share the tag (K01)
            var targetName = activeUnit?.TorrentName ?? request.TorrentName ?? activeUnit?.SelectedRelease?.Title ?? request.SelectedRelease?.Title;
            if (!string.IsNullOrEmpty(targetName))
            {
                var matches = torrents.Where(t => string.Equals(t.Name, targetName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1)
                {
                    torrent = matches[0];
                }
                else
                {
                    _logger.LogWarning("Multiple torrents found for tag {Tag} and cannot unambiguously match '{TargetName}'. Avoiding blind adoption.", targetTag, targetName);
                }
            }
            else
            {
                _logger.LogWarning("Multiple torrents found for tag {Tag} without hash or name evidence. Avoiding blind adoption.", targetTag);
            }
        }

        // If torrent not found in qBittorrent, check Arr queue for pending import or history before giving up (N06)
        if (torrent == null)
        {
            if (request.Kind == MediaKind.Series)
            {
                var queue = await _sonarrClient.GetQueueAsync(ct).ConfigureAwait(false);
                var queueItem = queue?.FirstOrDefault(q =>
                    (!string.IsNullOrEmpty(targetHash) && string.Equals(q.DownloadId, targetHash, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(q.Title) && !string.IsNullOrEmpty(targetTag) && q.Title.Contains(targetTag, StringComparison.OrdinalIgnoreCase)));

                if (queueItem != null && queueItem.TrackedDownloadState is "importPending" or "importing")
                {
                    if (activeUnit != null) activeUnit.Status = RequestStatus.Importing;
                    request.AddHistory(RequestStatus.Importing, "Descarga en cola de Sonarr lista para importación.");
                    await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
                    return;
                }

                if (!string.IsNullOrEmpty(targetHash))
                {
                    var history = await _sonarrClient.GetHistoryAsync(targetHash, ct).ConfigureAwait(false);
                    if (history.Any(h => h.EventType != null && (h.EventType.Contains("imported", StringComparison.OrdinalIgnoreCase) || h.EventType.Contains("downloadFolderImported", StringComparison.OrdinalIgnoreCase))))
                    {
                        if (activeUnit != null) activeUnit.Status = RequestStatus.Importing;
                        request.AddHistory(RequestStatus.Importing, "Descarga importada y retirada confirmada por el historial de Sonarr.");
                        await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
                        return;
                    }
                }
            }
            else if (request.Kind == MediaKind.Movie)
            {
                var queue = await _radarrClient.GetQueueAsync(ct).ConfigureAwait(false);
                var queueItem = queue?.FirstOrDefault(q =>
                    (!string.IsNullOrEmpty(targetHash) && string.Equals(q.DownloadId, targetHash, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(q.Title) && !string.IsNullOrEmpty(targetTag) && q.Title.Contains(targetTag, StringComparison.OrdinalIgnoreCase)));

                if (queueItem != null && queueItem.TrackedDownloadState is "importPending" or "importing")
                {
                    if (activeUnit != null) activeUnit.Status = RequestStatus.Importing;
                    request.AddHistory(RequestStatus.Importing, "Descarga en cola de Radarr lista para importación.");
                    await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
                    return;
                }

                if (!string.IsNullOrEmpty(targetHash))
                {
                    var history = await _radarrClient.GetHistoryAsync(targetHash, ct).ConfigureAwait(false);
                    if (history.Any(h => h.EventType != null && (h.EventType.Contains("imported", StringComparison.OrdinalIgnoreCase) || h.EventType.Contains("downloadFolderImported", StringComparison.OrdinalIgnoreCase))))
                    {
                        if (activeUnit != null) activeUnit.Status = RequestStatus.Importing;
                        request.AddHistory(RequestStatus.Importing, "Descarga importada y retirada confirmada por el historial de Radarr.");
                        await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
                        return;
                    }
                }
            }

            _logger.LogWarning("Torrent with tag {Tag} / hash {Hash} not yet found in qBittorrent or Arr queue", targetTag, targetHash);
            return;
        }

        if (string.IsNullOrEmpty(request.TorrentHash))
        {
            request.TorrentHash = torrent.Hash;
            if (activeUnit != null) activeUnit.TorrentHash = torrent.Hash;
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
        }

        // 3. Check completion
        if (torrent.IsCompleted)
        {
            if (activeUnit != null)
            {
                activeUnit.Status = RequestStatus.Importing;
                activeUnit.ContentPath = torrent.ContentPath ?? torrent.SavePath;
                activeUnit.ImportWaitStartedAt ??= DateTimeOffset.UtcNow;
            }
            request.AddHistory(RequestStatus.Importing, "Descarga completada en qBittorrent. Esperando importación por Arr.");
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleImportingAsync(AcquisitionRequest request, CancellationToken ct)
    {
        var activeUnit = request.GetActiveUnit();

        // Reconcile CDH queue diagnostics during importing (H05)
        if (await CheckQueueDiagnosticsAsync(request, activeUnit, ct).ConfigureAwait(false))
        {
            return;
        }

        var isUnitImportedInArr = false;

        if (request.Kind == MediaKind.Series && request.ArrId.HasValue)
        {
            var episodes = await _sonarrClient.GetEpisodesAsync(request.ArrId.Value, ct).ConfigureAwait(false);
            if (episodes == null)
            {
                _logger.LogWarning("Sonarr returned error when fetching episodes for Series {ArrId}", request.ArrId.Value);
                return; // Keep waiting in Importing, do NOT complete! (N02)
            }

            var epMap = episodes.ToDictionary(e => e.Id);

            if (activeUnit != null && activeUnit.EpisodeIds.Count > 0)
            {
                isUnitImportedInArr = activeUnit.EpisodeIds.All(id => epMap.TryGetValue(id, out var ep) && ep.HasFile);
            }
            else if (request.Scope.TargetEpisodes.Count > 0)
            {
                isUnitImportedInArr = request.Scope.TargetEpisodes.All(t => epMap.TryGetValue(t.EpisodeId, out var ep) && ep.HasFile);
            }
            else if (request.Scope.EpisodeNumbers.Count > 0)
            {
                var targetSeason = request.Scope.SeasonNumbers.FirstOrDefault(1);
                var targetEps = request.Scope.EpisodeNumbers.ToHashSet();
                var matching = episodes.Where(e => e.SeasonNumber == targetSeason && targetEps.Contains(e.EpisodeNumber)).ToList();
                isUnitImportedInArr = matching.Count == targetEps.Count && matching.All(e => e.HasFile);
            }
            else if (request.Scope.SeasonNumbers.Count > 0)
            {
                var targetSeason = request.Scope.SeasonNumbers[0];
                var seasonEps = episodes.Where(e => e.SeasonNumber == targetSeason).ToList();
                isUnitImportedInArr = seasonEps.Count > 0 && seasonEps.All(e => e.HasFile);
            }
            else
            {
                isUnitImportedInArr = episodes.Count > 0 && episodes.Any(e => e.HasFile);
            }
        }
        else if (request.Kind == MediaKind.Movie && request.ArrId.HasValue)
        {
            var movie = await _radarrClient.GetMovieByIdAsync(request.ArrId.Value, ct).ConfigureAwait(false);
            isUnitImportedInArr = movie?.HasFile ?? false;
        }

        // If not yet imported in Arr, keep waiting for CDH, then try a manual import, then ask the user
        if (!isUnitImportedInArr)
        {
            await HandlePendingArrImportAsync(request, activeUnit, ct).ConfigureAwait(false);
            return;
        }

        // Active unit completed in Arr!
        if (activeUnit != null)
        {
            activeUnit.Status = RequestStatus.Available;
            activeUnit.CompletedAt = DateTimeOffset.UtcNow;
        }

        // Are there more units to download? (N01, H02, H06, J02)
        var remainingUnit = request.Units.FirstOrDefault(u => u.Status == RequestStatus.Pending);
        if (remainingUnit != null)
        {
            remainingUnit.Status = RequestStatus.Searching;
            remainingUnit.SelectedRelease = null;
            remainingUnit.TorrentHash = null;
            request.SelectedRelease = null;
            request.TorrentHash = null;
            request.AvailableReleases.Clear();
            request.AvailableReleasesUnitId = null;
            request.AddHistory(RequestStatus.Searching, $"Unidad anterior importada. Iniciando búsqueda para siguiente unidad: {remainingUnit.Describe(request.Kind)}.");
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
            await HandleSearchingAsync(request, ct).ConfigureAwait(false);
            return;
        }

        // Everything else finished, but some downloads were set aside for review
        if (request.Units.Any(u => u.Status == RequestStatus.RequiresAttention))
        {
            await StopWithParkedUnitsAsync(request, ct).ConfigureAwait(false);
            return;
        }

        // 2. Separate Arr import from Jellyfin availability (N02 & N07)
        var localMatch = _localMatcher.FindLocalMatch(request.UserId, request.Kind, request.Title, request.Year, request.ProviderIds);
        var episodesAvailable = true;
        if (request.Kind == MediaKind.Series && localMatch.HasValue)
        {
            episodesAvailable = _localMatcher.AreEpisodesAvailable(request.UserId, localMatch.Value, request.Scope.TargetEpisodes);
        }

        if (!localMatch.HasValue || !episodesAvailable)
        {
            // Arr has the file, but Jellyfin has not detected it yet.
            if (!request.ImportingStartedAt.HasValue)
            {
                request.ImportingStartedAt = DateTimeOffset.UtcNow;
            }
            else if (DateTimeOffset.UtcNow - request.ImportingStartedAt.Value > TimeSpan.FromMinutes(15))
            {
                request.AddHistory(RequestStatus.RequiresAttention, "Archivos importados por Arr, pero no detectados por Jellyfin tras 15 minutos.");
                request.AttentionReason = "Media files imported by Arr but not detected in Jellyfin library. Please verify library paths.";
                await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
                return;
            }

            // Non-blocking throttled library refresh (N07)
            await RequestLibraryRefreshAsync(request, ct).ConfigureAwait(false);

            var lastHistory = request.History.LastOrDefault()?.Message;
            if (lastHistory != "Archivos importados por Arr. Esperando detección en biblioteca de Jellyfin...")
            {
                request.AddHistory(RequestStatus.Importing, "Archivos importados por Arr. Esperando detección en biblioteca de Jellyfin...");
                await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
            }
            return;
        }

        // 3. Jellyfin confirmed file presence and accessibility!
        request.LocalItemId = localMatch.Value;
        request.CompletedAt = DateTimeOffset.UtcNow;
        request.AddHistory(RequestStatus.Available, "Contenido importado y confirmado disponible en Jellyfin.");
        if (request.FollowAiring)
        {
            // Check right away: episodes may have aired while these were downloading
            request.NextFollowCheckUtc = null;
            request.AddHistory(RequestStatus.Available, "Siguiendo la emisión: los próximos episodios se descargarán según se emitan.");
        }

        await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
    }

    private async Task HandlePendingArrImportAsync(AcquisitionRequest request, DownloadUnit? unit, CancellationToken ct)
    {
        // Nothing was downloaded by the plugin for this unit (e.g. files already in Arr): just keep waiting
        if (unit == null || string.IsNullOrEmpty(unit.TorrentHash ?? request.TorrentHash) && string.IsNullOrEmpty(unit.ContentPath))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!unit.ImportWaitStartedAt.HasValue)
        {
            unit.ImportWaitStartedAt = now;
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
            return;
        }

        var waited = now - unit.ImportWaitStartedAt.Value;
        if (waited >= ArrImportTimeout)
        {
            var service = request.Kind == MediaKind.Series ? "Sonarr" : "Radarr";
            var reason = $"{service} no ha importado la descarga tras {(int)ArrImportTimeout.TotalMinutes} minutos. Revisa que la categoría '{request.Category}' sea la que supervisa {service} y su cola de actividad.";
            await ParkUnitAsync(request, unit, "La importación en Arr no se completó a tiempo.", reason, reason, clearSelection: false, ct).ConfigureAwait(false);
            return;
        }

        if (waited >= ManualImportDelay && !unit.ManualImportRequestedAt.HasValue && !string.IsNullOrEmpty(unit.ContentPath) && request.ArrId.HasValue)
        {
            unit.ManualImportRequestedAt = now;
            var started = await TryManualImportAsync(request, unit, ct).ConfigureAwait(false);
            request.AddHistory(
                RequestStatus.Importing,
                started
                    ? "Arr no importó la descarga automáticamente; se solicitó una importación manual."
                    : "Arr no importó la descarga automáticamente y no ofreció archivos importables para este título.");
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks Arr for manual import candidates in the downloaded folder and imports only files that Arr matched
    /// to this request's series/movie without rejections.
    /// </summary>
    private async Task<bool> TryManualImportAsync(AcquisitionRequest request, DownloadUnit unit, CancellationToken ct)
    {
        var folder = unit.ContentPath!;
        if (request.Kind == MediaKind.Series)
        {
            var wanted = unit.EpisodeIds.ToHashSet();
            var candidates = (await _sonarrClient.GetManualImportCandidatesAsync(folder, ct).ConfigureAwait(false))
                .Where(c => c.Rejections.Count == 0 &&
                            c.Series?.Id == request.ArrId &&
                            c.Episodes.Count > 0 &&
                            (wanted.Count == 0 || c.Episodes.All(e => wanted.Contains(e.Id))))
                .ToList();
            return candidates.Count > 0 && await _sonarrClient.ExecuteManualImportAsync(candidates, ct).ConfigureAwait(false);
        }

        var movieCandidates = (await _radarrClient.GetManualImportCandidatesAsync(folder, ct).ConfigureAwait(false))
            .Where(c => c.Rejections.Count == 0 && c.Movie?.Id == request.ArrId)
            .ToList();
        return movieCandidates.Count > 0 && await _radarrClient.ExecuteManualImportAsync(movieCandidates, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks Jellyfin to look at the imported folder; falls back to a throttled full library scan when the
    /// Arr path is not visible from Jellyfin (e.g. different container mounts).
    /// </summary>
    private async Task RequestLibraryRefreshAsync(AcquisitionRequest request, CancellationToken ct)
    {
        string? arrPath = null;
        if (_libraryMonitor != null && request.ArrId.HasValue)
        {
            try
            {
                arrPath = request.Kind == MediaKind.Series
                    ? (await _sonarrClient.GetSeriesByIdAsync(request.ArrId.Value, ct).ConfigureAwait(false))?.Path
                    : (await _radarrClient.GetMovieByIdAsync(request.ArrId.Value, ct).ConfigureAwait(false))?.Path;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not read Arr path for request {Id}", request.Id);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var waitingForJellyfin = request.ImportingStartedAt.HasValue ? now - request.ImportingStartedAt.Value : TimeSpan.Zero;
        if (_libraryMonitor != null && !string.IsNullOrEmpty(arrPath) && Directory.Exists(arrPath) && waitingForJellyfin < FullScanAfter)
        {
            if (!_reportedPaths.TryGetValue(arrPath, out var reportedAt) || now - reportedAt >= PathReportInterval)
            {
                _reportedPaths[arrPath] = now;
                _libraryMonitor.ReportFileSystemChanged(arrPath);
            }

            return;
        }

        lock (_libraryScanLock)
        {
            if (DateTimeOffset.UtcNow - _lastLibraryValidationRequested < LibraryScanInterval)
            {
                return;
            }

            _lastLibraryValidationRequested = DateTimeOffset.UtcNow;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background library validation error");
            }
        });
    }

    public async Task<RetryResult> CancelRequestAsync(string requestId, Func<AcquisitionRequest, bool> canManage, CancellationToken ct = default)
    {
        using (await AcquireLockAsync(requestId, ct).ConfigureAwait(false))
        {
            var request = await _requestRepo.GetByIdAsync(requestId, ct).ConfigureAwait(false);
            if (request == null)
            {
                return new RetryResult(RetryOutcome.NotFound, null);
            }

            if (!canManage(request))
            {
                return new RetryResult(RetryOutcome.Forbidden, request);
            }

            if (request.Status == RequestStatus.Available && !request.FollowAiring)
            {
                return new RetryResult(RetryOutcome.InvalidStatus, request, "La solicitud ya está disponible; no hay nada que cancelar.");
            }

            // Tombstone first: a poll that loaded the request before the delete must not save it back
            _cancelledRequests[requestId] = DateTimeOffset.UtcNow;
            await _requestRepo.DeleteAsync(requestId, ct).ConfigureAwait(false);
            _logger.LogInformation("Acquisition request {Id} ({Title}) cancelled", request.Id, request.Title);
            return new RetryResult(RetryOutcome.Success, request);
        }
    }

    public async Task<RetryResult> RetryRequestAsync(string requestId, Func<AcquisitionRequest, bool> canManage, CancellationToken ct = default)
    {
        using (await AcquireLockAsync(requestId, ct).ConfigureAwait(false))
        {
            var request = await _requestRepo.GetByIdAsync(requestId, ct).ConfigureAwait(false);
            if (request == null)
            {
                return new RetryResult(RetryOutcome.NotFound, null);
            }

            if (!canManage(request))
            {
                return new RetryResult(RetryOutcome.Forbidden, request);
            }

            var parked = request.Units.Where(u => u.Status is RequestStatus.RequiresAttention or RequestStatus.Failed).ToList();
            var requestStopped = request.Status is RequestStatus.RequiresAttention or RequestStatus.Failed;
            if (!requestStopped && parked.Count == 0)
            {
                return new RetryResult(RetryOutcome.InvalidStatus, request, "Only requests requiring attention or failed can be retried.");
            }

            // Every download set aside goes back to the queue, not just the first one
            foreach (var unit in parked)
            {
                unit.Status = RequestStatus.Pending;
                unit.ErrorMessage = null;
                unit.FetchFailures = 0;
                unit.SearchAttempts = 0;
                unit.ImportWaitStartedAt = null;
                unit.ManualImportRequestedAt = null;
            }

            if (!requestStopped)
            {
                // The request is still progressing: requeue the parked downloads without interrupting it
                request.AddHistory(request.Status, $"Se vuelven a poner en cola {parked.Count} descargas apartadas.");
                await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
                return new RetryResult(RetryOutcome.Success, request);
            }

            request.AttentionReason = null;
            request.ErrorMessage = null;
            request.NextAttemptUtc = null;
            request.ImportingStartedAt = null; // the Jellyfin detection window starts again

            request.AddHistory(RequestStatus.Pending, "Reintento de solicitud solicitado manualmente.");
            await _requestRepo.SaveOrUpdateAsync(request, ct).ConfigureAwait(false);
            return new RetryResult(RetryOutcome.Success, request);
        }
    }
}
