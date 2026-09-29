using System.Text.Json.Serialization;

namespace ZoomRecordingSync.Models;

public sealed class RecordingsResponse
{
    [JsonPropertyName("recordings")]
    public List<RecordingDto> Recordings { get; set; } = new();

    [JsonPropertyName("next_page_token")]
    public string? NextPageToken { get; set; }

    [JsonPropertyName("page_size")]
    public int? PageSize { get; set; }
}
