using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Storage;

namespace Jellyfin.Plugin.Acquisition.Tests;

public class PersistenceTests : IDisposable
{
    private readonly string _testDir;
    private readonly Mock<IApplicationPaths> _mockAppPaths;
    private readonly Mock<ILogger<JsonFileRequestRepository>> _mockLogger;

    public PersistenceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Acquisition_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _mockAppPaths = new Mock<IApplicationPaths>();
        _mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(_testDir);
        _mockLogger = new Mock<ILogger<JsonFileRequestRepository>>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SaveAndGet_PersistsAtomicallyAndRetrieves()
    {
        var repo = new JsonFileRequestRepository(_mockAppPaths.Object, _mockLogger.Object);

        var req = new AcquisitionRequest
        {
            Id = "req-123",
            Title = "Dune: Part Two",
            Year = 2024,
            Kind = MediaKind.Movie,
            Status = RequestStatus.Downloading,
            TorrentHash = "abc123hash"
        };

        await repo.SaveOrUpdateAsync(req);

        var retrieved = await repo.GetByIdAsync("req-123");
        Assert.NotNull(retrieved);
        Assert.Equal("Dune: Part Two", retrieved.Title);
        Assert.Equal(RequestStatus.Downloading, retrieved.Status);
        Assert.Equal("abc123hash", retrieved.TorrentHash);
    }

    [Fact]
    public async Task RecoveryFromBak_WhenPrimaryFileIsCorrupt_LoadsBackupAndRestoresPrimary()
    {
        var repo = new JsonFileRequestRepository(_mockAppPaths.Object, _mockLogger.Object);

        var req1 = new AcquisitionRequest { Id = "1", Title = "Movie 1", Kind = MediaKind.Movie };
        await repo.SaveOrUpdateAsync(req1);

        var req2 = new AcquisitionRequest { Id = "2", Title = "Movie 2", Kind = MediaKind.Movie };
        await repo.SaveOrUpdateAsync(req2); // This creates .bak with req1

        // Corrupt primary file
        var primaryFile = Path.Combine(_testDir, "Acquisition", "requests.json");
        await File.WriteAllTextAsync(primaryFile, "{ corrupted invalid json }}}");

        // Create new instance of repository to read from disk
        var newRepo = new JsonFileRequestRepository(_mockAppPaths.Object, _mockLogger.Object);
        var all = await newRepo.GetAllAsync();

        Assert.NotEmpty(all);
        Assert.Equal("Movie 1", all[0].Title);

        // Next save should NOT corrupt the backup (R16)
        var req3 = new AcquisitionRequest { Id = "3", Title = "Movie 3", Kind = MediaKind.Movie };
        await newRepo.SaveOrUpdateAsync(req3);

        var finalRepo = new JsonFileRequestRepository(_mockAppPaths.Object, _mockLogger.Object);
        var finalAll = await finalRepo.GetAllAsync();
        Assert.Equal(2, finalAll.Count);
    }

    [Fact]
    public async Task RecoveryFromBak_WhenBothCorrupt_ThrowsInvalidDataException()
    {
        var primaryFile = Path.Combine(_testDir, "Acquisition", "requests.json");
        var bakFile = Path.Combine(_testDir, "Acquisition", "requests.json.bak");
        Directory.CreateDirectory(Path.Combine(_testDir, "Acquisition"));

        await File.WriteAllTextAsync(primaryFile, "{ corrupted invalid json 1 }}}");
        await File.WriteAllTextAsync(bakFile, "{ corrupted invalid json 2 }}}");

        var repo = new JsonFileRequestRepository(_mockAppPaths.Object, _mockLogger.Object);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await repo.GetAllAsync();
        });
    }

    [Fact]
    public async Task SchemaVersion_UnsupportedVersion_ThrowsInvalidOperationException()
    {
        var primaryFile = Path.Combine(_testDir, "Acquisition", "requests.json");
        Directory.CreateDirectory(Path.Combine(_testDir, "Acquisition"));

        var json = """
        {
            "schemaVersion": 999,
            "requests": []
        }
        """;
        await File.WriteAllTextAsync(primaryFile, json);

        var repo = new JsonFileRequestRepository(_mockAppPaths.Object, _mockLogger.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await repo.GetAllAsync();
        });
    }

    [Fact]
    public async Task ConcurrentSaves_HandlesWithoutRaceCondition()
    {
        var repo = new JsonFileRequestRepository(_mockAppPaths.Object, _mockLogger.Object);

        var tasks = Enumerable.Range(1, 20).Select(i =>
            repo.SaveOrUpdateAsync(new AcquisitionRequest
            {
                Id = $"req-{i}",
                Title = $"Title {i}",
                Kind = MediaKind.Movie
            })
        );

        await Task.WhenAll(tasks);

        var all = await repo.GetAllAsync();
        Assert.Equal(20, all.Count);
    }
}
