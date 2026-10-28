using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Storage;

public class RequestStoreData
{
    public const int CurrentVersion = 3;

    public int SchemaVersion { get; set; } = CurrentVersion;
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
    public List<AcquisitionRequest> Requests { get; set; } = [];
}

public class JsonFileRequestRepository : IRequestRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly string _bakFilePath;
    private readonly string _tmpFilePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ILogger<JsonFileRequestRepository> _logger;

    public JsonFileRequestRepository(IApplicationPaths appPaths, ILogger<JsonFileRequestRepository> logger)
    {
        _logger = logger;
        var dir = Path.Combine(appPaths.PluginConfigurationsPath, "Acquisition");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "requests.json");
        _bakFilePath = Path.Combine(dir, "requests.json.bak");
        _tmpFilePath = Path.Combine(dir, "requests.json.tmp");
    }

    public async Task<IReadOnlyList<AcquisitionRequest>> GetAllAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadDataInternalAsync(ct).ConfigureAwait(false);
            return data.Requests.ToList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<AcquisitionRequest?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadDataInternalAsync(ct).ConfigureAwait(false);
            return data.Requests.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<AcquisitionRequest?> FindActiveByExternalIdAsync(MediaKind kind, string provider, string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadDataInternalAsync(ct).ConfigureAwait(false);
            return data.Requests.FirstOrDefault(r =>
                r.Kind == kind &&
                r.IsActive &&
                IsMatchingProvider(r.ProviderIds, provider, id));
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<AcquisitionRequest?> FindActiveByTitleAndYearAsync(string title, int? year, MediaKind kind, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadDataInternalAsync(ct).ConfigureAwait(false);
            return data.Requests.FirstOrDefault(r =>
                r.Kind == kind &&
                r.IsActive &&
                string.Equals(r.Title, title, StringComparison.OrdinalIgnoreCase) &&
                (!year.HasValue || !r.Year.HasValue || r.Year.Value == year.Value));
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task SaveOrUpdateAsync(AcquisitionRequest request, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadDataInternalAsync(ct).ConfigureAwait(false);
            var index = data.Requests.FindIndex(r => r.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                data.Requests[index] = request;
            }
            else
            {
                data.Requests.Add(request);
            }

            data.LastUpdated = DateTimeOffset.UtcNow;
            await SaveDataInternalAsync(data, ct).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadDataInternalAsync(ct).ConfigureAwait(false);
            var removed = data.Requests.RemoveAll(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                data.LastUpdated = DateTimeOffset.UtcNow;
                await SaveDataInternalAsync(data, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static bool IsMatchingProvider(ProviderIds ids, string provider, string id)
    {
        return provider.ToLowerInvariant() switch
        {
            "tvdb" => string.Equals(ids.Tvdb, id, StringComparison.OrdinalIgnoreCase),
            "tmdb" => string.Equals(ids.Tmdb, id, StringComparison.OrdinalIgnoreCase),
            "imdb" => string.Equals(ids.Imdb, id, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private async Task<RequestStoreData> LoadDataInternalAsync(CancellationToken ct)
    {
        var primaryExists = File.Exists(_filePath);
        var backupExists = File.Exists(_bakFilePath);

        if (!primaryExists && !backupExists)
        {
            return new RequestStoreData();
        }

        if (primaryExists)
        {
            try
            {
                using var stream = File.OpenRead(_filePath);
                var result = await JsonSerializer.DeserializeAsync<RequestStoreData>(stream, JsonOptions, ct).ConfigureAwait(false);
                if (result != null)
                {
                    if (result.SchemaVersion > RequestStoreData.CurrentVersion)
                    {
                        throw new InvalidOperationException($"Unsupported schema version {result.SchemaVersion} (supported: {RequestStoreData.CurrentVersion})");
                    }
                    if (result.SchemaVersion < RequestStoreData.CurrentVersion)
                    {
                        stream.Close();
                        await MigrateSchemaIfNecessaryAsync(result, ct).ConfigureAwait(false);
                    }
                    return result;
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                _logger.LogWarning(ex, "Failed to load requests from {FilePath}. Trying backup {BakPath}.", _filePath, _bakFilePath);
            }
        }

        if (backupExists)
        {
            try
            {
                using var stream = File.OpenRead(_bakFilePath);
                var result = await JsonSerializer.DeserializeAsync<RequestStoreData>(stream, JsonOptions, ct).ConfigureAwait(false);
                if (result != null)
                {
                    if (result.SchemaVersion > RequestStoreData.CurrentVersion)
                    {
                        throw new InvalidOperationException($"Unsupported schema version {result.SchemaVersion} in backup");
                    }

                    // Restore valid backup to primary file immediately (R16)
                    File.Copy(_bakFilePath, _filePath, overwrite: true);
                    _logger.LogWarning("Restored corrupted primary requests file from backup {BakPath}", _bakFilePath);

                    if (result.SchemaVersion < RequestStoreData.CurrentVersion)
                    {
                        stream.Close();
                        await MigrateSchemaIfNecessaryAsync(result, ct).ConfigureAwait(false);
                    }
                    return result;
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                _logger.LogError(ex, "Failed to load requests from backup {BakPath}.", _bakFilePath);
            }
        }

        // Both existed and failed: halt to prevent data loss (R16)
        throw new InvalidDataException("Both primary and backup acquisition requests files are corrupted. Halting to prevent data loss.");
    }

    private async Task MigrateSchemaIfNecessaryAsync(RequestStoreData data, CancellationToken ct)
    {
        if (data.SchemaVersion < RequestStoreData.CurrentVersion)
        {
            _logger.LogInformation("Migrating acquisition requests from SchemaVersion {From} to {To}", data.SchemaVersion, RequestStoreData.CurrentVersion);

            // 1. Create a migration backup separate from the rolling backup (Section 4)
            var v1Bak = Path.Combine(Path.GetDirectoryName(_filePath)!, "requests.json.v1.bak");
            if (File.Exists(_filePath) && !File.Exists(v1Bak))
            {
                File.Copy(_filePath, v1Bak, overwrite: true);
            }

            // 2. Migrate requests
            foreach (var req in data.Requests)
            {
                if (req.Units.Count == 0)
                {
                    if (req.Kind == MediaKind.Series)
                    {
                        if (req.Scope.TargetEpisodes.Count > 0)
                        {
                            var bySeason = req.Scope.TargetEpisodes.GroupBy(t => t.SeasonNumber).OrderBy(g => g.Key).ToList();

                            int? matchedSeason = null;
                            if (req.SelectedRelease != null)
                            {
                                if (req.SelectedRelease.SeasonNumber > 0)
                                {
                                    matchedSeason = req.SelectedRelease.SeasonNumber;
                                }
                                else if (!string.IsNullOrEmpty(req.SelectedRelease.Title))
                                {
                                    var sMatch = System.Text.RegularExpressions.Regex.Match(req.SelectedRelease.Title, @"[.\s_\-\(\[]+[Ss](\d{1,2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                    if (sMatch.Success && int.TryParse(sMatch.Groups[1].Value, out var sNum))
                                    {
                                        matchedSeason = sNum;
                                    }
                                }
                            }

                            if (!matchedSeason.HasValue && !string.IsNullOrEmpty(req.TorrentName))
                            {
                                var sMatch = System.Text.RegularExpressions.Regex.Match(req.TorrentName, @"[.\s_\-\(\[]+[Ss](\d{1,2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (sMatch.Success && int.TryParse(sMatch.Groups[1].Value, out var sNum))
                                {
                                    matchedSeason = sNum;
                                }
                            }

                            var isAmbiguous = false;
                            if (!matchedSeason.HasValue && (!string.IsNullOrEmpty(req.TorrentHash) || req.Status is RequestStatus.Downloading or RequestStatus.Importing or RequestStatus.Searching))
                            {
                                isAmbiguous = true;
                                req.Status = RequestStatus.RequiresAttention;
                                req.AttentionReason = "Migración ambigua: descarga previa sin temporada comprobada; requiere confirmación manual.";
                            }
                            else if (matchedSeason.HasValue && !bySeason.Any(g => g.Key == matchedSeason.Value))
                            {
                                isAmbiguous = true;
                                req.Status = RequestStatus.RequiresAttention;
                                req.AttentionReason = "Migración ambigua: la release previa no coincide con ninguna temporada solicitada.";
                            }

                            var originalTag = string.IsNullOrEmpty(req.CorrelationTag) ? $"acq-{req.Id}" : req.CorrelationTag;

                            foreach (var sGroup in bySeason)
                            {
                                var isPack = req.Scope.All || req.Scope.SeasonNumbers.Contains(sGroup.Key);
                                var isMatched = !isAmbiguous && matchedSeason.HasValue && sGroup.Key == matchedSeason.Value;

                                req.Units.Add(new DownloadUnit
                                {
                                    SeasonNumber = sGroup.Key,
                                    EpisodeNumbers = sGroup.Select(e => e.EpisodeNumber).OrderBy(x => x).ToList(),
                                    EpisodeIds = sGroup.Select(e => e.EpisodeId).ToList(),
                                    IsFullSeasonPack = isPack,
                                    Status = isMatched ? req.Status : RequestStatus.Pending,
                                    TorrentHash = isMatched ? req.TorrentHash : null,
                                    TorrentName = isMatched ? req.TorrentName : null,
                                    SelectedRelease = isMatched ? req.SelectedRelease : null,
                                    CorrelationTag = isMatched ? originalTag : null
                                });
                            }
                        }
                    }
                    else if (req.Kind == MediaKind.Movie)
                    {
                        var originalTag = string.IsNullOrEmpty(req.CorrelationTag) ? $"acq-{req.Id}" : req.CorrelationTag;
                        req.Units.Add(new DownloadUnit
                        {
                            SeasonNumber = 0,
                            IsFullSeasonPack = false,
                            Status = req.Status,
                            TorrentHash = req.TorrentHash,
                            TorrentName = req.TorrentName,
                            SelectedRelease = req.SelectedRelease,
                            CorrelationTag = originalTag
                        });
                    }
                }
            }

            // 3. Repair invariants for multi-unit requests (J02 / K02)
            // Preserves distinct valid associations (e.g. S01 Available with hash A, S02 Downloading with hash B)
            // and resolves duplicate associations by coverage evidence.
            foreach (var req in data.Requests)
            {
                if (req.Units.Count > 1)
                {
                    var duplicateHashGroups = req.Units
                        .Where(u => !string.IsNullOrEmpty(u.TorrentHash))
                        .GroupBy(u => u.TorrentHash!, StringComparer.OrdinalIgnoreCase)
                        .Where(g => g.Count() > 1)
                        .ToList();

                    foreach (var group in duplicateHashGroups)
                    {
                        var duplicatedUnits = group.ToList();

                        // Check if coverage evidence (season token in release or torrent name) resolves the rightful owner
                        var candidates = new List<DownloadUnit>();
                        foreach (var u in duplicatedUnits)
                        {
                            var seasonToken = DetermineSeasonNumber(u, req);
                            if (seasonToken.HasValue && seasonToken.Value == u.SeasonNumber)
                            {
                                candidates.Add(u);
                            }
                        }

                        DownloadUnit? matchingUnit = null;
                        if (candidates.Count == 1)
                        {
                            matchingUnit = candidates[0];
                        }
                        else if (candidates.Count == 0)
                        {
                            // No season token evidence: prefer the unit actively downloading/importing if only one is active
                            var activeUnits = duplicatedUnits.Where(u => u.Status is RequestStatus.Downloading or RequestStatus.Importing).ToList();
                            if (activeUnits.Count == 1)
                            {
                                matchingUnit = activeUnits[0];
                            }
                        }

                        if (matchingUnit != null)
                        {
                            // Keep matching unit, clear duplicate association from others
                            foreach (var u in duplicatedUnits.Where(u => u != matchingUnit))
                            {
                                u.TorrentHash = null;
                                u.TorrentName = null;
                                u.SelectedRelease = null;
                                u.CorrelationTag = null;
                                if (u.Status != RequestStatus.Available)
                                {
                                    u.Status = RequestStatus.Pending;
                                }
                            }
                        }
                        else
                        {
                            // Ambiguous duplication across multiple active units:
                            // Do not arbitrarily wipe. Preserve and request manual confirmation.
                            req.Status = RequestStatus.RequiresAttention;
                            req.AttentionReason = "Migración ambigua: múltiples unidades activas comparten el mismo torrent sin cobertura concluyente; requiere confirmación manual.";
                        }
                    }
                }
            }

            data.SchemaVersion = RequestStoreData.CurrentVersion;
            data.LastUpdated = DateTimeOffset.UtcNow;
            await SaveDataInternalAsync(data, ct).ConfigureAwait(false);
        }
    }

    private async Task SaveDataInternalAsync(RequestStoreData data, CancellationToken ct)
    {
        // 1. Write to temp file atomically
        using (var tmpStream = new FileStream(_tmpFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(tmpStream, data, JsonOptions, ct).ConfigureAwait(false);
            await tmpStream.FlushAsync(ct).ConfigureAwait(false);
        }

        // 2. Backup existing file if present and valid
        if (File.Exists(_filePath))
        {
            File.Copy(_filePath, _bakFilePath, overwrite: true);
        }

        // 3. Replace target file with temp file
        File.Move(_tmpFilePath, _filePath, overwrite: true);
    }

    private static int? DetermineSeasonNumber(DownloadUnit unit, AcquisitionRequest req)
    {
        var release = unit.SelectedRelease ?? req.SelectedRelease;
        if (release != null)
        {
            if (release.SeasonNumber > 0)
            {
                return release.SeasonNumber;
            }
            if (!string.IsNullOrEmpty(release.Title))
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    release.Title,
                    @"[.\s_\-\(\[]+[Ss](\d{1,2})",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var sNum))
                {
                    return sNum;
                }
            }
        }

        var torrentName = unit.TorrentName ?? req.TorrentName;
        if (!string.IsNullOrEmpty(torrentName))
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                torrentName,
                @"[.\s_\-\(\[]+[Ss](\d{1,2})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var sNum))
            {
                return sNum;
            }
        }

        return null;
    }
}
