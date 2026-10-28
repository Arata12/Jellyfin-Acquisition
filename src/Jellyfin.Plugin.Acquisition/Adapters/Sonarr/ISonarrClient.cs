using Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;

namespace Jellyfin.Plugin.Acquisition.Adapters.Sonarr;

public interface ISonarrClient
{
    Task<List<SonarrSeries>> LookupSeriesAsync(string term, CancellationToken ct = default);
    Task<SonarrLookupResult> LookupSeriesWithDiagnosticsAsync(string term, CancellationToken ct = default);
    Task<SonarrSeries?> GetSeriesByIdAsync(int id, CancellationToken ct = default);
    Task<SonarrSeries?> FindSeriesByTvdbIdAsync(int tvdbId, CancellationToken ct = default);
    Task<SonarrSeries> AddSeriesAsync(SonarrSeries series, CancellationToken ct = default);
    Task<List<SonarrEpisode>?> GetEpisodesAsync(int seriesId, CancellationToken ct = default);
    Task<SonarrParseResult?> ParseAsync(string title, CancellationToken ct = default);
    Task<List<SonarrQueueItem>?> GetQueueAsync(CancellationToken ct = default);
    Task<List<SonarrHistoryItem>> GetHistoryAsync(string? downloadId = null, CancellationToken ct = default);
    Task<List<SonarrManualImportCandidate>> GetManualImportCandidatesAsync(string folderPath, CancellationToken ct = default);
    Task<bool> ExecuteManualImportAsync(IEnumerable<SonarrManualImportCandidate> candidates, CancellationToken ct = default);
    Task<List<SonarrRootFolder>> GetRootFoldersAsync(CancellationToken ct = default);
    Task<List<SonarrQualityProfile>> GetQualityProfilesAsync(CancellationToken ct = default);
    Task<List<ArrDownloadClient>> GetDownloadClientsAsync(CancellationToken ct = default);
}
