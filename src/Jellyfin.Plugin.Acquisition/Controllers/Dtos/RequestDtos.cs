using System.Text.Json.Serialization;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Controllers.Dtos;

public class CreateRequestDto
{
    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MediaKind Kind { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("providerIds")]
    public ProviderIds ProviderIds { get; set; } = new();

    [JsonPropertyName("scope")]
    public RequestScope? Scope { get; set; }

    /// <summary>
    /// Stable identifier of the selected destination (configured destination ID or "sonarr-root:{id}" / "radarr-root:{id}").
    /// The server resolves the real root folder, profile and series type from it; paths sent by the browser are never trusted.
    /// </summary>
    [JsonPropertyName("destinationId")]
    public string? DestinationId { get; set; }

    /// <summary>
    /// Legacy selection by visible name. Only used when <see cref="DestinationId"/> is absent.
    /// </summary>
    [JsonPropertyName("destinationName")]
    public string? DestinationName { get; set; }

    /// <summary>Poster of the catalog item (opaque image path); validated on the server before it is stored.</summary>
    [JsonPropertyName("posterUrl")]
    public string? PosterUrl { get; set; }
}

public class ScopeConfirmationDto
{
    [JsonPropertyName("all")]
    public bool All { get; set; }

    [JsonPropertyName("seasonNumbers")]
    public List<int> SeasonNumbers { get; set; } = [];

    [JsonPropertyName("episodeNumbers")]
    public List<int> EpisodeNumbers { get; set; } = [];

    [JsonPropertyName("episodeIds")]
    public List<int> EpisodeIds { get; set; } = [];

    [JsonPropertyName("targetEpisodes")]
    public List<EpisodeTarget>? TargetEpisodes { get; set; }
}

public class ErrorResponseDto
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public class AutoSelectDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public class SelectReleaseDto
{
    [JsonPropertyName("releaseId")]
    public string ReleaseId { get; set; } = string.Empty;
}

public class ReleaseCandidateDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("seeders")]
    public int Seeders { get; set; }

    [JsonPropertyName("leechers")]
    public int Leechers { get; set; }

    [JsonPropertyName("indexer")]
    public string Indexer { get; set; } = string.Empty;

    [JsonPropertyName("codec")]
    public string? Codec { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    [JsonPropertyName("languages")]
    public List<string> Languages { get; set; } = [];

    [JsonPropertyName("isTorrent")]
    public bool IsTorrent { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("episodeNumber")]
    public int EpisodeNumber { get; set; }

    [JsonPropertyName("isSeasonPack")]
    public bool IsSeasonPack { get; set; }

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("isEligible")]
    public bool IsEligible { get; set; }

    [JsonPropertyName("rejectionReasons")]
    public List<string> RejectionReasons { get; set; } = [];

    public static ReleaseCandidateDto FromModel(ReleaseCandidate candidate)
    {
        return new ReleaseCandidateDto
        {
            Id = candidate.Id,
            Title = candidate.Title,
            Size = candidate.Size,
            Seeders = candidate.Seeders,
            Leechers = candidate.Leechers,
            Indexer = candidate.Indexer,
            Codec = candidate.Codec,
            Resolution = candidate.Resolution,
            Source = candidate.Source,
            Quality = candidate.Quality,
            Languages = candidate.Languages,
            IsTorrent = candidate.IsTorrent,
            SeasonNumber = candidate.SeasonNumber,
            EpisodeNumber = candidate.EpisodeNumber,
            IsSeasonPack = candidate.IsSeasonPack,
            Score = candidate.Score,
            IsEligible = candidate.IsEligible,
            RejectionReasons = candidate.RejectionReasons
        };
    }
}

