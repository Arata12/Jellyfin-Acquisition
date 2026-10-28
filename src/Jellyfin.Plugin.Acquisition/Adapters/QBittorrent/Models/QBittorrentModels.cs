using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Acquisition.Adapters.QBittorrent.Models;

public class QBittorrentTorrentInfo
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("eta")]
    public long Eta { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("tags")]
    public string? Tags { get; set; }

    [JsonPropertyName("content_path")]
    public string? ContentPath { get; set; }

    [JsonPropertyName("save_path")]
    public string? SavePath { get; set; }

    public bool IsCheckingOrMoving => State is "checkingUP" or "checkingDL" or "checkingResumeData" or "moving";
    public bool HasError => State is "error" or "missingFiles" or "unknown";

    // qBittorrent 5.0 renamed "pausedUP"/"pausedDL" to "stoppedUP"/"stoppedDL"; both spellings are accepted
    public bool IsCompleted => !IsCheckingOrMoving && !HasError &&
        (Progress >= 1.0 || State is "uploading" or "pausedUP" or "stoppedUP" or "queuedUP" or "stalledUP" or "forcedUP");

    public bool HasTag(string tag) =>
        !string.IsNullOrEmpty(Tags) && Tags.Split(',').Any(t => string.Equals(t.Trim(), tag, StringComparison.Ordinal));
}

public class QBittorrentFile
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }
}

public class QBittorrentAddOptions
{
    public byte[]? TorrentBytes { get; set; }
    public string? MagnetUri { get; set; }
    public string? Category { get; set; }
    public string? Tag { get; set; }
    public string? Rename { get; set; }
}

public class QBittorrentCategory
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("savePath")]
    public string? SavePath { get; set; }
}
