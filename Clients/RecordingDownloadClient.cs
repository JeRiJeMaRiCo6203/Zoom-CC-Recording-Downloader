using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Polly;
using ZoomRecordingSync.Models;
using ZoomRecordingSync.Options;
using ZoomRecordingSync.Storage;

namespace ZoomRecordingSync.Clients;

/// <summary>
/// Streams recording and transcript response bodies to the configured storage backend.
/// </summary>
public sealed class RecordingDownloadClient
{
    private const int MaxRedirects = 5;
    private const int MaxIdentifierLength = 80;

    private readonly HttpClient _httpClient;
    private readonly ZoomApiOptions _options;
    private readonly ZoomAuthClient? _authClient;
    private readonly ILogger<RecordingDownloadClient> _logger;
    private readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy;
    private readonly IAsyncPolicy<DownloadResult> _streamRetryPolicy;

    public RecordingDownloadClient(
        HttpClient httpClient,
        ZoomApiOptions options,
        ILogger<RecordingDownloadClient> logger,
        ZoomAuthClient? authClient = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _authClient = authClient;
        _retryPolicy = RetryPolicyFactory.Create(
            _logger,
            "Zoom recording download",
            _options.RetryAttempts);
        _streamRetryPolicy = RetryPolicyFactory.CreateOperationPolicy<DownloadResult>(
            _logger,
            "Zoom recording stream",
            _options.RetryAttempts);
    }

    public Task<DownloadResult> DownloadRecordingAsync(
        RecordingDto recording,
        string accessToken,
        IRecordingStorage storage,
        CancellationToken cancellationToken = default) =>
        DownloadRecordingAsync(recording, accessToken, storage, force: false, cancellationToken);

    public async Task<DownloadResult> DownloadRecordingAsync(
        RecordingDto recording,
        string accessToken,
        IRecordingStorage storage,
        bool force,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(storage);

        if (string.IsNullOrWhiteSpace(recording.DownloadUrl))
        {
            throw new RecordingDownloadException("The recording does not contain a download URL.");
        }

        return await DownloadAsync(
            recording,
            recording.DownloadUrl,
            accessToken,
            storage,
            force,
            isTranscript: false,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<DownloadResult> DownloadTranscriptAsync(
        RecordingDto recording,
        string accessToken,
        IRecordingStorage storage,
        CancellationToken cancellationToken = default) =>
        DownloadTranscriptAsync(recording, accessToken, storage, force: false, cancellationToken);

    public async Task<DownloadResult> DownloadTranscriptAsync(
        RecordingDto recording,
        string accessToken,
        IRecordingStorage storage,
        bool force,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(storage);

        if (string.IsNullOrWhiteSpace(recording.TranscriptUrl))
        {
            throw new RecordingDownloadException("The recording does not contain a transcript URL.");
        }

        return await DownloadAsync(
            recording,
            recording.TranscriptUrl,
            accessToken,
            storage,
            force,
            isTranscript: true,
            cancellationToken).ConfigureAwait(false);
    }

    public static string BuildFileName(RecordingDto recording, string extension)
    {
        ArgumentNullException.ThrowIfNull(recording);

        var recordingId = SanitizeIdentifier(
            string.IsNullOrWhiteSpace(recording.RecordingId) ? "recording" : recording.RecordingId);
        var timestamp = recording.RecordingStartTime?.ToUniversalTime()
            .ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) ?? "unknown";
        var normalizedExtension = NormalizeExtension(extension, isTranscript: false);

        return $"{recordingId}_{timestamp}.{normalizedExtension}";
    }

    private async Task<DownloadResult> DownloadAsync(
        RecordingDto recording,
        string downloadUrl,
        string accessToken,
        IRecordingStorage storage,
        bool force,
        bool isTranscript,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new RecordingDownloadException("An access token is required to download a recording.");
        }

        var ownerName = string.IsNullOrWhiteSpace(recording.OwnerName)
            ? "Unknown Owner"
            : recording.OwnerName;
        var initialExtension = ResolveExtension(recording, downloadUrl, isTranscript, contentType: null, contentDisposition: null);
        var initialFileName = BuildFileName(recording, initialExtension);

        if (!force && await storage.ExistsAsync(ownerName, initialFileName).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Skipping {Asset} for recording {RecordingId}; file already exists: {FileName}.",
                isTranscript ? "transcript" : "recording",
                recording.RecordingId,
                initialFileName);
            return DownloadResult.Skip(ownerName, initialFileName);
        }

