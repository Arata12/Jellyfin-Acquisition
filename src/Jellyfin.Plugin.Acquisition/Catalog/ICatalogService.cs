using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Catalog;

public interface ICatalogService
{
    Task<List<CatalogItemDto>> SearchCatalogAsync(Guid userId, string query, CancellationToken ct = default);
    /// <param name="includeLocal">Keep movies already in the library (the web search leaves them out; series always stay so more seasons can be requested).</param>
    Task<CatalogSearchResultDto> SearchCatalogWithDiagnosticsAsync(Guid userId, string query, CancellationToken ct = default, bool includeLocal = false);
    void InvalidateSearchCache(string? query = null);
    /// <summary>
    /// Maps a series already in the local library to its Sonarr (TVDB) catalog entry using its external IDs.
    /// </summary>
    Task<CatalogItemDto?> ResolveLocalSeriesAsync(Guid userId, string title, int? year, ProviderIds ids, CancellationToken ct = default);

    Task<CatalogItemDto?> GetDetailsAsync(Guid userId, MediaKind kind, string providerId, CancellationToken ct = default);
    Task<(byte[] Bytes, string ContentType)?> GetImageAsync(string opaqueId, CancellationToken ct = default);

    /// <summary>
    /// Registers a remote image and returns the opaque ID that <see cref="GetImageAsync"/> serves it under.
    /// </summary>
    string? RegisterImage(string? remoteUrl);

    /// <summary>
    /// Copies the items and marks the ones in the user's library and the ones with an active request
    /// (the same enrichment search results get). Movies already in the library are kept.
    /// </summary>
    Task<List<CatalogItemDto>> EnrichWithLocalAndRequestStatusAsync(Guid userId, List<CatalogItemDto> items, CancellationToken ct = default);
}
