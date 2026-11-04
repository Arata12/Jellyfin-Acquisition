namespace Jellyfin.Plugin.Acquisition.Models;

/// <summary>
/// Status of an acquisition request.
/// </summary>
public enum RequestStatus
{
    Pending,
    Searching,
    Downloading,
    Importing,
    Available,
    RequiresAttention,
    Failed,

    /// <summary>Download unit the user (or the 14-day limit) dropped; the request no longer waits for it.</summary>
    Skipped
}
