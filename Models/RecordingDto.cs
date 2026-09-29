using System.Text.Json.Serialization;

namespace ZoomRecordingSync.Models;

/// <summary>
/// A recording returned by the Contact Center recordings endpoint.
/// </summary>
public sealed class RecordingDto
{
    private string _recordingId = string.Empty;

    [JsonPropertyName("id")]
    public string RecordingId
    {
        get => _recordingId;
        set => _recordingId = value ?? string.Empty;
    }

    [JsonIgnore]
    public string Id => RecordingId;

    /// <summary>
    /// Accepts the recording_id spelling used by some API versions while keeping
    /// RecordingId as the canonical property.
    /// </summary>
    [JsonPropertyName("recording_id")]
    public string? LegacyRecordingId
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _recordingId = value;
            }
        }
    }

    [JsonPropertyName("owner_id")]
    public string? OwnerId { get; set; }

    [JsonPropertyName("owner_name")]
    public string OwnerName { get; set; } = "Unknown Owner";

    [JsonPropertyName("owner_type")]
    public string? OwnerType { get; set; }

    [JsonPropertyName("recording_start_time")]
    public DateTimeOffset? RecordingStartTime { get; set; }

    [JsonPropertyName("recording_end_time")]
    public DateTimeOffset? RecordingEndTime { get; set; }

    [JsonPropertyName("recording_type")]
    public string? RecordingType { get; set; }

    [JsonPropertyName("channel_type")]
    public string? ChannelType { get; set; }

    [JsonPropertyName("recording_format")]
    public string? RecordingFormat { get; set; }

    [JsonPropertyName("file_extension")]
    public string? FileExtension { get; set; }

    [JsonPropertyName("file_type")]
    public string? FileType { get; set; }

    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }

    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("transcript_url")]
    public string? TranscriptUrl { get; set; }
}
