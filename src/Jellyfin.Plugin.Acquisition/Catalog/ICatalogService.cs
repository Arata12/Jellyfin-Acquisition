using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Catalog;

public interface ICatalogService
{
    Task<List<CatalogItemDto>> SearchCatalogAsync(Guid userId, string query, CancellationToken ct = default);
    Task<CatalogSearchResultDto> SearchCatalogWithDiagnosticsAsync(Guid userId, string query, CancellationToken ct = default);
    void InvalidateSearchCache(string? query = null);
    /// <summary>
    /// Maps a series already in the local library to its Sonarr (TVDB) catalog entry using its external IDs.
    /// </summary>
    Task<CatalogItemDto?> ResolveLocalSeriesAsync(Guid userId, string title, int? year, ProviderIds ids, CancellationToken ct = default);

    Task<CatalogItemDto?> GetDetailsAsync(Guid userId, MediaKind kind, string providerId, CancellationToken ct = default);
    Task<(byte[] Bytes, string ContentType)?> GetImageAsync(string opaqueId, CancellationToken ct = default);
}
