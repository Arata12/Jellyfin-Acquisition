using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Releases;

public interface IReleaseService
{
    Task<List<ReleaseCandidate>> SearchAndEvaluateReleasesAsync(AcquisitionRequest request, CancellationToken ct = default);
    Task<List<ReleaseCandidate>> SearchAndEvaluateReleasesForUnitAsync(AcquisitionRequest request, DownloadUnit? unit, CancellationToken ct = default);
    ReleaseCandidate? PickBestRelease(IEnumerable<ReleaseCandidate> candidates);
}
