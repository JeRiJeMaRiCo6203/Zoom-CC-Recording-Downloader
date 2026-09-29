using System.Text.Json.Serialization;

namespace ZoomRecordingSync.Models;

public sealed class AccessTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("api_url")]
    public string? ApiUrl { get; set; }
}