        return await _streamRetryPolicy.ExecuteAsync(
            ct => DownloadAttemptAsync(
                recording,
                downloadUrl,
                accessToken,
                storage,
                force,
                isTranscript,
                ownerName,
                initialFileName,
                ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DownloadResult> DownloadAttemptAsync(
        RecordingDto recording,
        string downloadUrl,
        string accessToken,
        IRecordingStorage storage,
        bool force,
        bool isTranscript,
        string ownerName,
        string initialFileName,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithRefreshAndRedirectsAsync(
            downloadUrl,
            accessToken,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new RecordingDownloadException(
                $"Zoom download failed with HTTP {(int)status} ({status}).");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var contentDisposition = response.Content.Headers.ContentDisposition;
        var extension = ResolveExtension(recording, downloadUrl, isTranscript, contentType, contentDisposition);
        var fileName = BuildFileName(recording, extension);

        // The content type can provide a more accurate extension than the URL. Check the
        // final name too, so a rerun does not create a second copy when the URL has no suffix.
        if (!force &&
            !string.Equals(initialFileName, fileName, StringComparison.Ordinal) &&
            await storage.ExistsAsync(ownerName, fileName).ConfigureAwait(false))
        {
            response.Dispose();
            _logger.LogInformation(
                "Skipping {Asset} for recording {RecordingId}; file already exists: {FileName}.",
                isTranscript ? "transcript" : "recording",
                recording.RecordingId,
                fileName);
            return DownloadResult.Skip(ownerName, fileName);
        }

        Stream responseStream;
        try
        {
            responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The recording response stream timed out.", ex);
        }

        await using (responseStream.ConfigureAwait(false))
        {
            var countingStream = new CountingReadStream(responseStream, cancellationToken);
            await storage.SaveAsync(ownerName, fileName, countingStream).ConfigureAwait(false);

            _logger.LogInformation(
                "Downloaded {Asset} for recording {RecordingId}: {FileName} ({Bytes} bytes).",
                isTranscript ? "transcript" : "recording",
                recording.RecordingId,
                fileName,
                countingStream.BytesRead);
            return DownloadResult.Saved(ownerName, fileName, countingStream.BytesRead);
        }
    }

    private async Task<HttpResponseMessage> SendWithRefreshAndRedirectsAsync(
        string downloadUrl,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var response = await SendWithRedirectsAsync(
            downloadUrl,
            accessToken,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        if (_authClient is null)
        {
            throw new RecordingDownloadException(
                "Zoom rejected the download token and no OAuth client is available to refresh it.");
        }

        _logger.LogWarning("Zoom download returned 401; refreshing the access token once.");
        var refreshed = await _authClient.RefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        response = await SendWithRedirectsAsync(
            downloadUrl,
            refreshed.AccessToken,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new RecordingDownloadException(
                "Zoom download authentication failed after one token refresh.");
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendWithRedirectsAsync(
        string downloadUrl,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var currentUri) ||
            (currentUri.Scheme != Uri.UriSchemeHttps && currentUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new RecordingDownloadException("The recording download URL is invalid.");
        }

        for (var redirectCount = 0; ; redirectCount++)
        {
            // The URL returned by Zoom is the initial trusted source. For subsequent
            // redirects, only a Zoom HTTPS host may receive the Bearer header.
            var includeAuthorization = redirectCount == 0
                ? currentUri.Scheme == Uri.UriSchemeHttps || currentUri.IsLoopback
                : currentUri.Scheme == Uri.UriSchemeHttps && IsTrustedZoomHost(currentUri);
            var response = await SendWithRetryAsync(
                currentUri,
                includeAuthorization ? accessToken : null,
                cancellationToken).ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            if (redirectCount >= MaxRedirects)
            {
                response.Dispose();
                throw new RecordingDownloadException("The recording download exceeded the redirect limit.");
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new RecordingDownloadException("Zoom returned a redirect without a Location header.");
            }

            currentUri = location.IsAbsoluteUri
                ? location
                : new Uri(currentUri, location);
            if (currentUri.Scheme != Uri.UriSchemeHttps && currentUri.Scheme != Uri.UriSchemeHttp)
            {
                throw new RecordingDownloadException("The recording download redirect used an unsupported scheme.");
            }
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Uri requestUri,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _retryPolicy.ExecuteAsync(
                async ct =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    request.Headers.Accept.Clear();
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                    if (!string.IsNullOrWhiteSpace(accessToken))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    }

                    try
                    {
                        return await _httpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            ct).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException("The recording download request timed out.", ex);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RecordingDownloadException("The recording download request timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            throw new RecordingDownloadException("Unable to reach the Zoom recording download endpoint.", ex);
        }
    }

    private bool IsTrustedZoomHost(Uri uri)
    {
        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var trustedSuffixes = _options.TrustedDownloadHostSuffixes;
        if (trustedSuffixes is null || trustedSuffixes.Count == 0)
        {
            return false;
        }

        foreach (var suffix in trustedSuffixes)
        {
            if (string.IsNullOrWhiteSpace(suffix))
            {
                continue;
            }

            var normalizedSuffix = suffix.Trim().TrimStart('.').TrimEnd('.');
            if (uri.Host.Equals(normalizedSuffix, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith($".{normalizedSuffix}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        (int)statusCode is >= 300 and < 400;

    private static string ResolveExtension(
        RecordingDto recording,
        string url,
        bool isTranscript,
        string? contentType,
        ContentDispositionHeaderValue? contentDisposition)
    {
        var mediaType = contentType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        var extension = mediaType switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
            "audio/mp4" or "audio/m4a" or "audio/x-m4a" => "m4a",
            "audio/ogg" => "ogg",
            "audio/flac" or "audio/x-flac" => "flac",
            "text/vtt" or "application/vtt" => "vtt",
            "text/plain" => "txt",
            "application/json" => "json",
            _ => null
        };

        extension ??= GetSafeExtension(contentDisposition?.FileNameStar ?? contentDisposition?.FileName);
        if (!isTranscript)
        {
            extension ??= GetSafeExtension(recording.FileExtension);
            extension ??= GetSafeExtension(recording.RecordingFormat);
            extension ??= GetSafeExtension(recording.FileType);
            extension ??= GetSafeExtension(recording.ContentType);
        }

        extension ??= GetSafeExtension(GetUriExtension(url));

        return NormalizeExtension(extension, isTranscript);
    }

    private static string? GetMediaTypeExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Split(';', 2)[0].Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
            "audio/mp4" or "audio/m4a" or "audio/x-m4a" => "m4a",
            "audio/ogg" => "ogg",
            "audio/flac" or "audio/x-flac" => "flac",
            "text/vtt" or "application/vtt" => "vtt",
            "text/plain" => "txt",
            "application/json" => "json",
            _ => null
        };
    }

    private static string? GetSafeExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        var mediaTypeExtension = GetMediaTypeExtension(candidate);
        if (mediaTypeExtension is not null)
        {
            return mediaTypeExtension;
        }

        var extension = candidate.StartsWith('.')
            ? candidate[1..]
            : candidate.All(char.IsLetterOrDigit)
                ? candidate
                : Path.GetExtension(candidate).TrimStart('.');
        if (extension.Length is < 1 or > 10)
        {
            return null;
        }

        foreach (var character in extension)
        {
            if (!char.IsLetterOrDigit(character))
            {
                return null;
            }
        }

        return extension.ToLowerInvariant();
    }

    private static string? GetUriExtension(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? Path.GetExtension(uri.AbsolutePath)
            : null;
    }

    private static string NormalizeExtension(string? extension, bool isTranscript)
    {
        var mediaTypeExtension = GetMediaTypeExtension(extension);
        if (mediaTypeExtension is not null)
        {
            return mediaTypeExtension;
        }

        var normalized = extension?.Trim().TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Any(character => !char.IsLetterOrDigit(character)))
        {
            return isTranscript ? "vtt" : "mp3";
        }

        return normalized;
    }

    private static string SanitizeIdentifier(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaxIdentifierLength));
        foreach (var character in value)
        {
            if (builder.Length >= MaxIdentifierLength)
            {
                break;
            }

            if (char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('_');
            }
        }

        var result = builder.ToString().Trim('.', ' ');
        return result.Length == 0 ? "recording" : result;
    }

    private sealed class CountingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly CancellationToken _cancellationToken;

        public CountingReadStream(Stream inner, CancellationToken cancellationToken)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _cancellationToken = cancellationToken;
        }

        public long BytesRead { get; private set; }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var read = _inner.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
