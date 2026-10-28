using System.Net;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Configuration;
using Jellyfin.Plugin.Acquisition.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Acquisition.Registration;

public enum RegistrationOutcome
{
    /// <summary>The title is registered in Arr and <see cref="AcquisitionRequest.ArrId"/> is set.</summary>
    Registered,

    /// <summary>Arr could not be reached; try again later without user action.</summary>
    Transient,

    /// <summary>A decision or a fix is needed before the title can be registered.</summary>
    NeedsAttention
}

public sealed record RegistrationResult(RegistrationOutcome Outcome, string? Message = null)
{
    public static readonly RegistrationResult Registered = new(RegistrationOutcome.Registered);

    public static RegistrationResult Transient(string message) => new(RegistrationOutcome.Transient, message);

    public static RegistrationResult Attention(string message) => new(RegistrationOutcome.NeedsAttention, message);
}

/// <summary>
/// Registers a requested title in Sonarr/Radarr, shared by the controller (explicit prepare) and the tracker.
/// Existing registrations keep their path, profile and type. New registrations use the root folder chosen
/// for the request and the instance technical profile; nothing is picked arbitrarily.
/// </summary>
public sealed class ArrRegistrar
{
    private readonly ISonarrClient _sonarrClient;
    private readonly IRadarrClient? _radarrClient;
    private readonly ILogger _logger;

    public ArrRegistrar(ISonarrClient sonarrClient, IRadarrClient? radarrClient, ILogger logger)
    {
        _sonarrClient = sonarrClient;
        _radarrClient = radarrClient;
        _logger = logger;
    }

    public Task<RegistrationResult> EnsureRegisteredAsync(AcquisitionRequest request, PluginConfiguration? config, CancellationToken ct)
    {
        return request.Kind == MediaKind.Series
            ? EnsureSeriesAsync(request, config, ct)
            : EnsureMovieAsync(request, config, ct);
    }

    private async Task<RegistrationResult> EnsureSeriesAsync(AcquisitionRequest request, PluginConfiguration? config, CancellationToken ct)
    {
        if (request.ArrId.HasValue) return RegistrationResult.Registered;

        if (!int.TryParse(request.ProviderIds.Tvdb, out var tvdbId) || tvdbId <= 0)
        {
            return RegistrationResult.Attention("La serie no tiene identificador TVDB y Sonarr lo necesita para registrarla. Búscala desde el catálogo externo.");
        }

        try
        {
            var existing = await _sonarrClient.FindSeriesByTvdbIdAsync(tvdbId, ct).ConfigureAwait(false);
            if (existing != null)
            {
                request.ArrId = existing.Id;
                if (!string.IsNullOrEmpty(existing.RootFolderPath)) request.RootFolderPath = existing.RootFolderPath;
                if (existing.QualityProfileId > 0) request.QualityProfileId = existing.QualityProfileId;
                if (!string.IsNullOrEmpty(existing.SeriesType)) request.SeriesType = existing.SeriesType;
                return RegistrationResult.Registered;
            }

            var lookup = await _sonarrClient.LookupSeriesWithDiagnosticsAsync($"tvdb:{tvdbId}", ct).ConfigureAwait(false);
            if (lookup == null || lookup.Status != ProviderStatus.Ready)
            {
                return RegistrationResult.Transient(lookup?.ErrorMessage ?? "Sonarr no está disponible.");
            }

            var seriesToAdd = lookup.Items.FirstOrDefault();
            if (seriesToAdd == null)
            {
                return RegistrationResult.Attention($"Sonarr no encontró la serie con TVDB {tvdbId}.");
            }

            var roots = (await _sonarrClient.GetRootFoldersAsync(ct).ConfigureAwait(false)).Select(r => r.Path).ToList();
            var profiles = (await _sonarrClient.GetQualityProfilesAsync(ct).ConfigureAwait(false)).Select(p => p.Id).ToList();

            var rootError = ResolveRoot(request, roots, "Sonarr", "esta serie", out var root);
            if (rootError != null) return rootError;

            var profileError = ResolveProfile(request.QualityProfileId, config?.SonarrQualityProfileId ?? 0, profiles, "Sonarr", out var profileId);
            if (profileError != null) return profileError;

            seriesToAdd.RootFolderPath = root;
            seriesToAdd.QualityProfileId = profileId;
            if (!string.IsNullOrEmpty(request.SeriesType))
            {
                seriesToAdd.SeriesType = request.SeriesType;
            }

            var added = await _sonarrClient.AddSeriesAsync(seriesToAdd, ct).ConfigureAwait(false);
            request.ArrId = added.Id;
            request.RootFolderPath = root;
            request.QualityProfileId = profileId;
            if (!string.IsNullOrEmpty(added.SeriesType)) request.SeriesType = added.SeriesType;
            return RegistrationResult.Registered;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapFailure(ex, "Sonarr", request);
        }
    }

