using System.Collections.Concurrent;
using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Catalog;
using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Security;
using Jellyfin.Plugin.Acquisition.Seerr;

namespace Jellyfin.Plugin.Acquisition.Recommendations;

public interface IRecommendationService
{
    /// <summary>
    /// A single "You might like" row built from the user's most recently watched titles, the most recent weighing
    /// more. Empty when the option is off or the user has no history.
    /// </summary>
    Task<RecommendationsDto> GetRecommendationsAsync(Guid userId, CancellationToken ct = default);
}

public class RecommendationService : IRecommendationService
{
    internal const int MaxSeeds = 10;
    internal const int MaxItems = 30;
    internal const int HistoryLimit = 100;
    internal const string RowTitle = "Te podría gustar";
    private const double PositionDecay = 0.1;

    private static readonly TimeSpan TmdbCacheLifetime = TimeSpan.FromHours(12);
    private const int MaxCachedLists = 500;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ISimilarItemsManager _similarItems;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly ITmdbClient _tmdb;
    private readonly ICatalogService _catalog;
    private readonly ILogger<RecommendationService> _logger;

    private readonly ConcurrentDictionary<(MediaKind Kind, int TmdbId, string Language), (DateTimeOffset Expiry, List<SeerrMediaDto> Items)> _tmdbCache = new();
    // TMDB series id -> TVDB id (0 = none); the mapping never changes, so it is kept for the process lifetime
    private readonly ConcurrentDictionary<int, int> _tvdbIds = new();

    public RecommendationService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ISimilarItemsManager similarItems,
        IServerConfigurationManager serverConfig,
        ITmdbClient tmdb,
        ICatalogService catalog,
        ILogger<RecommendationService> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _similarItems = similarItems;
        _serverConfig = serverConfig;
        _tmdb = tmdb;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<RecommendationsDto> GetRecommendationsAsync(Guid userId, CancellationToken ct = default)
    {
        var result = new RecommendationsDto();
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableRecommendations) return result;

        var user = _userManager.GetUserById(userId);
        if (user == null) return result;

        var canRequest = SecurityHelper.CanAcquire(userId, _userManager, config);
        var language = GetMetadataLanguage();

        var seeds = GetSeeds(user);
        if (seeds.Count == 0) return result;

        // Watched titles are never recommended; by TMDB id too, in case the library match of a recommendation misses them
        var excluded = new HashSet<string>();
        foreach (var seed in seeds)
        {
            excluded.Add(LocalKey(seed.Id));
            excluded.Add($"{(seed is Series ? MediaKind.Series : MediaKind.Movie)}:{seed.GetProviderId(MetadataProvider.Tmdb)}");
        }

