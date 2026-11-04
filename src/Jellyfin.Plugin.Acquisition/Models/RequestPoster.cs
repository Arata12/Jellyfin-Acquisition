using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Acquisition.Models;

/// <summary>
/// Validates the poster reference stored on a request: only the plugin's own opaque image path or a plain http(s) URL,
/// never an arbitrary string (it ends up in an img src of every viewer of the list).
/// </summary>
public static class RequestPoster
{
    private const int MaxLength = 500;

    private static readonly Regex OpaquePath = new(@"^/Acquisition/images/[a-f0-9]{64}$", RegexOptions.Compiled);

    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var poster = value.Trim();
        if (poster.Length > MaxLength) return null;
        if (OpaquePath.IsMatch(poster)) return poster;

        if (Uri.TryCreate(poster, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(uri.UserInfo)
            && poster.All(c => c > ' ' && c != '"' && c != '\'' && c != '<' && c != '>'))
        {
            return poster;
        }

        return null;
    }
}
