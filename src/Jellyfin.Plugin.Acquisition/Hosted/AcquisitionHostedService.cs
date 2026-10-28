using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Tracking;

namespace Jellyfin.Plugin.Acquisition.Hosted;

public class AcquisitionHostedService : BackgroundService
{
    private readonly ITrackerService _trackerService;
    private readonly ILogger<AcquisitionHostedService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(15);

    public AcquisitionHostedService(ITrackerService trackerService, ILogger<AcquisitionHostedService> logger)
    {
        _trackerService = trackerService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Acquisition tracking worker started (interval: 15s)");

        // Initial delay on startup to allow Jellyfin & dependent services to initialize
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _trackerService.PollActiveRequestsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in acquisition polling loop");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Acquisition tracking worker stopped");
    }
}
