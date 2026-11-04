using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Tracking;

public enum RetryOutcome
{
    Success,
    NotFound,
    Forbidden,
    InvalidStatus
}

public record RetryResult(RetryOutcome Outcome, AcquisitionRequest? Request, string? ErrorMessage = null);

public enum AddScopeOutcome
{
    Added,
    AlreadyCovered,
    NotFound,
    Forbidden,
    Invalid,
    Unavailable
}

public record AddScopeResult(AddScopeOutcome Outcome, AcquisitionRequest? Request, string? Message = null);

public interface ITrackerService
{
    Task<IDisposable> AcquireLockAsync(string requestId, CancellationToken ct = default);
    Task ProcessRequestAsync(AcquisitionRequest request, CancellationToken ct = default);
    Task PollActiveRequestsAsync(CancellationToken ct = default);
    Task<RetryResult> RetryRequestAsync(string requestId, Func<AcquisitionRequest, bool> canManage, CancellationToken ct = default);

    /// <summary>
    /// Removes a request that is not yet available and stops tracking it. Torrents already sent to qBittorrent are kept,
    /// unless <paramref name="deleteDownloads"/> asks to remove (with files) those not yet imported and the title from
    /// Sonarr/Radarr when the plugin added it and nothing else uses it.
    /// </summary>
    Task<RetryResult> CancelRequestAsync(string requestId, Func<AcquisitionRequest, bool> canManage, bool deleteDownloads = false, CancellationToken ct = default);

    /// <summary>
    /// Adds seasons or episodes to the active request of a series instead of opening a second one (user decision: one
    /// request per series). <paramref name="all"/> turns it into "Serie completa", which follows the series while it airs.
    /// </summary>
    Task<AddScopeResult> AddToRequestAsync(string requestId, bool all, IReadOnlyCollection<int> seasonNumbers, IReadOnlyCollection<int> episodeIds, Func<AcquisitionRequest, bool> canManage, CancellationToken ct = default);

    /// <summary>
    /// Drops the downloads set aside for review: they become "skipped" and the request no longer waits for them.
    /// </summary>
    Task<RetryResult> SkipParkedAsync(string requestId, Func<AcquisitionRequest, bool> canManage, CancellationToken ct = default);

    /// <summary>
    /// Turns automatic release selection on or off for a request, so the tracker picks the best release itself
    /// even in manual selection mode.
    /// </summary>
    Task<RetryResult> SetAutoSelectAsync(string requestId, bool enabled, Func<AcquisitionRequest, bool> canManage, CancellationToken ct = default);
}
