namespace ZoomRecordingSync.Models;

public sealed record SyncFailure(string RecordingId, string Error);

public sealed class SyncRunResult
{
    public int TotalRecordingsFound { get; init; }

    public int PagesFetched { get; init; }

    public int Downloaded { get; init; }

    public int Skipped { get; init; }

    public int Failed { get; init; }

    public IReadOnlyList<SyncFailure> Failures { get; init; } = Array.Empty<SyncFailure>();

    public bool HasFailures => Failed > 0;
}
