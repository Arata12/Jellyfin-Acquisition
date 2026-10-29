using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Acquisition.Seerr;

public interface ISeerrSessionStore
{
    /// <summary>Creates a session for the Jellyfin user and returns the cookie value.</summary>
    Task<string> CreateAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Jellyfin user of a live session, extending it; null when unknown or expired.</summary>
    Task<Guid?> GetUserIdAsync(string? token, CancellationToken ct = default);

    Task RemoveAsync(string? token, CancellationToken ct = default);
}

public class SeerrSessionEntry
{
    /// <summary>SHA-256 of the cookie value; the value itself is never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Seerr-API sessions persisted next to the requests, so clients stay signed in across Jellyfin restarts.
/// </summary>
public class JsonFileSeerrSessionStore : ISeerrSessionStore
{
    /// <summary>Same lifetime as Seerr's session cookie; extended while the session is used.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private const int MaxSessionsPerUser = 20;

    private readonly string _filePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ILogger<JsonFileSeerrSessionStore> _logger;
    private List<SeerrSessionEntry>? _sessions;

    public JsonFileSeerrSessionStore(IApplicationPaths appPaths, ILogger<JsonFileSeerrSessionStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(appPaths.PluginConfigurationsPath, "Acquisition");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "seerr-sessions.json");
    }

    public async Task<string> CreateAsync(Guid userId, CancellationToken ct = default)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow;

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sessions = await LoadAsync(ct).ConfigureAwait(false);
            sessions.RemoveAll(s => s.ExpiresAt <= now);

            var own = sessions.Where(s => s.UserId == userId).OrderBy(s => s.CreatedAt).ToList();
            foreach (var old in own.Take(Math.Max(0, own.Count - MaxSessionsPerUser + 1)))
            {
                sessions.Remove(old);
            }

            sessions.Add(new SeerrSessionEntry { TokenHash = Hash(token), UserId = userId, CreatedAt = now, ExpiresAt = now + Lifetime });
            await SaveAsync(sessions, ct).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }

        return token;
    }

    public async Task<Guid?> GetUserIdAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = Hash(token);
        var now = DateTimeOffset.UtcNow;

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sessions = await LoadAsync(ct).ConfigureAwait(false);
            var session = sessions.FirstOrDefault(s => s.TokenHash == hash);
            if (session == null) return null;

            if (session.ExpiresAt <= now)
            {
                sessions.Remove(session);
                await SaveAsync(sessions, ct).ConfigureAwait(false);
                return null;
            }

            // Sliding expiry, written at most once a day per session
            if (session.ExpiresAt - now < Lifetime - TimeSpan.FromDays(1))
            {
                session.ExpiresAt = now + Lifetime;
                await SaveAsync(sessions, ct).ConfigureAwait(false);
            }

            return session.UserId;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task RemoveAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        var hash = Hash(token);
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sessions = await LoadAsync(ct).ConfigureAwait(false);
            if (sessions.RemoveAll(s => s.TokenHash == hash) > 0)
            {
                await SaveAsync(sessions, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private async Task<List<SeerrSessionEntry>> LoadAsync(CancellationToken ct)
    {
        if (_sessions != null) return _sessions;

        _sessions = [];
        if (File.Exists(_filePath))
        {
            try
            {
                await using var stream = File.OpenRead(_filePath);
                _sessions = await JsonSerializer.DeserializeAsync<List<SeerrSessionEntry>>(stream, SeerrJson.Options, ct).ConfigureAwait(false) ?? [];
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Losing the sessions only signs clients out; they log in again silently with their stored credentials
                _logger.LogWarning(ex, "Seerr session file is unreadable; starting with no sessions");
            }
        }

        return _sessions;
    }

    private async Task SaveAsync(List<SeerrSessionEntry> sessions, CancellationToken ct)
    {
        var tmp = _filePath + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, sessions, SeerrJson.Options, ct).ConfigureAwait(false);
        }

        File.Move(tmp, _filePath, overwrite: true);
    }
}

/// <summary>
/// Per-address limit on failed Seerr sign-ins, on top of Jellyfin's own account lockout.
/// </summary>
public class SeerrLoginThrottle
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Since)> _failures = new();

    public bool IsBlocked(IPAddress? address)
    {
        var key = address?.ToString() ?? string.Empty;
        return _failures.TryGetValue(key, out var entry) && entry.Count >= MaxFailures && DateTimeOffset.UtcNow - entry.Since < Window;
    }

    public void RecordFailure(IPAddress? address)
    {
        var key = address?.ToString() ?? string.Empty;
        var now = DateTimeOffset.UtcNow;
        _failures.AddOrUpdate(key, (1, now), (_, e) => now - e.Since >= Window ? (1, now) : (e.Count + 1, e.Since));

        if (_failures.Count > 10_000)
        {
            foreach (var stale in _failures.Where(e => now - e.Value.Since >= Window).ToList())
            {
                _failures.TryRemove(stale.Key, out _);
            }
        }
    }

    public void Reset(IPAddress? address) => _failures.TryRemove(address?.ToString() ?? string.Empty, out _);
}
