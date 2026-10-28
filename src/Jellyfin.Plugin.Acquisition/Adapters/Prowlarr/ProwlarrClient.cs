using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr.Models;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Adapters.Prowlarr;

public class ProwlarrClient : IProwlarrClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProwlarrClient> _logger;

    public ProwlarrClient(HttpClient httpClient, ILogger<ProwlarrClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// A service without an API key is simply not configured yet: its failures are expected and logged at Debug,
    /// so an unused integration does not fill the server log with errors on every search.
    /// </summary>
    private void LogFailure(Exception ex, string message, params object?[] args)
    {
        if (string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration?.ProwlarrApiKey))
        {
            _logger.LogDebug(ex, message, args);
        }
        else
        {
            _logger.LogError(ex, message, args);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var config = Plugin.Instance?.Configuration;
        var rawUrl = !string.IsNullOrWhiteSpace(config?.ProwlarrUrl) ? config.ProwlarrUrl : "http://localhost:9696";
        if (!rawUrl.EndsWith('/')) rawUrl += "/";
        var uri = new Uri(new Uri(rawUrl), relativePath.TrimStart('/'));

        var req = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(config?.ProwlarrApiKey))
        {
            req.Headers.Add("X-Api-Key", config.ProwlarrApiKey);
        }
        return req;
    }

    public async Task<List<ProwlarrIndexer>> GetIndexersAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, "api/v1/indexer");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];
            return await resp.Content.ReadFromJsonAsync<List<ProwlarrIndexer>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prowlarr GetIndexers failed");
            return [];
        }
    }

    public async Task<List<ProwlarrRelease>> SearchAsync(ProwlarrSearchQuery query, CancellationToken ct = default)
    {
        try
        {
            // Plan: use search with query, categories, and type
            // Categories: Movies = 2000, TV = 5000
            var categories = query.Kind == MediaKind.Movie ? "2000" : "5000";

            var queryString = query.Query;
            if (query.Kind == MediaKind.Series && query.AbsoluteEpisode.HasValue)
            {
                queryString = $"{query.Query} {query.AbsoluteEpisode.Value:D2}";
            }
            else if (query.Kind == MediaKind.Series && query.IsAnime)
            {
                // Fansub/batch releases rarely carry SxxEyy; search by title and let evaluation filter coverage
                queryString = query.Query;
            }
            else if (query.Kind == MediaKind.Series && query.Season.HasValue)
            {
                if (query.Episode.HasValue)
                {
                    queryString = $"{query.Query} S{query.Season.Value:D2}E{query.Episode.Value:D2}";
                }
                else
                {
                    queryString = $"{query.Query} S{query.Season.Value:D2}";
                }
            }

            var url = $"api/v1/search?query={Uri.EscapeDataString(queryString)}&categories={categories}&type=search";
            if (query.IndexerIds != null && query.IndexerIds.Count > 0)
            {
                foreach (var indexerId in query.IndexerIds)
                {
                    url += $"&indexerIds={indexerId}";
                }
            }

            using var req = CreateRequest(HttpMethod.Get, url);
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var reason = resp.StatusCode switch
                {
                    (HttpStatusCode)429 => "Prowlarr rate limit reached (HTTP 429).",
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"Prowlarr rejected the API key (HTTP {(int)resp.StatusCode}).",
                    _ => $"Prowlarr search returned HTTP {(int)resp.StatusCode}."
                };
                _logger.LogWarning("Prowlarr search failed for {Query}: {Reason}", query.Query, reason);
                throw new ProwlarrUnavailableException(reason);
            }

            return await resp.Content.ReadFromJsonAsync<List<ProwlarrRelease>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ProwlarrUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(ex, "Prowlarr Search failed for query {Query}", query.Query);
            throw new ProwlarrUnavailableException("Prowlarr could not be reached: " + Security.SecurityHelper.RedactSecrets(ex.Message), ex);
        }
    }

    public async Task<TorrentDownloadResult> FetchTorrentAsync(string downloadUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            return new TorrentDownloadResult { ErrorMessage = "Empty download URL" };
        }

        if (downloadUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return ParseMagnet(downloadUrl);
        }

        var config = Plugin.Instance?.Configuration;
        var rawBase = !string.IsNullOrWhiteSpace(config?.ProwlarrUrl) ? config.ProwlarrUrl : "http://localhost:9696";
        if (!rawBase.EndsWith('/')) rawBase += "/";
        Uri? prowlarrBaseUri = null;
        if (Uri.TryCreate(rawBase, UriKind.Absolute, out var parsedBase))
        {
            prowlarrBaseUri = parsedBase;
        }

        var currentUrl = downloadUrl;
        const int maxHops = 5;

        for (var hop = 0; hop < maxHops; hop++)
        {
            try
            {
                var targetUri = new Uri(currentUrl, UriKind.RelativeOrAbsolute);
                if (!targetUri.IsAbsoluteUri && prowlarrBaseUri != null)
                {
                    targetUri = new Uri(prowlarrBaseUri, targetUri);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, targetUri);

                // Pass X-Api-Key ONLY if request target matches Prowlarr origin (scheme, host, port)! (N04)
                var isProwlarrOrigin = prowlarrBaseUri != null &&
                                       string.Equals(targetUri.Scheme, prowlarrBaseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                                       string.Equals(targetUri.Host, prowlarrBaseUri.Host, StringComparison.OrdinalIgnoreCase) &&
                                       targetUri.Port == prowlarrBaseUri.Port;

                if (isProwlarrOrigin && !string.IsNullOrWhiteSpace(config?.ProwlarrApiKey))
                {
                    request.Headers.Add("X-Api-Key", config.ProwlarrApiKey);
                }

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or (HttpStatusCode)308)
                {
                    var location = response.Headers.Location?.ToString();
                    if (string.IsNullOrEmpty(location))
                    {
                        return new TorrentDownloadResult { ErrorMessage = "Redirect without location header" };
                    }

                    if (location.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                    {
                        return ParseMagnet(location);
                    }

                    // Resolve relative redirect URI
                    currentUrl = new Uri(targetUri, location).ToString();
                    continue;
                }

                if (response.StatusCode == (HttpStatusCode)429)
                {
                    var retryAfterSeconds = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                    _logger.LogWarning("Prowlarr returned 429 Too Many Requests (RetryAfter: {Secs}s)", retryAfterSeconds);
                    return new TorrentDownloadResult { ErrorMessage = $"Rate limit exceeded (429). Retry after {retryAfterSeconds}s" };
                }

                if (response.StatusCode == HttpStatusCode.Gone)
                {
                    return new TorrentDownloadResult { ErrorMessage = "Torrent is no longer available (410 Gone)" };
                }

                response.EnsureSuccessStatusCode();

                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length == 0)
                {
                    return new TorrentDownloadResult { ErrorMessage = "Downloaded torrent file is empty" };
                }

                // Check if body is magnet text
                var preview = System.Text.Encoding.UTF8.GetString(bytes.Take(Math.Min(100, bytes.Length)).ToArray());
                if (preview.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    var fullMagnet = System.Text.Encoding.UTF8.GetString(bytes).Trim();
                    return ParseMagnet(fullMagnet);
                }

                return new TorrentDownloadResult
                {
                    TorrentBytes = bytes
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed downloading torrent at hop {Hop} from {Url}", hop, Security.SecurityHelper.RedactSecrets(currentUrl));
                return new TorrentDownloadResult { ErrorMessage = Security.SecurityHelper.RedactSecrets(ex.Message) };
            }
        }

        return new TorrentDownloadResult { ErrorMessage = "Too many redirects fetching torrent" };
    }

    private static TorrentDownloadResult ParseMagnet(string magnetUri)
    {
        var hashMatch = Regex.Match(magnetUri, "urn:btih:([a-fA-F0-9]{40}|[a-zA-Z2-7]{32})", RegexOptions.IgnoreCase);
        var rawHash = hashMatch.Success ? hashMatch.Groups[1].Value : null;
        var normalizedHash = NormalizeInfoHash(rawHash);

        return new TorrentDownloadResult
        {
            MagnetUri = magnetUri,
            InfoHash = normalizedHash
        };
    }

    private static string? NormalizeInfoHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return null;

        // If 40 chars hex: return lowercase hex
        if (hash.Length == 40) return hash.ToLowerInvariant();

        // If 32 chars base32: convert to 40 chars hex
        if (hash.Length == 32)
        {
            try
            {
                var bytes = Base32Decode(hash);
                return Convert.ToHexString(bytes).ToLowerInvariant();
            }
            catch
            {
                return hash.ToLowerInvariant();
            }
        }

        return hash.ToLowerInvariant();
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var cleanInput = input.Trim().ToUpperInvariant();
        var output = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var c in cleanInput)
        {
            var val = alphabet.IndexOf(c);
            if (val < 0) continue;

            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        return output.ToArray();
    }
}
