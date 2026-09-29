using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZoomRecordingSync.Options;

namespace ZoomRecordingSync.Storage;

/// <summary>
/// Stores recordings under one sanitized folder per owner on the local file system.
/// </summary>
public sealed class LocalFileStorage : IRecordingStorage
{
    private const int MaxFolderNameLength = 120;
    private const int MaxFileNameLength = 180;
    private static readonly char[] InvalidNameCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private readonly string _rootPath;
    private readonly ILogger<LocalFileStorage> _logger;

    public LocalFileStorage(SyncOptions options, ILogger<LocalFileStorage> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            throw new ArgumentException("An output path is required.", nameof(options));
        }

        _rootPath = Path.GetFullPath(options.OutputPath);
        _logger = logger;
    }

    public LocalFileStorage(string outputPath)
        : this(
            new SyncOptions { OutputPath = outputPath },
            NullLogger<LocalFileStorage>.Instance)
    {
    }

    public Task<bool> ExistsAsync(string ownerName, string fileName)
    {
        try
        {
            return Task.FromResult(File.Exists(GetFilePath(ownerName, fileName)));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Ignoring an invalid output file name: {FileName}.", fileName);
            return Task.FromResult(false);
        }
    }

    public async Task SaveAsync(string ownerName, string fileName, Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var destination = GetFilePath(ownerName, fileName);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Could not determine the destination directory.");
        Directory.CreateDirectory(directory);

        // A temporary file prevents an interrupted transfer from looking like a complete
        // recording on the next run. The final name is only published after the copy ends.
        var temporaryPath = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await content.CopyToAsync(output).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }

            File.Move(temporaryPath, destination, overwrite: true);
            _logger.LogDebug("Saved {FileName} to {OwnerFolder}.", fileName, directory);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove temporary file {TemporaryFile}.", temporaryPath);
            }
        }
    }

    /// <summary>
    /// Ensures and returns the owner directory for callers that need to prepare a destination.
    /// </summary>
    public Task<string> EnsureOwnerFolderAsync(string ownerName)
    {
        var directory = GetOwnerDirectory(ownerName);
        Directory.CreateDirectory(directory);
        return Task.FromResult(directory);
    }

    public static string SanitizeFolderName(string? ownerName)
    {
        var value = (ownerName ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return "Unknown Owner";
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character < 32 || Array.IndexOf(InvalidNameCharacters, character) >= 0)
            {
                continue;
            }

            builder.Append(character);
        }

        var sanitized = builder.ToString().Trim().TrimEnd('.', ' ');
        if (sanitized.Length == 0)
        {
            return "Unknown Owner";
        }

        if (sanitized.Length > MaxFolderNameLength)
        {
            sanitized = sanitized[..MaxFolderNameLength].TrimEnd('.', ' ');
        }

        return IsReservedWindowsName(sanitized) ? $"_{sanitized}" : sanitized;
    }

    public static string SanitizeFileName(string fileName)
    {
        var value = (fileName ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return "recording";
        }

        // Do not allow a caller-supplied path to escape the owner directory.
        value = Path.GetFileName(value.Replace('\\', '/'));
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character < 32 || Array.IndexOf(InvalidNameCharacters, character) >= 0)
            {
                builder.Append('_');
                continue;
            }

            builder.Append(character);
        }

        var sanitized = builder.ToString().Trim().TrimEnd('.', ' ');
        if (sanitized.Length == 0 || sanitized is "." or "..")
        {
            return "recording";
        }

        if (sanitized.Length > MaxFileNameLength)
        {
            var extension = Path.GetExtension(sanitized);
            var extensionLength = Math.Min(extension.Length, 12);
            var stemLength = Math.Max(1, MaxFileNameLength - extensionLength);
            sanitized = string.Concat(
                sanitized[..stemLength].TrimEnd('.', ' '),
                extension[^extensionLength..]);
        }

        return sanitized;
    }

    private string GetOwnerDirectory(string ownerName)
    {
        var folderName = SanitizeFolderName(ownerName);
        var path = Path.GetFullPath(Path.Combine(_rootPath, folderName));
        var rootWithSeparator = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The owner folder is outside the configured output path.", nameof(ownerName));
        }

        return path;
    }

    private string GetFilePath(string ownerName, string fileName)
    {
        var ownerDirectory = GetOwnerDirectory(ownerName);
        var safeFileName = SanitizeFileName(fileName);
        return Path.GetFullPath(Path.Combine(ownerDirectory, safeFileName));
    }

    private static bool IsReservedWindowsName(string value)
    {
        var stem = Path.GetFileNameWithoutExtension(value);
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               (stem.Length == 4 && stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && char.IsDigit(stem[3])) ||
               (stem.Length == 4 && stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase) && char.IsDigit(stem[3]));
    }
}
