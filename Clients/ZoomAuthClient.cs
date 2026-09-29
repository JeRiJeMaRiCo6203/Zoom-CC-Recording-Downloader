using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Polly;
using ZoomRecordingSync.Models;
using ZoomRecordingSync.Options;

namespace ZoomRecordingSync.Clients;

/// <summary>
/// Obtains and caches Zoom Server-to-Server OAuth access tokens for the lifetime of a run.
/// </summary>
public sealed class ZoomAuthClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _httpClient;
    private readonly ZoomApiOptions _options;
    private readonly ILogger<ZoomAuthClient> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    private AccessTokenResponse? _cachedToken;
    private DateTimeOffset _tokenIssuedAt;
    private bool _disposed;

    public ZoomAuthClient(
        HttpClient httpClient,
        ZoomApiOptions options,
        ILogger<ZoomAuthClient> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retryPolicy = RetryPolicyFactory.Create(
            _logger,
            "Zoom OAuth token request",
            _options.RetryAttempts);
    }

    /// <summary>
    /// Gets the cached access token, refreshing it when it is expired or close to expiry.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        return token.AccessToken;
    }

    /// <summary>
    /// Gets the full token response, including the API URL returned by Zoom when present.
    /// </summary>
    public async Task<AccessTokenResponse> GetTokenAsync(CancellationToken cancellationToken = default) =>
        await GetTokenCoreAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Invalidates the cached token and obtains a new one.
    /// </summary>
    public async Task<AccessTokenResponse> RefreshAccessTokenAsync(
        CancellationToken cancellationToken = default) =>
        await GetTokenCoreAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);

    private async Task<AccessTokenResponse> GetTokenCoreAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && IsUsable(_cachedToken, _tokenIssuedAt))
            {
                return _cachedToken!;
            }

            if (forceRefresh)
            {
                _cachedToken = null;
                _tokenIssuedAt = default;
            }

            var token = await RequestTokenAsync(cancellationToken).ConfigureAwait(false);
            _cachedToken = token;
            _tokenIssuedAt = _timeProvider.GetUtcNow();
            _logger.LogInformation(
                "Zoom OAuth token obtained; it expires in approximately {ExpiresInSeconds} seconds.",
                token.ExpiresIn);
            return token;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private bool IsUsable(AccessTokenResponse? token, DateTimeOffset issuedAt)
    {
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return false;
        }

        var refreshBuffer = TimeSpan.FromSeconds(Math.Max(0, _options.TokenRefreshBufferSeconds));
        var usableUntil = issuedAt.AddSeconds(Math.Max(0, token.ExpiresIn)) - refreshBuffer;
        return _timeProvider.GetUtcNow() < usableUntil;
    }

    private async Task<AccessTokenResponse> RequestTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId) ||
            string.IsNullOrWhiteSpace(_options.ClientSecret) ||
            string.IsNullOrWhiteSpace(_options.AccountId))
        {
            throw new ZoomAuthenticationException(
                "Zoom credentials are not configured. Set client ID, client secret, and account ID.");
        }

        var requestUri = BuildTokenRequestUri(_options);
        var basicCredentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));

        HttpResponseMessage response;
        try
        {
            response = await _retryPolicy.ExecuteAsync(
                async ct =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicCredentials);
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
                        throw new TimeoutException("The Zoom OAuth request timed out.", ex);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ZoomApiException("Unable to reach the Zoom OAuth token endpoint.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                if (status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.BadRequest)
                {
                    throw new ZoomAuthenticationException(
                        $"Zoom OAuth authentication failed with HTTP {(int)status} ({status}).");
                }

                throw new ZoomApiException(
                    $"Zoom OAuth token request failed with HTTP {(int)status} ({status}).",
                    status);
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ZoomApiException("Zoom returned an unreadable OAuth token response.", ex);
            }

            AccessTokenResponse? token;
            try
            {
                token = JsonSerializer.Deserialize<AccessTokenResponse>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new ZoomApiException("Zoom returned an invalid OAuth token response.", ex);
            }

            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                throw new ZoomAuthenticationException(
                    "Zoom OAuth authentication succeeded but no access token was returned.");
            }

            if (token.ExpiresIn <= 0)
            {
                token.ExpiresIn = 3600;
            }

            return token;
        }
    }

    private static Uri BuildTokenRequestUri(ZoomApiOptions options)
    {
        if (!Uri.TryCreate(options.OAuthTokenUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback))
        {
            throw new ZoomApiException(
                "The configured Zoom OAuth token URL must use HTTPS (HTTP is allowed only for loopback test endpoints).");
        }

        var query = new List<string>
        {
            "grant_type=account_credentials",
            $"account_id={Uri.EscapeDataString(options.AccountId)}"
        };

        var builder = new UriBuilder(baseUri)
        {
            Query = string.Join("&", query)
        };
        return builder.Uri;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cacheLock.Dispose();
    }
}
