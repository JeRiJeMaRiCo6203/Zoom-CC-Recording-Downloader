using Microsoft.Extensions.Logging;
using ZoomRecordingSync.Clients;
using ZoomRecordingSync.Models;
using ZoomRecordingSync.Options;
using ZoomRecordingSync.Storage;

namespace ZoomRecordingSync.Services;

/// <summary>
/// Coordinates authentication, pagination, per-recording downloads, and run reporting.
/// </summary>
public sealed class RecordingSyncService
{
    private readonly ZoomAuthClient _authClient;
    private readonly ZoomRecordingsClient _recordingsClient;
    private readonly RecordingDownloadClient _downloadClient;
    private readonly IRecordingStorage _storage;
    private readonly ILogger<RecordingSyncService> _logger;

    public RecordingSyncService(
        ZoomAuthClient authClient,
        ZoomRecordingsClient recordingsClient,
        RecordingDownloadClient downloadClient,
        IRecordingStorage storage,
        ILogger<RecordingSyncService> logger)
    {
        _authClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
        _recordingsClient = recordingsClient ?? throw new ArgumentNullException(nameof(recordingsClient));
        _downloadClient = downloadClient ?? throw new ArgumentNullException(nameof(downloadClient));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SyncRunResult> RunAsync(
        SyncOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var (start, endExclusive) = options.GetUtcDateRange();
        _logger.LogInformation(
            "Starting Zoom recording sync for {From:O} through {ToExclusive:O} (owner filter: {OwnerFilter}).",
            start,
            endExclusive,
            string.IsNullOrWhiteSpace(options.OwnerName) ? "(none)" : options.OwnerName);

        var tokenResponse = await _authClient.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var totalRecordings = 0;
        var downloaded = 0;
        var skipped = 0;
        var failed = 0;
        var failures = new List<SyncFailure>();

        await foreach (var recording in _recordingsClient.GetAllRecordingsAsync(
            start,
            endExclusive,
            options.PageSize,
            options.OwnerName,
            tokenResponse,
            cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalRecordings++;

            if (recording is null)
            {
                failed++;
                failures.Add(new SyncFailure("unknown", "The recordings response contained a null recording."));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(recording.OwnerType) &&
                !recording.OwnerType.Equals("queue", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Skipping recording {RecordingId} with unexpected owner type {OwnerType}; the API was queried for queues.",
                    recording.RecordingId,
                    recording.OwnerType);
                continue;
            }

            var recordingId = string.IsNullOrWhiteSpace(recording.RecordingId)
                ? "unknown"
                : recording.RecordingId;

            var sensitiveAccessToken = string.Empty;
            try
            {
                // Re-read the cached token before each asset. If a download received a 401,
                // ZoomAuthClient refreshes the cache and subsequent assets use the new token.
                var accessToken = await _authClient.GetAccessTokenAsync(cancellationToken)
                    .ConfigureAwait(false);
                sensitiveAccessToken = accessToken;
                var audioResult = await _downloadClient.DownloadRecordingAsync(
                    recording,
                    accessToken,
                    _storage,
                    options.Force,
                    cancellationToken).ConfigureAwait(false);
                RecordResult(audioResult, ref downloaded, ref skipped);

                if (options.DownloadTranscripts)
                {
                    if (string.IsNullOrWhiteSpace(recording.TranscriptUrl))
                    {
                        _logger.LogDebug(
                            "No transcript URL was returned for recording {RecordingId}; skipping transcript.",
                            recordingId);
                    }
                    else
                    {
                        var transcriptToken = await _authClient
                            .GetAccessTokenAsync(cancellationToken)
                            .ConfigureAwait(false);
                        sensitiveAccessToken = transcriptToken;
                        var transcriptResult = await _downloadClient.DownloadTranscriptAsync(
                            recording,
                            transcriptToken,
                            _storage,
                            options.Force,
                            cancellationToken).ConfigureAwait(false);
                        RecordResult(transcriptResult, ref downloaded, ref skipped);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ZoomAuthenticationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                var message = LimitErrorMessage(ex.Message, sensitiveAccessToken);
                failures.Add(new SyncFailure(recordingId, message));
                _logger.LogError(
                    "Failed to process recording {RecordingId} ({ErrorType}): {Error}",
                    recordingId,
                    ex.GetType().Name,
                    message);
            }
        }

        var result = new SyncRunResult
        {
            TotalRecordingsFound = totalRecordings,
            PagesFetched = _recordingsClient.PagesFetched,
            Downloaded = downloaded,
            Skipped = skipped,
            Failed = failed,
            Failures = failures
        };

        _logger.LogInformation(
            "Zoom recording sync finished. Recordings found: {TotalRecordings}; pages: {PagesFetched}; downloaded: {Downloaded}; skipped: {Skipped}; failed: {Failed}.",
            result.TotalRecordingsFound,
            result.PagesFetched,
            result.Downloaded,
            result.Skipped,
            result.Failed);
        return result;
    }

    private static void RecordResult(DownloadResult result, ref int downloaded, ref int skipped)
    {
        if (result.Skipped)
        {
            skipped++;
        }
        else
        {
            downloaded++;
        }
    }

    private static string LimitErrorMessage(string? message, string? sensitiveValue = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Unknown error.";
        }

        var safeMessage = message;
        if (!string.IsNullOrEmpty(sensitiveValue))
        {
            safeMessage = safeMessage.Replace(sensitiveValue, "[redacted]", StringComparison.Ordinal);
        }

        const int maxLength = 1000;
        return safeMessage.Length <= maxLength ? safeMessage : safeMessage[..maxLength] + "…";
    }
}
