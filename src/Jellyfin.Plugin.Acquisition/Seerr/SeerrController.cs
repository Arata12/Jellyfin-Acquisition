using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.QuickConnect;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Catalog;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Security;

namespace Jellyfin.Plugin.Acquisition.Seerr;

/// <summary>
/// Seerr-compatible API (the subset plezy uses) so Seerr clients can search and request through the plugin.
/// Base URL for the client: https://host[/jellyfin]/Acquisition/seerr. Disabled unless <c>EnableSeerrApi</c> is on.
/// Uses its own session cookie (<c>connect.sid</c>) because Seerr clients never send Jellyfin tokens.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("Acquisition/seerr")]
public class SeerrController : ControllerBase
{
    public const string SessionCookieName = "connect.sid";

    private static readonly Regex OpaqueImageFile = new("^[a-f0-9]{64}$", RegexOptions.Compiled);
    private static readonly Regex TmdbImageSize = new("^(?:original|[a-z0-9_]{2,40})$", RegexOptions.Compiled);
    private static readonly Regex TmdbImageFile = new(@"^[A-Za-z0-9_\-]{1,100}\.(?:jpg|jpeg|png|webp|svg)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const long MaxImageBytes = 10 * 1024 * 1024;

    private readonly ISeerrBridge _bridge;
    private readonly ISeerrSessionStore _sessions;
    private readonly SeerrLoginThrottle _throttle;
    private readonly ITmdbClient _tmdb;
    private readonly ICatalogService _catalogService;
    private readonly IUserManager _userManager;
    private readonly IQuickConnect _quickConnect;
    private readonly ISessionManager _sessionManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SeerrController> _logger;

    public SeerrController(
        ISeerrBridge bridge,
        ISeerrSessionStore sessions,
        SeerrLoginThrottle throttle,
        ITmdbClient tmdb,
        ICatalogService catalogService,
        IUserManager userManager,
        IQuickConnect quickConnect,
        ISessionManager sessionManager,
        IHttpClientFactory httpClientFactory,
        ILogger<SeerrController> logger)
    {
        _bridge = bridge;
        _sessions = sessions;
        _throttle = throttle;
        _tmdb = tmdb;
        _catalogService = catalogService;
        _userManager = userManager;
        _quickConnect = quickConnect;
        _sessionManager = sessionManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static bool Enabled => Plugin.Instance?.Configuration.EnableSeerrApi == true;

    // ---------- Settings / auth ----------

    [HttpGet("api/v1/settings/public")]
    public IActionResult GetPublicSettings()
    {
        if (!Enabled) return Disabled();
        return Json(new SeerrPublicSettingsDto());
    }

    [HttpPost("api/v1/auth/jellyfin")]
    public async Task<IActionResult> SignInWithJellyfin([FromBody] JsonElement body)
    {
        if (!Enabled) return Disabled();

        var address = HttpContext.Connection.RemoteIpAddress;
        if (_throttle.IsBlocked(address))
        {
            return Message(StatusCodes.Status429TooManyRequests, "Demasiados intentos fallidos. Espera unos minutos.");
        }

        var username = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        var password = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        User? user = null;
        if (!string.IsNullOrWhiteSpace(username))
        {
            try
            {
                user = await _userManager.AuthenticateUser(username.Trim(), password ?? string.Empty, address?.ToString() ?? string.Empty, true).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Wrong password, disabled user or remote access not allowed: all look the same to the client
                _logger.LogInformation("Seerr sign-in rejected for {User}: {Reason}", username, ex.Message);
            }
        }

        if (user == null)
        {
            _throttle.RecordFailure(address);
            // Seerr forwards Jellyfin's 401 with this exact message; clients treat it as wrong credentials
            return Message(StatusCodes.Status401Unauthorized, "INVALID_CREDENTIALS");
        }

        _throttle.Reset(address);
        return await StartSessionAsync(user).ConfigureAwait(false);
    }

    [HttpPost("api/v1/auth/local")]
    [HttpPost("api/v1/auth/plex")]
    public IActionResult SignInUnsupported()
    {
        if (!Enabled) return Disabled();
        return Message(StatusCodes.Status403Forbidden, "Inicia sesión con tu cuenta de Jellyfin.");
    }

    [HttpPost("api/v1/auth/jellyfin/quickconnect/initiate")]
    public IActionResult InitiateQuickConnect()
    {
        if (!Enabled) return Disabled();
        if (!_quickConnect.IsEnabled)
        {
            return Message(StatusCodes.Status400BadRequest, "Quick Connect está desactivado en este servidor Jellyfin.");
        }

        try
        {
            var result = _quickConnect.TryConnect(new AuthorizationInfo
            {
                DeviceId = "acquisition-seerr-" + Guid.NewGuid().ToString("N"),
                Device = "Seerr client",
                Client = "Acquisition (Seerr API)",
                Version = typeof(SeerrController).Assembly.GetName().Version?.ToString() ?? "1.0.0"
            });

            return Json(new { code = result.Code, secret = result.Secret });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Quick Connect initiation failed");
            return Message(StatusCodes.Status400BadRequest, "No se pudo iniciar Quick Connect.");
        }
    }

    [HttpGet("api/v1/auth/jellyfin/quickconnect/check")]
    public IActionResult CheckQuickConnect([FromQuery] string? secret)
    {
        if (!Enabled) return Disabled();
        if (string.IsNullOrWhiteSpace(secret)) return Message(StatusCodes.Status404NotFound, "Quick Connect no encontrado.");

        try
        {
            var result = _quickConnect.CheckRequestStatus(secret);
            return Json(new { authenticated = result.Authenticated });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unknown or expired secret: the client stops polling on 404
            return Message(StatusCodes.Status404NotFound, "Quick Connect no encontrado o caducado.");
        }
    }

    [HttpPost("api/v1/auth/jellyfin/quickconnect/authenticate")]
    public async Task<IActionResult> AuthenticateQuickConnect([FromBody] JsonElement body)
    {
        if (!Enabled) return Disabled();

        var secret = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("secret", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(secret))
        {
            return Message(StatusCodes.Status403Forbidden, "Quick Connect no autorizado.");
        }

        try
        {
            var auth = _quickConnect.GetAuthorizedRequest(secret);
            var user = auth.User?.Id is { } id ? _userManager.GetUserById(id) : null;

            // Jellyfin opened a device session for the code; this API keeps its own session instead
            if (!string.IsNullOrEmpty(auth.AccessToken))
            {
                try
                {
                    await _sessionManager.Logout(auth.AccessToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Could not close the Quick Connect device session");
                }
            }

            if (user == null)
            {
                return Message(StatusCodes.Status403Forbidden, "Quick Connect no autorizado.");
            }

            return await StartSessionAsync(user).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation("Quick Connect sign-in rejected: {Reason}", ex.Message);
            return Message(StatusCodes.Status403Forbidden, "Quick Connect no autorizado.");
        }
    }

    [HttpGet("api/v1/auth/me")]
    public async Task<IActionResult> GetMe()
    {
        if (!Enabled) return Disabled();
        var user = await GetSessionUserAsync().ConfigureAwait(false);
        if (user == null) return NotAuthenticated();

        return Json(ToSeerrUser(user));
    }

    [HttpPost("api/v1/auth/logout")]
    public async Task<IActionResult> Logout()
    {
        if (!Enabled) return Disabled();
        await _sessions.RemoveAsync(Request.Cookies[SessionCookieName]).ConfigureAwait(false);
        Response.Headers.Append("Set-Cookie", $"{SessionCookieName}=; Path={CookiePath}; Max-Age=0; HttpOnly; SameSite=Lax");
        return Json(new { status = "ok" });
    }

    // ---------- Catalog ----------

    [HttpGet("api/v1/search")]
    public Task<IActionResult> Search([FromQuery] string? query, [FromQuery] int page = 1) =>
        WithUserAsync(async user =>
        {
            if (string.IsNullOrWhiteSpace(query)) return Json(new SeerrPageDto());
            return Json(await _bridge.SearchAsync(user.Id, query, page, HttpContext.RequestAborted).ConfigureAwait(false));
        });

    [HttpGet("api/v1/discover/movies")]
    public Task<IActionResult> DiscoverMovies([FromQuery] int page = 1, [FromQuery] string? language = null) =>
        DiscoverAsync("movie/popular", page, language, "movie");

    [HttpGet("api/v1/discover/tv")]
    public Task<IActionResult> DiscoverTv([FromQuery] int page = 1, [FromQuery] string? language = null) =>
        DiscoverAsync("tv/popular", page, language, "tv");

    [HttpGet("api/v1/discover/movies/upcoming")]
    public Task<IActionResult> DiscoverUpcomingMovies([FromQuery] int page = 1, [FromQuery] string? language = null) =>
        DiscoverAsync("movie/upcoming", page, language, "movie");

    [HttpGet("api/v1/discover/tv/upcoming")]
    public Task<IActionResult> DiscoverUpcomingTv([FromQuery] int page = 1, [FromQuery] string? language = null) =>
        DiscoverAsync("discover/tv", page, language, "tv", new Dictionary<string, string>
        {
            ["first_air_date.gte"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["sort_by"] = "popularity.desc"
        });

    [HttpGet("api/v1/discover/trending")]
    public Task<IActionResult> DiscoverTrending([FromQuery] int page = 1, [FromQuery] string? language = null) =>
        DiscoverAsync("trending/all/week", page, language, null);

    [HttpGet("api/v1/movie/{tmdbId:int}/recommendations")]
    public Task<IActionResult> MovieRecommendations([FromRoute] int tmdbId, [FromQuery] int page = 1, [FromQuery] string? language = null) =>
        DiscoverAsync($"movie/{tmdbId}/recommendations", page, language, "movie");

    [HttpGet("api/v1/tv/{tmdbId:int}/recommendations")]
    public Task<IActionResult> TvRecommendations([FromRoute] int tmdbId, [FromQuery] int page = 1, [FromQuery] string? language = null) =>
        DiscoverAsync($"tv/{tmdbId}/recommendations", page, language, "tv");

    [HttpGet("api/v1/movie/{tmdbId:int}")]
    public Task<IActionResult> GetMovie([FromRoute] int tmdbId) =>
        WithUserAsync(async user =>
        {
            var details = await _bridge.GetMovieAsync(user.Id, tmdbId, HttpContext.RequestAborted).ConfigureAwait(false);
            return details == null ? Message(StatusCodes.Status404NotFound, "Película no encontrada.") : Json(details);
        });

    [HttpGet("api/v1/tv/{tmdbId:int}")]
    public Task<IActionResult> GetTv([FromRoute] int tmdbId) =>
        WithUserAsync(async user =>
        {
            var details = await _bridge.GetTvAsync(user.Id, tmdbId, HttpContext.RequestAborted).ConfigureAwait(false);
            return details == null ? Message(StatusCodes.Status404NotFound, "Serie no encontrada.") : Json(details);
        });

    // ---------- Requests ----------

    [HttpPost("api/v1/request")]
    public Task<IActionResult> CreateRequest([FromBody] JsonElement body) =>
        WithUserAsync(async user =>
        {
            if (!SecurityHelper.CanAcquire(user.Id, _userManager, Plugin.Instance?.Configuration))
            {
                return Message(StatusCodes.Status403Forbidden, "No tienes permiso para solicitar contenido.");
            }

            SeerrCreateRequestDto? dto;
            try
            {
                dto = body.Deserialize<SeerrCreateRequestDto>(SeerrJson.Options);
            }
            catch (JsonException)
            {
                dto = null;
            }

            if (dto == null)
            {
                return Message(StatusCodes.Status400BadRequest, "Solicitud no válida.");
            }

            var created = await _bridge.CreateRequestAsync(user.Id, dto, HttpContext.RequestAborted).ConfigureAwait(false);
            return Json(created, StatusCodes.Status201Created);
        });

    [HttpGet("api/v1/service/radarr")]
    public Task<IActionResult> ListRadarrServers() => ListServersAsync(MediaKind.Movie);

    [HttpGet("api/v1/service/sonarr")]
    public Task<IActionResult> ListSonarrServers() => ListServersAsync(MediaKind.Series);

    [HttpGet("api/v1/service/radarr/{id:int}")]
    public Task<IActionResult> GetRadarrServer([FromRoute] int id) => GetServerAsync(MediaKind.Movie, id);

    [HttpGet("api/v1/service/sonarr/{id:int}")]
    public Task<IActionResult> GetSonarrServer([FromRoute] int id) => GetServerAsync(MediaKind.Series, id);

    // ---------- Images ----------

    /// <summary>
    /// Seerr's image proxy: TMDB artwork by its TMDB path, and the plugin's cached Sonarr/Radarr artwork by its
    /// opaque ID. Public like Seerr's, since image loaders do not send the session cookie.
    /// </summary>
    [HttpGet("imageproxy/tmdb/t/p/{size}/{file}")]
    [HttpGet("imageproxy/t/p/{size}/{file}")]
    public async Task<IActionResult> GetImage([FromRoute] string size, [FromRoute] string file)
    {
        if (!Enabled) return Disabled();

        if (OpaqueImageFile.IsMatch(file))
        {
            var img = await _catalogService.GetImageAsync(file, HttpContext.RequestAborted).ConfigureAwait(false);
            if (img == null) return NotFound();
            Response.Headers.CacheControl = "public, max-age=604800";
            return File(img.Value.Bytes, img.Value.ContentType);
        }

        if (!TmdbImageSize.IsMatch(size) || !TmdbImageFile.IsMatch(file))
        {
            return NotFound();
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync($"https://image.tmdb.org/t/p/{size}/{file}", HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || (response.Content.Headers.ContentLength ?? 0) > MaxImageBytes)
            {
                return NotFound();
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            if (bytes.Length > MaxImageBytes) return NotFound();

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType == null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return NotFound();
            }

            Response.Headers.CacheControl = "public, max-age=604800";
            return File(bytes, contentType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "TMDB image {File} could not be fetched", file);
            return NotFound();
        }
    }

    // ---------- Helpers ----------

    private async Task<IActionResult> DiscoverAsync(string path, int page, string? language, string? mediaType, IReadOnlyDictionary<string, string>? query = null)
    {
        return await WithUserAsync(async user =>
        {
            // Without a TMDB key the rows are simply empty; search and requests still work
            var result = await _tmdb.GetPageAsync(path, page, language, mediaType, query, HttpContext.RequestAborted).ConfigureAwait(false)
                         ?? new SeerrPageDto { Page = Math.Max(page, 1), TotalPages = Math.Max(page, 1) };
            await _bridge.AttachStatusAsync(user.Id, result, HttpContext.RequestAborted).ConfigureAwait(false);
            return Json(result);
        }).ConfigureAwait(false);
    }

    private Task<IActionResult> ListServersAsync(MediaKind kind) =>
        WithUserAsync(async _ => Json(await _bridge.ListServersAsync(kind, HttpContext.RequestAborted).ConfigureAwait(false)));

    private Task<IActionResult> GetServerAsync(MediaKind kind, int id) =>
        WithUserAsync(async _ =>
        {
            var servers = await _bridge.ListServersAsync(kind, HttpContext.RequestAborted).ConfigureAwait(false);
            var server = servers.FirstOrDefault(s => s.Id == id);
            return server == null
                ? Message(StatusCodes.Status404NotFound, "Destino no encontrado.")
                : Json(new SeerrServiceDetailDto { Server = server });
        });

    private async Task<IActionResult> WithUserAsync(Func<User, Task<IActionResult>> action)
    {
        if (!Enabled) return Disabled();

        var user = await GetSessionUserAsync().ConfigureAwait(false);
        if (user == null) return NotAuthenticated();

        try
        {
            return await action(user).ConfigureAwait(false);
        }
        catch (SeerrApiException ex)
        {
            return Message(ex.StatusCode, ex.Message);
        }
    }

    private async Task<User?> GetSessionUserAsync()
    {
        var userId = await _sessions.GetUserIdAsync(Request.Cookies[SessionCookieName], HttpContext.RequestAborted).ConfigureAwait(false);
        if (!userId.HasValue) return null;

        var user = _userManager.GetUserById(userId.Value);
        if (user == null || IsDisabled(user)) return null;
        return user;
    }

    private async Task<IActionResult> StartSessionAsync(User user)
    {
        var token = await _sessions.CreateAsync(user.Id, HttpContext.RequestAborted).ConfigureAwait(false);

        // Max-Age instead of Expires: clients split joined Set-Cookie headers on commas
        var secure = Request.IsHttps ? "; Secure" : string.Empty;
        Response.Headers.Append(
            "Set-Cookie",
            $"{SessionCookieName}={token}; Path={CookiePath}; Max-Age={(int)JsonFileSeerrSessionStore.Lifetime.TotalSeconds}; HttpOnly; SameSite=Lax{secure}");

        return Json(ToSeerrUser(user));
    }

    private string CookiePath => $"{Request.PathBase}/Acquisition/seerr";

    private SeerrUserDto ToSeerrUser(User user)
    {
        var canRequest = SecurityHelper.CanAcquire(user.Id, _userManager, Plugin.Instance?.Configuration);
        return new SeerrUserDto
        {
            Id = SeerrBridge.StableId(user.Id.ToString("N")),
            DisplayName = user.Username,
            Username = user.Username,
            JellyfinUsername = user.Username,
            // "Advanced" only lets the client pick a destination when there is more than one
            Permissions = canRequest
                ? SeerrPermission.Request | SeerrPermission.RequestMovie | SeerrPermission.RequestTv | SeerrPermission.RequestAdvanced
                : 0
        };
    }

    private static bool IsDisabled(User user) =>
        user.Permissions.Any(p => p.Kind.ToString().Equals("IsDisabled", StringComparison.OrdinalIgnoreCase) && p.Value);

    private ContentResult Json(object value, int statusCode = StatusCodes.Status200OK) => new()
    {
        Content = JsonSerializer.Serialize(value, value.GetType(), SeerrJson.Options),
        ContentType = "application/json; charset=utf-8",
        StatusCode = statusCode
    };

    /// <summary>Seerr's error-handler shape: <c>{message}</c>.</summary>
    private ContentResult Message(int statusCode, string message) => Json(new { message }, statusCode);

    /// <summary>Seerr's authentication-middleware shape, which clients read as "session expired".</summary>
    private ContentResult NotAuthenticated() =>
        Json(new { status = StatusCodes.Status403Forbidden, error = "You do not have permission to access this endpoint" }, StatusCodes.Status403Forbidden);

    private ContentResult Disabled() => Message(StatusCodes.Status404NotFound, "La API compatible con Seerr está desactivada.");
}
