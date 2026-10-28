using System.Net.Http;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr;
using Jellyfin.Plugin.Acquisition.Adapters.QBittorrent;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Catalog;
using Jellyfin.Plugin.Acquisition.Hosted;
using Jellyfin.Plugin.Acquisition.Releases;
using Jellyfin.Plugin.Acquisition.Storage;
using Jellyfin.Plugin.Acquisition.Tracking;
using Jellyfin.Plugin.Acquisition.Web;

namespace Jellyfin.Plugin.Acquisition;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    private const string QBittorrentClientName = "Acquisition.QBittorrent";

    // Typed clients end up captured by singleton services, so their handler is never rotated by the factory.
    // A pooled connection lifetime keeps DNS changes (e.g. container restarts) from pinning stale connections.
    private static SocketsHttpHandler CreateLongLivedHandler(bool allowAutoRedirect = true) => new()
    {
        AllowAutoRedirect = allowAutoRedirect,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // 1. Web Injection Middleware
        serviceCollection.AddTransient<IStartupFilter, WebInjectionStartupFilter>();

        // 2. HTTP Clients
        serviceCollection.AddHttpClient();
        serviceCollection.AddHttpClient<ISonarrClient, SonarrClient>()
            .ConfigurePrimaryHttpMessageHandler(() => CreateLongLivedHandler())
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        serviceCollection.AddHttpClient<IRadarrClient, RadarrClient>()
            .ConfigurePrimaryHttpMessageHandler(() => CreateLongLivedHandler())
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        serviceCollection.AddHttpClient<IProwlarrClient, ProwlarrClient>()
            .ConfigurePrimaryHttpMessageHandler(() => CreateLongLivedHandler(allowAutoRedirect: false))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        // qBittorrent keeps a session cookie: a single instance shares it between the controller and the tracker.
        serviceCollection.AddHttpClient(QBittorrentClientName)
            .ConfigurePrimaryHttpMessageHandler(() => CreateLongLivedHandler())
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        serviceCollection.AddSingleton<IQBittorrentClient>(sp => new QBittorrentClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(QBittorrentClientName),
            sp.GetRequiredService<ILogger<QBittorrentClient>>()));

        // 3. Storage
        serviceCollection.AddSingleton<IRequestRepository, JsonFileRequestRepository>();

        // 4. Domain & Matching Services
        serviceCollection.AddSingleton<ILocalMatcherService, LocalMatcherService>();
        serviceCollection.AddSingleton<ICatalogService, CatalogService>();
        serviceCollection.AddSingleton<IReleaseService, ReleaseService>();
        serviceCollection.AddSingleton<ITrackerService, TrackerService>();

        // 5. Background Tracking Worker
        serviceCollection.AddHostedService<AcquisitionHostedService>();
    }
}
