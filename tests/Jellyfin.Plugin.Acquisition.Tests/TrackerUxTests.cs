using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.QBittorrent;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;
using Jellyfin.Plugin.Acquisition.Catalog;
using Jellyfin.Plugin.Acquisition.Configuration;
using Jellyfin.Plugin.Acquisition.Controllers;
using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Messages;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Releases;
using Jellyfin.Plugin.Acquisition.Storage;
using Jellyfin.Plugin.Acquisition.Tracking;

namespace Jellyfin.Plugin.Acquisition.Tests;

/// <summary>
/// Spanish messages, waiting instead of searching forever, parked downloads that never resolve, episodes Sonarr does
/// not know about and the switch from manual to automatic release selection.
/// </summary>
public class TrackerUxTests
{
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    private static PluginConfiguration UsePluginConfiguration(PluginConfiguration config)
    {
        var mockAppPaths = new Mock<IApplicationPaths>();
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(tempDir);
        mockAppPaths.Setup(p => p.PluginsPath).Returns(tempDir);
        var mockXml = new Mock<IXmlSerializer>();
        mockXml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(config);
        return new Plugin(mockAppPaths.Object, mockXml.Object).Configuration;
    }

    private static Mock<IRequestRepository> RepoFor(AcquisitionRequest request)
    {
        var repo = new Mock<IRequestRepository>();
        repo.Setup(r => r.GetByIdAsync(request.Id, It.IsAny<CancellationToken>())).ReturnsAsync(request);
        repo.Setup(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return repo;
    }

    private static Mock<IReleaseService> ReleasesReturning(List<ReleaseCandidate> candidates)
    {
        var releases = new Mock<IReleaseService>();
        releases.Setup(r => r.SearchAndEvaluateReleasesForUnitAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<DownloadUnit>(), It.IsAny<CancellationToken>())).ReturnsAsync(candidates);
        releases.Setup(r => r.SearchAndEvaluateReleasesAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(candidates);
        releases.Setup(r => r.PickBestRelease(It.IsAny<IEnumerable<ReleaseCandidate>>()))
            .Returns<IEnumerable<ReleaseCandidate>>(c => c.FirstOrDefault(x => x.IsEligible));
        return releases;
    }

    private static TrackerService CreateTracker(
        Mock<IRequestRepository> repo,
        Mock<ISonarrClient>? sonarr = null,
        Mock<IRadarrClient>? radarr = null,
        Mock<IProwlarrClient>? prowlarr = null,
        Mock<IReleaseService>? releases = null,
        Mock<ILocalMatcherService>? local = null)
    {
        return new TrackerService(
            repo.Object,
            (sonarr ?? new Mock<ISonarrClient>()).Object,
            (radarr ?? new Mock<IRadarrClient>()).Object,
            (prowlarr ?? new Mock<IProwlarrClient>()).Object,
            new Mock<IQBittorrentClient>().Object,
            (releases ?? ReleasesReturning([])).Object,
            (local ?? new Mock<ILocalMatcherService>()).Object,
            Mock.Of<ILibraryManager>(),
            Mock.Of<ILogger<TrackerService>>());
    }

    private static EpisodeTarget Target(int id, int season, int episode) =>
        new() { SeriesId = 5, EpisodeId = id, SeasonNumber = season, EpisodeNumber = episode };

    private static DownloadUnit EpisodeUnit(int id, int season, int episode, RequestStatus status) => new()
    {
        Id = $"u{id}",
        SeasonNumber = season,
        EpisodeNumbers = [episode],
        EpisodeIds = [id],
        Status = status
    };

    // ---- 1. Friendly Spanish messages ----

    [Fact]
    public void FriendlyErrors_MapHttpFailuresToPlainSpanish()
    {
        Assert.Equal("Ese torrent ya no existe en el indexador; se buscará otro.", FriendlyErrors.TorrentDownload(HttpStatusCode.Gone));
        Assert.Equal("Ese torrent ya no existe en el indexador; se buscará otro.", FriendlyErrors.TorrentDownload(HttpStatusCode.NotFound));
        Assert.Contains("clave de API", FriendlyErrors.FromStatus(HttpStatusCode.Unauthorized, "Sonarr"));
        Assert.Contains("Sonarr", FriendlyErrors.FromStatus(HttpStatusCode.Forbidden, "Sonarr"));
        Assert.Contains("URL", FriendlyErrors.FromStatus(HttpStatusCode.NotFound, "Radarr"));
        Assert.Contains("tardó demasiado", FriendlyErrors.FromException(new TaskCanceledException(), "Prowlarr"));
        Assert.Contains("tardó demasiado", FriendlyErrors.FromException(new HttpRequestException("t", new TimeoutException()), "Prowlarr"));

        var refused = new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
        var message = FriendlyErrors.FromException(refused, "qBittorrent");
        Assert.Contains("conexión rechazada", message);
        Assert.Contains("qBittorrent", message);
        Assert.DoesNotContain("Connection refused", message);
    }

    [Fact]
    public async Task ProwlarrClient_Fetch410_SaysTheTorrentIsGone()
    {
        UsePluginConfiguration(new PluginConfiguration { ProwlarrApiKey = "k", ProwlarrUrl = "http://prowlarr.test" });
        var client = new ProwlarrClient(new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Gone))), Mock.Of<ILogger<ProwlarrClient>>());

        var result = await client.FetchTorrentAsync("http://prowlarr.test/dl/1.torrent");

        Assert.False(result.IsSuccess);
        Assert.True(result.IsGone);
        Assert.Equal("Ese torrent ya no existe en el indexador; se buscará otro.", result.ErrorMessage);
    }

    [Fact]
    public async Task ProwlarrClient_SearchFailure_ExplainsInSpanish()
    {
        UsePluginConfiguration(new PluginConfiguration { ProwlarrApiKey = "k" });
        var client = new ProwlarrClient(new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))), Mock.Of<ILogger<ProwlarrClient>>(), TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<ProwlarrUnavailableException>(() => client.SearchAsync(new ProwlarrSearchQuery { Query = "Dune", Kind = MediaKind.Movie }));

        Assert.Contains("clave de API", ex.Message);
        Assert.DoesNotContain("rejected", ex.Message);
    }

    [Fact]
    public async Task Tracker_GoneTorrent_IsNotPickedAgainAndAnotherReleaseIsSearched()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var dead = new ReleaseCandidate { Id = "a", Title = "Dune.2021.1080p.WEB-DL", InfoHash = "AAA", Indexer = "idx", DownloadUrl = "http://x/1", IsEligible = true };
        var alive = new ReleaseCandidate { Id = "b", Title = "Dune.2021.720p.WEB-DL", InfoHash = "BBB", Indexer = "idx", DownloadUrl = "http://x/2", IsEligible = true };
        var req = new AcquisitionRequest
        {
            Id = "gone",
            Kind = MediaKind.Movie,
            Title = "Dune",
            ArrId = 9,
            Category = "radarr",
            Status = RequestStatus.Searching,
            Units = [new DownloadUnit { Id = "u", Status = RequestStatus.Searching, SelectedRelease = dead }]
        };
        req.SelectedRelease = dead;
        var prowlarr = new Mock<IProwlarrClient>();
        prowlarr.Setup(p => p.FetchTorrentAsync("http://x/1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TorrentDownloadResult { ErrorMessage = FriendlyErrors.TorrentDownload(HttpStatusCode.Gone), IsGone = true });
        prowlarr.Setup(p => p.FetchTorrentAsync("http://x/2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TorrentDownloadResult { MagnetUri = "magnet:?xt=urn:btih:BBB", InfoHash = "BBB" });
        var releases = ReleasesReturning([dead, alive]);
        var tracker = CreateTracker(RepoFor(req), prowlarr: prowlarr, releases: releases);

        await tracker.ProcessRequestAsync(req);

        // The dead release is dropped on the spot and the user is told why
        Assert.Null(req.Units[0].SelectedRelease);
        Assert.Contains("ya no existe en el indexador", req.History.Last().Message);

        await tracker.ProcessRequestAsync(req);

        Assert.Equal("BBB", req.Units[0].SelectedRelease?.InfoHash);
        Assert.False(dead.IsEligible);
        Assert.Contains("Ese torrent ya no existe en el indexador.", dead.RejectionReasons);
    }

    // ---- 2a. Movies wait for their digital release ----

    [Fact]
    public void SearchSchedule_Movie_WaitsForFutureDigitalOrPhysicalRelease()
    {
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

        var digital = SearchSchedule.ForMovie(now.UtcDateTime.AddDays(-30), new DateTime(2027, 3, 12, 0, 0, 0, DateTimeKind.Utc), null, now);
        Assert.NotNull(digital);
        Assert.Contains("12/03/2027", digital!.Note);
        Assert.Equal(new DateTimeOffset(2027, 3, 12, 0, 0, 0, TimeSpan.Zero), digital.RetryAt);

        var physical = SearchSchedule.ForMovie(null, null, new DateTime(2027, 5, 1, 0, 0, 0, DateTimeKind.Utc), now);
        Assert.NotNull(physical);
        Assert.Contains("01/05/2027", physical!.Note);

        // Already out digitally: search, even if the disc comes later
        Assert.Null(SearchSchedule.ForMovie(null, now.UtcDateTime.AddDays(-2), now.UtcDateTime.AddDays(30), now));
    }

    [Fact]
    public void SearchSchedule_MovieWithoutDigitalDate_WaitsOnlyAfterRecentCinemaRelease()
    {
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

        var recent = SearchSchedule.ForMovie(now.UtcDateTime.AddDays(-20), null, null, now);
        Assert.NotNull(recent);
        Assert.Equal("Estreno en cines reciente; se buscará cuando salga en digital.", recent!.Note);
        Assert.Equal(now.AddHours(24), recent.RetryAt);

        Assert.NotNull(SearchSchedule.ForMovie(now.UtcDateTime.AddDays(10), null, null, now));
        Assert.Null(SearchSchedule.ForMovie(now.UtcDateTime.AddDays(-200), null, null, now));
        Assert.Null(SearchSchedule.ForMovie(null, null, null, now));
    }

    [Fact]
    public async Task Tracker_MovieNotYetDigital_WaitsWithoutSearching()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var digital = DateTime.UtcNow.Date.AddDays(40);
        var req = new AcquisitionRequest
        {
            Id = "wait",
            Kind = MediaKind.Movie,
            Title = "Dune",
            ArrId = 9,
            Status = RequestStatus.Searching,
            Units = [new DownloadUnit { Id = "u", Status = RequestStatus.Pending }]
        };
        var radarr = new Mock<IRadarrClient>();
        radarr.Setup(r => r.GetMovieByIdAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RadarrMovie { Id = 9, InCinemas = DateTime.UtcNow.AddDays(-20), DigitalRelease = digital });
        var releases = ReleasesReturning([]);

        await CreateTracker(RepoFor(req), radarr: radarr, releases: releases).ProcessRequestAsync(req);

        releases.Verify(r => r.SearchAndEvaluateReleasesForUnitAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<DownloadUnit>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(new DateTimeOffset(digital, TimeSpan.Zero), req.NextAttemptUtc);
        Assert.StartsWith("Lanzamiento digital el " + digital.ToString("dd/MM/yyyy"), req.WaitingNote);
        Assert.Equal(RequestStatus.Searching, req.Status);

        // The same note is exposed where the series "waitingForAir" is
        var controller = CreateController(RepoFor(req), req, out _);
        var dto = (AcquisitionRequestDto)((OkObjectResult)(await controller.GetRequest("wait", CancellationToken.None)).Result!).Value!;
        Assert.Equal(req.WaitingNote, dto.Progress.WaitingForAir);
        Assert.Equal(req.NextAttemptUtc, dto.NextAttemptUtc);
    }

    [Fact]
    public async Task Tracker_MovieAlreadyDigital_Searches()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = new AcquisitionRequest
        {
            Id = "go",
            Kind = MediaKind.Movie,
            Title = "Dune",
            ArrId = 9,
            Status = RequestStatus.Searching,
            WaitingNote = "Lanzamiento digital el 01/01/2026; se buscará a partir de esa fecha.",
            Units = [new DownloadUnit { Id = "u", Status = RequestStatus.Pending }]
        };
        var radarr = new Mock<IRadarrClient>();
        radarr.Setup(r => r.GetMovieByIdAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RadarrMovie { Id = 9, DigitalRelease = DateTime.UtcNow.AddDays(-3) });
        var releases = ReleasesReturning([]);

        await CreateTracker(RepoFor(req), radarr: radarr, releases: releases).ProcessRequestAsync(req);

        releases.Verify(r => r.SearchAndEvaluateReleasesForUnitAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<DownloadUnit>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(req.WaitingNote);
    }

    // ---- 2b. Backoff for empty searches ----

    [Theory]
    [InlineData(0, 5)]
    [InlineData(30, 5)]
    [InlineData(59, 5)]
    [InlineData(60, 60)]
    [InlineData(23 * 60, 60)]
    [InlineData(24 * 60, 360)]
    [InlineData(5 * 24 * 60, 360)]
    public void SearchSchedule_EmptySearchDelay_GrowsFromFiveMinutesToSixHours(int minutesSinceFirst, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), SearchSchedule.EmptySearchDelay(TimeSpan.FromMinutes(minutesSinceFirst)));
    }

    [Fact]
    public async Task Tracker_EmptySearches_BackOffAndExposeNextAttempt()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = new AcquisitionRequest
        {
            Id = "empty",
            Kind = MediaKind.Series,
            Title = "Show",
            ArrId = 5,
            Status = RequestStatus.Searching,
            Units = [EpisodeUnit(101, 1, 1, RequestStatus.Pending)]
        };
        req.Units[0].FirstEmptySearchUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(3);
        var tracker = CreateTracker(RepoFor(req), releases: ReleasesReturning([]));

        await tracker.ProcessRequestAsync(req);

        // Three hours into the run: hourly, not every five minutes
        Assert.InRange(req.NextAttemptUtc!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));

        var controller = CreateController(RepoFor(req), req, out _);
        var dto = (AcquisitionRequestDto)((OkObjectResult)(await controller.GetRequest("empty", CancellationToken.None)).Result!).Value!;
        Assert.Equal(req.NextAttemptUtc, dto.NextAttemptUtc);
    }

    [Fact]
    public async Task Tracker_FirstEmptySearch_StartsTheRunAndRetriesInFiveMinutes()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = new AcquisitionRequest
        {
            Id = "first",
            Kind = MediaKind.Series,
            Title = "Show",
            ArrId = 5,
            Status = RequestStatus.Searching,
            Units = [EpisodeUnit(101, 1, 1, RequestStatus.Pending)]
        };

        await CreateTracker(RepoFor(req), releases: ReleasesReturning([])).ProcessRequestAsync(req);

        Assert.NotNull(req.Units[0].FirstEmptySearchUtc);
        Assert.InRange(req.NextAttemptUtc!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(6));
        Assert.Contains("pausas cada vez más largas", req.History.Last().Message);
    }

    // ---- 3. Parked downloads that never resolve ----

    private static AcquisitionRequest SeriesWithParkedEpisode(Action<DownloadUnit>? tweak = null)
    {
        var parked = EpisodeUnit(102, 1, 2, RequestStatus.RequiresAttention);
        parked.ErrorMessage = "Ese torrent ya no existe en el indexador.";
        parked.ParkedAtUtc = DateTimeOffset.UtcNow;
        parked.FirstParkedUtc = DateTimeOffset.UtcNow;
        tweak?.Invoke(parked);
        return new AcquisitionRequest
        {
            Id = "parked",
            Kind = MediaKind.Series,
            Title = "Show",
            ArrId = 5,
            Status = RequestStatus.RequiresAttention,
            Scope = new RequestScope { All = false, EpisodeIds = [101, 102], TargetEpisodes = [Target(101, 1, 1), Target(102, 1, 2)] },
            Units = [EpisodeUnit(101, 1, 1, RequestStatus.Available), parked]
        };
    }

    [Fact]
    public async Task SkipParked_DropsParkedUnits_AndRequestBecomesAvailableWhenTheRestIs()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = SeriesWithParkedEpisode();
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.GetEpisodesAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync([
            new SonarrEpisode { Id = 101, SeasonNumber = 1, EpisodeNumber = 1, HasFile = true },
            new SonarrEpisode { Id = 102, SeasonNumber = 1, EpisodeNumber = 2, HasFile = false }
        ]);
        var seriesGuid = Guid.NewGuid();
        var local = new Mock<ILocalMatcherService>();
        local.Setup(l => l.FindLocalMatch(It.IsAny<Guid>(), MediaKind.Series, "Show", It.IsAny<int?>(), It.IsAny<ProviderIds>())).Returns(seriesGuid);
        // Only the episode that was not skipped has to be in Jellyfin
        local.Setup(l => l.AreEpisodesAvailable(It.IsAny<Guid>(), seriesGuid, It.Is<IReadOnlyList<EpisodeTarget>>(t => t.Count == 1 && t[0].EpisodeId == 101))).Returns(true);
        var tracker = CreateTracker(RepoFor(req), sonarr: sonarr, local: local);

        var result = await tracker.SkipParkedAsync("parked", _ => true);

        Assert.Equal(RetryOutcome.Success, result.Outcome);
        Assert.Equal(RequestStatus.Skipped, req.Units[1].Status);
        Assert.Equal(RequestStatus.Importing, req.Status);

        await tracker.ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.Available, req.Status);

        var controller = CreateController(RepoFor(req), req, out _);
        var dto = (AcquisitionRequestDto)((OkObjectResult)(await controller.GetRequest("parked", CancellationToken.None)).Result!).Value!;
        Assert.Equal(1, dto.Progress.Skipped);
        Assert.Equal("S01E02", dto.Progress.SkippedItems.Single().Label);
        Assert.Equal(0, dto.Progress.Attention);
    }

    [Fact]
    public async Task SkipParked_WithNothingParked_IsRejected()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = SeriesWithParkedEpisode();
        req.Units[1].Status = RequestStatus.Available;
        req.Status = RequestStatus.Downloading;

        var result = await CreateTracker(RepoFor(req)).SkipParkedAsync("parked", _ => true);

        Assert.Equal(RetryOutcome.InvalidStatus, result.Outcome);
        Assert.Equal("No hay descargas apartadas que omitir.", result.ErrorMessage);
    }

    [Fact]
    public async Task SkipParked_RespectsPermissions()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = SeriesWithParkedEpisode();

        var result = await CreateTracker(RepoFor(req)).SkipParkedAsync("parked", _ => false);

        Assert.Equal(RetryOutcome.Forbidden, result.Outcome);
        Assert.Equal(RequestStatus.RequiresAttention, req.Units[1].Status);
    }

    [Fact]
    public async Task SkipParked_EverythingSkippedAndNothingDownloaded_ClosesTheRequest()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = SeriesWithParkedEpisode();
        req.Units[0].Status = RequestStatus.RequiresAttention;

        var result = await CreateTracker(RepoFor(req)).SkipParkedAsync("parked", _ => true);

        Assert.Equal(RetryOutcome.Success, result.Outcome);
        Assert.Equal(RequestStatus.Failed, req.Status);
        Assert.All(req.Units, u => Assert.Equal(RequestStatus.Skipped, u.Status));
    }

    [Fact]
    public async Task SkipParked_OtherUnitsStillPending_ResumesSearching()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = SeriesWithParkedEpisode();
        req.Units[0].Status = RequestStatus.Pending;

        var result = await CreateTracker(RepoFor(req)).SkipParkedAsync("parked", _ => true);

        Assert.Equal(RetryOutcome.Success, result.Outcome);
        Assert.Equal(RequestStatus.Searching, req.Status);
        Assert.Equal(RequestStatus.Pending, req.Units[0].Status);
    }

    [Fact]
    public async Task AutoPolicy_ParkedForADay_IsRequeuedAutomatically()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = SeriesWithParkedEpisode(u =>
        {
            u.ParkedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(25);
            u.FirstParkedUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(25);
        });

        await CreateTracker(RepoFor(req)).ProcessRequestAsync(req);

        Assert.NotEqual(RequestStatus.RequiresAttention, req.Units[1].Status);
        Assert.NotEqual(RequestStatus.Skipped, req.Units[1].Status);
        Assert.NotEqual(RequestStatus.RequiresAttention, req.Status);
        Assert.Contains(req.History, h => h.Message.Contains("Se vuelve a intentar automáticamente"));
    }

    [Fact]
    public async Task AutoPolicy_ParkedForLessThanADay_IsLeftAlone()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = SeriesWithParkedEpisode(u => u.ParkedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(23));

        await CreateTracker(RepoFor(req)).ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.RequiresAttention, req.Units[1].Status);
        Assert.Equal(RequestStatus.RequiresAttention, req.Status);
    }

    [Fact]
    public async Task AutoPolicy_ParkedForFourteenDays_IsSkippedWithReason()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = SeriesWithParkedEpisode(u =>
        {
            u.FirstParkedUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(15);
            u.ParkedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(2); // parked again after a daily retry
        });

        await CreateTracker(RepoFor(req)).ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.Skipped, req.Units[1].Status);
        Assert.Equal("No se encontró en 14 días; puedes volver a pedirlo más tarde.", req.Units[1].ErrorMessage);
        Assert.NotEqual(RequestStatus.RequiresAttention, req.Status);
    }

    [Fact]
    public async Task AutoPolicy_ParkingTwice_KeepsTheFirstParkedDate()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var first = DateTimeOffset.UtcNow - TimeSpan.FromDays(3);
        var req = new AcquisitionRequest
        {
            Id = "repark",
            Kind = MediaKind.Series,
            Title = "Show",
            ArrId = 5,
            Status = RequestStatus.Searching,
            Units = [EpisodeUnit(101, 1, 1, RequestStatus.Pending), EpisodeUnit(102, 1, 2, RequestStatus.Pending)]
        };
        req.Units[0].FirstParkedUtc = first;
        req.Units[0].SearchAttempts = 2;

        await CreateTracker(RepoFor(req), releases: ReleasesReturning([])).ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.RequiresAttention, req.Units[0].Status);
        Assert.Equal(first, req.Units[0].FirstParkedUtc);
        Assert.True(req.Units[0].ParkedAtUtc > first);
    }

    [Fact]
    public async Task Retry_ResetsTheParkedClock()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = SeriesWithParkedEpisode(u => u.FirstParkedUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(10));

        var result = await CreateTracker(RepoFor(req)).RetryRequestAsync("parked", _ => true);

        Assert.Equal(RetryOutcome.Success, result.Outcome);
        Assert.Null(req.Units[1].FirstParkedUtc);
        Assert.Null(req.Units[1].ParkedAtUtc);
    }

    [Fact]
    public async Task AddToRequest_SkippedEpisodesCanBePickedAgain_AsNewUnits()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var skipped = EpisodeUnit(102, 1, 2, RequestStatus.Skipped);
        var req = new AcquisitionRequest
        {
            Id = "readd",
            Kind = MediaKind.Series,
            Title = "Show",
            ArrId = 5,
            Status = RequestStatus.Searching,
            Scope = new RequestScope { All = false, EpisodeIds = [101, 102], TargetEpisodes = [Target(101, 1, 1), Target(102, 1, 2)] },
            Units = [EpisodeUnit(101, 1, 1, RequestStatus.Downloading), skipped]
        };
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.GetEpisodesAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync([
            new SonarrEpisode { Id = 101, SeasonNumber = 1, EpisodeNumber = 1 },
            new SonarrEpisode { Id = 102, SeasonNumber = 1, EpisodeNumber = 2 }
        ]);

        var result = await CreateTracker(RepoFor(req), sonarr: sonarr).AddToRequestAsync("readd", false, [], [102], _ => true);

        Assert.Equal(AddScopeOutcome.Added, result.Outcome);
        Assert.DoesNotContain(req.Units, u => u.Status == RequestStatus.Skipped);
        var added = req.Units.Single(u => u.EpisodeIds.Contains(102));
        Assert.Equal(RequestStatus.Pending, added.Status);
        Assert.Single(req.Scope.TargetEpisodes, t => t.EpisodeId == 102);
    }

    [Fact]
    public async Task AddToRequest_FollowedSeries_StillAcceptsASkippedEpisode_ButNothingElse()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = new AcquisitionRequest
        {
            Id = "follow",
            Kind = MediaKind.Series,
            Title = "Show",
            ArrId = 5,
            Status = RequestStatus.Searching,
            FollowAiring = true,
            Scope = new RequestScope { All = true, TargetEpisodes = [Target(101, 1, 1), Target(102, 1, 2)] },
            Units = [EpisodeUnit(101, 1, 1, RequestStatus.Downloading), EpisodeUnit(102, 1, 2, RequestStatus.Skipped)]
        };
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.GetEpisodesAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync([
            new SonarrEpisode { Id = 101, SeasonNumber = 1, EpisodeNumber = 1 },
            new SonarrEpisode { Id = 102, SeasonNumber = 1, EpisodeNumber = 2 },
            new SonarrEpisode { Id = 103, SeasonNumber = 1, EpisodeNumber = 3 }
        ]);
        var tracker = CreateTracker(RepoFor(req), sonarr: sonarr);

        var other = await tracker.AddToRequestAsync("follow", false, [], [103], _ => true);
        Assert.Equal(AddScopeOutcome.AlreadyCovered, other.Outcome);

        var again = await tracker.AddToRequestAsync("follow", false, [], [102], _ => true);
        Assert.Equal(AddScopeOutcome.Added, again.Outcome);
        Assert.Contains(req.Units, u => u.EpisodeIds.Contains(102) && u.Status == RequestStatus.Pending);
    }

    [Fact]
    public async Task Episodes_SkippedOnesAreNotSelected_AndLibraryOnesArePresent()
    {
        UsePluginConfiguration(new PluginConfiguration { AllowAllUsers = true });
        var userId = Guid.NewGuid();
        var req = new AcquisitionRequest
        {
            Id = "eps",
            UserId = userId,
            Kind = MediaKind.Series,
            Title = "Show",
            ArrId = 5,
            Status = RequestStatus.Searching,
            Scope = new RequestScope { All = false, SeasonNumbers = [1] },
            Units = [EpisodeUnit(102, 1, 2, RequestStatus.Skipped)]
        };
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.GetEpisodesAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync([
            new SonarrEpisode { Id = 101, SeasonNumber = 1, EpisodeNumber = 1, HasFile = true },
            new SonarrEpisode { Id = 102, SeasonNumber = 1, EpisodeNumber = 2 },
            new SonarrEpisode { Id = 103, SeasonNumber = 1, EpisodeNumber = 3 }
        ]);
        var local = new Mock<ILocalMatcherService>();
        local.Setup(l => l.GetLocalEpisodes(userId, MediaKind.Series, "Show", It.IsAny<int?>(), It.IsAny<ProviderIds>()))
            .Returns(new HashSet<(int, int)> { (1, 3) });
        var controller = CreateController(RepoFor(req), req, out _, sonarr, local.Object, userId);

        var ok = Assert.IsType<OkObjectResult>((await controller.GetEpisodes("eps", CancellationToken.None)).Result);
        var episodes = Assert.IsType<List<EpisodeDto>>(ok.Value);

        var e1 = episodes.Single(e => e.Id == 101);
        Assert.True(e1.HasFile);
        Assert.False(e1.InJellyfin);

        var e2 = episodes.Single(e => e.Id == 102);
        Assert.False(e2.IsSelected);
        Assert.False(e2.HasFile);

        var e3 = episodes.Single(e => e.Id == 103);
        Assert.True(e3.HasFile);
        Assert.True(e3.InJellyfin);
        Assert.True(e3.IsSelected);
    }

    // ---- 5. Episodes Sonarr does not know about ----

    [Fact]
    public async Task Planning_EpisodesAlreadyInJellyfin_AreNotDownloadedAgain()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var old = DateTime.UtcNow.AddYears(-3);
        var req = new AcquisitionRequest
        {
            Id = "plan",
            Kind = MediaKind.Series,
            Title = "Show",
            ArrId = 5,
            Status = RequestStatus.Pending,
            Scope = new RequestScope { All = true }
        };
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.GetEpisodesAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync([
            new SonarrEpisode { Id = 101, SeasonNumber = 1, EpisodeNumber = 1, AirDateUtc = old },
            new SonarrEpisode { Id = 102, SeasonNumber = 1, EpisodeNumber = 2, AirDateUtc = old },
            new SonarrEpisode { Id = 103, SeasonNumber = 1, EpisodeNumber = 3, AirDateUtc = old }
        ]);
        var local = new Mock<ILocalMatcherService>();
        local.Setup(l => l.GetLocalEpisodes(It.IsAny<Guid>(), MediaKind.Series, "Show", It.IsAny<int?>(), It.IsAny<ProviderIds>()))
            .Returns(new HashSet<(int, int)> { (1, 1), (1, 2) });

        await CreateTracker(RepoFor(req), sonarr: sonarr, local: local).ProcessRequestAsync(req);

        Assert.Equal([103], req.Scope.TargetEpisodes.Select(t => t.EpisodeId).ToArray());
    }

    [Fact]
    public async Task FollowAiring_EpisodeAlreadyInJellyfin_IsNotAdded()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = new AcquisitionRequest
        {
            Id = "fol",
            Kind = MediaKind.Series,
            Title = "Show",
            ArrId = 5,
            Status = RequestStatus.Available,
            FollowAiring = true,
            Scope = new RequestScope { All = true, TargetEpisodes = [Target(101, 1, 1)] },
            Units = [EpisodeUnit(101, 1, 1, RequestStatus.Available)]
        };
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.GetEpisodesAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync([
            new SonarrEpisode { Id = 101, SeasonNumber = 1, EpisodeNumber = 1, HasFile = true, AirDateUtc = DateTime.UtcNow.AddDays(-10) },
            new SonarrEpisode { Id = 102, SeasonNumber = 1, EpisodeNumber = 2, AirDateUtc = DateTime.UtcNow.AddDays(-3) }
        ]);
        var local = new Mock<ILocalMatcherService>();
        local.Setup(l => l.GetLocalEpisodes(It.IsAny<Guid>(), MediaKind.Series, "Show", It.IsAny<int?>(), It.IsAny<ProviderIds>()))
            .Returns(new HashSet<(int, int)> { (1, 2) });

        await CreateTracker(RepoFor(req), sonarr: sonarr, local: local).ProcessRequestAsync(req);

        Assert.Single(req.Units);
    }

    // ---- 6. Manual request switched to automatic ----

    [Fact]
    public async Task AutoSelect_SetsTheFlag_AndRespectsPermissions()
    {
        UsePluginConfiguration(new PluginConfiguration { ReleaseSelectionMode = "manual" });
        var req = SeriesWithParkedEpisode();
        req.Status = RequestStatus.Searching;
        var tracker = CreateTracker(RepoFor(req));

        var denied = await tracker.SetAutoSelectAsync("parked", true, _ => false);
        Assert.Equal(RetryOutcome.Forbidden, denied.Outcome);
        Assert.False(req.AutoReleaseSelection);

        var on = await tracker.SetAutoSelectAsync("parked", true, _ => true);
        Assert.Equal(RetryOutcome.Success, on.Outcome);
        Assert.True(req.AutoReleaseSelection);

        var off = await tracker.SetAutoSelectAsync("parked", false, _ => true);
        Assert.Equal(RetryOutcome.Success, off.Outcome);
        Assert.False(req.AutoReleaseSelection);
    }

    [Fact]
    public async Task AutoSelect_ListWaitingForTheUser_IsPickedOnTheNextTickWithoutSearching()
    {
        UsePluginConfiguration(new PluginConfiguration { ReleaseSelectionMode = "manual" });
        var best = new ReleaseCandidate { Id = "a", Title = "Show.S01E01.1080p.WEB-DL", InfoHash = "AAA", Indexer = "idx", DownloadUrl = "http://x/1", IsEligible = true };
        var unit = EpisodeUnit(101, 1, 1, RequestStatus.Searching);
        var req = new AcquisitionRequest
        {
            Id = "auto",
            Kind = MediaKind.Series,
            Title = "Show",
            ArrId = 5,
            Category = "sonarr",
            Status = RequestStatus.Searching,
            Scope = new RequestScope { All = false, TargetEpisodes = [Target(101, 1, 1)] },
            Units = [unit],
            AvailableReleases = [best],
            AvailableReleasesUnitId = unit.Id,
            AvailableReleasesUpdatedAt = DateTimeOffset.UtcNow
        };
        var releases = ReleasesReturning([]);
        var prowlarr = new Mock<IProwlarrClient>();
        prowlarr.Setup(p => p.FetchTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TorrentDownloadResult { ErrorMessage = "x" });
        var tracker = CreateTracker(RepoFor(req), prowlarr: prowlarr, releases: releases);

        // Manual: it keeps waiting for the user's choice
        await tracker.ProcessRequestAsync(req);
        Assert.Null(unit.SelectedRelease);

        await tracker.SetAutoSelectAsync("auto", true, _ => true);
        await tracker.ProcessRequestAsync(req);

        Assert.Equal("AAA", unit.SelectedRelease?.InfoHash);
        releases.Verify(r => r.SearchAndEvaluateReleasesForUnitAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<DownloadUnit>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Dto_ExposesAutoReleaseSelectionAndNewProgressFields()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = SeriesWithParkedEpisode();
        req.AutoReleaseSelection = true;
        req.NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(30);
        var controller = CreateController(RepoFor(req), req, out _);

        var dto = (AcquisitionRequestDto)((OkObjectResult)(await controller.GetRequest("parked", CancellationToken.None)).Result!).Value!;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto));

        Assert.True(json.RootElement.GetProperty("autoReleaseSelection").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, json.RootElement.GetProperty("nextAttemptUtc").ValueKind);
        var progress = json.RootElement.GetProperty("progress");
        Assert.Equal(0, progress.GetProperty("skipped").GetInt32());
        Assert.Equal(JsonValueKind.Array, progress.GetProperty("skippedItems").ValueKind);
    }

    // ---- helpers ----

    private static AcquisitionController CreateController(
        Mock<IRequestRepository> repo,
        AcquisitionRequest request,
        out Mock<ITrackerService> tracker,
        Mock<ISonarrClient>? sonarr = null,
        ILocalMatcherService? local = null,
        Guid? userId = null)
    {
        var id = userId ?? Guid.NewGuid();
        request.UserId = id;
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(id)).Returns(new Jellyfin.Database.Implementations.Entities.User("user", "user", "user"));
        tracker = new Mock<ITrackerService>();
        tracker.Setup(t => t.AcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<IDisposable>());

        return new AcquisitionController(
            Mock.Of<ICatalogService>(),
            repo.Object,
            tracker.Object,
            Mock.Of<IReleaseService>(),
            (sonarr ?? new Mock<ISonarrClient>()).Object,
            users.Object,
            Mock.Of<ILogger<AcquisitionController>>(),
            null,
            null,
            local)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", id.ToString())], "Test"))
                }
            }
        };
    }
}
