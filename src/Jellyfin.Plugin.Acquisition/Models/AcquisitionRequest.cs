namespace Jellyfin.Plugin.Acquisition.Models;

/// <summary>
/// Core acquisition request entity persisted in local JSON storage.
/// </summary>
public class AcquisitionRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public Guid UserId { get; set; }
    public MediaKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public ProviderIds ProviderIds { get; set; } = new();

    /// <summary>
    /// ID assigned by Sonarr (series) or Radarr (movie).
    /// </summary>
    public int? ArrId { get; set; }

    /// <summary>
    /// Selected destination name (e.g. "Anime", "Series", "Películas") and resolved Arr settings.
    /// </summary>
    public string? DestinationId { get; set; }
    public string? DestinationName { get; set; }
    public string? RootFolderPath { get; set; }
    public int? QualityProfileId { get; set; }
    public string? SeriesType { get; set; }

    public RequestScope Scope { get; set; } = new();

    /// <summary>
    /// "Serie completa" of a series: after the aired episodes are available, keep checking Sonarr and download
    /// newly aired episodes until the series ends or the request is cancelled (user decision).
    /// </summary>
    public bool FollowAiring { get; set; }

    /// <summary>Next time a followed series is checked for newly aired episodes (null = as soon as possible).</summary>
    public DateTimeOffset? NextFollowCheckUtc { get; set; }

    /// <summary>A request still counts as active while it follows an airing series, even if everything so far is available.</summary>
    public bool IsActive => Status is not (RequestStatus.Available or RequestStatus.Failed) || (Status == RequestStatus.Available && FollowAiring);
    public List<DownloadUnit> Units { get; set; } = [];
    public RequestStatus Status { get; set; } = RequestStatus.Pending;

    /// <summary>
    /// Unique correlation tag attached to qBittorrent and used for Arr queue matching.
    /// E.g. "acq-{id}"
    /// </summary>
    public string CorrelationTag { get; set; } = string.Empty;

    public string? TorrentHash { get; set; }
    public string? TorrentName { get; set; }
    public string? Category { get; set; }

    public ReleaseCandidate? SelectedRelease { get; set; }
    public List<ReleaseCandidate> AvailableReleases { get; set; } = [];
    public string? AvailableReleasesUnitId { get; set; }
    public DateTimeOffset? AvailableReleasesUpdatedAt { get; set; }

    public string? ErrorMessage { get; set; }
    public string? AttentionReason { get; set; }
    public Guid? LocalItemId { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ImportingStartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public List<RequestHistoryEntry> History { get; set; } = [];

    /// <summary>
    /// Returns the unit that is currently active or in progress.
    /// Prioritizes units in Importing, Downloading or Searching, then Pending (J02). Units set aside because they
    /// need attention come last, so one failing episode does not block the others (user decision: sequential, non-blocking).
    /// </summary>
    public DownloadUnit? GetActiveUnit()
    {
        if (Units.Count == 0) return null;
        return Units.FirstOrDefault(u => u.Status == RequestStatus.Importing)
            ?? Units.FirstOrDefault(u => u.Status == RequestStatus.Downloading)
            ?? Units.FirstOrDefault(u => u.Status == RequestStatus.Searching)
            ?? Units.FirstOrDefault(u => u.Status == RequestStatus.Pending)
            ?? Units.FirstOrDefault(u => u.Status != RequestStatus.Available);
    }

    /// <summary>
    /// Maximum number of history entries kept per request; older entries are discarded to bound the store size.
    /// </summary>
    public const int MaxHistoryEntries = 200;

    public void AddHistory(RequestStatus status, string message, string? details = null)
    {
        Status = status;
        UpdatedAt = DateTimeOffset.UtcNow;
        History.Add(new RequestHistoryEntry
        {
            Timestamp = UpdatedAt,
            Status = status,
            Message = Security.SecurityHelper.RedactSecrets(message),
            Details = details != null ? Security.SecurityHelper.RedactSecrets(details) : null
        });

        if (History.Count > MaxHistoryEntries)
        {
            History.RemoveRange(0, History.Count - MaxHistoryEntries);
        }
    }
}
