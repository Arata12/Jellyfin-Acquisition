using System.Diagnostics;
using System.Net;
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
using Jellyfin.Plugin.Acquisition.Adapters.QBittorrent.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;
using Jellyfin.Plugin.Acquisition.Catalog;
using Jellyfin.Plugin.Acquisition.Configuration;
using Jellyfin.Plugin.Acquisition.Controllers;
using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Releases;
using Jellyfin.Plugin.Acquisition.Security;
using Jellyfin.Plugin.Acquisition.Storage;
using Jellyfin.Plugin.Acquisition.Tracking;

namespace Jellyfin.Plugin.Acquisition.Tests;

/// <summary>
/// Covers the fixes from the code review of October 2026 (enum contract, destinations, category, import,
/// cancellation, Prowlarr failures, anime numbering and smaller hardening items).
/// </summary>
public class ReviewFixesTests
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

    private static (AcquisitionController Controller, Mock<IRequestRepository> Repo, Mock<ISonarrClient> Sonarr) CreateController(
        Mock<ISonarrClient>? sonarr = null,
        Mock<IRequestRepository>? repo = null,
        Mock<ITrackerService>? tracker = null,
        Mock<IReleaseService>? releases = null,
        Guid? userId = null,
        IRadarrClient? radarr = null)
    {
        var id = userId ?? Guid.NewGuid();
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(id)).Returns(new Jellyfin.Database.Implementations.Entities.User("user", "user", "user"));
        sonarr ??= new Mock<ISonarrClient>();
        repo ??= new Mock<IRequestRepository>();
        if (tracker == null)
        {
            tracker = new Mock<ITrackerService>();
            tracker.Setup(t => t.AcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<IDisposable>());
        }

        var controller = new AcquisitionController(
            Mock.Of<ICatalogService>(),
            repo.Object,
            tracker.Object,
            (releases ?? new Mock<IReleaseService>()).Object,
            sonarr.Object,
            users.Object,
            Mock.Of<ILogger<AcquisitionController>>(),
            radarr)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", id.ToString())], "Test"))
                }
            }
        };
        return (controller, repo, sonarr);
    }

    private static TrackerService CreateTracker(
        Mock<IRequestRepository> repo,
        Mock<ISonarrClient>? sonarr = null,
        Mock<IRadarrClient>? radarr = null,
        Mock<IProwlarrClient>? prowlarr = null,
        Mock<IQBittorrentClient>? qb = null,
        Mock<IReleaseService>? releases = null)
    {
        return new TrackerService(
            repo.Object,
            (sonarr ?? new Mock<ISonarrClient>()).Object,
            (radarr ?? new Mock<IRadarrClient>()).Object,
            (prowlarr ?? new Mock<IProwlarrClient>()).Object,
            (qb ?? new Mock<IQBittorrentClient>()).Object,
            (releases ?? new Mock<IReleaseService>()).Object,
            Mock.Of<ILocalMatcherService>(),
            Mock.Of<ILibraryManager>(),
            Mock.Of<ILogger<TrackerService>>());
    }

    private static Mock<IRequestRepository> RepoFor(AcquisitionRequest request)
    {
        var repo = new Mock<IRequestRepository>();
        repo.Setup(r => r.GetByIdAsync(request.Id, It.IsAny<CancellationToken>())).ReturnsAsync(request);
        repo.Setup(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return repo;
    }

    // ---- 1. Enum contract between server and client ----

    [Fact]
    public void Dtos_SerializeEnumsByName_EvenWithServerPascalCaseOptions()
    {
        var serverOptions = new JsonSerializerOptions { PropertyNamingPolicy = null };
        var json = JsonSerializer.Serialize(new CatalogItemDto { Kind = MediaKind.Series, RequestStatus = RequestStatus.Available }, serverOptions)
                 + JsonSerializer.Serialize(new AcquisitionRequestDto { Kind = MediaKind.Movie, Status = RequestStatus.RequiresAttention }, serverOptions);

        Assert.Contains("\"kind\":\"Series\"", json);
        Assert.Contains("\"requestStatus\":\"Available\"", json);
        Assert.Contains("\"kind\":\"Movie\"", json);
        Assert.Contains("\"status\":\"RequiresAttention\"", json);

        // Requests keep accepting numeric values from older clients
        var fromNumber = JsonSerializer.Deserialize<CreateRequestDto>("{\"kind\":1}", serverOptions);
        var fromName = JsonSerializer.Deserialize<CreateRequestDto>("{\"kind\":\"Movie\"}", serverOptions);
        Assert.Equal(MediaKind.Series, fromNumber!.Kind);
        Assert.Equal(MediaKind.Movie, fromName!.Kind);
    }

    [Fact]
    public void JavaScriptClient_NormalizesNumericAndNamedEnums()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.Acquisition.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var jsPath = Path.Combine(dir.FullName, "src/Jellyfin.Plugin.Acquisition/Web/acquisition.js").Replace("\\", "/");
        var script = $$"""
            const mod = require('{{jsPath}}');
            const a = mod.normalizeCatalogItem({ kind: 1, requestStatus: 4 });
            const b = mod.normalizeCatalogItem({ Kind: 'Movie', RequestStatus: 'Searching' });
            if (a.kind !== 'Series' || a.requestStatus !== 'Available') throw new Error('numeric enums not normalized: ' + JSON.stringify(a));
            if (b.kind !== 'Movie' || b.requestStatus !== 'Searching') throw new Error('named enums not kept: ' + JSON.stringify(b));
            const headers = mod.getAuthHeaders();
            if (headers['X-Emby-Token']) throw new Error('legacy token header must not be sent');
            console.log('enum normalization ok');
            """;

        var tempScript = Path.Combine(Path.GetTempPath(), $"enum_test_{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(tempScript, script);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                Arguments = tempScript,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            Assert.NotNull(process);
            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, stdErr + stdOut);
            Assert.Contains("enum normalization ok", stdOut);
        }
        finally
        {
            try { File.Delete(tempScript); } catch { }
        }
    }

    // ---- 4/6/7. Request creation: identity, destination and duplicates ----

    [Fact]
    public async Task CreateRequest_SeriesWithoutTvdb_IsRejected()
    {
        UsePluginConfiguration(new PluginConfiguration { AllowAllUsers = true });
        var (controller, repo, _) = CreateController();

        var result = await controller.CreateRequest(new CreateRequestDto
        {
            Kind = MediaKind.Series,
            Title = "Local Only Show",
            ProviderIds = new ProviderIds { Tmdb = "1234" }
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        repo.Verify(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateRequest_SeveralRootsWithoutChoice_AsksForDestinationInsteadOfUsingTheFirst()
    {
        UsePluginConfiguration(new PluginConfiguration { AllowAllUsers = true });
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.GetRootFoldersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
            new SonarrRootFolder { Id = 1, Path = "/data/anime" },
            new SonarrRootFolder { Id = 2, Path = "/data/tv" }
        ]);
        var (controller, repo, _) = CreateController(sonarr);

        var noChoice = await controller.CreateRequest(new CreateRequestDto
        {
            Kind = MediaKind.Series,
            Title = "Frieren",
            ProviderIds = new ProviderIds { Tvdb = "424536" }
        }, CancellationToken.None);
        var unknown = await controller.CreateRequest(new CreateRequestDto
        {
            Kind = MediaKind.Series,
            Title = "Frieren",
            ProviderIds = new ProviderIds { Tvdb = "424536" },
            DestinationId = "sonarr-root:99"
        }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<ObjectResult>(noChoice.Result).StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<ObjectResult>(unknown.Result).StatusCode);
        repo.Verify(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateRequest_ResolvesDestinationById_AndDetectsDuplicatesByExternalId()
    {
        var config = UsePluginConfiguration(new PluginConfiguration { AllowAllUsers = true });
        config.Destinations =
        [
            new DestinationConfig { Id = "dest-anime", Name = "Anime", Kind = MediaKind.Series, RootFolderPath = "/data/anime", SeriesType = "anime" },
            new DestinationConfig { Id = "dest-tv", Name = "Series", Kind = MediaKind.Series, RootFolderPath = "/data/tv", SeriesType = "standard" }
        ];

        AcquisitionRequest? saved = null;
        var repo = new Mock<IRequestRepository>();
        repo.Setup(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AcquisitionRequest, CancellationToken>((r, _) => saved = r)
            .Returns(Task.CompletedTask);
        var (controller, _, _) = CreateController(repo: repo);

        var created = await controller.CreateRequest(new CreateRequestDto
        {
            Kind = MediaKind.Series,
            Title = "Frieren",
            ProviderIds = new ProviderIds { Tvdb = "424536" },
            DestinationId = "dest-anime"
        }, CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(created.Result);
        Assert.Equal("dest-anime", saved!.DestinationId);
        Assert.Equal("/data/anime", saved.RootFolderPath);
        Assert.Equal("anime", saved.SeriesType);
        Assert.Null(saved.QualityProfileId);

        repo.Setup(r => r.FindActiveByExternalIdAsync(MediaKind.Series, "tvdb", "424536", It.IsAny<CancellationToken>()))
            .ReturnsAsync(saved);
        var duplicate = await controller.CreateRequest(new CreateRequestDto
        {
            Kind = MediaKind.Series,
            Title = "Sousou no Frieren",
            ProviderIds = new ProviderIds { Tvdb = "424536" },
            DestinationId = "dest-tv"
        }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(duplicate.Result);
    }

    // ---- 4. Registration in Arr without arbitrary choices ----

    private static Mock<ISonarrClient> SonarrForNewSeries(List<SonarrQualityProfile> profiles, params string[] roots)
    {
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.FindSeriesByTvdbIdAsync(424536, It.IsAny<CancellationToken>())).ReturnsAsync((SonarrSeries?)null);
        sonarr.Setup(s => s.LookupSeriesWithDiagnosticsAsync("tvdb:424536", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrLookupResult { Status = ProviderStatus.Ready, Items = [new SonarrSeries { Title = "Frieren", TvdbId = 424536 }] });
        sonarr.Setup(s => s.GetRootFoldersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(roots.Select((p, i) => new SonarrRootFolder { Id = i + 1, Path = p }).ToList());
        sonarr.Setup(s => s.GetQualityProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profiles);
        return sonarr;
    }

    private static AcquisitionRequest NewSeriesRequest(string? root) => new()
    {
        Id = "req-reg",
        Kind = MediaKind.Series,
        Title = "Frieren",
        Status = RequestStatus.Pending,
        ProviderIds = new ProviderIds { Tvdb = "424536" },
        RootFolderPath = root,
        SeriesType = "anime",
        Scope = new RequestScope { All = true }
    };

    [Fact]
    public async Task Registration_SeveralProfilesWithoutTechnicalProfile_NeedsAttentionAndDoesNotAdd()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var sonarr = SonarrForNewSeries([new SonarrQualityProfile { Id = 1, Name = "Any" }, new SonarrQualityProfile { Id = 4, Name = "HD-1080p" }], "/data/anime");
        var req = NewSeriesRequest("/data/anime");

        await CreateTracker(RepoFor(req), sonarr).ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.RequiresAttention, req.Status);
        Assert.Contains("perfil técnico", req.AttentionReason);
        sonarr.Verify(s => s.AddSeriesAsync(It.IsAny<SonarrSeries>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Registration_RemovedRootFolder_NeedsAttentionInsteadOfUsingAnother()
    {
        UsePluginConfiguration(new PluginConfiguration { SonarrQualityProfileId = 4 });
        var sonarr = SonarrForNewSeries([new SonarrQualityProfile { Id = 4, Name = "HD-1080p" }], "/data/tv");
        var req = NewSeriesRequest("/data/anime");

        await CreateTracker(RepoFor(req), sonarr).ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.RequiresAttention, req.Status);
        Assert.Contains("/data/anime", req.AttentionReason);
        sonarr.Verify(s => s.AddSeriesAsync(It.IsAny<SonarrSeries>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Registration_UsesChosenRootTechnicalProfileAndSeriesType()
    {
        UsePluginConfiguration(new PluginConfiguration { SonarrQualityProfileId = 4 });
        var sonarr = SonarrForNewSeries(
            [new SonarrQualityProfile { Id = 1, Name = "Any" }, new SonarrQualityProfile { Id = 4, Name = "HD-1080p" }],
            "/data/tv", "/data/anime");
        SonarrSeries? added = null;
        sonarr.Setup(s => s.AddSeriesAsync(It.IsAny<SonarrSeries>(), It.IsAny<CancellationToken>()))
            .Callback<SonarrSeries, CancellationToken>((s, _) => added = s)
            .ReturnsAsync(new SonarrSeries { Id = 77, SeriesType = "anime" });
        sonarr.Setup(s => s.GetEpisodesAsync(77, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var req = NewSeriesRequest("/data/anime/");

        await CreateTracker(RepoFor(req), sonarr).ProcessRequestAsync(req);

        Assert.NotNull(added);
        Assert.Equal("/data/anime", added.RootFolderPath);
        Assert.Equal(4, added.QualityProfileId);
        Assert.Equal("anime", added.SeriesType);
        Assert.Equal(77, req.ArrId);
    }

    // ---- 2. qBittorrent category ----

    private static (AcquisitionRequest Request, Mock<IQBittorrentClient> Qb, Mock<IProwlarrClient> Prowlarr) MovieReadyToSend()
    {
        var release = new ReleaseCandidate { Id = "rel", Title = "Dune.2021.1080p", InfoHash = "abc", DownloadUrl = "http://prowlarr/dl", IsTorrent = true, IsEligible = true, IdentityVerified = true };
        var req = new AcquisitionRequest
        {
            Id = "req-cat",
            Kind = MediaKind.Movie,
            Title = "Dune",
            ArrId = 9,
            Status = RequestStatus.Searching,
            SelectedRelease = release,
            Units = [new DownloadUnit { Id = "u", SelectedRelease = release, Status = RequestStatus.Searching }]
        };
        var qb = new Mock<IQBittorrentClient>();
        qb.Setup(q => q.GetTorrentsInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        qb.Setup(q => q.AddTorrentAsync(It.IsAny<QBittorrentAddOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var prowlarr = new Mock<IProwlarrClient>();
        prowlarr.Setup(p => p.FetchTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TorrentDownloadResult { TorrentBytes = [1, 2, 3] });
        return (req, qb, prowlarr);
    }

    [Fact]
    public async Task Category_NoQbittorrentClientInArr_NeedsAttentionAndNothingIsSent()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var (req, qb, prowlarr) = MovieReadyToSend();
        var radarr = new Mock<IRadarrClient>();
        radarr.Setup(r => r.GetDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await CreateTracker(RepoFor(req), radarr: radarr, prowlarr: prowlarr, qb: qb).ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.RequiresAttention, req.Status);
        Assert.Null(req.Category);
        qb.Verify(q => q.AddTorrentAsync(It.IsAny<QBittorrentAddOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Category_SeveralClients_UsesTheOnePointingToThePluginQbittorrent()
    {
        UsePluginConfiguration(new PluginConfiguration { QBittorrentUrl = "http://qbit.lan:8080" });
        var (req, qb, prowlarr) = MovieReadyToSend();
        var radarr = new Mock<IRadarrClient>();
        radarr.Setup(r => r.GetDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
            new ArrDownloadClient { Name = "other", Implementation = "QBittorrent", Enable = true, Fields = [new() { Name = "host", Value = "other.lan" }, new() { Name = "port", Value = 8080 }, new() { Name = "movieCategory", Value = "wrong" }] },
            new ArrDownloadClient { Name = "main", Implementation = "QBittorrent", Enable = true, Fields = [new() { Name = "host", Value = "qbit.lan" }, new() { Name = "port", Value = 8080 }, new() { Name = "movieCategory", Value = "radarr" }] }
        ]);
        QBittorrentAddOptions? sent = null;
        qb.Setup(q => q.AddTorrentAsync(It.IsAny<QBittorrentAddOptions>(), It.IsAny<CancellationToken>()))
            .Callback<QBittorrentAddOptions, CancellationToken>((o, _) => sent = o)
            .ReturnsAsync(true);

        await CreateTracker(RepoFor(req), radarr: radarr, prowlarr: prowlarr, qb: qb).ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.Downloading, req.Status);
        Assert.Equal("radarr", sent!.Category);
    }

    [Fact]
    public async Task FetchFailure_IsRetriedWithBackoffBeforeAskingTheUser()
    {
        UsePluginConfiguration(new PluginConfiguration { RadarrCategory = "radarr" });
        var (req, qb, prowlarr) = MovieReadyToSend();
        prowlarr.Setup(p => p.FetchTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TorrentDownloadResult { ErrorMessage = "HTTP 503" });

        await CreateTracker(RepoFor(req), prowlarr: prowlarr, qb: qb).ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.Searching, req.Status);
        Assert.Equal(1, req.Units[0].FetchFailures);
        Assert.True(req.NextAttemptUtc > DateTimeOffset.UtcNow);
    }

    // ---- 9. Prowlarr failures are not "no releases" ----

    [Fact]
    public async Task ProwlarrClient_SearchFailure_Throws()
    {
        UsePluginConfiguration(new PluginConfiguration { ProwlarrApiKey = "k" });
        var client = new ProwlarrClient(new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))), Mock.Of<ILogger<ProwlarrClient>>());

        await Assert.ThrowsAsync<ProwlarrUnavailableException>(() => client.SearchAsync(new ProwlarrSearchQuery { Query = "Dune", Kind = MediaKind.Movie }));
    }

    [Fact]
    public async Task Tracker_ProwlarrUnavailable_KeepsSearchingAndExplainsWhy()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = new AcquisitionRequest
        {
            Id = "req-prow",
            Kind = MediaKind.Movie,
            Title = "Dune",
            ArrId = 9,
            Status = RequestStatus.Searching,
            Units = [new DownloadUnit { Id = "u", Status = RequestStatus.Searching }]
        };
        var releases = new Mock<IReleaseService>();
        releases.Setup(r => r.SearchAndEvaluateReleasesForUnitAsync(req, It.IsAny<DownloadUnit>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProwlarrUnavailableException("Prowlarr search returned HTTP 500."));

        await CreateTracker(RepoFor(req), releases: releases).ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.Searching, req.Status);
        Assert.Contains("Prowlarr", req.History.Last().Message);
        Assert.DoesNotContain(req.History, h => h.Message.Contains("No se encontraron releases"));
        Assert.True(req.NextAttemptUtc > DateTimeOffset.UtcNow);
    }

    // ---- 3. Import without an exit ----

    private static AcquisitionRequest ImportingMovie(TimeSpan waited) => new()
    {
        Id = "req-imp",
        Kind = MediaKind.Movie,
        Title = "Dune",
        ArrId = 9,
        Category = "radarr",
        Status = RequestStatus.Importing,
        TorrentHash = "abc",
        Units = [new DownloadUnit
        {
            Id = "u",
            Status = RequestStatus.Importing,
            TorrentHash = "abc",
            ContentPath = "/downloads/Dune.2021.1080p",
            ImportWaitStartedAt = DateTimeOffset.UtcNow - waited
        }]
    };

    [Fact]
    public async Task Import_NotImportedAfterTimeout_NeedsAttention()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = ImportingMovie(TimeSpan.FromMinutes(61));
        var radarr = new Mock<IRadarrClient>();
        radarr.Setup(r => r.GetQueueAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        radarr.Setup(r => r.GetMovieByIdAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync(new Adapters.Radarr.Models.RadarrMovie { Id = 9, HasFile = false });

        await CreateTracker(RepoFor(req), radarr: radarr).ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.RequiresAttention, req.Status);
        Assert.Contains("radarr", req.AttentionReason);
    }

    [Fact]
    public async Task Import_StalledDownload_RequestsManualImportOfMatchingFilesOnly()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var req = ImportingMovie(TimeSpan.FromMinutes(11));
        var radarr = new Mock<IRadarrClient>();
        radarr.Setup(r => r.GetQueueAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        radarr.Setup(r => r.GetMovieByIdAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync(new Adapters.Radarr.Models.RadarrMovie { Id = 9, HasFile = false });
        radarr.Setup(r => r.GetManualImportCandidatesAsync("/downloads/Dune.2021.1080p", It.IsAny<CancellationToken>())).ReturnsAsync([
            new Adapters.Radarr.Models.RadarrManualImportCandidate { Path = "/downloads/Dune.2021.1080p/dune.mkv", Movie = new Adapters.Radarr.Models.RadarrMovie { Id = 9 } },
            new Adapters.Radarr.Models.RadarrManualImportCandidate { Path = "/downloads/Dune.2021.1080p/sample.mkv", Movie = new Adapters.Radarr.Models.RadarrMovie { Id = 9 }, Rejections = [new() { Reason = "Sample" }] }
        ]);
        List<Adapters.Radarr.Models.RadarrManualImportCandidate>? imported = null;
        radarr.Setup(r => r.ExecuteManualImportAsync(It.IsAny<IEnumerable<Adapters.Radarr.Models.RadarrManualImportCandidate>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Adapters.Radarr.Models.RadarrManualImportCandidate>, CancellationToken>((c, _) => imported = c.ToList())
            .ReturnsAsync(true);

        await CreateTracker(RepoFor(req), radarr: radarr).ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.Importing, req.Status);
        Assert.NotNull(req.Units[0].ManualImportRequestedAt);
        Assert.Single(imported!);
        Assert.EndsWith("dune.mkv", imported![0].Path);
    }

    private static (AcquisitionRequest Request, Mock<ISonarrClient> Sonarr, Mock<ILibraryManager> Library, Mock<ILibraryMonitor> Monitor, TrackerService Tracker) WaitingForJellyfin(TimeSpan waited)
    {
        UsePluginConfiguration(new PluginConfiguration());
        var seriesPath = Directory.CreateTempSubdirectory("acq-series-").FullName;
        var req = new AcquisitionRequest
        {
            Id = "req-jf",
            Kind = MediaKind.Series,
            Title = "Frieren",
            ArrId = 1,
            Status = RequestStatus.Importing,
            ImportingStartedAt = waited == TimeSpan.Zero ? null : DateTimeOffset.UtcNow - waited,
            Scope = new RequestScope { TargetEpisodes = [new EpisodeTarget { EpisodeId = 11, SeasonNumber = 1, EpisodeNumber = 1 }] }
        };
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.GetEpisodesAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync([new SonarrEpisode { Id = 11, SeasonNumber = 1, EpisodeNumber = 1, HasFile = true }]);
        sonarr.Setup(s => s.GetSeriesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new SonarrSeries { Id = 1, Path = seriesPath });
        var library = new Mock<ILibraryManager>();
        var monitor = new Mock<ILibraryMonitor>();
        var tracker = new TrackerService(RepoFor(req).Object, sonarr.Object, Mock.Of<IRadarrClient>(), Mock.Of<IProwlarrClient>(), Mock.Of<IQBittorrentClient>(),
            Mock.Of<IReleaseService>(), Mock.Of<ILocalMatcherService>(), library.Object, Mock.Of<ILogger<TrackerService>>(), monitor.Object);
        return (req, sonarr, library, monitor, tracker);
    }

    [Fact]
    public async Task LibraryRefresh_ReportsThePathOnce_InsteadOfRestartingJellyfinsTimerEveryPoll()
    {
        var (req, _, library, monitor, tracker) = WaitingForJellyfin(TimeSpan.Zero);

        await tracker.ProcessRequestAsync(req);
        await tracker.ProcessRequestAsync(req);
        await tracker.ProcessRequestAsync(req);

        monitor.Verify(m => m.ReportFileSystemChanged(It.IsAny<string>()), Times.Once);
        library.Verify(l => l.ValidateMediaLibrary(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LibraryRefresh_StillNotDetectedAfterFiveMinutes_FallsBackToFullScan()
    {
        var (req, _, library, monitor, tracker) = WaitingForJellyfin(TimeSpan.FromMinutes(6));

        await tracker.ProcessRequestAsync(req);
        await Task.Delay(200); // the full scan runs in the background

        library.Verify(l => l.ValidateMediaLibrary(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Once);
        monitor.Verify(m => m.ReportFileSystemChanged(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Retry_RestartsTheJellyfinDetectionWindow()
    {
        var req = new AcquisitionRequest { Id = "req-retry", Status = RequestStatus.RequiresAttention, ImportingStartedAt = DateTimeOffset.UtcNow.AddMinutes(-20) };

        var result = await CreateTracker(RepoFor(req)).RetryRequestAsync(req.Id, _ => true);

        Assert.Equal(RetryOutcome.Success, result.Outcome);
        Assert.Null(req.ImportingStartedAt);
    }

    // ---- 5. Cancellation ----

    [Fact]
    public async Task Cancel_DeletesRequest_AndAStalePollDoesNotResurrectIt()
    {
        var req = new AcquisitionRequest { Id = "req-cancel", Kind = MediaKind.Series, Title = "X", Status = RequestStatus.Pending, ProviderIds = new ProviderIds { Tvdb = "1" } };
        var repo = RepoFor(req);
        var sonarr = new Mock<ISonarrClient>();
        var tracker = CreateTracker(repo, sonarr);

        var result = await tracker.CancelRequestAsync(req.Id, _ => true);
        await tracker.ProcessRequestAsync(req);

        Assert.Equal(RetryOutcome.Success, result.Outcome);
        repo.Verify(r => r.DeleteAsync(req.Id, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        sonarr.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Cancel_AvailableRequest_IsRejected()
    {
        var req = new AcquisitionRequest { Id = "req-done", Status = RequestStatus.Available };
        var repo = RepoFor(req);

        var result = await CreateTracker(repo).CancelRequestAsync(req.Id, _ => true);

        Assert.Equal(RetryOutcome.InvalidStatus, result.Outcome);
        repo.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- 10/14. Releases and request options endpoints ----

    [Fact]
    public async Task Releases_RecentEmptyResult_IsReusedWithoutSearchingAgain()
    {
        UsePluginConfiguration(new PluginConfiguration { AllowAllUsers = true });
        var userId = Guid.NewGuid();
        var req = new AcquisitionRequest
        {
            Id = "req-rel",
            UserId = userId,
            Kind = MediaKind.Movie,
            Status = RequestStatus.Searching,
            AvailableReleases = [],
            AvailableReleasesUnitId = null,
            AvailableReleasesUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var releases = new Mock<IReleaseService>();
        var (controller, _, _) = CreateController(repo: RepoFor(req), releases: releases, userId: userId);

        var result = await controller.GetReleases(req.Id, refresh: false, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        releases.Verify(r => r.SearchAndEvaluateReleasesAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Releases_UserWhoCannotManage_CannotTriggerSearches()
    {
        UsePluginConfiguration(new PluginConfiguration { AllowAllUsers = false });
        var userId = Guid.NewGuid();
        var req = new AcquisitionRequest { Id = "req-view", UserId = userId, Kind = MediaKind.Movie, Status = RequestStatus.Searching };
        var releases = new Mock<IReleaseService>();
        var (controller, _, _) = CreateController(repo: RepoFor(req), releases: releases, userId: userId);

        var result = await controller.GetReleases(req.Id, refresh: true, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        releases.VerifyNoOtherCalls();
    }

    private static AcquisitionRequest DownloadingWithTrackerPick(Guid userId) => new()
    {
        Id = "req-sel",
        UserId = userId,
        Kind = MediaKind.Movie,
        Status = RequestStatus.Downloading,
        Units = [new DownloadUnit { Id = "u", Status = RequestStatus.Downloading, SelectedRelease = new ReleaseCandidate { Id = "auto", Title = "Dune.2021.1080p", InfoHash = "abc", Indexer = "Mock" } }],
        AvailableReleases =
        [
            new ReleaseCandidate { Id = "same", Title = "Dune.2021.1080p", InfoHash = "ABC", Indexer = "Mock", IsTorrent = true, IsEligible = true },
            new ReleaseCandidate { Id = "other", Title = "Dune.2021.2160p", InfoHash = "def", Indexer = "Mock", IsTorrent = true, IsEligible = true }
        ],
        AvailableReleasesUnitId = "u"
    };

    [Fact]
    public async Task SelectRelease_SameReleaseTheTrackerAlreadySent_IsAcceptedWithoutError()
    {
        UsePluginConfiguration(new PluginConfiguration { AllowAllUsers = true });
        var userId = Guid.NewGuid();
        var req = DownloadingWithTrackerPick(userId);
        var (controller, repo, _) = CreateController(repo: RepoFor(req), userId: userId);

        var result = await controller.SelectRelease(req.Id, new SelectReleaseDto { ReleaseId = "same" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("abc", req.Units[0].SelectedRelease!.InfoHash);
        repo.Verify(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SelectRelease_AnotherReleaseWhileDownloading_ExplainsWhatIsBeingDownloaded()
    {
        UsePluginConfiguration(new PluginConfiguration { AllowAllUsers = true });
        var userId = Guid.NewGuid();
        var req = DownloadingWithTrackerPick(userId);
        var (controller, _, _) = CreateController(repo: RepoFor(req), userId: userId);

        var result = await controller.SelectRelease(req.Id, new SelectReleaseDto { ReleaseId = "other" }, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("Dune.2021.1080p", Assert.IsType<ErrorResponseDto>(conflict.Value).Message);
    }

    [Fact]
    public async Task RequestOptions_ArrFailure_IsReportedInsteadOfAnEmptyList()
    {
        UsePluginConfiguration(new PluginConfiguration { AllowAllUsers = true });
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.GetRootFoldersAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("connection refused"));
        var (controller, _, _) = CreateController(sonarr);

        var result = await controller.GetRequestOptions(MediaKind.Series, CancellationToken.None);

        var options = Assert.IsType<RequestOptionsDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(options.Destinations);
        Assert.Contains("Sonarr", options.ErrorMessage);
    }

    // ---- 11. Anime absolute numbering ----

    [Fact]
    public async Task Anime_AbsoluteNumberedRelease_MatchesTheRequestedEpisode()
    {
        UsePluginConfiguration(new PluginConfiguration());
        var prowlarr = new Mock<IProwlarrClient>();
        ProwlarrSearchQuery? query = null;
        prowlarr.Setup(p => p.SearchAsync(It.IsAny<ProwlarrSearchQuery>(), It.IsAny<CancellationToken>()))
            .Callback<ProwlarrSearchQuery, CancellationToken>((q, _) => query = q)
            .ReturnsAsync([
                new ProwlarrRelease { Title = "[SubsPlease] Frieren - 33 (1080p) [ABCD1234].mkv", Protocol = "torrent", Seeders = 50, Size = 1_000_000_000 },
                new ProwlarrRelease { Title = "[SubsPlease] Frieren - 34 (1080p) [EFGH5678].mkv", Protocol = "torrent", Seeders = 80, Size = 1_000_000_000 }
            ]);
        var service = new ReleaseService(prowlarr.Object, Mock.Of<ISonarrClient>(), Mock.Of<IRadarrClient>(), Mock.Of<ILogger<ReleaseService>>());
        var request = new AcquisitionRequest { Kind = MediaKind.Series, Title = "Frieren", SeriesType = "anime" };
        var unit = new DownloadUnit { SeasonNumber = 2, EpisodeNumbers = [5], AbsoluteEpisodeNumbers = [33] };

        var results = await service.SearchAndEvaluateReleasesForUnitAsync(request, unit);

        Assert.Equal(33, query!.AbsoluteEpisode);
        var best = service.PickBestRelease(results);
        Assert.NotNull(best);
        Assert.Contains("Frieren - 33", best.Title);
        Assert.Contains(results, r => r.Title.Contains("- 34") && !r.IsEligible);
    }

    [Theory]
    [InlineData("[SubsPlease] Frieren - 05 (1080p) [ABCD].mkv", 5)]
    [InlineData("[Erai-raws] One Piece - 1100v2 [1080p].mkv", 1100)]
    [InlineData("Show.S01E05.1080p.WEB-DL", -1)]
    public void ReleaseParser_AbsoluteEpisode(string title, int expected)
    {
        Assert.Equal(expected, ReleaseParser.ParseAbsoluteEpisode(title));
    }

    // ---- 7/12/low. Catalog, matcher, redaction and migration ----

    [Fact]
    public async Task Catalog_RequestForOneWork_DoesNotMarkAnotherWorkWithTheSameTitle()
    {
        UsePluginConfiguration(new PluginConfiguration { RadarrApiKey = "k" });
        var radarr = new Mock<IRadarrClient>();
        radarr.Setup(r => r.LookupMovieWithDiagnosticsAsync("dune", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Adapters.Radarr.Models.RadarrLookupResult
            {
                Status = ProviderStatus.Ready,
                Items = [new() { Title = "Dune", Year = 2021, TmdbId = 438631 }, new() { Title = "Dune", Year = 1984, TmdbId = 841 }]
            });
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.LookupSeriesWithDiagnosticsAsync("dune", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrLookupResult { Status = ProviderStatus.Ready });
        var repo = new Mock<IRequestRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
            new AcquisitionRequest { Id = "r1", Kind = MediaKind.Movie, Title = "Dune", Year = 2021, Status = RequestStatus.Searching, ProviderIds = new ProviderIds { Tmdb = "438631" } }
        ]);
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        var service = new CatalogService(sonarr.Object, radarr.Object, Mock.Of<ILocalMatcherService>(), repo.Object, Mock.Of<IHttpClientFactory>(), appPaths.Object, Mock.Of<ILogger<CatalogService>>());

        var result = await service.SearchCatalogWithDiagnosticsAsync(Guid.NewGuid(), "dune");

        Assert.Equal("r1", result.Items.Single(i => i.Year == 2021).RequestId);
        Assert.Null(result.Items.Single(i => i.Year == 1984).RequestId);
        Assert.Null(await service.GetImageAsync("../../etc/passwd"));
    }

    [Fact]
    public async Task Catalog_LocalSeriesWithoutTvdb_IsResolvedByImdbNotByTitle()
    {
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.LookupSeriesAsync("The Office", It.IsAny<CancellationToken>())).ReturnsAsync([
            new SonarrSeries { Title = "The Office", Year = 2001, TvdbId = 78107, ImdbId = "tt0290978" },
            new SonarrSeries { Title = "The Office (US)", Year = 2005, TvdbId = 73244, ImdbId = "tt0386676" }
        ]);
        sonarr.Setup(s => s.LookupSeriesAsync("tvdb:73244", It.IsAny<CancellationToken>())).ReturnsAsync([
            new SonarrSeries { Title = "The Office (US)", Year = 2005, TvdbId = 73244, ImdbId = "tt0386676" }
        ]);
        var repo = new Mock<IRequestRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        var service = new CatalogService(sonarr.Object, Mock.Of<IRadarrClient>(), Mock.Of<ILocalMatcherService>(), repo.Object, Mock.Of<IHttpClientFactory>(), appPaths.Object, Mock.Of<ILogger<CatalogService>>());

        var item = await service.ResolveLocalSeriesAsync(Guid.NewGuid(), "The Office", 2005, new ProviderIds { Imdb = "tt0386676" });
        var unknown = await service.ResolveLocalSeriesAsync(Guid.NewGuid(), "The Office", 2005, new ProviderIds { Imdb = "tt9999999" });

        Assert.Equal("73244", item!.ProviderIds.Tvdb);
        Assert.Null(unknown);
    }

    [Fact]
    public async Task QBittorrent53_LoginWith204AndPortSuffixedCookie_IsReusedOnRequests()
    {
        UsePluginConfiguration(new PluginConfiguration { QBittorrentUrl = "http://qbittorrent:8080", QBittorrentPassword = "pw" });
        string? cookieSent = null;
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/auth/login"))
            {
                // qBittorrent 5.3.0: 204 No Content and a session cookie named after the WebUI port
                var login = new HttpResponseMessage(HttpStatusCode.NoContent);
                login.Headers.Add("Set-Cookie", "QBT_SID_8080=abc123; HttpOnly; SameSite=Lax; path=/");
                return login;
            }

            cookieSent = req.Headers.TryGetValues("Cookie", out var values) ? values.Single() : null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });
        var client = new QBittorrentClient(new HttpClient(handler), Mock.Of<ILogger<QBittorrentClient>>());

        Assert.True(await client.LoginAsync());
        await client.GetTorrentsInfoAsync(tag: "acq-x");

        Assert.Equal("QBT_SID_8080=abc123", cookieSent);
    }

    [Fact]
    public async Task QBittorrent4_LoginOkWithSidCookie_AndIgnoredTagFilter_OnlyReturnsTaggedTorrents()
    {
        UsePluginConfiguration(new PluginConfiguration { QBittorrentUrl = "http://qbittorrent:8080", QBittorrentPassword = "pw" });
        string? cookieSent = null;
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/auth/login"))
            {
                // qBittorrent 4.x: 200 "Ok." and a cookie named SID
                var login = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
                login.Headers.Add("Set-Cookie", "SID=legacy42; HttpOnly; path=/");
                return login;
            }

            cookieSent = req.Headers.TryGetValues("Cookie", out var values) ? values.Single() : null;
            // A server that ignores the tag filter returns every torrent
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"hash":"a","name":"mine","tags":"other, acq-1","state":"stoppedUP","progress":1},{"hash":"b","name":"foreign","tags":"","state":"pausedUP","progress":1}]""")
            };
        });
        var client = new QBittorrentClient(new HttpClient(handler), Mock.Of<ILogger<QBittorrentClient>>());

        var torrents = await client.GetTorrentsInfoAsync(tag: "acq-1");

        Assert.Equal("SID=legacy42", cookieSent);
        var only = Assert.Single(torrents);
        Assert.Equal("a", only.Hash);
        Assert.True(only.IsCompleted);
    }

    [Fact]
    public async Task QBittorrent4_WrongPassword_FailsLoginWithoutThrowing()
    {
        UsePluginConfiguration(new PluginConfiguration { QBittorrentUrl = "http://qbittorrent:8080", QBittorrentPassword = "bad" });
        var client = new QBittorrentClient(
            new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Fails.") })),
            Mock.Of<ILogger<QBittorrentClient>>());

        Assert.False(await client.LoginAsync());
    }

    [Theory]
    [InlineData("abyss", "abyss")]
    [InlineData(" Jellyfin ", "jellyfin")]
    [InlineData("auto", "auto")]
    [InlineData("\"><script>alert(1)</script>", "auto")]
    [InlineData(null, "auto")]
    public void SearchStyle_OnlyAllowedValuesReachThePage(string? configured, string expected)
    {
        Assert.Equal(expected, new PluginConfiguration { SearchStyle = configured! }.GetSearchStyle());
    }

    [Fact]
    public async Task WebInjection_InjectsSearchStyle_AndRebuildsWhenTheOptionChanges()
    {
        var config = UsePluginConfiguration(new PluginConfiguration { SearchStyle = "abyss" });
        var webDir = Directory.CreateTempSubdirectory("acq-web-").FullName;
        File.WriteAllText(Path.Combine(webDir, "index.html"), "<html><body></body></html>");
        var paths = new Mock<MediaBrowser.Controller.IServerApplicationPaths>();
        paths.Setup(p => p.WebPath).Returns(webDir);
        var middleware = new Web.WebInjectionMiddleware(_ => Task.CompletedTask, paths.Object, Mock.Of<ILogger<Web.WebInjectionMiddleware>>());

        async Task<string> Render()
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = "/web/index.html";
            context.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(context);
            context.Response.Body.Position = 0;
            return await new StreamReader(context.Response.Body).ReadToEndAsync();
        }

        Assert.Contains("window.__ACQUISITION_STYLE__ = \"abyss\"", await Render());
        config.SearchStyle = "jellyfin";
        Assert.Contains("window.__ACQUISITION_STYLE__ = \"jellyfin\"", await Render());
    }

    [Fact]
    public void JavaScriptClient_ResolvesTheSearchStyle()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.Acquisition.sln")))
        {
            dir = dir.Parent;
        }

        var jsPath = Path.Combine(dir!.FullName, "src/Jellyfin.Plugin.Acquisition/Web/acquisition.js").Replace("\\", "/");
        var script = $$"""
            global.window = {};
            global.document = { documentElement: {}, readyState: 'loading', addEventListener: () => {} };
            let abyssAccent = '';
            global.getComputedStyle = () => ({ getPropertyValue: name => name === '--abyss-accent' ? abyssAccent : '' });
            const mod = require('{{jsPath}}');
            const check = (expected) => { const c = mod.themeClassName('acquisition-section'); if (c !== 'acquisition-section acq-theme-' + expected) throw new Error(c + ' != ' + expected); };
            check('jellyfin');                                     // auto, Abyss not installed
            abyssAccent = ' 245, 245, 247'; check('abyss');         // auto, Abyss installed
            window.__ACQUISITION_STYLE__ = 'jellyfin'; check('jellyfin');
            abyssAccent = ''; window.__ACQUISITION_STYLE__ = 'abyss'; check('abyss');
            console.log('style ok');
            """;
        var tempScript = Path.Combine(Path.GetTempPath(), $"style_test_{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(tempScript, script);
            using var process = Process.Start(new ProcessStartInfo { FileName = "node", Arguments = tempScript, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            Assert.NotNull(process);
            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, stdErr + stdOut);
            Assert.Contains("style ok", stdOut);
        }
        finally
        {
            try { File.Delete(tempScript); } catch { }
        }
    }

    [Fact]
    public void RedactSecrets_KeepsHttpsScheme()
    {
        Assert.Equal("https://user:[REDACTED]@indexer.example/feed", SecurityHelper.RedactSecrets("https://user:secret@indexer.example/feed"));
    }

    [Fact]
    public void Migration_ClearsLegacyGlobalRootsAfterMovingThem()
    {
        var config = new PluginConfiguration { SonarrRootFolderPath = "/media/tv", Destinations = [] };

        Assert.True(config.EnsureMigratedDestinations());
        Assert.Single(config.Destinations);
        Assert.Equal(string.Empty, config.SonarrRootFolderPath);
        Assert.False(config.EnsureMigratedDestinations());
    }

    [Fact]
    public void History_IsBounded()
    {
        var req = new AcquisitionRequest();
        for (var i = 0; i < AcquisitionRequest.MaxHistoryEntries + 50; i++)
        {
            req.AddHistory(RequestStatus.Searching, $"msg {i}");
        }

        Assert.Equal(AcquisitionRequest.MaxHistoryEntries, req.History.Count);
        Assert.Equal($"msg {AcquisitionRequest.MaxHistoryEntries + 49}", req.History.Last().Message);
    }
}
