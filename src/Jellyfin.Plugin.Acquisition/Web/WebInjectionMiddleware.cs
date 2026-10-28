using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Acquisition.Web;

public class WebInjectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IServerApplicationPaths _serverPaths;
    private readonly IServerConfigurationManager? _configManager;
    private readonly ILogger<WebInjectionMiddleware> _logger;

    private string? _cachedHtml;
    private string? _cachedEtag;
    private string? _cachedBaseUrl;
    private string? _cachedStyle;
    private DateTime _lastHtmlRead = DateTime.MinValue;

    private static readonly Lazy<string> AssetsHash = new(() =>
    {
        using var sha = SHA256.Create();
        var assembly = typeof(WebInjectionMiddleware).Assembly;
        foreach (var name in new[] { "Jellyfin.Plugin.Acquisition.Web.acquisition.js", "Jellyfin.Plugin.Acquisition.Web.acquisition.css" })
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null) continue;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!)[..12].ToLowerInvariant();
    });

    public WebInjectionMiddleware(
        RequestDelegate next,
        IServerApplicationPaths serverPaths,
        ILogger<WebInjectionMiddleware> logger,
        IServerConfigurationManager? configManager = null)
    {
        _next = next;
        _serverPaths = serverPaths;
        _logger = logger;
        _configManager = configManager;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value?.TrimEnd('/') ?? string.Empty;
        var isIndexPath = path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase) ||
                          path.EndsWith("/web", StringComparison.OrdinalIgnoreCase) ||
                          path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);

        if (!isIndexPath)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var webPath = _serverPaths.WebPath;
        if (string.IsNullOrEmpty(webPath))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var indexFilePath = Path.Combine(webPath, "index.html");
        if (!File.Exists(indexFilePath))
        {
            _logger.LogWarning("WebPath is configured ({WebPath}), but index.html was not found", webPath);
            await _next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            var fileInfo = new FileInfo(indexFilePath);

            // Determine effective base URL from NetworkConfiguration and Request.PathBase
            var configuredBaseUrl = string.Empty;
            if (_configManager != null)
            {
                try
                {
                    var netConfig = _configManager.GetConfiguration("network");
                    var baseUrlProp = netConfig?.GetType().GetProperty("BaseUrl");
                    configuredBaseUrl = baseUrlProp?.GetValue(netConfig)?.ToString()?.TrimEnd('/') ?? string.Empty;
                }
                catch
                {
                    // Ignore if network configuration not yet initialized
                }
            }

            var requestPathBase = context.Request.PathBase.Value?.TrimEnd('/') ?? string.Empty;
            var effectiveBase = !string.IsNullOrEmpty(configuredBaseUrl) ? configuredBaseUrl : requestPathBase;

            var searchStyle = Plugin.Instance?.Configuration.GetSearchStyle() ?? "auto";

            if (_cachedHtml == null || fileInfo.LastWriteTimeUtc > _lastHtmlRead || _cachedBaseUrl != effectiveBase || _cachedStyle != searchStyle)
            {
                var originalHtml = await File.ReadAllTextAsync(indexFilePath).ConfigureAwait(false);
                // Content hash in the URL: browsers must not keep an older client script cached under the same plugin version
                var version = $"{Plugin.Instance?.Version.ToString() ?? "0.0.0.0"}-{AssetsHash.Value}";
                var scriptTag = $"<script>window.__ACQUISITION_BASE_URL__ = \"{effectiveBase}\"; window.__ACQUISITION_STYLE__ = \"{searchStyle}\";</script><link rel=\"stylesheet\" href=\"{effectiveBase}/Acquisition/web/acquisition.css?v={version}\" /><script type=\"module\" src=\"{effectiveBase}/Acquisition/web/acquisition.js?v={version}\"></script>";

                if (originalHtml.Contains("</body>", StringComparison.OrdinalIgnoreCase))
                {
                    _cachedHtml = originalHtml.Replace("</body>", $"{scriptTag}</body>", StringComparison.OrdinalIgnoreCase);
                }
                else if (originalHtml.Contains("</head>", StringComparison.OrdinalIgnoreCase))
                {
                    _cachedHtml = originalHtml.Replace("</head>", $"{scriptTag}</head>", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    _cachedHtml = originalHtml + scriptTag;
                }

                _lastHtmlRead = fileInfo.LastWriteTimeUtc;
                _cachedBaseUrl = effectiveBase;
                _cachedStyle = searchStyle;
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_cachedHtml)));
                _cachedEtag = $"\"{hash}\"";
            }

            // Check caching headers (If-None-Match)
            if (context.Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch) &&
                string.Equals(ifNoneMatch.ToString(), _cachedEtag, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.ETag = _cachedEtag;
            // Always revalidate: a cached index.html would keep loading the client script of a previous plugin build
            context.Response.Headers.CacheControl = "no-cache";

            if (HttpMethods.IsGet(method))
            {
                await context.Response.WriteAsync(_cachedHtml, Encoding.UTF8).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inject acquisition web assets into index.html");
            await _next(context).ConfigureAwait(false);
        }
    }
}
