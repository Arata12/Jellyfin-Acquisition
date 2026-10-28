using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Acquisition.Models;

public class EpisodeTarget
{
    [JsonPropertyName("seriesId")]
    public int SeriesId { get; set; }

    [JsonPropertyName("episodeId")]
    public int EpisodeId { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("episodeNumber")]
    public int EpisodeNumber { get; set; }

    [JsonPropertyName("absoluteEpisodeNumber")]
    public int? AbsoluteEpisodeNumber { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("airDateUtc")]
    public DateTime? AirDateUtc { get; set; }
}

public class DownloadUnit
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int SeasonNumber { get; set; }
    public List<int> EpisodeNumbers { get; set; } = [];
    public List<int> EpisodeIds { get; set; } = [];
    public List<int> AbsoluteEpisodeNumbers { get; set; } = [];

    /// <summary>Air date of a single-episode unit (null for season packs or episodes without a date).</summary>
    public DateTime? AirDateUtc { get; set; }

    public bool IsFullSeasonPack { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public string? CorrelationTag { get; set; }
    public string? TorrentHash { get; set; }
    public string? TorrentName { get; set; }
    public ReleaseCandidate? SelectedRelease { get; set; }
    public List<ReleaseCandidate> AvailableReleases { get; set; } = [];
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Content path reported by qBittorrent when the download completed (used for manual import).</summary>
    public string? ContentPath { get; set; }

    /// <summary>When the plugin started waiting for Arr to import this unit.</summary>
    public DateTimeOffset? ImportWaitStartedAt { get; set; }

    /// <summary>When a manual import was requested from Arr for this unit, if any.</summary>
    public DateTimeOffset? ManualImportRequestedAt { get; set; }

    /// <summary>Consecutive failures fetching the selected torrent from Prowlarr.</summary>
    public int FetchFailures { get; set; }

    /// <summary>Consecutive searches without any eligible release.</summary>
    public int SearchAttempts { get; set; }

    /// <summary>
    /// Margin after an episode airs before it is searched, so releases have time to appear on the indexers.
    /// </summary>
    public static readonly TimeSpan ReleaseGrace = TimeSpan.FromHours(1);

    /// <summary>
    /// When the unit can be searched: now for episodes without a date (explicitly chosen) or already aired,
    /// otherwise one <see cref="ReleaseGrace"/> after its air date.
    /// </summary>
    public DateTime SearchableFromUtc => AirDateUtc.HasValue ? AirDateUtc.Value + ReleaseGrace : DateTime.MinValue;

    /// <summary>Short human label used in history and summaries, e.g. "S01E04" or "Temporada 1".</summary>
    public string Describe(MediaKind kind)
    {
        if (kind == MediaKind.Movie) return "la película";
        if (IsFullSeasonPack || EpisodeNumbers.Count != 1) return $"Temporada {SeasonNumber}";
        return $"S{SeasonNumber:D2}E{EpisodeNumbers[0]:D2}";
    }
}

/// <summary>
/// Scope of episodes or parts requested for media.
/// </summary>
public class RequestScope
{
    [JsonPropertyName("all")]
    public bool All { get; set; } = true;

    [JsonPropertyName("seasonNumbers")]
    public List<int> SeasonNumbers { get; set; } = [];

    [JsonPropertyName("episodeIds")]
    public List<int> EpisodeIds { get; set; } = [];

    [JsonPropertyName("episodeNumbers")]
    public List<int> EpisodeNumbers { get; set; } = [];

    [JsonPropertyName("targetEpisodes")]
    public List<EpisodeTarget> TargetEpisodes { get; set; } = [];
}
