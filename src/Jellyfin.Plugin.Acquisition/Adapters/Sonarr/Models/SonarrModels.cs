using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;

public class SonarrImage
{
    [JsonPropertyName("coverType")]
    public string? CoverType { get; set; }

    [JsonPropertyName("remoteUrl")]
    public string? RemoteUrl { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public class SonarrSeason
{
    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("monitored")]
    public bool Monitored { get; set; }
}

public class SonarrAddOptions
{
    [JsonPropertyName("searchForMissingEpisodes")]
    public bool SearchForMissingEpisodes { get; set; } = false;

    [JsonPropertyName("searchForCutoffUnmetEpisodes")]
    public bool SearchForCutoffUnmetEpisodes { get; set; } = false;

    [JsonPropertyName("monitor")]
    public string Monitor { get; set; } = "none";
}

public class SonarrSeries
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("sortTitle")]
    public string? SortTitle { get; set; }

    [JsonPropertyName("year")]
    public int Year { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("tvdbId")]
    public int TvdbId { get; set; }

    /// <summary>TMDB id, when the Sonarr version exposes it (0 otherwise).</summary>
    [JsonPropertyName("tmdbId")]
    public int TmdbId { get; set; }

    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("qualityProfileId")]
    public int QualityProfileId { get; set; }

    [JsonPropertyName("rootFolderPath")]
    public string? RootFolderPath { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("monitored")]
    public bool Monitored { get; set; }

    [JsonPropertyName("seasons")]
    public List<SonarrSeason> Seasons { get; set; } = [];

    [JsonPropertyName("images")]
    public List<SonarrImage> Images { get; set; } = [];

    /// <summary>"continuing", "ended", "upcoming" or "deleted".</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("seriesType")]
    public string? SeriesType { get; set; } = "standard";

    [JsonPropertyName("addOptions")]
    public SonarrAddOptions? AddOptions { get; set; }
}

public class SonarrEpisode
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("seriesId")]
    public int SeriesId { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("episodeNumber")]
    public int EpisodeNumber { get; set; }

    [JsonPropertyName("absoluteEpisodeNumber")]
    public int? AbsoluteEpisodeNumber { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("hasFile")]
    public bool HasFile { get; set; }

    [JsonPropertyName("monitored")]
    public bool Monitored { get; set; }

    [JsonPropertyName("airDateUtc")]
    public DateTime? AirDateUtc { get; set; }
}

public class SonarrStatusMessage
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("messages")]
    public List<string> Messages { get; set; } = [];
}

public class SonarrQueueItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("seriesId")]
    public int? SeriesId { get; set; }

    [JsonPropertyName("downloadId")]
    public string? DownloadId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("trackedDownloadStatus")]
    public string? TrackedDownloadStatus { get; set; }

    [JsonPropertyName("trackedDownloadState")]
    public string? TrackedDownloadState { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("statusMessages")]
    public List<SonarrStatusMessage> StatusMessages { get; set; } = [];
}

public class SonarrHistoryItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("seriesId")]
    public int? SeriesId { get; set; }

    [JsonPropertyName("episodeId")]
    public int? EpisodeId { get; set; }

    [JsonPropertyName("eventType")]
    public string? EventType { get; set; }

    [JsonPropertyName("downloadId")]
    public string? DownloadId { get; set; }
}

public class SonarrParseResult
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("parsedEpisodeInfo")]
    public SonarrParsedInfo? ParsedEpisodeInfo { get; set; }

    [JsonPropertyName("series")]
    public SonarrSeries? Series { get; set; }

    [JsonPropertyName("episodes")]
    public List<SonarrEpisode> Episodes { get; set; } = [];
}

public class SonarrParsedInfo
{
    [JsonPropertyName("releaseTitle")]
    public string? ReleaseTitle { get; set; }

    [JsonPropertyName("seriesTitle")]
    public string? SeriesTitle { get; set; }

    [JsonPropertyName("quality")]
    public SonarrQualityModel? Quality { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("episodeNumbers")]
    public List<int> EpisodeNumbers { get; set; } = [];

    [JsonPropertyName("absoluteEpisodeNumbers")]
    public List<int> AbsoluteEpisodeNumbers { get; set; } = [];

    [JsonPropertyName("languages")]
    public List<SonarrLanguage> Languages { get; set; } = [];
}

public class SonarrQualityModel
{
    [JsonPropertyName("quality")]
    public SonarrQualityDetail? Quality { get; set; }

    [JsonPropertyName("revision")]
    public SonarrRevision? Revision { get; set; }
}

public class SonarrQualityDetail
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("resolution")]
    public int Resolution { get; set; }
}

public class SonarrRevision
{
    [JsonPropertyName("version")]
    public int Version { get; set; }
}

public class SonarrLanguage
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class SonarrRootFolder
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("freeSpace")]
    public long? FreeSpace { get; set; }
}

public class SonarrQualityProfile
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class SonarrManualImportCandidate
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("relativePath")]
    public string? RelativePath { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("series")]
    public SonarrSeries? Series { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("episodes")]
    public List<SonarrEpisode> Episodes { get; set; } = [];

    [JsonPropertyName("quality")]
    public SonarrQualityModel? Quality { get; set; }

    [JsonPropertyName("rejections")]
    public List<SonarrRejection> Rejections { get; set; } = [];
}

public class SonarrRejection
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public class SonarrLookupResult
{
    public List<SonarrSeries> Items { get; set; } = [];
    public Jellyfin.Plugin.Acquisition.Models.ProviderStatus Status { get; set; } = Jellyfin.Plugin.Acquisition.Models.ProviderStatus.Ready;
    public string? ErrorMessage { get; set; }
}

public class ArrDownloadClientField
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("value")]
    public object? Value { get; set; }
}

public class ArrDownloadClient
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("implementation")]
    public string? Implementation { get; set; }

    [JsonPropertyName("enable")]
    public bool Enable { get; set; }

    [JsonPropertyName("fields")]
    public List<ArrDownloadClientField> Fields { get; set; } = [];

    public string? GetCategory()
    {
        var field = Fields.FirstOrDefault(f =>
            string.Equals(f.Name, "category", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.Name, "tvCategory", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.Name, "movieCategory", StringComparison.OrdinalIgnoreCase));
        return field?.Value?.ToString();
    }

    public string? GetHost()
    {
        return Fields.FirstOrDefault(f => string.Equals(f.Name, "host", StringComparison.OrdinalIgnoreCase))?.Value?.ToString();
    }

    public int? GetPort()
    {
        var val = Fields.FirstOrDefault(f => string.Equals(f.Name, "port", StringComparison.OrdinalIgnoreCase))?.Value?.ToString();
        return int.TryParse(val, out var port) ? port : null;
    }

    public bool IsEnabledQBittorrent =>
        Enable && !string.IsNullOrEmpty(Implementation) && Implementation.Contains("qBittorrent", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when this Arr client points to the same host and port as the plugin's direct qBittorrent connection.
    /// </summary>
    public bool PointsTo(Uri qbittorrentUri)
    {
        var host = GetHost();
        return !string.IsNullOrEmpty(host) &&
               string.Equals(host.Trim(), qbittorrentUri.Host, StringComparison.OrdinalIgnoreCase) &&
               GetPort() == qbittorrentUri.Port;
    }
}
