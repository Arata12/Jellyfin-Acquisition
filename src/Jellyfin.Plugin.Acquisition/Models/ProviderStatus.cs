namespace Jellyfin.Plugin.Acquisition.Models;

/// <summary>
/// Status of an external catalog or download provider.
/// </summary>
public enum ProviderStatus
{
    Ready,
    NotConfigured,
    Unauthorized,
    Unavailable,
    RateLimited
}
