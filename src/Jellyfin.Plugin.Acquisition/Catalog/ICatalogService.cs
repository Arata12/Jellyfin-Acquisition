using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Catalog;

public interface ICatalogService
{
    Task<List<CatalogItemDto>> SearchCatalogAsync(Guid userId, string query, CancellationToken ct = default);
    /// <param name="includeLocal">Keep titles already in the library (the web search leaves them out).</param>
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
}
