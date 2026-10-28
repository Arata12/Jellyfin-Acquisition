using Jellyfin.Plugin.Acquisition.Adapters.QBittorrent.Models;

namespace Jellyfin.Plugin.Acquisition.Adapters.QBittorrent;

public interface IQBittorrentClient
{
    Task<bool> LoginAsync(CancellationToken ct = default);
    Task<bool> AddTorrentAsync(QBittorrentAddOptions options, CancellationToken ct = default);
    Task<List<QBittorrentTorrentInfo>> GetTorrentsInfoAsync(string? category = null, string? tag = null, string? hash = null, CancellationToken ct = default);
    Task<List<QBittorrentFile>> GetTorrentFilesAsync(string hash, CancellationToken ct = default);
    Task<List<QBittorrentCategory>> GetCategoriesAsync(CancellationToken ct = default);

    /// <summary>All tags defined in qBittorrent, or null when they could not be read.</summary>
    Task<List<string>?> GetTagsAsync(CancellationToken ct = default);

    /// <summary>Deletes tags from qBittorrent (and from every torrent that has them). Torrents are kept.</summary>
    Task<bool> DeleteTagsAsync(IReadOnlyCollection<string> tags, CancellationToken ct = default);
}