public class AcquisitionRequestDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MediaKind Kind { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("providerIds")]
    public ProviderIds ProviderIds { get; set; } = new();

    [JsonPropertyName("scope")]
    public RequestScope Scope { get; set; } = new();

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RequestStatus Status { get; set; }

    [JsonPropertyName("statusText")]
    public string StatusText { get; set; } = string.Empty;

    [JsonPropertyName("torrentName")]
    public string? TorrentName { get; set; }

    [JsonPropertyName("destinationName")]
    public string? DestinationName { get; set; }

    [JsonPropertyName("rootFolderPath")]
    public string? RootFolderPath { get; set; }

    [JsonPropertyName("qualityProfileId")]
    public int? QualityProfileId { get; set; }

    [JsonPropertyName("seriesType")]
    public string? SeriesType { get; set; }

    [JsonPropertyName("attentionReason")]
    public string? AttentionReason { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("localItemId")]
    public Guid? LocalItemId { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; set; }

    [JsonPropertyName("history")]
    public List<RequestHistoryEntry> History { get; set; } = [];

    /// <summary>Next time the tracker searches again (empty-search backoff, a movie waiting for its release); null when it is not waiting.</summary>
    [JsonPropertyName("nextAttemptUtc")]
    public DateTimeOffset? NextAttemptUtc { get; set; }

    /// <summary>The tracker picks the best release itself, even in manual selection mode.</summary>
    [JsonPropertyName("autoReleaseSelection")]
    public bool AutoReleaseSelection { get; set; }

    /// <summary>The server asks for a manual choice of release (global setting); the request can still opt out with <see cref="AutoReleaseSelection"/>.</summary>
    [JsonPropertyName("manualReleaseMode")]
    public bool ManualReleaseMode { get; set; }

    [JsonPropertyName("progress")]
    public RequestProgressDto Progress { get; set; } = new();
}

public class RequestProgressDto
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("available")]
    public int Available { get; set; }

    [JsonPropertyName("inProgress")]
    public int InProgress { get; set; }

    [JsonPropertyName("pending")]
    public int Pending { get; set; }

    [JsonPropertyName("attention")]
    public int Attention { get; set; }

    /// <summary>Download being processed now, e.g. "S01E04".</summary>
    [JsonPropertyName("current")]
    public string? Current { get; set; }

    [JsonPropertyName("attentionItems")]
    public List<UnitIssueDto> AttentionItems { get; set; } = [];

    /// <summary>Downloads dropped by the user or by the 14-day limit.</summary>
    [JsonPropertyName("skipped")]
    public int Skipped { get; set; }

    [JsonPropertyName("skippedItems")]
    public List<UnitIssueDto> SkippedItems { get; set; } = [];

    /// <summary>"Serie completa" of an airing series: new episodes are downloaded as they air.</summary>
    [JsonPropertyName("following")]
    public bool Following { get; set; }

    /// <summary>
    /// Why nothing is being searched yet: an episode waiting for its air date ("S01E08 se buscará a partir del 02/11 19:00 UTC")
    /// or a movie waiting for its digital release ("Lanzamiento digital el 12/03/2027; se buscará a partir de esa fecha.").
    /// </summary>
    [JsonPropertyName("waitingForAir")]
    public string? WaitingForAir { get; set; }

    /// <summary>Manual selection mode: a release list is ready and the tracker waits for the user's choice.</summary>
    [JsonPropertyName("awaitingSelection")]
    public bool AwaitingSelection { get; set; }
}

public class UnitIssueDto
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>Row of "Tus solicitudes".</summary>
public class RequestSummaryDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MediaKind Kind { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("posterUrl")]
    public string? PosterUrl { get; set; }

    [JsonPropertyName("providerIds")]
    public ProviderIds ProviderIds { get; set; } = new();

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RequestStatus Status { get; set; }

    [JsonPropertyName("progress")]
    public RequestSummaryProgressDto Progress { get; set; } = new();

    /// <summary>The user has to do something: choose a release or review downloads set aside.</summary>
    [JsonPropertyName("needsAction")]
    public bool NeedsAction { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>User name of the requester; only filled for administrators.</summary>
    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; set; }
}

public class RequestSummaryProgressDto
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("available")]
    public int Available { get; set; }

    [JsonPropertyName("attention")]
    public int Attention { get; set; }

    [JsonPropertyName("awaitingSelection")]
    public bool AwaitingSelection { get; set; }

    [JsonPropertyName("following")]
    public bool Following { get; set; }

    [JsonPropertyName("waitingForAir")]
    public string? WaitingForAir { get; set; }
}

public class AttentionCountDto
{
    [JsonPropertyName("count")]
    public int Count { get; set; }
}
