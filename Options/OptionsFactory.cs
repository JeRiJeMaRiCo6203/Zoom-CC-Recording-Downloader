using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace ZoomRecordingSync.Options;

/// <summary>
/// Builds options from appsettings.json, environment variables, and CLI values in that order.
/// </summary>
public static class OptionsFactory
{
    public static ZoomApiOptions CreateZoomApiOptions(
        IConfiguration configuration,
        CommandLineArguments commandLine)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(commandLine);

        var options = new ZoomApiOptions
        {
            ClientId = ReadString(
                configuration,
                "ZoomApi:client_id",
                "ZoomApi:ClientId",
                "client_id",
                "clientId",
                "CLIENT_ID",
                "ZOOM_CLIENT_ID",
                "ZOOM_API_CLIENT_ID") ?? string.Empty,
            ClientSecret = ReadString(
                configuration,
                "ZoomApi:client_secret",
                "ZoomApi:ClientSecret",
                "client_secret",
                "clientSecret",
                "CLIENT_SECRET",
                "ZOOM_CLIENT_SECRET",
                "ZOOM_API_CLIENT_SECRET") ?? string.Empty,
            AccountId = ReadString(
                configuration,
                "ZoomApi:account_id",
                "ZoomApi:AccountId",
                "account_id",
                "accountId",
                "ACCOUNT_ID",
                "ZOOM_ACCOUNT_ID",
                "ZOOM_API_ACCOUNT_ID") ?? string.Empty,
            OAuthTokenUrl = ReadString(
                configuration,
                "ZoomApi:oauth_token_url",
                "ZoomApi:OAuthTokenUrl",
                "oauth_token_url",
                "oauthTokenUrl",
                "ZOOM_OAUTH_TOKEN_URL") ?? "https://zoom.us/oauth/token",
            ApiBaseUrl = ReadString(
                configuration,
                "ZoomApi:api_base_url",
                "ZoomApi:ApiBaseUrl",
                "api_base_url",
                "apiBaseUrl",
                "ZOOM_API_BASE_URL") ?? "https://api.zoom.us",
            TokenRefreshBufferSeconds = ReadInt(
                configuration,
                60,
                "ZoomApi:token_refresh_buffer_seconds",
                "ZoomApi:TokenRefreshBufferSeconds",
                "token_refresh_buffer_seconds",
                "tokenRefreshBufferSeconds",
                "ZOOM_TOKEN_REFRESH_BUFFER_SECONDS"),
            RetryAttempts = ReadInt(
                configuration,
                3,
                "ZoomApi:retry_attempts",
                "ZoomApi:RetryAttempts",
                "retry_attempts",
                "retryAttempts",
                "ZOOM_RETRY_ATTEMPTS")
        };

        options.TrustedDownloadHostSuffixes = ReadStringList(
            configuration,
            new[] { "zoom.us", "zoom.com" },
            "ZoomApi:trusted_download_host_suffixes",
            "ZoomApi:TrustedDownloadHostSuffixes",
            "trusted_download_host_suffixes",
            "trustedDownloadHostSuffixes",
            "ZOOM_TRUSTED_DOWNLOAD_HOST_SUFFIXES");

        options.ClientId = commandLine.ClientId ?? ReadEnvironment(configuration, "ZOOM_CLIENT_ID", "client_id", "clientId") ?? options.ClientId;
        options.ClientSecret = commandLine.ClientSecret ?? ReadEnvironment(configuration, "ZOOM_CLIENT_SECRET", "client_secret", "clientSecret") ?? options.ClientSecret;
        options.AccountId = commandLine.AccountId ?? ReadEnvironment(configuration, "ZOOM_ACCOUNT_ID", "account_id", "accountId") ?? options.AccountId;
        options.OAuthTokenUrl = ReadEnvironment(configuration, "ZOOM_OAUTH_TOKEN_URL") ?? options.OAuthTokenUrl;
        options.ApiBaseUrl = ReadEnvironment(configuration, "ZOOM_API_BASE_URL") ?? options.ApiBaseUrl;
        options.RetryAttempts = commandLine.RetryAttempts ?? options.RetryAttempts;
        options.TokenRefreshBufferSeconds = ReadEnvironmentInt(
            configuration,
            options.TokenRefreshBufferSeconds,
            "ZOOM_TOKEN_REFRESH_BUFFER_SECONDS");

