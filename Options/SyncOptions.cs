namespace ZoomRecordingSync.Options;

/// <summary>
/// Options controlling one recording synchronization run.
/// </summary>
public sealed class SyncOptions
{
    public const string SectionName = "ZoomRecordingSync";

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    public int PageSize { get; set; } = 50;

    public string? OutputPath { get; set; }

    public string? OwnerName { get; set; }

    public bool DownloadTranscripts { get; set; }

    public string StorageMode { get; set; } = "local";

    public bool Force { get; set; }

    /// <summary>
    /// Gets the UTC date range used by the API. The end is exclusive and represents
    /// the end of the requested calendar day (the following UTC midnight).
    /// </summary>
    public (DateTimeOffset Start, DateTimeOffset EndExclusive) GetUtcDateRange()
    {
        if (From is null || To is null)
        {
            throw new ArgumentException("Both --from and --to are required.");
        }

        var startDate = From.Value.Date;
        var endDate = To.Value.Date;

        if (endDate < startDate)
        {
            throw new ArgumentException("The --to date must be on or after --from.");
        }

        try
        {
            var endExclusiveDate = endDate.AddDays(1);
            return (
                new DateTimeOffset(DateTime.SpecifyKind(startDate, DateTimeKind.Utc)),
                new DateTimeOffset(DateTime.SpecifyKind(endExclusiveDate, DateTimeKind.Utc)));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ArgumentException("The requested date range is not representable.", nameof(To), ex);
        }
    }

    public void Validate()
    {
        _ = GetUtcDateRange();

        if (PageSize <= 0)
        {
            throw new ArgumentException("Page size must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            throw new ArgumentException("An output path is required.");
        }

        try
        {
            _ = Path.GetFullPath(OutputPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The output path is invalid.", nameof(OutputPath), ex);
        }

        if (string.IsNullOrWhiteSpace(StorageMode))
        {
            throw new ArgumentException("Storage mode must be specified.");
        }
    }
}
