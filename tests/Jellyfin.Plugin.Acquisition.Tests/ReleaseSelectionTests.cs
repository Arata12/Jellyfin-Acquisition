using Moq;
using Xunit;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Releases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Acquisition.Tests;

public class ReleaseSelectionTests
{
    [Theory]
    [InlineData("Show.S01E01.1080p.WEB.H264-GROUP", "WEB-DL")]           // scene naming for WEB-DL
    [InlineData("Show.S01E01.2160p.WEB.h265-GROUP", "WEB-DL")]
    [InlineData("Show.S01E01.1080p.AMZN.WEB.DL.DDP5.1.H.264-GROUP", "WEB-DL")]
    [InlineData("Show S01E01 1080p WEB DL AAC", "WEB-DL")]
    [InlineData("Movie.2023.1080p.WEBDL.x264", "WEB-DL")]
    [InlineData("Movie.2023.1080p.WEBRip.x264-GROUP", "WEBRip")]
    [InlineData("Movie.2023.1080p.WEB-Rip.x265", "WEBRip")]
    [InlineData("Movie 2023 1080p WEB Rip", "WEBRip")]
    [InlineData("Charlottes.Web.2006.DVDRip.XviD", "Unknown")]          // "Web" in the title is not a source
    [InlineData("Charlottes.Web.2006.1080p.BluRay.x264", "Bluray")]
    public void NormalizeSource_RecognisesWebDlAndWebRipSpellings(string title, string expected)
    {
        Assert.Equal(expected, ReleaseParser.NormalizeSource(title));
    }

    [Theory]
    [InlineData("web", "WEB-DL")]        // Sonarr: WEBDL
    [InlineData("webRip", "WEBRip")]     // Sonarr
    [InlineData("webdl", "WEB-DL")]      // Radarr
    [InlineData("webrip", "WEBRip")]     // Radarr
    [InlineData("television", "HDTV")]
    [InlineData("bluray", "Bluray")]
    [InlineData("unknown", "Unknown")]
    [InlineData(null, "Unknown")]
    public void NormalizeArrSource_MapsSonarrAndRadarrSources(string? arrSource, string expected)
    {
        Assert.Equal(expected, ReleaseParser.NormalizeArrSource(arrSource));
    }

    [Theory]
    [InlineData("Movie.2023.1080p.WEB-DL.x264", "H.264", "1080p", "WEB-DL")]
    [InlineData("Show.S02E04.720p.HDTV.h264", "H.264", "720p", "HDTV")]
    [InlineData("Show.S01.2160p.UHD.BluRay.x265.HEVC", "H.265", "2160p", "Bluray")]
    [InlineData("Movie.1080p.AVC.DTS-HD", "H.264", "1080p", "Unknown")]
    public void ReleaseParser_TokenExtraction_CorrectlyNormalizes(string title, string expectedCodec, string expectedRes, string expectedSource)
    {
        Assert.Equal(expectedCodec, ReleaseParser.NormalizeCodec(title));
        Assert.Equal(expectedRes, ReleaseParser.NormalizeResolution(title));
        Assert.Equal(expectedSource, ReleaseParser.NormalizeSource(title));
    }

    [Theory]
    [InlineData("Show.Name.S03E12.1080p.mkv", 3, 12, false)]
    [InlineData("Show.Name.2x05.720p.mkv", 2, 5, false)]
    [InlineData("Show.Name.Season.1.Complete.1080p", 1, -1, true)]
    [InlineData("Movie.Title.2020.1080p", -1, -1, false)]
    public void ReleaseParser_ParseSeasonEpisode_CorrectlyIdentifies(string title, int expectedSeason, int expectedEpisode, bool expectedPack)
    {
        var (season, episode, isPack) = ReleaseParser.ParseSeasonEpisode(title);
        Assert.Equal(expectedSeason, season);
        Assert.Equal(expectedEpisode, episode);
        Assert.Equal(expectedPack, isPack);
    }

    [Fact]
    public async Task SearchAndEvaluateReleases_FiltersNonTorrentAndScoresPreferences()
    {
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockLogger = new Mock<ILogger<ReleaseService>>();

        var releases = new List<ProwlarrRelease>
        {
            new() { Title = "Show.S01E01.1080p.WEB-DL.x264", Protocol = "torrent", Seeders = 50, Size = 1_500_000_000L },
            new() { Title = "Show.S01E01.720p.HDTV.x264", Protocol = "torrent", Seeders = 10, Size = 600_000_000L },
            new() { Title = "Show.S01E01.Usenet.nzb", Protocol = "usenet", Seeders = 0, Size = 1_500_000_000L }, // should be rejected (protocol)
            new() { Title = "Show.S02E01.1080p.WEB-DL.x264", Protocol = "torrent", Seeders = 100, Size = 1_500_000_000L } // wrong season
        };

        mockProwlarr.Setup(p => p.SearchAsync(It.IsAny<ProwlarrSearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(releases);

        var service = new ReleaseService(mockProwlarr.Object, mockSonarr.Object, mockRadarr.Object, mockLogger.Object);

        var request = new AcquisitionRequest
        {
            Title = "Show",
            Kind = MediaKind.Series,
            Scope = new RequestScope { All = false, SeasonNumbers = [1], EpisodeNumbers = [1] }
        };

        var evaluated = await service.SearchAndEvaluateReleasesAsync(request);

        Assert.Equal(4, evaluated.Count);

        // 1080p WEB-DL should be eligible and top scored
        var top = evaluated[0];
        Assert.True(top.IsEligible);
        Assert.Equal("1080p", top.Resolution);
        Assert.Equal("Show.S01E01.1080p.WEB-DL.x264", top.Title);

        // Usenet release should be rejected
        var usenet = evaluated.First(e => e.Title.Contains("Usenet"));
        Assert.False(usenet.IsEligible);
        Assert.Contains("Protocol is not torrent", usenet.RejectionReasons);

        // Season 2 release should be rejected for Season 1 request
        var s2 = evaluated.First(e => e.Title.Contains("S02E01"));
        Assert.False(s2.IsEligible);
        Assert.Contains(s2.RejectionReasons, r => r.Contains("Season mismatch"));
    }
}
