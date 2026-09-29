namespace ZoomRecordingSync.Options;

/// <summary>
/// Configuration for the Zoom Server-to-Server OAuth application and API endpoints.
/// </summary>
public sealed class ZoomApiOptions
{
    public const string SectionName = "ZoomApi";

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;

    public string OAuthTokenUrl { get; set; } = "https://zoom.us/oauth/token";

    public string ApiBaseUrl { get; set; } = "https://api.zoom.us";

    /// <summary>
    /// The token is refreshed this many seconds before its reported expiry.
    /// </summary>
    public int TokenRefreshBufferSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum number of attempts for transient HTTP failures (initial attempt included).
    /// </summary>
    public int RetryAttempts { get; set; } = 3;

    /// <summary>
    /// Hosts to which an Authorization header may be sent during a download redirect.
    /// </summary>
    public IReadOnlyList<string> TrustedDownloadHostSuffixes { get; set; } =
        new[] { "zoom.us", "zoom.com" };
}
