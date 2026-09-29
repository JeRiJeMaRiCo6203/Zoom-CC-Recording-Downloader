using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Polly;
using ZoomRecordingSync.Models;
using ZoomRecordingSync.Options;

namespace ZoomRecordingSync.Clients;

/// <summary>
/// Reads paginated Contact Center voice recordings from Zoom.
/// </summary>
public sealed class ZoomRecordingsClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _httpClient;
    private readonly ZoomAuthClient _authClient;
    private readonly ZoomApiOptions _options;
    private readonly ILogger<ZoomRecordingsClient> _logger;
    private readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy;
    private int _pagesFetched;

    public ZoomRecordingsClient(
        HttpClient httpClient,
        ZoomAuthClient authClient,
        ZoomApiOptions options,
        ILogger<ZoomRecordingsClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _authClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _retryPolicy = RetryPolicyFactory.Create(
            _logger,
            "Zoom recordings request",
            _options.RetryAttempts);
    }

    public int PagesFetched => Volatile.Read(ref _pagesFetched);

    /// <summary>
    /// Streams all recordings matching the requested UTC range. The end boundary is exclusive.
    /// </summary>
    public async IAsyncEnumerable<RecordingDto> GetAllRecordingsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int pageSize = 50,
        string? ownerName = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tokenResponse = await _authClient.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var recording in GetAllRecordingsCoreAsync(
            from,
            to,
            pageSize,
            ownerName,
            tokenResponse,
            cancellationToken).ConfigureAwait(false))
        {
            yield return recording;
        }
    }

    /// <summary>
    /// Variant used by the orchestrator when it already has a token response for this run.
    /// </summary>
    public async IAsyncEnumerable<RecordingDto> GetAllRecordingsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int pageSize,
        string? ownerName,
        AccessTokenResponse tokenResponse,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenResponse);
        await foreach (var recording in GetAllRecordingsCoreAsync(
            from,
            to,
            pageSize,
            ownerName,
            tokenResponse,
            cancellationToken).ConfigureAwait(false))
        {
            yield return recording;
        }
    }

    private async IAsyncEnumerable<RecordingDto> GetAllRecordingsCoreAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int pageSize,
        string? ownerName,
        AccessTokenResponse tokenResponse,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (to <= from)
        {
            throw new ArgumentException("The recordings date range must have a positive duration.", nameof(to));
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be greater than zero.");
        }

        Volatile.Write(ref _pagesFetched, 0);
        string? nextPageToken = null;
        var seenPageTokens = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentToken = await _authClient.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            tokenResponse.AccessToken = currentToken.AccessToken;
            if (!string.IsNullOrWhiteSpace(currentToken.ApiUrl))
            {
                tokenResponse.ApiUrl = currentToken.ApiUrl;
            }

            var requestUri = BuildRecordingsUri(
                string.IsNullOrWhiteSpace(tokenResponse.ApiUrl) ? _options.ApiBaseUrl : tokenResponse.ApiUrl,
                from,
                to,
                pageSize,
                ownerName,
                nextPageToken);

            using var response = await SendPageWithRefreshAsync(
                requestUri,
                tokenResponse,
                cancellationToken).ConfigureAwait(false);

            RecordingsResponse page;
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                page = JsonSerializer.Deserialize<RecordingsResponse>(body, JsonOptions)
                    ?? new RecordingsResponse();
            }
            catch (JsonException ex)
            {
                throw new ZoomApiException("Zoom returned an invalid recordings response.", ex);
            }

            page.Recordings ??= new List<RecordingDto>();
            Interlocked.Increment(ref _pagesFetched);
            _logger.LogDebug(
                "Fetched recordings page {PageNumber}; returned {RecordingCount} recordings.",
                PagesFetched,
                page.Recordings.Count);

            foreach (var recording in page.Recordings)
            {
                yield return recording;
            }

            nextPageToken = page.NextPageToken;
            if (string.IsNullOrWhiteSpace(nextPageToken))
            {
                yield break;
            }

            if (!seenPageTokens.Add(nextPageToken))
            {
                throw new ZoomApiException("Zoom returned a repeated next_page_token; stopping pagination.");
            }
        }
    }

    private async Task<HttpResponseMessage> SendPageWithRefreshAsync(
        Uri requestUri,
        AccessTokenResponse tokenResponse,
        CancellationToken cancellationToken)
    {
        var response = await SendWithRetryAsync(
            requestUri,
            tokenResponse.AccessToken,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            EnsureSuccess(response, "recordings");
            return response;
        }

        response.Dispose();
        _logger.LogWarning("Zoom recordings endpoint returned 401; refreshing the access token once.");

        var refreshed = await _authClient.RefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        tokenResponse.AccessToken = refreshed.AccessToken;
        if (!string.IsNullOrWhiteSpace(refreshed.ApiUrl))
        {
            tokenResponse.ApiUrl = refreshed.ApiUrl;
        }

        response = await SendWithRetryAsync(requestUri, tokenResponse.AccessToken, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new ZoomApiException(
                "Zoom recordings authentication failed after one token refresh.",
                HttpStatusCode.Unauthorized);
        }

        EnsureSuccess(response, "recordings");
        return response;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Uri requestUri,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (requestUri.Scheme != Uri.UriSchemeHttps && !requestUri.IsLoopback)
        {
            throw new ZoomApiException("Zoom API requests must use HTTPS.");
        }

        try
        {
            return await _retryPolicy.ExecuteAsync(
                async ct =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    try
                    {
                        return await _httpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            ct).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException("The Zoom recordings request timed out.", ex);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ZoomApiException("The Zoom recordings request timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            throw new ZoomApiException("Unable to reach the Zoom recordings endpoint.", ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = response.StatusCode;
        response.Dispose();
        throw new ZoomApiException(
            $"Zoom {operation} request failed with HTTP {(int)status} ({status}).",
            status);
    }

    private static Uri BuildRecordingsUri(
        string apiBaseUrl,
        DateTimeOffset from,
        DateTimeOffset to,
        int pageSize,
        string? ownerName,
        string? nextPageToken)
    {
        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ZoomApiException("The configured Zoom API base URL is invalid.");
        }

        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var recordingsPath = basePath.EndsWith("/v2/contact_center/recordings", StringComparison.OrdinalIgnoreCase)
            ? basePath
            : basePath.EndsWith("/v2/contact_center", StringComparison.OrdinalIgnoreCase)
                ? $"{basePath}/recordings"
                : basePath.EndsWith("/v2", StringComparison.OrdinalIgnoreCase)
                    ? $"{basePath}/contact_center/recordings"
                    : $"{basePath}/v2/contact_center/recordings";

        var query = new List<string>
        {
            "query_date_type=recording_start_time",
            $"from={Uri.EscapeDataString(FormatUtcDate(from))}",
            $"to={Uri.EscapeDataString(FormatUtcDate(to))}",
            "recording_type=automatic",
            "owner_type=queue",
            "channel_type=voice",
            "channel=voice",
            "exemption=false",
            $"page_size={pageSize.ToString(CultureInfo.InvariantCulture)}"
        };

        if (!string.IsNullOrWhiteSpace(ownerName))
        {
            query.Add($"owner_name={Uri.EscapeDataString(ownerName)}");
        }

        if (!string.IsNullOrWhiteSpace(nextPageToken))
        {
            query.Add($"next_page_token={Uri.EscapeDataString(nextPageToken)}");
        }

        return new UriBuilder(baseUri)
        {
            Path = recordingsPath,
            Query = string.Join("&", query)
        }.Uri;
    }

    private static string FormatUtcDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}
