using System.Text.Json.Serialization;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Adapters.Prowlarr.Models;

public class ProwlarrIndexer
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("enable")]
    public bool Enable { get; set; }

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }
}

public class ProwlarrRelease
{
    [JsonPropertyName("guid")]
    public string? Guid { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("magnetUrl")]
    public string? MagnetUrl { get; set; }

    [JsonPropertyName("infoHash")]
    public string? InfoHash { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("seeders")]
    public int? Seeders { get; set; }

    [JsonPropertyName("leechers")]
    public int? Leechers { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("indexer")]
    public string? Indexer { get; set; }

    [JsonPropertyName("indexerId")]
    public int IndexerId { get; set; }

    [JsonPropertyName("categories")]
    public List<ProwlarrCategory>? Categories { get; set; }
}

public class ProwlarrCategory
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class ProwlarrSearchQuery
{
    public string Query { get; set; } = string.Empty;
    public MediaKind Kind { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }

    /// <summary>Absolute episode number (anime numbering), used instead of SxxEyy when set.</summary>
    public int? AbsoluteEpisode { get; set; }

    /// <summary>Anime releases rarely use SxxEyy; the query omits it unless an absolute number is known.</summary>
    public bool IsAnime { get; set; }

    public string? TvdbId { get; set; }
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public List<int>? IndexerIds { get; set; }
}

/// <summary>
/// Raised when Prowlarr could not be queried, so callers can tell a failure apart from "no releases found".
/// </summary>
public class ProwlarrUnavailableException : Exception
{
    public ProwlarrUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public class TorrentDownloadResult
{
    public byte[]? TorrentBytes { get; set; }
    public string? MagnetUri { get; set; }
    public string? InfoHash { get; set; }
    public bool IsSuccess => TorrentBytes != null || !string.IsNullOrWhiteSpace(MagnetUri);
    public string? ErrorMessage { get; set; }
}
