using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Catalog;

public class LocalMatcherService : ILocalMatcherService
{
    /// <summary>
    /// Library indexes are reused for a short time so each search keystroke does not load the whole library;
    /// any library change invalidates them immediately.
    /// </summary>
    private static readonly TimeSpan IndexLifetime = TimeSpan.FromSeconds(60);

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<LocalMatcherService> _logger;
    private readonly ConcurrentDictionary<(Guid UserId, MediaKind Kind), LibraryIndex> _indexes = new();

    private sealed class LibraryIndex
    {
        public DateTimeOffset BuiltAt { get; } = DateTimeOffset.UtcNow;
        public Dictionary<string, Guid> ByTvdb { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Guid> ByTmdb { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Guid> ByImdb { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(string Title, int Year), List<Guid>> ByTitleYear { get; } = new();
    }

    public LocalMatcherService(ILibraryManager libraryManager, IUserManager userManager, ILogger<LocalMatcherService> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;

        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemUpdated += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Series or Movie)
        {
            _indexes.Clear();
        }
    }

    private LibraryIndex GetIndex(Jellyfin.Database.Implementations.Entities.User user, MediaKind kind)
    {
        var key = (user.Id, kind);
        if (_indexes.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.BuiltAt < IndexLifetime)
        {
            return cached;
        }

        var index = new LibraryIndex();
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [kind == MediaKind.Series ? BaseItemKind.Series : BaseItemKind.Movie],
            Recursive = true
        };

        foreach (var item in _libraryManager.GetItemList(query).Where(i => kind == MediaKind.Series ? i is Series : i is Movie))
        {
            var tvdb = item.GetProviderId(MetadataProvider.Tvdb);
            if (!string.IsNullOrEmpty(tvdb)) index.ByTvdb[tvdb] = item.Id;

            // Jellyfin's built-in metadata is TMDB-based; series often have no TVDB id unless the TVDB plugin is installed
            var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
            if (!string.IsNullOrEmpty(tmdb)) index.ByTmdb[tmdb] = item.Id;

            var imdb = item.GetProviderId(MetadataProvider.Imdb);
            if (!string.IsNullOrEmpty(imdb)) index.ByImdb[imdb] = item.Id;

            if (!string.IsNullOrEmpty(item.Name) && item.ProductionYear.HasValue)
            {
                var titleKey = (item.Name.Trim().ToLowerInvariant(), item.ProductionYear.Value);
                if (!index.ByTitleYear.TryGetValue(titleKey, out var list))
                {
                    list = [];
                    index.ByTitleYear[titleKey] = list;
                }

                list.Add(item.Id);
            }
        }

        _indexes[key] = index;
        return index;
    }

    public void MatchBatch(Guid userId, List<CatalogItemDto> items)
    {
        if (items.Count == 0) return;

        var user = _userManager.GetUserById(userId);
        if (user == null) return;

        try
        {
            var seriesIndex = items.Any(i => i.Kind == MediaKind.Series) ? GetIndex(user, MediaKind.Series) : null;
            var movieIndex = items.Any(i => i.Kind == MediaKind.Movie) ? GetIndex(user, MediaKind.Movie) : null;

            foreach (var item in items)
            {
                var index = item.Kind == MediaKind.Series ? seriesIndex : movieIndex;
                if (index == null) continue;

                var ids = item.ProviderIds;
                if (TryGet(index.ByTvdb, item.Kind == MediaKind.Series ? ids.Tvdb : null, out var id) ||
                    TryGet(index.ByTmdb, ids.Tmdb, out id) ||
                    TryGet(index.ByImdb, ids.Imdb, out id))
                {
                    item.IsLocal = true;
                    item.LocalItemId = id;
                    continue;
                }

                // Strict fallback: only if external IDs are missing and title+year match is unique
                var hasIds = !string.IsNullOrEmpty(ids.Tvdb) || !string.IsNullOrEmpty(ids.Tmdb) || !string.IsNullOrEmpty(ids.Imdb);
                if (!hasIds && !string.IsNullOrEmpty(item.Title) && item.Year.HasValue &&
                    index.ByTitleYear.TryGetValue((item.Title.Trim().ToLowerInvariant(), item.Year.Value), out var matches) &&
                    matches.Count == 1)
                {
                    item.IsLocal = true;
                    item.LocalItemId = matches[0];
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in MatchBatch for user {UserId}", userId);
        }
    }

    private static bool TryGet(Dictionary<string, Guid> index, string? key, out Guid id)
    {
        id = Guid.Empty;
        return !string.IsNullOrEmpty(key) && index.TryGetValue(key, out id);
    }

    public Guid? FindLocalMatch(Guid userId, MediaKind kind, string title, int? year, ProviderIds providerIds)
    {
        var dummy = new CatalogItemDto
        {
            Kind = kind,
            Title = title,
            Year = year,
            ProviderIds = providerIds
        };

        MatchBatch(userId, [dummy]);
        return dummy.LocalItemId;
    }

    public bool AreEpisodesAvailable(Guid userId, Guid seriesId, IReadOnlyList<EpisodeTarget> targets)
    {
        if (targets.Count == 0) return true;

        var user = _userManager.GetUserById(userId);
        if (user == null) return false;

        try
        {
            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                AncestorIds = [seriesId],
                Recursive = true
            };

            var localEpisodes = _libraryManager.GetItemList(query).OfType<Episode>().ToList();
            if (localEpisodes.Count == 0) return false;

            var existingMap = new HashSet<(int Season, int Episode)>();
            foreach (var ep in localEpisodes)
            {
                var sNum = ep.ParentIndexNumber ?? 1;
                var eNum = ep.IndexNumber ?? -1;
                if (eNum >= 0 && (ep.LocationType == LocationType.FileSystem || !string.IsNullOrEmpty(ep.Path)))
                {
                    existingMap.Add((sNum, eNum));
                }
            }

            return targets.All(t => existingMap.Contains((t.SeasonNumber, t.EpisodeNumber)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking AreEpisodesAvailable for series {SeriesId}", seriesId);
            return false;
        }
    }
}
