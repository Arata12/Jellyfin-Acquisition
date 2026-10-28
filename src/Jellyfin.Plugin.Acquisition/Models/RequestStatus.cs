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
    Failed
}
