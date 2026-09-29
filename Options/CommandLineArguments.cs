using CommandLine;

namespace ZoomRecordingSync.Options;

/// <summary>
/// Raw command-line values. Nullable properties are used so an omitted option can
/// fall back to environment variables or appsettings.json.
/// </summary>
public sealed class CommandLineArguments
{
    [Option("from", Required = true, HelpText = "Start date in yyyy-MM-dd format.")]
    public string? From { get; set; }

    [Option("to", Required = true, HelpText = "End date in yyyy-MM-dd format (inclusive).")]
    public string? To { get; set; }

    [Option("output", HelpText = "Root directory for downloaded recordings.")]
    public string? Output { get; set; }

    [Option("output-path", HelpText = "Alias for --output.")]
    public string? OutputPath { get; set; }

    [Option("page-size", HelpText = "Number of recordings requested per API page.")]
    public int? PageSize { get; set; }

    [Option("owner-name", HelpText = "Optional queue/owner name filter.")]
    public string? OwnerName { get; set; }

    [Option("download-transcripts", HelpText = "Download transcript files when available.")]
    public bool? DownloadTranscripts { get; set; }

    [Option("storage-mode", HelpText = "Storage backend: local (default) or cloud (reserved).")]
    public string? StorageMode { get; set; }

    [Option("force", HelpText = "Re-download files even when they already exist.")]
    public bool? Force { get; set; }

    [Option("client-id", HelpText = "Zoom Server-to-Server OAuth client ID.")]
    public string? ClientId { get; set; }

    [Option("client-secret", HelpText = "Zoom Server-to-Server OAuth client secret.")]
    public string? ClientSecret { get; set; }

    [Option("account-id", HelpText = "Zoom account ID.")]
    public string? AccountId { get; set; }

    [Option("retry-attempts", HelpText = "Maximum attempts for transient HTTP failures.")]
    public int? RetryAttempts { get; set; }
}