    private async Task<RegistrationResult> EnsureMovieAsync(AcquisitionRequest request, PluginConfiguration? config, CancellationToken ct)
    {
        if (request.ArrId.HasValue) return RegistrationResult.Registered;

        if (_radarrClient == null)
        {
            return RegistrationResult.Attention("Radarr no está disponible en este servidor.");
        }

        if (!int.TryParse(request.ProviderIds.Tmdb, out var tmdbId) || tmdbId <= 0)
        {
            return RegistrationResult.Attention("La película no tiene identificador TMDB y Radarr lo necesita para registrarla. Búscala desde el catálogo externo.");
        }

        try
        {
            var existing = await _radarrClient.FindMovieByTmdbIdAsync(tmdbId, ct).ConfigureAwait(false);
            if (existing != null)
            {
                request.ArrId = existing.Id;
                if (!string.IsNullOrEmpty(existing.RootFolderPath)) request.RootFolderPath = existing.RootFolderPath;
                if (existing.QualityProfileId > 0) request.QualityProfileId = existing.QualityProfileId;
                return RegistrationResult.Registered;
            }

            var lookup = await _radarrClient.LookupMovieWithDiagnosticsAsync($"tmdb:{tmdbId}", ct).ConfigureAwait(false);
            if (lookup == null || lookup.Status != ProviderStatus.Ready)
            {
                return RegistrationResult.Transient(lookup?.ErrorMessage ?? "Radarr no está disponible.");
            }

            var movieToAdd = lookup.Items.FirstOrDefault();
            if (movieToAdd == null)
            {
                return RegistrationResult.Attention($"Radarr no encontró la película con TMDB {tmdbId}.");
            }

            var roots = (await _radarrClient.GetRootFoldersAsync(ct).ConfigureAwait(false)).Select(r => r.Path).ToList();
            var profiles = (await _radarrClient.GetQualityProfilesAsync(ct).ConfigureAwait(false)).Select(p => p.Id).ToList();

            var rootError = ResolveRoot(request, roots, "Radarr", "esta película", out var root);
            if (rootError != null) return rootError;

            var profileError = ResolveProfile(request.QualityProfileId, config?.RadarrQualityProfileId ?? 0, profiles, "Radarr", out var profileId);
            if (profileError != null) return profileError;

            movieToAdd.RootFolderPath = root;
            movieToAdd.QualityProfileId = profileId;

            var added = await _radarrClient.AddMovieAsync(movieToAdd, ct).ConfigureAwait(false);
            request.ArrId = added.Id;
            request.RootFolderPath = root;
            request.QualityProfileId = profileId;
            return RegistrationResult.Registered;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapFailure(ex, "Radarr", request);
        }
    }

    /// <summary>
    /// The request's chosen root must still exist in Arr. Only a single existing root may be used without a choice.
    /// </summary>
    private static RegistrationResult? ResolveRoot(AcquisitionRequest request, List<string?> roots, string service, string what, out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(request.RootFolderPath))
        {
            var available = roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
            if (available.Count != 1)
            {
                return RegistrationResult.Attention(available.Count == 0
                    ? $"{service} no tiene carpetas raíz configuradas."
                    : $"Elige un destino para {what}: {service} tiene varias carpetas raíz.");
            }

            root = available[0]!;
            return null;
        }

        var chosen = request.RootFolderPath;
        var current = roots.FirstOrDefault(r => DestinationResolver.PathsEqual(r, chosen));
        if (current == null)
        {
            return RegistrationResult.Attention($"La carpeta '{chosen}' ya no existe en {service}. Elige otro destino.");
        }

        root = current;
        return null;
    }

    /// <summary>
    /// Technical registration profile: explicit destination profile, then the instance profile chosen by the admin,
    /// then the only profile Arr has. Never the first of several and never an assumed ID 1.
    /// </summary>
    private static RegistrationResult? ResolveProfile(int? requestProfile, int instanceProfile, List<int> profiles, string service, out int profileId)
    {
        profileId = requestProfile is > 0 ? requestProfile.Value
            : instanceProfile > 0 ? instanceProfile
            : profiles.Count == 1 ? profiles[0]
            : 0;

        if (profileId == 0)
        {
            return RegistrationResult.Attention($"Selecciona el perfil técnico de {service} en la configuración del plugin: hay varios y no se elige uno por defecto.");
        }

        if (!profiles.Contains(profileId))
        {
            var missing = profileId;
            profileId = 0;
            return RegistrationResult.Attention($"El perfil de calidad {missing} ya no existe en {service}. Revisa la configuración del plugin.");
        }

        return null;
    }

    private RegistrationResult MapFailure(Exception ex, string service, AcquisitionRequest request)
    {
        _logger.LogWarning(ex, "Could not register request {Id} ({Title}) in {Service}", request.Id, request.Title, service);
        var message = Security.SecurityHelper.RedactSecrets(ex.Message);

        // 4xx means Arr rejected the data (validation): retrying unchanged will not help
        if (ex is HttpRequestException { StatusCode: { } status } && (int)status >= 400 && (int)status < 500
            && status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.RequestTimeout or (HttpStatusCode)429))
        {
            return RegistrationResult.Attention($"{service} rechazó el alta: {message}");
        }

        return RegistrationResult.Transient($"{service} no está disponible: {message}");
    }
}
