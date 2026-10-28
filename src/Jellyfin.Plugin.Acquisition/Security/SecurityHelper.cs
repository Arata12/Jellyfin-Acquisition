using System.Security.Claims;
using MediaBrowser.Controller.Library;
using Jellyfin.Plugin.Acquisition.Configuration;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Security;

public static class SecurityHelper
{
    public const string JellyfinUserIdClaim = "Jellyfin-UserId";

    public static Guid? GetCurrentUserId(ClaimsPrincipal principal)
    {
        var idStr = principal.FindFirst(JellyfinUserIdClaim)?.Value
                 ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (!string.IsNullOrEmpty(idStr) && Guid.TryParse(idStr, out var id) && id != Guid.Empty)
        {
            return id;
        }

        return null;
    }

    public static bool IsAdmin(Guid userId, IUserManager userManager)
    {
        if (userId == Guid.Empty) return false;
        var user = userManager.GetUserById(userId);
        if (user == null) return false;

        return user.Permissions.Any(p =>
            p.Kind.ToString().Equals("IsAdministrator", StringComparison.OrdinalIgnoreCase) && p.Value);
    }

    public static bool CanAcquire(Guid userId, IUserManager userManager, PluginConfiguration? config)
    {
        if (userId == Guid.Empty) return false;
        var user = userManager.GetUserById(userId);
        if (user == null) return false;

        if (IsAdmin(userId, userManager)) return true;
        if (config == null) return false;

        if (config.AllowAllUsers) return true;

        return config.AllowedUserIds.Contains(userId.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    public static bool CanViewRequest(Guid userId, AcquisitionRequest request, IUserManager userManager, PluginConfiguration? config)
    {
        if (userId == Guid.Empty) return false;
        if (IsAdmin(userId, userManager)) return true;
        return request.UserId == userId;
    }

    public static bool CanManageRequest(Guid userId, AcquisitionRequest request, IUserManager userManager, PluginConfiguration? config)
    {
        if (userId == Guid.Empty) return false;
        if (IsAdmin(userId, userManager)) return true;
        if (request.UserId != userId) return false;

        return CanAcquire(userId, userManager, config);
    }

    public static string RedactSecrets(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        // Redact api keys, passkeys, tokens
        var result = System.Text.RegularExpressions.Regex.Replace(
            input,
            @"(?i)(api[_-]?key|apikey|token|passkey)=([^&""\s]+)",
            "$1=[REDACTED]");

        // Redact basic auth in URLs, keeping the original scheme
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(https?://)([^:/@\s]+):([^@\s]+)@",
            "$1$2:[REDACTED]@");

        return result;
    }
}
