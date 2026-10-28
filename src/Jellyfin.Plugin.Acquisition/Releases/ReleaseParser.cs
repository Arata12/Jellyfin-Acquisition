using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Acquisition.Releases;

public static class ReleaseParser
{
    private static readonly Regex SeasonEpisodeRegex = new(@"[sS](?<season>\d{1,2})[eE](?<episode>\d{1,4})|\b(?<season>\d{1,2})x(?<episode>\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "[Group] Title - 123 [1080p]" style absolute numbering used by anime releases
    private static readonly Regex AbsoluteEpisodeRegex = new(@"\s-\s(?<abs>\d{1,4})(?:v\d)?(?=[\s\[\(.]|$)", RegexOptions.Compiled);
    private static readonly Regex SeasonPackRegex = new(@"[sS]eason[\s._-]*(?<season>\d{1,2})|[sS](?<season>\d{1,2})(?![eE\d])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex H264Regex = new(@"(?:x264|h264|h\.264|avc)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex H265Regex = new(@"(?:x265|h265|h\.265|hevc)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Av1Regex = new(@"\bav1\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Res2160p = new(@"(?:2160p|4k|uhd)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Res1080p = new(@"(?:1080p|1080i|fhd)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Res720p = new(@"(?:720p|hd\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Res480p = new(@"(?:480p|sd\b|dvd)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BlurayRegex = new(@"(?:bluray|blu-ray|bdrip|brrip)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WebRipRegex = new(@"\bweb[-_. ]?rip\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // WEB-DL in any spelling (WEB-DL, WEBDL, WEB.DL, WEB DL). A bare "WEB" is how scene WEB-DL releases are named
    // (as Sonarr/Radarr parse it), but only next to a resolution or codec token so titles like "Charlotte's Web" don't match.
    private static readonly Regex WebDlRegex = new(
        @"\bweb[-_. ]?dl\b|(?<=\b(?:2160p|1080p|1080i|720p|576p|480p|4k|uhd)[-_. ])web\b|\bweb(?=[-_. ](?:h[-_. ]?26[45]|x26[45]|hevc|avc|av1|aac|ddp?\d|ddp|dd\+|e-?ac-?3|atmos|opus|flac)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HdtvRegex = new(@"(?:hdtv|pdtv)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string NormalizeCodec(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        if (H264Regex.IsMatch(raw)) return "H.264";
        if (H265Regex.IsMatch(raw)) return "H.265";
        if (Av1Regex.IsMatch(raw)) return "AV1";
        return "Unknown";
    }

    public static string NormalizeResolution(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        if (Res2160p.IsMatch(raw)) return "2160p";
        if (Res1080p.IsMatch(raw)) return "1080p";
        if (Res720p.IsMatch(raw)) return "720p";
        if (Res480p.IsMatch(raw)) return "480p";
        return "Unknown";
    }

    public static string NormalizeSource(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        if (BlurayRegex.IsMatch(raw)) return "Bluray";
        if (WebRipRegex.IsMatch(raw)) return "WEBRip";
        if (WebDlRegex.IsMatch(raw)) return "WEB-DL";
        if (HdtvRegex.IsMatch(raw)) return "HDTV";
        return "Unknown";
    }

    /// <summary>
    /// Source reported by Sonarr ("web", "webRip", "television", "bluray"…) or Radarr ("webdl", "webrip", "tv"…),
    /// or "Unknown". Sonarr's "web" is WEB-DL, not WEBRip.
    /// </summary>
    public static string NormalizeArrSource(string? arrSource) => arrSource?.Trim().ToLowerInvariant() switch
    {
        "web" or "webdl" => "WEB-DL",
        "webrip" => "WEBRip",
        "bluray" or "blurayraw" => "Bluray",
        "television" or "televisionraw" or "tv" => "HDTV",
        _ => "Unknown"
    };

    /// <summary>
    /// Absolute episode number of releases without SxxEyy (anime), or -1 when none is present.
    /// </summary>
    public static int ParseAbsoluteEpisode(string title)
    {
        var match = AbsoluteEpisodeRegex.Match(title);
        return match.Success && int.TryParse(match.Groups["abs"].Value, out var abs) && abs > 0 ? abs : -1;
    }

    public static (int Season, int Episode, bool IsSeasonPack) ParseSeasonEpisode(string title)
    {
        var seMatch = SeasonEpisodeRegex.Match(title);
        if (seMatch.Success)
        {
            var s = int.Parse(seMatch.Groups["season"].Value);
            var e = int.Parse(seMatch.Groups["episode"].Value);
            return (s, e, false);
        }

        var spMatch = SeasonPackRegex.Match(title);
        if (spMatch.Success)
        {
            var s = int.Parse(spMatch.Groups["season"].Value);
            return (s, -1, true);
        }

        return (-1, -1, false);
    }
}