        // Every appearance of a title in a seed's list adds seedWeight * positionWeight to its score
        var candidates = new Dictionary<string, Candidate>();
        for (var index = 0; index < seeds.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var seedWeight = seeds.Count - index;
            try
            {
                var found = await GetSeedCandidatesAsync(user, userId, seeds[index], language, excluded, ct).ConfigureAwait(false);
                foreach (var candidate in found)
                {
                    var weight = seedWeight / (1 + PositionDecay * candidate.Position);
                    if (candidates.TryGetValue(candidate.Key, out var existing))
                    {
                        existing.Score += weight;
                    }
                    else
                    {
                        candidate.Score = weight;
                        candidates[candidate.Key] = candidate;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not get recommendations for {Seed}", seeds[index].Name);
            }
        }

        // Best score first; available titles first among equals. The costly checks run only on what gets to the row.
        var items = new List<CatalogItemDto>();
        var seriesSlots = new List<int>();
        foreach (var candidate in candidates.Values.OrderByDescending(c => c.Score).ThenByDescending(c => c.Item.IsLocal))
        {
            if (items.Count >= MaxItems) break;
            ct.ThrowIfCancellationRequested();

            var item = candidate.Item;
            if (item.IsLocal)
            {
                var local = candidate.Local ?? (item.LocalItemId.HasValue ? _libraryManager.GetItemById(item.LocalItemId.Value) : null);
                if (local == null || HasStarted(user, local)) continue;

                item.PosterUrl = LocalPosterUrl(local) ?? item.PosterUrl;
                items.Add(item);
            }
            else if (canRequest)
            {
                if (item.Kind == MediaKind.Series)
                {
                    // Requests of series are keyed by TVDB; TMDB gives it separately
                    var tvdb = 0;
                    try
                    {
                        tvdb = int.TryParse(item.ProviderIds.Tmdb, out var tmdbId) ? await GetTvdbIdAsync(tmdbId, ct).ConfigureAwait(false) : 0;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Could not resolve the TVDB id of {Title}", item.Title);
                    }

                    if (tvdb <= 0) continue;

                    item.ProviderIds.Tvdb = tvdb.ToString(CultureInfo.InvariantCulture);
                    item.Id = $"series-{tvdb}";
                    seriesSlots.Add(items.Count);
                }

                items.Add(item);
            }
        }

        if (seriesSlots.Count > 0)
        {
            // The request status of a series is matched by its TVDB id, known only now
            var withStatus = await _catalog.EnrichWithLocalAndRequestStatusAsync(userId, seriesSlots.Select(i => items[i]).ToList(), ct).ConfigureAwait(false);
            for (var i = 0; i < seriesSlots.Count && i < withStatus.Count; i++) items[seriesSlots[i]] = withStatus[i];
        }

        if (items.Count > 0)
        {
            result.Rows.Add(new RecommendationRowDto { Title = RowTitle, Items = items });
        }

        return result;
    }

    private sealed class Candidate
    {
        public required string Key { get; init; }
        public required CatalogItemDto Item { get; init; }
        public BaseItem? Local { get; init; }
        /// <summary>Place in the seed's own list (TMDB or library), from 0.</summary>
        public int Position { get; init; }
        public double Score { get; set; }
    }

    /// <summary>The most recently watched movies and series (episodes count for their series), newest first.</summary>
    private List<BaseItem> GetSeeds(Jellyfin.Database.Implementations.Entities.User user)
    {
        var played = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            IsPlayed = true,
            Recursive = true,
            Limit = HistoryLimit,
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)]
        });

        var seeds = new List<BaseItem>();
        var seenIds = new HashSet<Guid>();
        foreach (var entry in played)
        {
            BaseItem? seed = entry;
            if (entry is Episode episode)
            {
                seed = episode.SeriesId == Guid.Empty ? null : _libraryManager.GetItemById(episode.SeriesId);
            }

            if (seed is not (Movie or Series) || !seenIds.Add(seed.Id)) continue;

            seeds.Add(seed);
            if (seeds.Count >= MaxSeeds) break;
        }

        return seeds;
    }

    /// <summary>
    /// The candidates of one seed in order: TMDB recommendations first, then similar titles from the library. Titles
    /// of the user's history and requestable titles for a user who cannot request are already left out.
    /// </summary>
    private async Task<List<Candidate>> GetSeedCandidatesAsync(
        Jellyfin.Database.Implementations.Entities.User user,
        Guid userId,
        BaseItem seed,
        string? language,
        HashSet<string> excluded,
        CancellationToken ct)
    {
        var kind = seed is Series ? MediaKind.Series : MediaKind.Movie;
        var found = new List<Candidate>();
        // A title can come from both lists of the seed (each counts), but only once from each
        var keys = new HashSet<string>();
        var position = 0;

        // a) TMDB recommendations, in TMDB's order
        var tmdb = await GetTmdbCandidatesAsync(kind, seed, language, ct).ConfigureAwait(false);
        if (tmdb.Count > 0)
        {
            var enriched = await _catalog.EnrichWithLocalAndRequestStatusAsync(userId, tmdb, ct).ConfigureAwait(false);
            foreach (var item in enriched)
            {
                var key = item.IsLocal && item.LocalItemId.HasValue ? LocalKey(item.LocalItemId.Value) : RemoteKey(item);
                if (item.IsLocal && !item.LocalItemId.HasValue) continue;
                if (excluded.Contains(key) || !keys.Add(key)) continue;

                found.Add(new Candidate { Key = key, Item = item, Position = position++ });
            }
        }

        // b) Similar titles from the library: the same source Jellyfin uses for "More like this"
        keys.Clear();
        position = 0;
        var similar = await _similarItems.GetSimilarItemsAsync(
            seed, [], user, new DtoOptions(false), MaxItems * 2, _libraryManager.GetLibraryOptions(seed), ct).ConfigureAwait(false);

        foreach (var local in similar)
        {
            if (kind == MediaKind.Series ? local is not Series : local is not Movie) continue;

            var key = LocalKey(local.Id);
            if (excluded.Contains(key) || !keys.Add(key)) continue;

            found.Add(new Candidate { Key = key, Item = MapLocal(local, kind), Local = local, Position = position++ });
        }

        return found;
    }

    private async Task<List<CatalogItemDto>> GetTmdbCandidatesAsync(MediaKind kind, BaseItem seed, string? language, CancellationToken ct)
    {
        if (!_tmdb.IsConfigured || !int.TryParse(seed.GetProviderId(MetadataProvider.Tmdb), out var seedTmdb) || seedTmdb <= 0)
        {
            return [];
        }

        var key = (kind, seedTmdb, language ?? string.Empty);
        List<SeerrMediaDto>? list = null;
        if (_tmdbCache.TryGetValue(key, out var cached) && cached.Expiry > DateTimeOffset.UtcNow)
        {
            list = cached.Items;
        }
        else
        {
            var path = kind == MediaKind.Series ? $"tv/{seedTmdb}/recommendations" : $"movie/{seedTmdb}/recommendations";
            var page = await _tmdb.GetPageAsync(path, 1, language, kind == MediaKind.Series ? "tv" : "movie", null, ct).ConfigureAwait(false);
            if (page != null)
            {
                list = page.Results;
                if (_tmdbCache.Count >= MaxCachedLists) _tmdbCache.Clear();
                _tmdbCache[key] = (DateTimeOffset.UtcNow + TmdbCacheLifetime, list);
            }
        }

        return (list ?? [])
            .Where(m => m.Id > 0 && m.MediaType == (kind == MediaKind.Series ? "tv" : "movie"))
            .Select(m => MapTmdb(m, kind))
            .ToList();
    }

    private async Task<int> GetTvdbIdAsync(int tmdbId, CancellationToken ct)
    {
        if (_tvdbIds.TryGetValue(tmdbId, out var known)) return known;

        var tvdb = await _tmdb.GetTvdbIdAsync(tmdbId, ct).ConfigureAwait(false);
        if (tvdb == null) return 0; // TMDB unreadable right now: do not cache the failure
        _tvdbIds[tmdbId] = tvdb.Value;
        return tvdb.Value;
    }

    private CatalogItemDto MapTmdb(SeerrMediaDto media, MediaKind kind)
    {
        var isSeries = kind == MediaKind.Series;
        var date = isSeries ? media.FirstAirDate : media.ReleaseDate;
        int? year = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;
        var posterId = string.IsNullOrEmpty(media.PosterPath)
            ? null
            : _catalog.RegisterImage($"https://image.tmdb.org/t/p/w342{media.PosterPath}");

        return new CatalogItemDto
        {
            Id = isSeries ? $"series-tmdb-{media.Id}" : $"movie-{media.Id}",
            Kind = kind,
            Title = (isSeries ? media.Name : media.Title) ?? media.Title ?? media.Name ?? string.Empty,
            Year = year,
            Overview = media.Overview,
            PosterUrl = posterId != null ? $"/Acquisition/images/{posterId}" : null,
            ProviderIds = new ProviderIds { Tmdb = media.Id.ToString(CultureInfo.InvariantCulture) },
            OriginalLanguage = media.OriginalLanguage
        };
    }

    private static CatalogItemDto MapLocal(BaseItem item, MediaKind kind) => new()
    {
        Id = item.Id.ToString("N"),
        Kind = kind,
        Title = item.Name ?? string.Empty,
        Year = item.ProductionYear,
        Overview = item.Overview,
        PosterUrl = LocalPosterUrl(item),
        ProviderIds = new ProviderIds
        {
            Tvdb = item.GetProviderId(MetadataProvider.Tvdb),
            Tmdb = item.GetProviderId(MetadataProvider.Tmdb),
            Imdb = item.GetProviderId(MetadataProvider.Imdb)
        },
        IsLocal = true,
        LocalItemId = item.Id
    };

    /// <summary>Jellyfin's own poster endpoint (the client prefixes the server base path).</summary>
    private static string? LocalPosterUrl(BaseItem item) =>
        item.HasImage(ImageType.Primary) ? $"/Items/{item.Id:N}/Images/Primary?maxWidth=342&quality=90" : null;

    /// <summary>Movie: played or partly played. Series: any episode played or in progress.</summary>
    private bool HasStarted(Jellyfin.Database.Implementations.Entities.User user, BaseItem item)
    {
        if (item is Series)
        {
            var started = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                AncestorIds = [item.Id],
                IsPlayed = true,
                Recursive = true,
                Limit = 1
            });
            if (started.Count > 0) return true;

            var resumable = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                AncestorIds = [item.Id],
                IsResumable = true,
                Recursive = true,
                Limit = 1
            });
            return resumable.Count > 0;
        }

        var data = _userDataManager.GetUserData(user, item);
        return data != null && (data.Played || data.PlaybackPositionTicks > 0);
    }

    private string? GetMetadataLanguage()
    {
        var cfg = _serverConfig.Configuration;
        var lang = cfg.PreferredMetadataLanguage;
        if (string.IsNullOrWhiteSpace(lang)) return null;

        return string.IsNullOrWhiteSpace(cfg.MetadataCountryCode) ? lang : $"{lang}-{cfg.MetadataCountryCode}";
    }

    private static string LocalKey(Guid id) => "local:" + id.ToString("N");

    private static string RemoteKey(CatalogItemDto item) => $"{item.Kind}:{item.ProviderIds.Tmdb}";
}
