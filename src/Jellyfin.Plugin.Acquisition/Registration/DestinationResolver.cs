using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Configuration;
using Jellyfin.Plugin.Acquisition.Models;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Acquisition.Registration;

/// <summary>
/// A destination the requester may choose: a configured destination or, when none is configured, an Arr root folder.
/// </summary>
public sealed class DestinationOption
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string RootFolderPath { get; init; } = string.Empty;

    /// <summary>Explicit Arr quality profile of a configured destination; 0 = use the instance technical profile.</summary>
    public int QualityProfileId { get; init; }

    public string? SeriesType { get; init; }
    public bool IsDefault { get; init; }
}

public sealed record DestinationListResult(List<DestinationOption> Options, string? ErrorMessage);

public sealed record DestinationResolution(DestinationOption? Destination, string? ErrorMessage, int StatusCode)
{
    public static DestinationResolution Resolved(DestinationOption destination) => new(destination, null, StatusCodes.Status200OK);
}

/// <summary>
/// Single place where a requester's destination choice is turned into a real root folder.
/// Never falls back to an arbitrary root folder and never infers the series type from a folder name.
/// </summary>
public static class DestinationResolver
{
    public const string SonarrRootPrefix = "sonarr-root:";
    public const string RadarrRootPrefix = "radarr-root:";

    public static async Task<DestinationListResult> ListAsync(
        MediaKind kind,
        PluginConfiguration? config,
        ISonarrClient sonarrClient,
        IRadarrClient? radarrClient,
        CancellationToken ct)
    {
        var configured = config?.Destinations.Where(d => d.Kind == kind).ToList() ?? [];
        if (configured.Count > 0)
        {
            return new DestinationListResult(
                configured.Select(d => new DestinationOption
                {
                    Id = d.Id,
                    Name = d.Name,
                    RootFolderPath = d.RootFolderPath,
                    QualityProfileId = d.QualityProfileId,
                    SeriesType = kind == MediaKind.Series && !string.IsNullOrWhiteSpace(d.SeriesType) ? d.SeriesType : null,
                    IsDefault = d.IsDefault
                }).ToList(),
                null);
        }

        try
        {
            List<(int Id, string? Path)> roots;
            string prefix;
            if (kind == MediaKind.Series)
            {
                roots = (await sonarrClient.GetRootFoldersAsync(ct).ConfigureAwait(false) ?? []).Select(r => (r.Id, r.Path)).ToList();
                prefix = SonarrRootPrefix;
            }
            else
            {
                if (radarrClient == null)
                {
                    return new DestinationListResult([], "Radarr no está disponible en este servidor.");
                }

                roots = (await radarrClient.GetRootFoldersAsync(ct).ConfigureAwait(false) ?? []).Select(r => (r.Id, r.Path)).ToList();
                prefix = RadarrRootPrefix;
            }

            var options = roots
                .Where(r => !string.IsNullOrWhiteSpace(r.Path))
                .Select(r => new DestinationOption
                {
                    Id = prefix + r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Name = FolderName(r.Path!),
                    RootFolderPath = r.Path!,
                    IsDefault = roots.Count == 1
                }).ToList();

            return new DestinationListResult(options, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var service = kind == MediaKind.Series ? "Sonarr" : "Radarr";
            return new DestinationListResult([], $"No se pudieron leer las carpetas raíz de {service}: {Security.SecurityHelper.RedactSecrets(ex.Message)}");
        }
    }

    public static async Task<DestinationResolution> ResolveAsync(
        MediaKind kind,
        string? destinationId,
        string? destinationName,
        PluginConfiguration? config,
        ISonarrClient sonarrClient,
        IRadarrClient? radarrClient,
        CancellationToken ct)
    {
        var list = await ListAsync(kind, config, sonarrClient, radarrClient, ct).ConfigureAwait(false);
        if (list.ErrorMessage != null)
        {
            return new DestinationResolution(null, list.ErrorMessage, StatusCodes.Status503ServiceUnavailable);
        }

        var service = kind == MediaKind.Series ? "Sonarr" : "Radarr";
        if (list.Options.Count == 0)
        {
            return new DestinationResolution(null, $"{service} no tiene carpetas raíz configuradas.", StatusCodes.Status400BadRequest);
        }

        DestinationOption? match = null;
        if (!string.IsNullOrWhiteSpace(destinationId))
        {
            var id = destinationId.Trim();
            match = list.Options.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
        }
        else if (!string.IsNullOrWhiteSpace(destinationName))
        {
            var name = destinationName.Trim();
            match = list.Options.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? list.Options.FirstOrDefault(o => PathsEqual(o.RootFolderPath, name));
        }
        else
        {
            // No choice sent: only an unambiguous single option or an explicit default may be used
            var defaults = list.Options.Where(o => o.IsDefault).ToList();
            match = list.Options.Count == 1 ? list.Options[0] : defaults.Count == 1 ? defaults[0] : null;
            if (match == null)
            {
                return new DestinationResolution(null, "Hay varios destinos disponibles; elige uno.", StatusCodes.Status400BadRequest);
            }
        }

        return match != null
            ? DestinationResolution.Resolved(match)
            : new DestinationResolution(null, "El destino seleccionado ya no existe; elige otro.", StatusCodes.Status400BadRequest);
    }

    public static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a.Trim().TrimEnd('/', '\\'), b.Trim().TrimEnd('/', '\\'), StringComparison.Ordinal);
    }

    private static string FolderName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
