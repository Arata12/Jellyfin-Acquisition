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

    // Release Preferences
    public string PreferredResolution { get; set; } = "1080p";
    public string PreferredSource { get; set; } = "WEB-DL";
    public string PreferredCodec { get; set; } = "H.264";
    public long MaxMovieSizeBytes { get; set; } = 15_000_000_000L;
    public long MaxEpisodeSizeBytes { get; set; } = 4_000_000_000L;
    public long MaxSeasonSizeBytes { get; set; } = 40_000_000_000L;

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

    // Security & User Permissions
    public List<string> AllowedUserIds { get; set; } = [];
    public bool AllowAllUsers { get; set; } = false;
}