        return options;
    }

    public static SyncOptions CreateSyncOptions(
        IConfiguration configuration,
        CommandLineArguments commandLine)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(commandLine);

        var options = new SyncOptions
        {
            From = ReadDate(
                configuration,
                null,
                "ZoomRecordingSync:from",
                "ZoomRecordingSync:From",
                "Sync:from",
                "Sync:From",
                "from"),
            To = ReadDate(
                configuration,
                null,
                "ZoomRecordingSync:to",
                "ZoomRecordingSync:To",
                "Sync:to",
                "Sync:To",
                "to"),
            PageSize = ReadInt(
                configuration,
                50,
                "ZoomRecordingSync:page_size",
                "ZoomRecordingSync:PageSize",
                "Sync:page_size",
                "Sync:PageSize",
                "page_size",
                "pageSize",
                "PAGE_SIZE",
                "ZOOM_PAGE_SIZE"),
            OutputPath = ReadString(
                configuration,
                "ZoomRecordingSync:output_path",
                "ZoomRecordingSync:OutputPath",
                "Sync:output_path",
                "Sync:OutputPath",
                "output_path",
                "outputPath",
                "OUTPUT_PATH",
                "ZOOM_OUTPUT_PATH"),
            OwnerName = ReadString(
                configuration,
                "ZoomRecordingSync:owner_name",
                "ZoomRecordingSync:OwnerName",
                "Sync:owner_name",
                "Sync:OwnerName",
                "owner_name",
                "ownerName",
                "OWNER_NAME",
                "ZOOM_OWNER_NAME"),
            DownloadTranscripts = ReadBool(
                configuration,
                false,
                "ZoomRecordingSync:download_transcripts",
                "ZoomRecordingSync:DownloadTranscripts",
                "Sync:download_transcripts",
                "Sync:DownloadTranscripts",
                "download_transcripts",
                "downloadTranscripts",
                "DOWNLOAD_TRANSCRIPTS",
                "ZOOM_DOWNLOAD_TRANSCRIPTS"),
            StorageMode = ReadString(
                configuration,
                "ZoomRecordingSync:storage_mode",
                "ZoomRecordingSync:StorageMode",
                "Sync:storage_mode",
                "Sync:StorageMode",
                "storage_mode",
                "storageMode",
                "STORAGE_MODE",
                "ZOOM_STORAGE_MODE") ?? "local",
            Force = ReadBool(
                configuration,
                false,
                "ZoomRecordingSync:force",
                "ZoomRecordingSync:Force",
                "Sync:force",
                "Sync:Force",
                "force",
                "FORCE",
                "ZOOM_FORCE")
        };

        options.From = ParseDate(commandLine.From, "from") ?? options.From;
        options.To = ParseDate(commandLine.To, "to") ?? options.To;
        options.OutputPath = commandLine.OutputPath ?? commandLine.Output ?? ReadEnvironment(configuration, "ZOOM_OUTPUT_PATH") ?? options.OutputPath;
        options.PageSize = commandLine.PageSize ?? options.PageSize;
        options.OwnerName = commandLine.OwnerName ?? ReadEnvironment(configuration, "ZOOM_OWNER_NAME") ?? options.OwnerName;
        options.DownloadTranscripts = commandLine.DownloadTranscripts ?? options.DownloadTranscripts;
        options.StorageMode = commandLine.StorageMode ?? ReadEnvironment(configuration, "ZOOM_STORAGE_MODE") ?? options.StorageMode;
        options.Force = commandLine.Force ?? options.Force;

        return options;
    }

    private static string? ReadEnvironment(
        IConfiguration configuration,
        params string[] keys)
    {
        // Check real environment variables first so an environment alias always
        // overrides a value from appsettings.json, regardless of key ordering.
        foreach (var key in keys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        foreach (var key in keys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadString(IConfiguration configuration, params string[] keys) =>
        ReadEnvironment(configuration, keys);

    private static int ReadInt(IConfiguration configuration, int defaultValue, params string[] keys)
    {
        var value = ReadEnvironment(configuration, keys);
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new InvalidOperationException($"Configuration value '{keys[0]}' must be an integer.");
        }

        return result;
    }

    private static int ReadEnvironmentInt(
        IConfiguration configuration,
        int defaultValue,
        params string[] keys)
    {
        var value = ReadEnvironment(configuration, keys);
        if (value is null)
        {
            return defaultValue;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : defaultValue;
    }

    private static bool ReadBool(IConfiguration configuration, bool defaultValue, params string[] keys)
    {
        var value = ReadEnvironment(configuration, keys);
        if (value is null)
        {
            return defaultValue;
        }

        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        return value switch
        {
            "1" => true,
            "0" => false,
            _ => throw new InvalidOperationException($"Configuration value '{keys[0]}' must be true or false.")
        };
    }

    private static DateTime? ReadDate(
        IConfiguration configuration,
        DateTime? defaultValue,
        params string[] keys)
    {
        var value = ReadEnvironment(configuration, keys);
        return value is null ? defaultValue : ParseDate(value, keys[0]);
    }

    private static DateTime? ParseDate(string? value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTime.TryParseExact(
                value.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            throw new ArgumentException($"{optionName} must use yyyy-MM-dd format.");
        }

        return date;
    }

    private static IReadOnlyList<string> ReadStringList(
        IConfiguration configuration,
        IReadOnlyList<string> defaultValue,
        params string[] keys)
    {
        // Environment values must win over the array in appsettings.json.
        var environmentValue = ReadEnvironment(configuration, keys.Skip(1).ToArray());
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => item.Length > 0)
                .ToArray();
        }

        foreach (var sectionPath in keys.Where(key => key.Contains(':', StringComparison.Ordinal)))
        {
            var section = configuration.GetSection(sectionPath);
            var values = section.GetChildren()
                .Select(child => child.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .ToArray();
            if (values.Length > 0)
            {
                return values;
            }
        }

        return defaultValue;
    }
}
