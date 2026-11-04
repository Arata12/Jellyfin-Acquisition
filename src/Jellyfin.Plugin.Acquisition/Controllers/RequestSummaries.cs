using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Controllers;

/// <summary>
/// Builds the "Tus solicitudes" list: which requests a user sees, whether they need action and in what order.
/// </summary>
public static class RequestSummaries
{
    /// <summary>Finished requests (available or failed) disappear from the list after this long.</summary>
    public static readonly TimeSpan FinishedRetention = TimeSpan.FromDays(30);

    public static bool NeedsAction(AcquisitionRequest r, RequestProgressDto progress) =>
        progress.AwaitingSelection || progress.Attention > 0 || r.Status == RequestStatus.RequiresAttention;

    // Administrators see everyone's requests (with the requester's name via userName); others only their own.
    public static List<RequestSummaryDto> Build(
        IEnumerable<AcquisitionRequest> requests,
        Guid userId,
        bool isAdmin,
        Func<Guid, string?> userName,
        DateTimeOffset now)
    {
        var rows = new List<(RequestSummaryDto Dto, bool Active)>();
        foreach (var r in requests)
        {
            if (!isAdmin && r.UserId != userId) continue;

            // A series still in the scope picker (not confirmed, nothing queued) is not a request yet
            if (r.Kind == MediaKind.Series && r.Status == RequestStatus.Pending && !r.ScopeConfirmed && r.Units.Count == 0) continue;

            var finished = !r.IsActive;
            if (finished && now - (r.CompletedAt ?? r.UpdatedAt) > FinishedRetention) continue;

            var progress = AcquisitionController.BuildProgress(r);
            rows.Add((new RequestSummaryDto
            {
                Id = r.Id,
                Kind = r.Kind,
                Title = r.Title,
                Year = r.Year,
                PosterUrl = RequestPoster.Sanitize(r.PosterUrl),
                ProviderIds = r.ProviderIds,
                Status = r.Status,
                NeedsAction = !finished && NeedsAction(r, progress),
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
                RequestedBy = isAdmin ? userName(r.UserId) : null,
                Progress = new RequestSummaryProgressDto
                {
                    Total = progress.Total,
                    Available = progress.Available,
                    Attention = progress.Attention,
                    AwaitingSelection = progress.AwaitingSelection,
                    Following = progress.Following,
                    WaitingForAir = progress.WaitingForAir
                }
            }, !finished));
        }

        // One card per title: older finished requests of the same title (e.g. from before "one request per series")
        // would show the same series several times; the most relevant one (needs action, active, newest) stays
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return rows
            .OrderBy(x => x.Dto.NeedsAction ? 0 : x.Active ? 1 : 2)
            .ThenByDescending(x => x.Dto.UpdatedAt)
            .Select(x => x.Dto)
            .Where(dto => seen.Add(TitleKey(dto)))
            .ToList();
    }

    private static string TitleKey(RequestSummaryDto dto)
    {
        var ids = dto.ProviderIds;
        var id = !string.IsNullOrWhiteSpace(ids?.Tvdb) ? "tvdb:" + ids!.Tvdb
            : !string.IsNullOrWhiteSpace(ids?.Tmdb) ? "tmdb:" + ids!.Tmdb
            : !string.IsNullOrWhiteSpace(ids?.Imdb) ? "imdb:" + ids!.Imdb
            : "title:" + dto.Title.Trim() + "|" + dto.Year;
        return dto.Kind + "|" + id;
    }
}
