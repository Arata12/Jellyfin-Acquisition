using Jellyfin.Plugin.Acquisition.Adapters.Radarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;

namespace Jellyfin.Plugin.Acquisition.Adapters.Radarr;

public interface IRadarrClient
{
    Task<List<RadarrMovie>> LookupMovieAsync(string term, CancellationToken ct = default);
    Task<RadarrLookupResult> LookupMovieWithDiagnosticsAsync(string term, CancellationToken ct = default);
    Task<RadarrMovie?> GetMovieByIdAsync(int id, CancellationToken ct = default);
    Task<RadarrMovie?> FindMovieByTmdbIdAsync(int tmdbId, CancellationToken ct = default);
    Task<RadarrMovie> AddMovieAsync(RadarrMovie movie, CancellationToken ct = default);
    Task<RadarrParseResult?> ParseAsync(string title, CancellationToken ct = default);
    Task<List<RadarrQueueItem>?> GetQueueAsync(CancellationToken ct = default);
    Task<List<RadarrHistoryItem>> GetHistoryAsync(string? downloadId = null, CancellationToken ct = default);
    Task<List<RadarrManualImportCandidate>> GetManualImportCandidatesAsync(string folderPath, CancellationToken ct = default);
    Task<bool> ExecuteManualImportAsync(IEnumerable<RadarrManualImportCandidate> candidates, CancellationToken ct = default);
    Task<List<RadarrRootFolder>> GetRootFoldersAsync(CancellationToken ct = default);
    Task<List<RadarrQualityProfile>> GetQualityProfilesAsync(CancellationToken ct = default);
    Task<List<ArrDownloadClient>> GetDownloadClientsAsync(CancellationToken ct = default);
}
