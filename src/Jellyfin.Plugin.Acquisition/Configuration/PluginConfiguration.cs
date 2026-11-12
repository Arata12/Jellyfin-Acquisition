using MediaBrowser.Model.Plugins;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Configuration;

public class DestinationConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty; // e.g. "Series", "Anime", "Películas"
    public MediaKind Kind { get; set; }
    public string RootFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Optional explicit Arr quality profile for this destination. 0 means "use the instance technical profile".
    /// </summary>
    public int QualityProfileId { get; set; }
    public string? SeriesType { get; set; } = "standard"; // standard, anime, daily
    public bool IsDefault { get; set; }
}

/// <summary>
/// Plugin configuration persisted in Jellyfin plugin-configurations directory.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // Named Destinations / Multiple Root Folders
    public List<DestinationConfig> Destinations { get; set; } = [];

    /// <summary>
    /// Moves the legacy global root folders into the unified destination list and clears them,
    /// so they no longer compete with destinations.
    /// </summary>
    /// <returns>True when the configuration changed and should be saved.</returns>
    public bool EnsureMigratedDestinations()
    {
        var changed = false;
        if (Destinations.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(SonarrRootFolderPath))
            {
                Destinations.Add(new DestinationConfig
                {
                    Name = "Series",
                    Kind = MediaKind.Series,
                    RootFolderPath = SonarrRootFolderPath,
                    QualityProfileId = Math.Max(SonarrQualityProfileId, 0),
                    SeriesType = "standard",
                    IsDefault = true
                });
            }

            if (!string.IsNullOrWhiteSpace(RadarrRootFolderPath))
            {
                Destinations.Add(new DestinationConfig
                {
                    Name = "Películas",
                    Kind = MediaKind.Movie,
                    RootFolderPath = RadarrRootFolderPath,
                    QualityProfileId = Math.Max(RadarrQualityProfileId, 0),
                    IsDefault = true
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(SonarrRootFolderPath) || !string.IsNullOrWhiteSpace(RadarrRootFolderPath))
        {
            SonarrRootFolderPath = string.Empty;
            RadarrRootFolderPath = string.Empty;
            changed = true;
        }

        return changed;
    }

    // Sonarr settings
    public string SonarrUrl { get; set; } = "http://localhost:8989";
    public string SonarrApiKey { get; set; } = string.Empty;
    /// <summary>Legacy global root folder; only read by <see cref="EnsureMigratedDestinations"/>.</summary>
    public string SonarrRootFolderPath { get; set; } = string.Empty;

    /// <summary>Technical Sonarr profile used to register new series. 0 = not chosen (resolved only if Sonarr has a single profile).</summary>
    public int SonarrQualityProfileId { get; set; }

    /// <summary>Optional manual override of the qBittorrent category; empty = read it from Sonarr's download client.</summary>
    public string SonarrCategory { get; set; } = string.Empty;

    // Radarr settings
    public string RadarrUrl { get; set; } = "http://localhost:7878";
    public string RadarrApiKey { get; set; } = string.Empty;
    /// <summary>Legacy global root folder; only read by <see cref="EnsureMigratedDestinations"/>.</summary>
    public string RadarrRootFolderPath { get; set; } = string.Empty;

    /// <summary>Technical Radarr profile used to register new movies. 0 = not chosen (resolved only if Radarr has a single profile).</summary>
    public int RadarrQualityProfileId { get; set; }

    /// <summary>Optional manual override of the qBittorrent category; empty = read it from Radarr's download client.</summary>
    public string RadarrCategory { get; set; } = string.Empty;

    // Prowlarr settings
    public string ProwlarrUrl { get; set; } = "http://localhost:9696";
    public string ProwlarrApiKey { get; set; } = string.Empty;

    // qBittorrent settings
    public string QBittorrentUrl { get; set; } = "http://localhost:8080";
    public string QBittorrentUsername { get; set; } = "admin";
    public string QBittorrentPassword { get; set; } = string.Empty;

    /// <summary>
    /// Delete the plugin's "acq-…" correlation tags from qBittorrent once their download is imported or the request
    /// is cancelled, so they don't pile up. Torrents are never removed.
    /// </summary>
    public bool RemoveTagsAfterImport { get; set; } = true;

    // Quality points (user decision): a release scores the points of its resolution + its source + its codec and the
    // highest total wins (ties: most seeders). Negative points push a quality down; nothing is ever rejected for it.
    public int Points2160p { get; set; } = 40;
    public int Points1080p { get; set; } = 100;
    public int Points720p { get; set; } = 60;
    public int Points480p { get; set; } = 10;
    public int PointsResolutionUnknown { get; set; } = 20;
    public int PointsWebDl { get; set; } = 100;
    public int PointsBluray { get; set; } = 90;
    public int PointsWebRip { get; set; } = 70;
    public int PointsHdtv { get; set; } = 30;
    public int PointsSourceUnknown { get; set; } = 20;
    public int PointsH264 { get; set; } = 30;
    public int PointsH265 { get; set; } = 30;
    public int PointsAv1 { get; set; } = 20;
    public int PointsCodecUnknown { get; set; } = 10;

    /// <summary>
    /// Look of the external search block: "auto" (Abyss when that theme is installed, otherwise classic Jellyfin),
    /// "jellyfin" or "abyss". Colours and sizes can still be overridden with --acquisition-* CSS variables.
    /// </summary>
    public string SearchStyle { get; set; } = "auto";

    /// <summary>Allowed value of <see cref="SearchStyle"/>, falling back to "auto".</summary>
    public string GetSearchStyle() => SearchStyle?.Trim().ToLowerInvariant() switch
    {
        "jellyfin" => "jellyfin",
        "abyss" => "abyss",
        _ => "auto"
    };

    /// <summary>
    /// "auto": the tracker sends the best eligible release by itself. "manual": the tracker only searches and waits
    /// for the user to pick a release (no race between the automatic choice and the user's click).
    /// </summary>
    public string ReleaseSelectionMode { get; set; } = "auto";

    public bool IsManualReleaseSelection() =>
        string.Equals(ReleaseSelectionMode?.Trim(), "manual", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Serve a Seerr-compatible API under /Acquisition/seerr so Seerr clients (plezy) can search and request through
    /// the plugin. Off by default: it adds an anonymous login endpoint.
    /// </summary>
    public bool EnableSeerrApi { get; set; }

    /// <summary>
    /// Per-user "Because you watched X" rows on the Jellyfin home page, mixing titles already in the library with
    /// titles that can be requested.
    /// </summary>
    public bool EnableRecommendations { get; set; } = true;

    /// <summary>
    /// Optional TMDB key (v3 API key or v4 read access token) for the Seerr API's discover rows and recommendations.
    /// Search, details and requests work without it.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Requests in progress a non-admin user may have at once (0 = no limit). Enforced automatically; adding seasons or
    /// episodes to a series already requested does not count.
    /// </summary>
    public int MaxActiveRequestsPerUser { get; set; } = 10;

    // Security & User Permissions
    public List<string> AllowedUserIds { get; set; } = [];
    public bool AllowAllUsers { get; set; } = false;
}
