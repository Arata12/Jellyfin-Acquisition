using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Catalog;

public interface ILocalMatcherService
{
    Guid? FindLocalMatch(Guid userId, MediaKind kind, string title, int? year, ProviderIds providerIds);
    bool AreEpisodesAvailable(Guid userId, Guid seriesId, IReadOnlyList<EpisodeTarget> targets);
    void MatchBatch(Guid userId, List<CatalogItemDto> items);
}
