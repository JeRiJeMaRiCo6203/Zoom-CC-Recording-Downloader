using System.Net;

namespace ZoomRecordingSync.Clients;

public sealed class ZoomAuthenticationException : Exception
{
    public ZoomAuthenticationException(string message)
        : base(message)
    {
    }

    public ZoomAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ZoomApiException : Exception
{
    public ZoomApiException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public ZoomApiException(string message, Exception innerException, HttpStatusCode? statusCode = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

public sealed class RecordingDownloadException : Exception
{
    public RecordingDownloadException(string message)
        : base(message)
    {
    }

    public RecordingDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
