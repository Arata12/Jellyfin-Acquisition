using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Storage;

/// <summary>
/// Repository interface for persisting acquisition requests.
/// </summary>
public interface IRequestRepository
{
    Task<IReadOnlyList<AcquisitionRequest>> GetAllAsync(CancellationToken ct = default);
    Task<AcquisitionRequest?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<AcquisitionRequest?> FindActiveByExternalIdAsync(MediaKind kind, string provider, string id, CancellationToken ct = default);
    Task<AcquisitionRequest?> FindActiveByTitleAndYearAsync(string title, int? year, MediaKind kind, CancellationToken ct = default);
    Task SaveOrUpdateAsync(AcquisitionRequest request, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
