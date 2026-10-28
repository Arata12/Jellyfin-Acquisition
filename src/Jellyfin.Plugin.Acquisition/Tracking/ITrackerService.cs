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

public interface ITrackerService
{
    Task<IDisposable> AcquireLockAsync(string requestId, CancellationToken ct = default);
    Task ProcessRequestAsync(AcquisitionRequest request, CancellationToken ct = default);
    Task PollActiveRequestsAsync(CancellationToken ct = default);
    Task<RetryResult> RetryRequestAsync(string requestId, Func<AcquisitionRequest, bool> canManage, CancellationToken ct = default);

    /// <summary>
    /// Removes a request that is not yet available and stops tracking it. Torrents already sent to qBittorrent are kept.
    /// </summary>
    Task<RetryResult> CancelRequestAsync(string requestId, Func<AcquisitionRequest, bool> canManage, CancellationToken ct = default);
}
