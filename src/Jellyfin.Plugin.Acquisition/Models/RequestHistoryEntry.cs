using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Acquisition.Models;

/// <summary>
/// A timestamped history entry for state transitions and diagnostic tracking.
/// </summary>
public class RequestHistoryEntry
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RequestStatus Status { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public string? Details { get; set; }
}
