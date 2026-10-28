using System.Text.Json.Serialization;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Controllers.Dtos;

public class ProviderDiagnosticsDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "Ready"; // Ready, NotConfigured, Unauthorized, Unavailable, RateLimited

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("isConfigured")]
    public bool IsConfigured { get; set; }
}

public class CatalogSearchResultDto
{
    [JsonPropertyName("items")]
    public List<CatalogItemDto> Items { get; set; } = [];

    [JsonPropertyName("sonarr")]
    public ProviderDiagnosticsDto Sonarr { get; set; } = new();

    [JsonPropertyName("radarr")]
    public ProviderDiagnosticsDto Radarr { get; set; } = new();

    [JsonPropertyName("isPartial")]
    public bool IsPartial { get; set; }

    [JsonPropertyName("totalRemoteFound")]
    public int TotalRemoteFound { get; set; }

    [JsonPropertyName("localMatchesCount")]
    public int LocalMatchesCount { get; set; }

    [JsonPropertyName("diagnosticMessage")]
    public string? DiagnosticMessage { get; set; }
}

public class NamedOptionDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public string? Details { get; set; }
}

public class RequestOptionDestinationDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("rootFolderPath")]
    public string RootFolderPath { get; set; } = string.Empty;

    [JsonPropertyName("qualityProfileId")]
    public int QualityProfileId { get; set; }

    [JsonPropertyName("qualityProfileName")]
    public string? QualityProfileName { get; set; }

    [JsonPropertyName("seriesType")]
    public string? SeriesType { get; set; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }
}

public class RequestOptionsDto
{
    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MediaKind Kind { get; set; }

    [JsonPropertyName("destinations")]
    public List<RequestOptionDestinationDto> Destinations { get; set; } = [];

    [JsonPropertyName("rootFolders")]
    public List<NamedOptionDto> RootFolders { get; set; } = [];

    [JsonPropertyName("qualityProfiles")]
    public List<NamedOptionDto> QualityProfiles { get; set; } = [];

    [JsonPropertyName("defaultDestinationId")]
    public string? DefaultDestinationId { get; set; }

    /// <summary>
    /// Set when the destination list could not be read (e.g. Arr unavailable), so the UI can explain it instead of hiding the selector.
    /// </summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

public class DownloadClientSummaryDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("implementation")]
    public string Implementation { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public class DestinationConfigDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MediaKind Kind { get; set; }

    [JsonPropertyName("rootFolderPath")]
    public string RootFolderPath { get; set; } = string.Empty;

    [JsonPropertyName("qualityProfileId")]
    public int QualityProfileId { get; set; }

    [JsonPropertyName("seriesType")]
    public string? SeriesType { get; set; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }
}

public class ServiceOptionsSummaryDto
{
    [JsonPropertyName("isConnected")]
    public bool IsConnected { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("rootFolders")]
    public List<NamedOptionDto> RootFolders { get; set; } = [];

    [JsonPropertyName("qualityProfiles")]
    public List<NamedOptionDto> QualityProfiles { get; set; } = [];

    [JsonPropertyName("downloadClients")]
    public List<DownloadClientSummaryDto> DownloadClients { get; set; } = [];

    [JsonPropertyName("categories")]
    public List<NamedOptionDto> Categories { get; set; } = [];
}

public class SetupOptionsDto
{
    [JsonPropertyName("sonarr")]
    public ServiceOptionsSummaryDto Sonarr { get; set; } = new();

    [JsonPropertyName("radarr")]
    public ServiceOptionsSummaryDto Radarr { get; set; } = new();

    [JsonPropertyName("qBittorrent")]
    public ServiceOptionsSummaryDto QBittorrent { get; set; } = new();

    [JsonPropertyName("configuredDestinations")]
    public List<DestinationConfigDto> ConfiguredDestinations { get; set; } = [];
}

public class ConflictResponseDto
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;
}
