using System.Net;
using Microsoft.Extensions.Logging;
using Polly;

namespace ZoomRecordingSync.Clients;

internal static class RetryPolicyFactory
{
    public static IAsyncPolicy<HttpResponseMessage> Create(
        ILogger logger,
        string operation,
        int maxAttempts)
    {
        var retryCount = Math.Max(0, maxAttempts - 1);

        Func<int, DelegateResult<HttpResponseMessage>, Context, TimeSpan> delayProvider =
            (attempt, outcome, _) => GetRetryDelay(attempt, outcome.Result);
        Func<DelegateResult<HttpResponseMessage>, TimeSpan, int, Context, Task> onRetry =
            (outcome, delay, attempt, _) =>
            {
                outcome.Result?.Dispose();
                logger.LogWarning(
                    "Transient HTTP failure during {Operation}; retrying (attempt {Attempt}/{MaxAttempts}) in {Delay}.",
                    operation,
                    attempt,
                    maxAttempts,
                    delay);
                return Task.CompletedTask;
            };

        return Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .Or<TimeoutException>()
            .OrResult(response => IsTransient(response.StatusCode))
            .WaitAndRetryAsync(retryCount, delayProvider, onRetry);
    }

    public static IAsyncPolicy<TResult> CreateOperationPolicy<TResult>(
        ILogger logger,
        string operation,
        int maxAttempts)
    {
        var retryCount = Math.Max(0, maxAttempts - 1);
        return Policy<TResult>
            .Handle<HttpRequestException>()
            .Or<TimeoutException>()
            .Or<IOException>()
            .WaitAndRetryAsync(
                retryCount,
                attempt => TimeSpan.FromSeconds(Math.Pow(2, Math.Max(0, attempt - 1))),
                (exception, delay, attempt, _) =>
                {
                    logger.LogWarning(
                        "Transient I/O failure during {Operation}; retrying (attempt {Attempt}/{MaxAttempts}) in {Delay} ({ErrorType}).",
                        operation,
                        attempt,
                        maxAttempts,
                        delay,
                        exception.GetType().Name);
                });
    }

    public static bool IsTransient(HttpStatusCode statusCode)
    {
        var numericStatus = (int)statusCode;
        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
               numericStatus is >= 500 and <= 599;
    }

    private static TimeSpan GetRetryDelay(int attempt, HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        if (response?.Headers.RetryAfter?.Date is { } retryAt)
        {
            var retryDelay = retryAt - DateTimeOffset.UtcNow;
            return retryDelay > TimeSpan.Zero ? retryDelay : TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(Math.Pow(2, Math.Max(0, attempt - 1)));
    }
}
