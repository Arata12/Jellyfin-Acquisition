using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Adapters.QBittorrent.Models;

namespace Jellyfin.Plugin.Acquisition.Adapters.QBittorrent;

public class QBittorrentClient : IQBittorrentClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<QBittorrentClient> _logger;
    // Full "name=value" of the session cookie: "SID" up to qBittorrent 5.2, "QBT_SID_<port>" from 5.3
    private string? _sidCookie;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    public QBittorrentClient(HttpClient httpClient, ILogger<QBittorrentClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    private Uri BuildUri(string relativePath)
    {
        var config = Plugin.Instance?.Configuration;
        var rawUrl = !string.IsNullOrWhiteSpace(config?.QBittorrentUrl) ? config.QBittorrentUrl : "http://localhost:8080";
        if (!rawUrl.EndsWith('/')) rawUrl += "/";
        return new Uri(new Uri(rawUrl), relativePath.TrimStart('/'));
    }

    private void ApplyCookie(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_sidCookie))
        {
            request.Headers.Add("Cookie", _sidCookie);
        }
    }

    public async Task<bool> LoginAsync(CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null) return false;

        await _loginLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("api/v2/auth/login"));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "username", config.QBittorrentUsername },
                { "password", config.QBittorrentPassword }
            });

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("qBittorrent login failed with HTTP {Status}", response.StatusCode);
                return false;
            }

            var setCookie = response.Headers.FirstOrDefault(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)).Value;
            if (setCookie != null)
            {
                foreach (var cookie in setCookie)
                {
                    var pair = cookie.Split(';')[0].Trim();
                    var name = pair.Split('=')[0];
                    if (pair.Contains('=') &&
                        (name.Equals("SID", StringComparison.OrdinalIgnoreCase) || name.StartsWith("QBT_SID", StringComparison.OrdinalIgnoreCase)))
                    {
                        _sidCookie = pair;
                        _logger.LogInformation("qBittorrent authenticated successfully");
                        return true;
                    }
                }
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.Contains("Ok.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to authenticate with qBittorrent");
            return false;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendWithAuthAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_sidCookie))
        {
            await LoginAsync(ct).ConfigureAwait(false);
        }

        var req = requestFactory();
        ApplyCookie(req);
        var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            _logger.LogInformation("qBittorrent returned {Status}, re-authenticating...", resp.StatusCode);
            _sidCookie = null;
            if (await LoginAsync(ct).ConfigureAwait(false))
            {
                resp.Dispose();
                var retryReq = requestFactory();
                ApplyCookie(retryReq);
                return await _httpClient.SendAsync(retryReq, ct).ConfigureAwait(false);
            }
        }

        return resp;
    }

    public async Task<bool> AddTorrentAsync(QBittorrentAddOptions options, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendWithAuthAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BuildUri("api/v2/torrents/add"));
                var content = new MultipartFormDataContent();

                if (options.TorrentBytes != null && options.TorrentBytes.Length > 0)
                {
                    var fileContent = new ByteArrayContent(options.TorrentBytes);
                    fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-bittorrent");
                    content.Add(fileContent, "torrents", "download.torrent");
                }
                else if (!string.IsNullOrWhiteSpace(options.MagnetUri))
                {
                    content.Add(new StringContent(options.MagnetUri), "urls");
                }

                if (!string.IsNullOrWhiteSpace(options.Category))
                {
                    content.Add(new StringContent(options.Category), "category");
                }

                if (!string.IsNullOrWhiteSpace(options.Tag))
                {
                    content.Add(new StringContent(options.Tag), "tags");
                }

                if (!string.IsNullOrWhiteSpace(options.Rename))
                {
                    content.Add(new StringContent(options.Rename), "rename");
                }

                req.Content = content;
                return req;
            }, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("qBittorrent AddTorrent failed with HTTP {Status}", response.StatusCode);
                return false;
            }

            var resultText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Returns "Ok." or JSON in 5.3 with added_torrent_ids
            return response.IsSuccessStatusCode && !resultText.Contains("Fails", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "qBittorrent AddTorrent exception");
            return false;
        }
    }

    public async Task<List<QBittorrentTorrentInfo>> GetTorrentsInfoAsync(string? category = null, string? tag = null, string? hash = null, CancellationToken ct = default)
    {
        try
        {
            var relative = "api/v2/torrents/info?";
            var parameters = new List<string>();
            if (!string.IsNullOrWhiteSpace(category)) parameters.Add($"category={Uri.EscapeDataString(category)}");
            if (!string.IsNullOrWhiteSpace(tag)) parameters.Add($"tag={Uri.EscapeDataString(tag)}");
            if (!string.IsNullOrWhiteSpace(hash)) parameters.Add($"hashes={Uri.EscapeDataString(hash.ToLowerInvariant())}");

            relative += string.Join("&", parameters);

            using var response = await SendWithAuthAsync(() => new HttpRequestMessage(HttpMethod.Get, BuildUri(relative)), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var list = await response.Content.ReadFromJsonAsync<List<QBittorrentTorrentInfo>>(cancellationToken: ct).ConfigureAwait(false) ?? [];

            // Older qBittorrent 4.x releases ignore the "tag" filter and return every torrent: filter here too
            return string.IsNullOrWhiteSpace(tag) ? list : list.Where(t => t.HasTag(tag)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "qBittorrent GetTorrentsInfo failed");
            return [];
        }
    }

    public async Task<List<QBittorrentFile>> GetTorrentFilesAsync(string hash, CancellationToken ct = default)
    {
        try
        {
            var relative = $"api/v2/torrents/files?hash={Uri.EscapeDataString(hash.ToLowerInvariant())}";
            using var response = await SendWithAuthAsync(() => new HttpRequestMessage(HttpMethod.Get, BuildUri(relative)), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var list = await response.Content.ReadFromJsonAsync<List<QBittorrentFile>>(cancellationToken: ct).ConfigureAwait(false);
            return list ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "qBittorrent GetTorrentFiles failed for hash {Hash}", hash);
            return [];
        }
    }

    public async Task<List<string>?> GetTagsAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await SendWithAuthAsync(() => new HttpRequestMessage(HttpMethod.Get, BuildUri("api/v2/torrents/tags")), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<List<string>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "qBittorrent GetTags failed");
            return null;
        }
    }

    public async Task<bool> DeleteTagsAsync(IReadOnlyCollection<string> tags, CancellationToken ct = default)
    {
        if (tags.Count == 0) return true;

        try
        {
            using var response = await SendWithAuthAsync(() => new HttpRequestMessage(HttpMethod.Post, BuildUri("api/v2/torrents/deleteTags"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { { "tags", string.Join(",", tags) } })
            }, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "qBittorrent DeleteTags failed");
            return false;
        }
    }

    public async Task<List<QBittorrentCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await SendWithAuthAsync(() => new HttpRequestMessage(HttpMethod.Get, BuildUri("api/v2/torrents/categories")), ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var dict = await response.Content.ReadFromJsonAsync<Dictionary<string, QBittorrentCategory>>(cancellationToken: ct).ConfigureAwait(false);
            if (dict != null)
            {
                var list = new List<QBittorrentCategory>();
                foreach (var (k, v) in dict)
                {
                    if (string.IsNullOrEmpty(v.Name)) v.Name = k;
                    list.Add(v);
                }
                return list;
            }
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "qBittorrent GetCategories failed");
            throw;
        }
    }
}
