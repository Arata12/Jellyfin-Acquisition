using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Acquisition.Adapters.Radarr.Models;

public class RadarrImage
{
    [JsonPropertyName("coverType")]
    public string? CoverType { get; set; }

    [JsonPropertyName("remoteUrl")]
    public string? RemoteUrl { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public class RadarrAddOptions
{
    [JsonPropertyName("searchForMovie")]
    public bool SearchForMovie { get; set; } = false;

    [JsonPropertyName("monitor")]
    public string Monitor { get; set; } = "none";
}

public class RadarrMovie
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

    [JsonPropertyName("hasFile")]
    public bool HasFile { get; set; }

    [JsonPropertyName("images")]
    public List<RadarrImage> Images { get; set; } = [];

    [JsonPropertyName("genres")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Genres { get; set; }

    [JsonPropertyName("runtime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Runtime { get; set; }

    [JsonPropertyName("studio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Studio { get; set; }

    [JsonPropertyName("certification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Certification { get; set; }

    [JsonPropertyName("inCinemas")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? InCinemas { get; set; }

    [JsonPropertyName("addOptions")]
    public RadarrAddOptions? AddOptions { get; set; }
}

public class RadarrStatusMessage
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("messages")]
    public List<string> Messages { get; set; } = [];
}

public class RadarrQueueItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("movieId")]
    public int? MovieId { get; set; }

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
    public List<RadarrStatusMessage> StatusMessages { get; set; } = [];
}

public class RadarrHistoryItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("movieId")]
    public int? MovieId { get; set; }

    [JsonPropertyName("eventType")]
    public string? EventType { get; set; }

    [JsonPropertyName("downloadId")]
    public string? DownloadId { get; set; }
}

public class RadarrParseResult
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("parsedMovieInfo")]
    public RadarrParsedMovieInfo? ParsedMovieInfo { get; set; }

    [JsonPropertyName("movie")]
    public RadarrMovie? Movie { get; set; }
}

public class RadarrParsedMovieInfo
{
    [JsonPropertyName("movieTitle")]
    public string? MovieTitle { get; set; }

    [JsonPropertyName("year")]
    public int Year { get; set; }

    [JsonPropertyName("quality")]
    public RadarrQualityModel? Quality { get; set; }

    [JsonPropertyName("languages")]
    public List<RadarrLanguage> Languages { get; set; } = [];
}

public class RadarrQualityModel
{
    [JsonPropertyName("quality")]
    public RadarrQualityDetail? Quality { get; set; }
}

public class RadarrQualityDetail
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

public class RadarrLanguage
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class RadarrRootFolder
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("freeSpace")]
    public long? FreeSpace { get; set; }
}

public class RadarrQualityProfile
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class RadarrManualImportCandidate
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

    [JsonPropertyName("movie")]
    public RadarrMovie? Movie { get; set; }

    [JsonPropertyName("quality")]
    public RadarrQualityModel? Quality { get; set; }

    [JsonPropertyName("rejections")]
    public List<RadarrRejection> Rejections { get; set; } = [];
}

public class RadarrRejection
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public class RadarrLookupResult
{
    public List<RadarrMovie> Items { get; set; } = [];
    public Jellyfin.Plugin.Acquisition.Models.ProviderStatus Status { get; set; } = Jellyfin.Plugin.Acquisition.Models.ProviderStatus.Ready;
    public string? ErrorMessage { get; set; }
}
