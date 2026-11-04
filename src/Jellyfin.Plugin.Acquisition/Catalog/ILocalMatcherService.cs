using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Catalog;

public interface ILocalMatcherService
{
    Guid? FindLocalMatch(Guid userId, MediaKind kind, string title, int? year, ProviderIds providerIds);
    bool AreEpisodesAvailable(Guid userId, Guid seriesId, IReadOnlyList<EpisodeTarget> targets);

    /// <summary>
    /// (season, episode) pairs of the series that exist as files in the Jellyfin library, for episodes Sonarr does not
    /// know about (copied to the library by hand). Empty when the series is not in the library.
    /// </summary>
    IReadOnlySet<(int Season, int Episode)> GetLocalEpisodes(Guid userId, MediaKind kind, string title, int? year, ProviderIds providerIds);

    void MatchBatch(Guid userId, List<CatalogItemDto> items);
}
