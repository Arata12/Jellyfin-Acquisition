using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr;
using Jellyfin.Plugin.Acquisition.Adapters.QBittorrent;
using Jellyfin.Plugin.Acquisition.Adapters.QBittorrent.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;
using Jellyfin.Plugin.Acquisition.Catalog;
using Jellyfin.Plugin.Acquisition.Controllers;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Releases;
using Jellyfin.Plugin.Acquisition.Storage;
using Jellyfin.Plugin.Acquisition.Tracking;

namespace Jellyfin.Plugin.Acquisition.Tests;

/// <summary>
/// "Tus solicitudes" (list, poster reference) and the cancel that really cancels (torrents and Sonarr/Radarr titles).
/// </summary>
public class RequestManagementTests
{
    private static readonly string OpaquePoster = "/Acquisition/images/" + new string('a', 64);

    // ---- Poster reference ----

    [Theory]
    [InlineData("/Acquisition/images/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("https://image.tmdb.org/t/p/w500/abc.jpg", true)]
    [InlineData("http://example.com/p.png", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:image/png;base64,AAAA", false)]
    [InlineData("/Acquisition/images/../../etc/passwd", false)]
    [InlineData("/Acquisition/images/ABC", false)]
    [InlineData("https://user:pw@example.com/p.png", false)]
    [InlineData("https://example.com/\"onerror=\"x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Poster_OnlyOpaquePathOrHttpUrlIsAccepted(string? value, bool accepted)
    {
        var result = RequestPoster.Sanitize(value);
        Assert.Equal(accepted, result != null);
    }

    // ---- List ----

    private static AcquisitionRequest Req(string id, Guid user, RequestStatus status, DateTimeOffset updated, Action<AcquisitionRequest>? tweak = null)
    {
        var r = new AcquisitionRequest { Id = id, UserId = user, Kind = MediaKind.Movie, Title = id, Status = status, UpdatedAt = updated, CreatedAt = updated, ScopeConfirmed = true };
        tweak?.Invoke(r);
        return r;
    }

    [Fact]
    public void List_ShowsOneCardPerTitle_KeepingTheMostRelevantRequest()
    {
        var me = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        void Frieren(AcquisitionRequest r)
        {
            r.Kind = MediaKind.Series;
            r.Title = "Frieren";
            r.ProviderIds = new ProviderIds { Tvdb = "424536" };
        }

        var all = new[]
        {
            Req("old-done", me, RequestStatus.Available, now.AddDays(-2), Frieren),
            Req("active", me, RequestStatus.Searching, now.AddDays(-5), Frieren),
            Req("other", me, RequestStatus.Available, now.AddDays(-1))
        };

        var rows = RequestSummaries.Build(all, me, false, _ => null, now);

        Assert.Equal(["active", "other"], rows.Select(r => r.Id));
    }

    [Fact]
    public void List_NonAdminSeesOnlyOwn_AdminSeesAllWithRequesterName()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var all = new[]
        {
            Req("mine", me, RequestStatus.Searching, now),
            Req("theirs", other, RequestStatus.Searching, now)
        };

        var asUser = RequestSummaries.Build(all, me, false, _ => "x", now);
        var asAdmin = RequestSummaries.Build(all, me, true, id => id == other ? "ana" : "yo", now);

        Assert.Equal(["mine"], asUser.Select(r => r.Id));
        Assert.Null(asUser[0].RequestedBy);
        Assert.Equal(2, asAdmin.Count);
        Assert.Equal("ana", asAdmin.Single(r => r.Id == "theirs").RequestedBy);
    }

    [Fact]
    public void List_OrdersNeedsActionFirst_ThenActive_ThenFinished_AndDropsOldFinished()
    {
        var me = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var all = new[]
        {
            Req("done-recent", me, RequestStatus.Available, now.AddHours(-1), r => r.CompletedAt = now.AddHours(-1)),
            Req("done-old", me, RequestStatus.Available, now.AddDays(-40), r => r.CompletedAt = now.AddDays(-40)),
            Req("failed-recent", me, RequestStatus.Failed, now.AddDays(-2)),
            Req("active-old", me, RequestStatus.Downloading, now.AddMinutes(-30)),
            Req("active-new", me, RequestStatus.Searching, now.AddMinutes(-1)),
            Req("attention-old", me, RequestStatus.RequiresAttention, now.AddDays(-3)),
            Req("parked", me, RequestStatus.Downloading, now.AddMinutes(-90), r =>
            {
                r.Kind = MediaKind.Series;
                r.Units = [new DownloadUnit { Status = RequestStatus.RequiresAttention }, new DownloadUnit { Status = RequestStatus.Downloading }];
            })
        };

        var rows = RequestSummaries.Build(all, me, false, _ => null, now);

        Assert.Equal(["parked", "attention-old", "active-new", "active-old", "done-recent", "failed-recent"], rows.Select(r => r.Id));
        Assert.True(rows[0].NeedsAction);
        Assert.True(rows[1].NeedsAction);
        Assert.False(rows[2].NeedsAction);
        Assert.False(rows[4].NeedsAction);
    }

    [Fact]
    public void List_HidesSeriesStillInTheScopePicker_AndCarriesPosterAndProgress()
    {
        var me = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var picker = Req("picker", me, RequestStatus.Pending, now, r => { r.Kind = MediaKind.Series; r.ScopeConfirmed = false; });
        var series = Req("series", me, RequestStatus.Downloading, now, r =>
        {
            r.Kind = MediaKind.Series;
            r.FollowAiring = true;
            r.PosterUrl = OpaquePoster;
            r.Units =
            [
                new DownloadUnit { Status = RequestStatus.Available },
                new DownloadUnit { Status = RequestStatus.Available },
                new DownloadUnit { Status = RequestStatus.Downloading }
            ];
        });
        var evil = Req("evil", me, RequestStatus.Searching, now, r => r.PosterUrl = "javascript:alert(1)");

        var rows = RequestSummaries.Build([picker, series, evil], me, false, _ => null, now);

        Assert.DoesNotContain(rows, r => r.Id == "picker");
        var s = rows.Single(r => r.Id == "series");
        Assert.Equal(OpaquePoster, s.PosterUrl);
        Assert.Equal(3, s.Progress.Total);
        Assert.Equal(2, s.Progress.Available);
        Assert.True(s.Progress.Following);
        Assert.Null(rows.Single(r => r.Id == "evil").PosterUrl);
    }

    // ---- Cancel that really cancels ----

    private sealed class Rig
    {
        public Mock<IRequestRepository> Repo = new();
        public Mock<ISonarrClient> Sonarr = new();
        public Mock<IRadarrClient> Radarr = new();
        public Mock<IQBittorrentClient> Qb = new();
        public List<string> DeletedHashes = [];

        public Rig(AcquisitionRequest target, params AcquisitionRequest[] others)
        {
            Repo.Setup(r => r.GetByIdAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);
            Repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(others.Prepend(target).ToList());
            Qb.Setup(q => q.DeleteTorrentsAsync(It.IsAny<IReadOnlyCollection<string>>(), true, It.IsAny<CancellationToken>()))
                .Callback<IReadOnlyCollection<string>, bool, CancellationToken>((h, _, _) => DeletedHashes.AddRange(h))
                .ReturnsAsync(true);
            Qb.Setup(q => q.GetTorrentsInfoAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            Sonarr.Setup(s => s.DeleteSeriesAsync(It.IsAny<int>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Radarr.Setup(s => s.DeleteMovieAsync(It.IsAny<int>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        }

        public TrackerService Tracker() => new(
            Repo.Object, Sonarr.Object, Radarr.Object, Mock.Of<IProwlarrClient>(), Qb.Object, Mock.Of<IReleaseService>(),
            Mock.Of<ILocalMatcherService>(), Mock.Of<ILibraryManager>(), Mock.Of<ILogger<TrackerService>>());
    }

    private static AcquisitionRequest Series(string id, Action<AcquisitionRequest>? tweak = null)
    {
        var r = new AcquisitionRequest { Id = id, Kind = MediaKind.Series, Title = id, Status = RequestStatus.Downloading, ArrId = 7, ArrAddedByPlugin = true, ScopeConfirmed = true };
        tweak?.Invoke(r);
        return r;
    }

    [Fact]
    public async Task Cancel_WithDeleteDownloads_RemovesOnlyUnimportedTorrentsNotSharedWithOthers()
    {
        var req = Series("a", r => r.Units =
        [
            new DownloadUnit { Status = RequestStatus.Available, TorrentHash = "IMPORTED" },
            new DownloadUnit { Status = RequestStatus.Downloading, TorrentHash = "DOWNLOADING" },
            new DownloadUnit { Status = RequestStatus.Importing, TorrentHash = "SHARED" },
            new DownloadUnit { Status = RequestStatus.Downloading, TorrentHash = "imported" } // same torrent as an imported unit (season pack)
        ]);
        var other = Series("b", r => { r.ArrId = 99; r.Units = [new DownloadUnit { Status = RequestStatus.Downloading, TorrentHash = "shared" }]; });
        var rig = new Rig(req, other);

        var result = await rig.Tracker().CancelRequestAsync("a", _ => true, deleteDownloads: true);

        Assert.Equal(RetryOutcome.Success, result.Outcome);
        Assert.Equal(["DOWNLOADING"], rig.DeletedHashes);
    }

    [Fact]
    public async Task Cancel_WithDeleteDownloads_FindsTorrentsByTagWhenHashWasNotRecorded()
    {
        var req = Series("a", r => r.Units = [new DownloadUnit { Status = RequestStatus.Downloading, CorrelationTag = "acq-a-u1" }]);
        var rig = new Rig(req);
        rig.Qb.Setup(q => q.GetTorrentsInfoAsync(null, "acq-a-u1", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new QBittorrentTorrentInfo { Hash = "bytag" }]);

        await rig.Tracker().CancelRequestAsync("a", _ => true, deleteDownloads: true);

        Assert.Equal(["bytag"], rig.DeletedHashes);
    }

    [Fact]
    public async Task Cancel_WithoutDeleteDownloads_KeepsTorrentsAndArrTitle()
    {
        var req = Series("a", r => r.Units = [new DownloadUnit { Status = RequestStatus.Downloading, TorrentHash = "h" }]);
        var rig = new Rig(req);

        await rig.Tracker().CancelRequestAsync("a", _ => true);

        rig.Qb.Verify(q => q.DeleteTorrentsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Sonarr.Verify(s => s.DeleteSeriesAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_WithDeleteDownloads_RemovesSeriesFromSonarrOnlyWhenPluginAddedItAndItHasNoFiles()
    {
        var req = Series("a", r => r.Units = [new DownloadUnit { Status = RequestStatus.Downloading, TorrentHash = "h" }]);
        var rig = new Rig(req);
        rig.Sonarr.Setup(s => s.GetEpisodesAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync([new SonarrEpisode { HasFile = false }]);
        await rig.Tracker().CancelRequestAsync("a", _ => true, deleteDownloads: true);
        rig.Sonarr.Verify(s => s.DeleteSeriesAsync(7, false, It.IsAny<CancellationToken>()), Times.Once);

        // Sonarr already has files for it: kept
        var withFiles = Series("c", r => r.Units = [new DownloadUnit { Status = RequestStatus.Downloading }]);
        var rig2 = new Rig(withFiles);
        rig2.Sonarr.Setup(s => s.GetEpisodesAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync([new SonarrEpisode { HasFile = true }]);
        await rig2.Tracker().CancelRequestAsync("c", _ => true, deleteDownloads: true);
        rig2.Sonarr.Verify(s => s.DeleteSeriesAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

        // The series was already in Sonarr before the request: kept
        var preexisting = Series("d", r => { r.ArrAddedByPlugin = false; r.Units = [new DownloadUnit { Status = RequestStatus.Downloading }]; });
        var rig3 = new Rig(preexisting);
        await rig3.Tracker().CancelRequestAsync("d", _ => true, deleteDownloads: true);
        rig3.Sonarr.Verify(s => s.DeleteSeriesAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_KeepsArrTitleWhileAnotherActiveRequestUsesIt()
    {
        var req = Series("a", r => r.Units = [new DownloadUnit { Status = RequestStatus.Downloading }]);
        var other = Series("b", r => r.Units = [new DownloadUnit { Status = RequestStatus.Downloading }]); // same ArrId 7
        var rig = new Rig(req, other);
        rig.Sonarr.Setup(s => s.GetEpisodesAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync([new SonarrEpisode { HasFile = false }]);

        await rig.Tracker().CancelRequestAsync("a", _ => true, deleteDownloads: true);

        rig.Sonarr.Verify(s => s.DeleteSeriesAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_UnconfirmedSeries_AlwaysRemovesItFromSonarr_EvenWithoutDeleteDownloads()
    {
        var req = Series("a", r => { r.Status = RequestStatus.Pending; r.ScopeConfirmed = false; r.Units = []; });
        var rig = new Rig(req);

        await rig.Tracker().CancelRequestAsync("a", _ => true);

        rig.Sonarr.Verify(s => s.DeleteSeriesAsync(7, false, It.IsAny<CancellationToken>()), Times.Once);
        rig.Qb.Verify(q => q.DeleteTorrentsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_ConfirmedSeriesWithoutUnitsYet_IsNotRemovedFromSonarrByDefault()
    {
        var req = Series("a", r => { r.Status = RequestStatus.Pending; r.Units = []; });
        var rig = new Rig(req);

        await rig.Tracker().CancelRequestAsync("a", _ => true);

        rig.Sonarr.Verify(s => s.DeleteSeriesAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_Movie_WithDeleteDownloads_RemovesTorrentAndRadarrTitleWithoutFiles()
    {
        var req = new AcquisitionRequest
        {
            Id = "m", Kind = MediaKind.Movie, Status = RequestStatus.Downloading, ArrId = 5, ArrAddedByPlugin = true,
            TorrentHash = "MOVIEHASH", ScopeConfirmed = true
        };
        var rig = new Rig(req);
        rig.Radarr.Setup(r => r.GetMovieByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(new RadarrMovie { Id = 5, HasFile = false });

        await rig.Tracker().CancelRequestAsync("m", _ => true, deleteDownloads: true);

        Assert.Equal(["MOVIEHASH"], rig.DeletedHashes);
        rig.Radarr.Verify(r => r.DeleteMovieAsync(5, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancel_CleanupFailure_DoesNotFailTheCancel()
    {
        var req = Series("a", r => r.Units = [new DownloadUnit { Status = RequestStatus.Downloading, TorrentHash = "h" }]);
        var rig = new Rig(req);
        rig.Qb.Setup(q => q.DeleteTorrentsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("qBittorrent down"));

        var result = await rig.Tracker().CancelRequestAsync("a", _ => true, deleteDownloads: true);

        Assert.Equal(RetryOutcome.Success, result.Outcome);
        rig.Repo.Verify(r => r.DeleteAsync("a", It.IsAny<CancellationToken>()), Times.Once);
    }
}
