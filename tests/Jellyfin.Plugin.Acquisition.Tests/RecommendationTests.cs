using System.Diagnostics;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Jellyfin.Plugin.Acquisition.Catalog;
using Jellyfin.Plugin.Acquisition.Configuration;
using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Recommendations;
using Jellyfin.Plugin.Acquisition.Seerr;

namespace Jellyfin.Plugin.Acquisition.Tests;

public class RecommendationTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Jellyfin.Database.Implementations.Entities.User _user = new("u", "u", "u");

    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Mock<ISimilarItemsManager> _similar = new();
    private readonly Mock<IServerConfigurationManager> _serverConfig = new();
    private readonly Mock<ITmdbClient> _tmdb = new();
    private readonly Mock<ICatalogService> _catalog = new();

    // What the user has watched, newest first, and what the library holds
    private readonly List<BaseItem> _history = [];
    private readonly Dictionary<Guid, BaseItem> _byId = new();
    private readonly Dictionary<Guid, List<BaseItem>> _similarTo = new();
    private readonly HashSet<Guid> _startedSeries = [];
    private readonly HashSet<Guid> _resumableSeries = [];
    private readonly Dictionary<string, Guid> _localByTmdb = new();
    private readonly Dictionary<string, RequestStatus> _requestedByTmdb = new();

    public RecommendationTests()
    {
        _users.Setup(u => u.GetUserById(_userId)).Returns(_user);

        _library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _byId.GetValueOrDefault(id));
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) =>
        {
            if (q.AncestorIds.Length > 0)
            {
                var series = q.AncestorIds[0];
                var hit = q.IsPlayed == true ? _startedSeries.Contains(series) : q.IsResumable == true && _resumableSeries.Contains(series);
                return hit ? [new Episode()] : [];
            }

            return _history.ToList();
        });

        _similar.Setup(s => s.GetSimilarItemsAsync(It.IsAny<BaseItem>(), It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
                It.IsAny<DtoOptions>(), It.IsAny<int?>(), It.IsAny<LibraryOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BaseItem seed, IReadOnlyList<Guid> _, Jellyfin.Database.Implementations.Entities.User _, DtoOptions _, int? _, LibraryOptions _, CancellationToken _) =>
                (IReadOnlyList<BaseItem>)(_similarTo.GetValueOrDefault(seed.Id) ?? []));

        _userData.Setup(d => d.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) => _movieData.GetValueOrDefault(item.Id) ?? new UserItemData { Key = "k" });

        _serverConfig.SetupGet(c => c.Configuration).Returns(new ServerConfiguration { PreferredMetadataLanguage = "es", MetadataCountryCode = "ES" });

        _tmdb.SetupGet(t => t.IsConfigured).Returns(true);
        _catalog.Setup(c => c.RegisterImage(It.IsAny<string>())).Returns((string? url) => url == null ? null : "abc" + Math.Abs(url.GetHashCode()));
        _catalog.Setup(c => c.EnrichWithLocalAndRequestStatusAsync(_userId, It.IsAny<List<CatalogItemDto>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, List<CatalogItemDto> items, CancellationToken _) => items.Select(i =>
            {
                var copy = i.Clone();
                if (i.ProviderIds.Tmdb != null && _localByTmdb.TryGetValue(i.ProviderIds.Tmdb, out var local))
                {
                    copy.IsLocal = true;
                    copy.LocalItemId = local;
                }

                if (i.ProviderIds.Tmdb != null && _requestedByTmdb.TryGetValue(i.ProviderIds.Tmdb, out var status))
                {
                    copy.RequestId = "r-" + i.ProviderIds.Tmdb;
                    copy.RequestStatus = status;
                }

                return copy;
            }).ToList());
    }

    private readonly Dictionary<Guid, UserItemData> _movieData = new();

    private RecommendationService Service(bool enabled = true, bool allowAll = true)
    {
        var appPaths = new Mock<IApplicationPaths>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(temp);
        appPaths.Setup(p => p.PluginsPath).Returns(temp);
        var xml = new Mock<MediaBrowser.Model.Serialization.IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(new PluginConfiguration { AllowAllUsers = allowAll, EnableRecommendations = enabled });
        _ = new Plugin(appPaths.Object, xml.Object);

        return new RecommendationService(_library.Object, _users.Object, _userData.Object, _similar.Object, _serverConfig.Object,
            _tmdb.Object, _catalog.Object, Mock.Of<ILogger<RecommendationService>>());
    }

    private Movie AddMovie(string name, string? tmdb = null)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = name };
        if (tmdb != null) movie.ProviderIds["Tmdb"] = tmdb;
        _byId[movie.Id] = movie;
        return movie;
    }

    private Series AddSeries(string name, string? tmdb = null)
    {
        var series = new Series { Id = Guid.NewGuid(), Name = name };
        if (tmdb != null) series.ProviderIds["Tmdb"] = tmdb;
        _byId[series.Id] = series;
        return series;
    }

    private static SeerrMediaDto Rec(int id, string type, string title) =>
        new() { Id = id, MediaType = type, Title = type == "movie" ? title : null, Name = type == "tv" ? title : null, PosterPath = $"/p{id}.jpg", ReleaseDate = type == "movie" ? "2020-05-01" : null, FirstAirDate = type == "tv" ? "2021-01-01" : null };

    private void TmdbReturns(string path, params SeerrMediaDto[] results) =>
        _tmdb.Setup(t => t.GetPageAsync(path, 1, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeerrPageDto { Results = results.ToList() });

    [Fact]
    public async Task Disabled_ReturnsNoRows_AndDoesNotCallTmdb()
    {
        var movie = AddMovie("Seed", "10");
        _history.Add(movie);

        var result = await Service(enabled: false).GetRecommendationsAsync(_userId);

        Assert.Empty(result.Rows);
        _tmdb.Verify(t => t.GetPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoHistory_ReturnsNoRows()
    {
        var result = await Service().GetRecommendationsAsync(_userId);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task EpisodesAreGroupedIntoTheirSeries_AndSeedsAreLimitedToTen()
    {
        var series = Enumerable.Range(0, 12).Select(i => AddSeries("S" + i)).ToList();
        foreach (var s in series) _similarTo[s.Id] = [AddSeries("fill-" + s.Name)];
        // S0 has three episodes: they count as one seed, so S10 still makes it into the ten
        _history.AddRange([new Episode { SeriesId = series[0].Id }, new Episode { SeriesId = series[0].Id }, new Episode { SeriesId = series[0].Id }]);
        _history.AddRange(series.Skip(1).Select(s => (BaseItem)new Episode { SeriesId = s.Id }));

        var result = await Service().GetRecommendationsAsync(_userId);

        var row = Assert.Single(result.Rows);
        Assert.Equal("Te podría gustar", row.Title);
        Assert.Null(row.SeedItemId);
        var titles = row.Items.Select(i => i.Title).ToList();
        Assert.Equal(10, titles.Count);
        Assert.Contains("fill-S9", titles);
        Assert.DoesNotContain("fill-S10", titles);
        Assert.DoesNotContain("fill-S11", titles);
    }

    [Fact]
    public async Task TitleRecommendedByManySeeds_RanksAheadOfOneRecommendedByOne()
    {
        var first = AddMovie("First");
        var second = AddMovie("Second");
        _history.AddRange([first, second]);
        var x = AddMovie("X");
        var y = AddMovie("Y");
        var z = AddMovie("Z");
        _similarTo[first.Id] = [x, y];
        _similarTo[second.Id] = [z, y];

        var result = await Service().GetRecommendationsAsync(_userId);

        // Y: 2/1.1 + 1/1.1 = 2.73, X: 2, Z: 1
        Assert.Equal(["Y", "X", "Z"], Assert.Single(result.Rows).Items.Select(i => i.Title));
    }

    [Fact]
    public async Task RecentSeeds_WeighMore()
    {
        var recent = AddMovie("Recent");
        var old = AddMovie("Old");
        _history.AddRange([recent, old]);
        _similarTo[recent.Id] = [AddMovie("FromRecent")];
        _similarTo[old.Id] = [AddMovie("FromOld")];

        var result = await Service().GetRecommendationsAsync(_userId);

        Assert.Equal(["FromRecent", "FromOld"], Assert.Single(result.Rows).Items.Select(i => i.Title));
    }

    [Fact]
    public async Task SameLocalTitle_FromTmdbAndSimilar_IsMergedAndSumsItsScore()
    {
        var seed = AddMovie("Seed", "100");
        _history.Add(seed);
        var shared = AddMovie("Shared", "1");
        _localByTmdb["1"] = shared.Id;
        _similarTo[seed.Id] = [shared];
        TmdbReturns("movie/100/recommendations", Rec(2, "movie", "Remote"), Rec(1, "movie", "Shared"));

        var result = await Service().GetRecommendationsAsync(_userId);

        // Shared: 1/1.1 (TMDB) + 1 (similar) = 1.91, ahead of Remote at 1
        var items = Assert.Single(result.Rows).Items;
        Assert.Equal(["Shared", "Remote"], items.Select(i => i.Title));
        Assert.True(items[0].IsLocal);
        Assert.Equal(shared.Id, items[0].LocalItemId);
    }

    [Fact]
    public async Task EqualScores_PutAvailableTitlesFirst()
    {
        var seed = AddMovie("Seed", "100");
        _history.Add(seed);
        _similarTo[seed.Id] = [AddMovie("Local")];
        TmdbReturns("movie/100/recommendations", Rec(2, "movie", "Remote"));

        var result = await Service().GetRecommendationsAsync(_userId);

        Assert.Equal(["Local", "Remote"], Assert.Single(result.Rows).Items.Select(i => i.Title));
    }

    [Fact]
    public async Task RowIsLimitedToThirty_AndTvdbIsOnlyResolvedForWhatIsShown()
    {
        var seed = AddSeries("Seed", "100");
        _history.Add(new Episode { SeriesId = seed.Id });
        TmdbReturns("tv/100/recommendations", Enumerable.Range(1, 40).Select(i => Rec(i, "tv", "T" + i)).ToArray());
        _tmdb.Setup(t => t.GetTvdbIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((int id, CancellationToken _) => id + 1000);

        var result = await Service().GetRecommendationsAsync(_userId);

        var items = Assert.Single(result.Rows).Items;
        Assert.Equal(30, items.Count);
        Assert.Equal("T1", items[0].Title);
        _tmdb.Verify(t => t.GetTvdbIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(30));
    }

    [Fact]
    public async Task FailingSeed_IsIgnored_AndTheOthersStillCount()
    {
        var broken = AddMovie("Broken");
        var fine = AddMovie("Fine");
        _history.AddRange([broken, fine]);
        _similarTo[fine.Id] = [AddMovie("Ok")];
        _similar.Setup(s => s.GetSimilarItemsAsync(It.Is<BaseItem>(b => b.Id == broken.Id), It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
                It.IsAny<DtoOptions>(), It.IsAny<int?>(), It.IsAny<LibraryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await Service().GetRecommendationsAsync(_userId);

        Assert.Equal(["Ok"], Assert.Single(result.Rows).Items.Select(i => i.Title));
    }

    [Fact]
    public async Task WatchedAndStartedTitlesAreExcluded()
    {
        var seed = AddMovie("Seed");
        _history.Add(seed);
        var played = AddMovie("Played");
        var partial = AddMovie("Partial");
        var fresh = AddMovie("Fresh");
        _movieData[played.Id] = new UserItemData { Key = "k", Played = true };
        _movieData[partial.Id] = new UserItemData { Key = "k", PlaybackPositionTicks = 5000 };
        _similarTo[seed.Id] = [played, partial, fresh];

        var result = await Service().GetRecommendationsAsync(_userId);

        var item = Assert.Single(Assert.Single(result.Rows).Items);
        Assert.Equal("Fresh", item.Title);
        Assert.True(item.IsLocal);
        Assert.Equal(fresh.Id, item.LocalItemId);
    }

    [Fact]
    public async Task Series_WithAnyWatchedOrInProgressEpisode_AreExcluded()
    {
        var seed = AddSeries("Seed");
        _history.Add(new Episode { SeriesId = seed.Id });
        var watched = AddSeries("Watched");
        var inProgress = AddSeries("InProgress");
        var fresh = AddSeries("Fresh");
        _startedSeries.Add(watched.Id);
        _resumableSeries.Add(inProgress.Id);
        _similarTo[seed.Id] = [watched, inProgress, fresh, seed];

        var result = await Service().GetRecommendationsAsync(_userId);

        Assert.Equal(["Fresh"], Assert.Single(result.Rows).Items.Select(i => i.Title));
    }

    [Fact]
    public async Task MixesAvailableAndRequestable_ByScore()
    {
        var seed = AddMovie("Seed", "100");
        _history.Add(seed);
        var availableMovie = AddMovie("Available", "1");
        _localByTmdb["1"] = availableMovie.Id;
        var seenMovie = AddMovie("Seen", "3");
        _localByTmdb["3"] = seenMovie.Id;
        _movieData[seenMovie.Id] = new UserItemData { Key = "k", Played = true };
        _requestedByTmdb["4"] = RequestStatus.Downloading;
        var filler = AddMovie("Filler");
        _similarTo[seed.Id] = [filler];
        TmdbReturns("movie/100/recommendations", Rec(1, "movie", "Available"), Rec(2, "movie", "Remote"), Rec(3, "movie", "Seen"), Rec(4, "movie", "Requested"));

        var result = await Service().GetRecommendationsAsync(_userId);

        var items = Assert.Single(result.Rows).Items;
        Assert.Equal(["Available", "Filler", "Remote", "Requested"], items.Select(i => i.Title));
        Assert.True(items[0].IsLocal);
        Assert.True(items[1].IsLocal);
        Assert.False(items[2].IsLocal);
        Assert.Equal("movie-2", items[2].Id);
        Assert.Equal(2020, items[2].Year);
        Assert.StartsWith("/Acquisition/images/", items[2].PosterUrl);
        Assert.Equal(RequestStatus.Downloading, items[3].RequestStatus);
        // The server's metadata language is sent to TMDB
        _tmdb.Verify(t => t.GetPageAsync("movie/100/recommendations", 1, "es-ES", "movie", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Seeds_AreLeftOut_AndDuplicatesAppearOnce()
    {
        var one = AddMovie("One", "100");
        var two = AddMovie("Two", "200");
        _history.AddRange([one, two]);
        var sharedLocal = AddMovie("SharedLocal");
        _similarTo[one.Id] = [one, two, sharedLocal];
        _similarTo[two.Id] = [one, two, sharedLocal];
        TmdbReturns("movie/100/recommendations", Rec(7, "movie", "Shared"), Rec(100, "movie", "One"), Rec(200, "movie", "Two"));
        TmdbReturns("movie/200/recommendations", Rec(7, "movie", "Shared"), Rec(8, "movie", "Other"));

        var result = await Service().GetRecommendationsAsync(_userId);

        Assert.Equal(["Other", "Shared", "SharedLocal"], Assert.Single(result.Rows).Items.Select(i => i.Title).Order());
    }

    [Fact]
    public async Task WithoutTmdb_OnlyLibraryTitlesAppear()
    {
        _tmdb.SetupGet(t => t.IsConfigured).Returns(false);
        var seed = AddMovie("Seed", "100");
        _history.Add(seed);
        _similarTo[seed.Id] = [AddMovie("Local")];

        var result = await Service().GetRecommendationsAsync(_userId);

        Assert.Equal(["Local"], Assert.Single(result.Rows).Items.Select(i => i.Title));
        _tmdb.Verify(t => t.GetPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TmdbFailure_FallsBackToLibraryTitles()
    {
        var seed = AddMovie("Seed", "100");
        _history.Add(seed);
        _similarTo[seed.Id] = [AddMovie("Local")];
        _tmdb.Setup(t => t.GetPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeerrPageDto?)null);

        var result = await Service().GetRecommendationsAsync(_userId);

        Assert.Equal(["Local"], Assert.Single(result.Rows).Items.Select(i => i.Title));
    }

    [Fact]
    public async Task UserWithoutRequestPermission_OnlyGetsAvailableTitles()
    {
        var seed = AddMovie("Seed", "100");
        _history.Add(seed);
        var available = AddMovie("Available", "1");
        _localByTmdb["1"] = available.Id;
        TmdbReturns("movie/100/recommendations", Rec(1, "movie", "Available"), Rec(2, "movie", "Remote"));

        var result = await Service(allowAll: false).GetRecommendationsAsync(_userId);

        var item = Assert.Single(Assert.Single(result.Rows).Items);
        Assert.Equal("Available", item.Title);
        _tmdb.Verify(t => t.GetTvdbIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoteSeries_NeedATvdbId_AndTheLookupIsCached()
    {
        var seed = AddSeries("Seed", "100");
        _history.Add(new Episode { SeriesId = seed.Id });
        TmdbReturns("tv/100/recommendations", Rec(5, "tv", "WithTvdb"), Rec(6, "tv", "NoTvdb"));
        _tmdb.Setup(t => t.GetTvdbIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(555);
        _tmdb.Setup(t => t.GetTvdbIdAsync(6, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        var service = Service();

        var first = await service.GetRecommendationsAsync(_userId);
        var second = await service.GetRecommendationsAsync(_userId);

        var item = Assert.Single(Assert.Single(first.Rows).Items);
        Assert.Equal("WithTvdb", item.Title);
        Assert.Equal(MediaKind.Series, item.Kind);
        Assert.Equal("series-555", item.Id);
        Assert.Equal("555", item.ProviderIds.Tvdb);
        Assert.Equal("5", item.ProviderIds.Tmdb);
        Assert.Equal(2021, item.Year);
        Assert.Single(Assert.Single(second.Rows).Items);
        // TMDB answers (the list and the ids) are reused by the second call
        _tmdb.Verify(t => t.GetPageAsync("tv/100/recommendations", 1, It.IsAny<string?>(), "tv", null, It.IsAny<CancellationToken>()), Times.Once);
        _tmdb.Verify(t => t.GetTvdbIdAsync(5, It.IsAny<CancellationToken>()), Times.Once);
        _tmdb.Verify(t => t.GetTvdbIdAsync(6, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- Web client ----

    private static string RunNode(string body)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.Acquisition.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var jsPath = Path.Combine(dir.FullName, "src/Jellyfin.Plugin.Acquisition/Web/acquisition.js").Replace("\\", "/");

        var script = """
            class El {
                constructor(tag) { this.tag = tag; this.children = []; this.className = ''; this.dataset = {}; this.id = ''; this._html = ''; this.scroller = null; }
                set innerHTML(v) { this._html = v; this.scroller = v.includes('acquisition-recs-scroller') ? new El('div') : null; }
                get innerHTML() { return this._html; }
                appendChild(c) { this.children.push(c); return c; }
                querySelector(sel) { return sel === '.acquisition-recs-scroller' ? this.scroller : null; }
                querySelectorAll() { return []; }
                addEventListener() {}
            }
            global.document = {
                readyState: 'complete', body: new El('body'),
                createElement: (tag) => new El(tag), getElementById: () => null,
                querySelector: () => null, querySelectorAll: () => [], addEventListener: () => {}
            };
            global.window = { addEventListener: () => {}, MutationObserver: class { observe() {} }, ApiClient: null };
            global.MutationObserver = global.window.MutationObserver;
            """ + $"const mod = require('{jsPath}');\n" + body;

        var temp = Path.Combine(Path.GetTempPath(), $"recs_test_{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(temp, script);
            using var process = Process.Start(new ProcessStartInfo { FileName = "node", Arguments = temp, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            Assert.NotNull(process);
            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, stdErr + stdOut);
            return stdOut;
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    [Fact]
    public void JavaScript_IsHomeRoute_RecognisesTheHomePage()
    {
        var output = RunNode("""
            const yes = ['#/home', '#/home.html', '#!/home', '#/home?tab=1', '#!/home.html?topParentId=abc'];
            const no = ['', '#/search', '#/homevideos', '#/details?id=1', '#/list?type=home', '#/home/x'];
            yes.forEach(h => { if (!mod.isHomeRoute(h)) throw new Error('should be home: ' + h); });
            no.forEach(h => { if (mod.isHomeRoute(h)) throw new Error('should not be home: ' + h); });
            console.log('home route ok');
            """);
        Assert.Contains("home route ok", output);
    }

    [Fact]
    public void JavaScript_RendersRowsWithAvailableAndRequestableBadges()
    {
        var output = RunNode("""
            const data = mod.normalizeRecommendations({
                Rows: [
                    { Title: 'Te podría gustar <Frieren>', SeedItemId: 'seed', Items: [
                        { Id: 'a', Kind: 'Series', Title: 'Local', IsLocal: true, LocalItemId: 'guid-1', PosterUrl: '/Items/guid-1/Images/Primary' },
                        { Id: 'movie-2', Kind: 'Movie', Title: 'Remote', ProviderIds: { Tmdb: '2' } },
                        { Id: 'movie-3', Kind: 'Movie', Title: 'Requested', RequestStatus: 'Downloading', RequestId: 'r1' }
                    ] },
                    { Title: 'Vacía', SeedItemId: 'x', Items: [] }
                ]
            });
            if (data.rows.length !== 1) throw new Error('empty rows must be dropped: ' + data.rows.length);

            const section = mod.renderRecommendations(data);
            if (section.id !== 'acquisition-recommendations') throw new Error('wrong id ' + section.id);
            if (!section.className.includes('acquisition-recs') || !section.className.includes('acq-theme-')) throw new Error('wrong class ' + section.className);
            const row = section.children[0];
            if (!row.innerHTML.includes('Te podría gustar &lt;Frieren&gt;')) throw new Error('title not escaped: ' + row.innerHTML);
            const cards = row.scroller.children;
            if (cards.length !== 3) throw new Error('expected 3 cards, got ' + cards.length);
            const [local, remote, requested] = cards.map(c => c.innerHTML);
            if (!local.includes('status-Available') || !local.includes('Disponible')) throw new Error('available badge missing: ' + local);
            if (local.includes('Pedir')) throw new Error('available title must not offer to request');
            if (!remote.includes('status-Suggested') || !remote.includes('Pedir')) throw new Error('request badge missing: ' + remote);
            if (!requested.includes('status-Downloading') || requested.includes('Pedir')) throw new Error('request status badge wrong: ' + requested);
            console.log('recommendation rows ok');
            """);
        Assert.Contains("recommendation rows ok", output);
    }
}
