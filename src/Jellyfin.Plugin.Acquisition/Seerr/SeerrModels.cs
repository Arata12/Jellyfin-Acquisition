using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Acquisition.Seerr;

// Wire shapes of the Seerr (Jellyseerr) API subset plezy reads. Property names follow Seerr's camelCase JSON.

/// <summary>Seerr <c>MediaInfo.status</c> codes (Jellyseerr numbering).</summary>
public static class SeerrMediaStatus
{
    public const int Unknown = 1;
    public const int Pending = 2;
    public const int Processing = 3;
    public const int PartiallyAvailable = 4;
    public const int Available = 5;
}

/// <summary>Seerr <c>MediaRequest.status</c> codes.</summary>
public static class SeerrRequestStatus
{
    public const int Pending = 1;
    public const int Approved = 2;
    public const int Declined = 3;
    public const int Failed = 4;
    public const int Completed = 5;
}

/// <summary>Seerr permission bits (server/lib/permissions.ts) the plugin grants.</summary>
public static class SeerrPermission
{
    public const int Request = 32;
    public const int RequestAdvanced = 8192;
    public const int RequestMovie = 262144;
    public const int RequestTv = 524288;
}

public static class SeerrJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
}

public class SeerrPublicSettingsDto
{
    public bool Initialized { get; set; } = true;
    public string ApplicationTitle { get; set; } = "Acquisition";
    public bool LocalLogin { get; set; }
    public bool MediaServerLogin { get; set; } = true;

    /// <summary>2 = Jellyfin. Its presence also makes clients treat the instance as Jellyseerr.</summary>
    public int MediaServerType { get; set; } = 2;
    public bool Movie4kEnabled { get; set; }
    public bool Series4kEnabled { get; set; }
    public bool PartialRequestsEnabled { get; set; } = true;

    /// <summary>Artwork is always served through this API's /imageproxy route.</summary>
    public bool CacheImages { get; set; } = true;

    /// <summary>Present (even empty) on Jellyseerr releases that use the typed /imageproxy/tmdb route.</summary>
    public string YoutubeUrl { get; set; } = string.Empty;
}

public class SeerrUserDto
{
    public int Id { get; set; }
    public string? DisplayName { get; set; }
    public string? Username { get; set; }
    public string? JellyfinUsername { get; set; }
    public int Permissions { get; set; }

    /// <summary>3 = Jellyfin user.</summary>
    public int UserType { get; set; } = 3;
}

public class SeerrPageDto
{
    public int Page { get; set; } = 1;
    public int TotalPages { get; set; } = 1;
    public int TotalResults { get; set; }
    public List<SeerrMediaDto> Results { get; set; } = [];
}

public class SeerrMediaDto
{
    public int Id { get; set; }
    public string MediaType { get; set; } = "movie";
    public string? Title { get; set; }
    public string? Name { get; set; }
    public string? OriginalTitle { get; set; }
    public string? OriginalName { get; set; }
    public string? Overview { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? ReleaseDate { get; set; }
    public string? FirstAirDate { get; set; }
    public double? VoteAverage { get; set; }
    public int? VoteCount { get; set; }
    public double? Popularity { get; set; }
    public string? OriginalLanguage { get; set; }
    public bool? Adult { get; set; }
    public List<string>? OriginCountry { get; set; }
    public SeerrMediaInfoDto? MediaInfo { get; set; }
}

public class SeerrMediaInfoDto
{
    public int Id { get; set; }
    public int TmdbId { get; set; }
    public int? TvdbId { get; set; }
    public int Status { get; set; } = SeerrMediaStatus.Unknown;
    public int Status4k { get; set; } = SeerrMediaStatus.Unknown;
    public List<SeerrSeasonInfoDto>? Seasons { get; set; }
    public List<SeerrRequestDto>? Requests { get; set; }
}

public class SeerrSeasonInfoDto
{
    public int SeasonNumber { get; set; }
    public int Status { get; set; } = SeerrMediaStatus.Unknown;
    public int Status4k { get; set; } = SeerrMediaStatus.Unknown;
}

public class SeerrRequestDto
{
    public int Id { get; set; }
    public int Status { get; set; } = SeerrRequestStatus.Approved;
    public bool Is4k { get; set; }
    public SeerrMediaInfoDto? Media { get; set; }
    public List<SeerrRequestSeasonDto>? Seasons { get; set; }
}

public class SeerrRequestSeasonDto
{
    public int SeasonNumber { get; set; }
}

public class SeerrNamedValueDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class SeerrExternalIdsDto
{
    public string? ImdbId { get; set; }
    public int? TvdbId { get; set; }
}

public class SeerrSeasonDto
{
    public int Id { get; set; }
    public int SeasonNumber { get; set; }

