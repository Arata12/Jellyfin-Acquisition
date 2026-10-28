using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr.Models;

namespace Jellyfin.Plugin.Acquisition.Adapters.Prowlarr;

public interface IProwlarrClient
{
    Task<List<ProwlarrIndexer>> GetIndexersAsync(CancellationToken ct = default);
    Task<List<ProwlarrRelease>> SearchAsync(ProwlarrSearchQuery query, CancellationToken ct = default);
    Task<TorrentDownloadResult> FetchTorrentAsync(string downloadUrl, CancellationToken ct = default);
}
