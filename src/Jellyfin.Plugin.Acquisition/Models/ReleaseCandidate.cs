namespace Jellyfin.Plugin.Acquisition.Models;

/// <summary>
/// A candidate release parsed from indexers.
/// </summary>
public class ReleaseCandidate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string? InfoHash { get; set; }
    public string? MagnetUri { get; set; }
    public string? DownloadUrl { get; set; }
    public long Size { get; set; }
    public int Seeders { get; set; }
    public int Leechers { get; set; }
    public string Indexer { get; set; } = string.Empty;
    public int IndexerPriority { get; set; }

    public string? Codec { get; set; }
    public string? Resolution { get; set; }
    public string? Source { get; set; }
    public string? Quality { get; set; }
    public List<string> Languages { get; set; } = [];

    public bool IsTorrent { get; set; } = true;
    public int SeasonNumber { get; set; } = -1;
    public int EpisodeNumber { get; set; } = -1;
    public int AbsoluteEpisodeNumber { get; set; } = -1;
    public bool IsSeasonPack { get; set; }

    public int Score { get; set; }
    public bool IsEligible { get; set; } = true;
    public bool IdentityVerified { get; set; }
    public List<string> RejectionReasons { get; set; } = [];
}
