using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Jellyfin.Plugin.Acquisition.Configuration;

namespace Jellyfin.Plugin.Acquisition;

/// <summary>
/// Main plugin class for Jellyfin Acquisition plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginGuid = Guid.Parse("e2a567a4-0c6d-4789-9a72-f672d24268b8");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        try
        {
            if (Configuration.EnsureMigratedDestinations())
            {
                SaveConfiguration();
            }
        }
        catch (Exception)
        {
            // Migration is retried on the next start; a failure must not prevent the plugin from loading.
        }
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Jellyfind";

    public override Guid Id => PluginGuid;

    public override string Description => "Busca y pide series y películas desde Jellyfin con Sonarr, Radarr, Prowlarr y qBittorrent";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "Jellyfind",
                EmbeddedResourcePath = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0}.Configuration.config.html",
                    GetType().Namespace)
            }
        ];
    }
}