    /// <summary>Left empty so clients show their own localized "Season N".</summary>
    public string? Name { get; set; }
    public int EpisodeCount { get; set; }
    public string? AirDate { get; set; }
}

public class SeerrCastMemberDto
{
    public string Name { get; set; } = string.Empty;
    public string? Character { get; set; }
    public string? ProfilePath { get; set; }
}

public class SeerrCreditsDto
{
    public List<SeerrCastMemberDto> Cast { get; set; } = [];
    public List<SeerrCastMemberDto> Crew { get; set; } = [];
}

public class SeerrCountryRatingDto
{
    [JsonPropertyName("iso_3166_1")]
    public string CountryCode { get; set; } = "US";

    public string? Rating { get; set; }

    [JsonPropertyName("release_dates")]
    public List<SeerrCertificationDto>? ReleaseDates { get; set; }
}

public class SeerrCertificationDto
{
    public string? Certification { get; set; }
}

public class SeerrCountryRatingsDto
{
    public List<SeerrCountryRatingDto> Results { get; set; } = [];
}

public class SeerrDetailsDto
{
    public int Id { get; set; }
    public string? ImdbId { get; set; }
    public string? Title { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? ReleaseDate { get; set; }
    public string? FirstAirDate { get; set; }
    public string? LastAirDate { get; set; }
    public int? Runtime { get; set; }
    public List<int>? EpisodeRunTime { get; set; }
    public double? VoteAverage { get; set; }
    public int? VoteCount { get; set; }
    public string? Status { get; set; }
    public int? NumberOfEpisodes { get; set; }
    public List<SeerrNamedValueDto>? Genres { get; set; }
    public List<SeerrNamedValueDto>? Networks { get; set; }
    public List<SeerrNamedValueDto>? ProductionCompanies { get; set; }
    public List<SeerrNamedValueDto>? Keywords { get; set; }
    public SeerrCountryRatingsDto? Releases { get; set; }
    public SeerrCountryRatingsDto? ContentRatings { get; set; }
    public SeerrExternalIdsDto? ExternalIds { get; set; }
    public List<SeerrSeasonDto>? Seasons { get; set; }
    public SeerrCreditsDto? Credits { get; set; }
    public SeerrMediaInfoDto? MediaInfo { get; set; }
}

public class SeerrServiceInstanceDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Is4k { get; set; }
    public bool IsDefault { get; set; }
    public List<int> ActiveTags { get; set; } = [];
}

public class SeerrServiceDetailDto
{
    public SeerrServiceInstanceDto? Server { get; set; }
    public List<object> Profiles { get; set; } = [];
    public List<object> RootFolders { get; set; } = [];
    public List<object> Tags { get; set; } = [];
}

/// <summary>Body of <c>POST /request</c>; <c>seasons</c> is either <c>"all"</c> or a list of season numbers.</summary>
public class SeerrCreateRequestDto
{
    public string? MediaType { get; set; }
    public int MediaId { get; set; }
    public JsonElement Seasons { get; set; }
    public bool Is4k { get; set; }
    public int? ServerId { get; set; }
}
