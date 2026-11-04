using Jellyfin.Plugin.Acquisition.Configuration;
using Jellyfin.Plugin.Acquisition.Storage;

namespace Jellyfin.Plugin.Acquisition.Models;

/// <summary>
/// Light per-user cap on requests still in progress, enforced automatically (no approval step). Administrators are exempt,
/// and adding seasons or episodes to a series already requested never counts as a new request.
/// </summary>
public static class RequestLimit
{
    /// <summary>Requests that still occupy a slot: active ones, except a finished series that only follows airing and a
    /// series still in the episode picker (not a request yet).</summary>
    public static bool CountsTowardsLimit(AcquisitionRequest r) =>
        r.IsActive
        && !(r.Status == RequestStatus.Available && r.FollowAiring)
        && !(r.Kind == MediaKind.Series && r.Status == RequestStatus.Pending && !r.ScopeConfirmed && r.Units.Count == 0);

    /// <summary>Returns the message to show when the user may not create another request, or null when they may.</summary>
    public static async Task<string?> CheckAsync(Guid userId, bool exempt, PluginConfiguration? config, IRequestRepository repo, CancellationToken ct)
    {
        var max = config?.MaxActiveRequestsPerUser ?? 0;
        if (exempt || max <= 0) return null;

        var all = await repo.GetAllAsync(ct).ConfigureAwait(false) ?? [];
        var inProgress = all.Count(r => r.UserId == userId && CountsTowardsLimit(r));
        return inProgress >= max
            ? (inProgress == 1
                ? $"Ya tienes una solicitud en curso y el máximo es {max}. Cuando termine podrás pedir otra."
                : $"Tienes {inProgress} solicitudes en curso y el máximo es {max}. Cuando termine alguna podrás pedir más.")
            : null;
    }
}
