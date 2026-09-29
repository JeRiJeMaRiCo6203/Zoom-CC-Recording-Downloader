using System.Net;
using CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using ZoomRecordingSync.Clients;
using ZoomRecordingSync.Options;
using ZoomRecordingSync.Services;
using ZoomRecordingSync.Storage;

namespace ZoomRecordingSync;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var parser = new Parser(settings =>
        {
            settings.HelpWriter = Console.Out;
            settings.CaseInsensitiveEnumValues = true;
        });

        ParserResult<CommandLineArguments> parsed;
        var normalizedArgs = NormalizeBooleanFlags(args);
        try
        {
            parsed = parser.ParseArguments<CommandLineArguments>(normalizedArgs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not parse command-line arguments: {ex.Message}");
            return 2;
        }

        if (parsed.Errors.Any(error => error.Tag is ErrorType.HelpRequestedError or ErrorType.VersionRequestedError))
        {
            return 0;
        }

        if (parsed is not Parsed<CommandLineArguments> parsedArguments)
        {
            foreach (var error in parsed.Errors)
            {
                Console.Error.WriteLine(error.ToString());
            }

            return 2;
        }

        var commandLine = parsedArguments.Value;

        HostApplicationBuilder builder;
        ZoomApiOptions apiOptions;
        SyncOptions syncOptions;
        try
        {
            builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = normalizedArgs,
                ContentRootPath = AppContext.BaseDirectory
            });

            apiOptions = OptionsFactory.CreateZoomApiOptions(builder.Configuration, commandLine);
            syncOptions = OptionsFactory.CreateSyncOptions(builder.Configuration, commandLine);
            ValidateApiOptions(apiOptions);
            syncOptions.Validate();
            EnsureStorageModeIsAvailable(syncOptions.StorageMode);
            ConfigureLogging(builder.Configuration);
            builder.Logging.ClearProviders();
            builder.Logging.AddFilter("Microsoft", LogLevel.Error);
            builder.Logging.AddFilter("System.Net.Http", LogLevel.Error);
            builder.Services.AddSerilog(Log.Logger, dispose: false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Log.CloseAndFlush();
            return 2;
        }

        builder.Services.AddSingleton(apiOptions);
        builder.Services.AddSingleton(syncOptions);
        builder.Services.AddSingleton(TimeProvider.System);
        AddHttpClients(builder.Services);

        builder.Services.AddSingleton<ZoomAuthClient>(serviceProvider =>
            new ZoomAuthClient(
                serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("ZoomAuth"),
                serviceProvider.GetRequiredService<ZoomApiOptions>(),
                serviceProvider.GetRequiredService<ILogger<ZoomAuthClient>>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<ZoomRecordingsClient>(serviceProvider =>
            new ZoomRecordingsClient(
                serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("ZoomRecordings"),
                serviceProvider.GetRequiredService<ZoomAuthClient>(),
                serviceProvider.GetRequiredService<ZoomApiOptions>(),
                serviceProvider.GetRequiredService<ILogger<ZoomRecordingsClient>>()));
        builder.Services.AddSingleton<RecordingDownloadClient>(serviceProvider =>
            new RecordingDownloadClient(
                serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("ZoomDownload"),
                serviceProvider.GetRequiredService<ZoomApiOptions>(),
                serviceProvider.GetRequiredService<ILogger<RecordingDownloadClient>>(),
                serviceProvider.GetRequiredService<ZoomAuthClient>()));
        // Storage selection lives at the composition root. A future cloud provider can
        // replace this registration without changing the sync or download components.
        builder.Services.AddSingleton<IRecordingStorage, LocalFileStorage>();
        builder.Services.AddSingleton<RecordingSyncService>();

        using var host = builder.Build();
        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };
        Console.CancelKeyPress += handler;

        try
        {
            var service = host.Services.GetRequiredService<RecordingSyncService>();
            var result = await service
                .RunAsync(syncOptions, cancellationSource.Token)
                .ConfigureAwait(false);
            return result.HasFailures ? 1 : 0;
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            Log.Warning("Recording sync was cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Log.Fatal(
                "Recording sync failed ({ErrorType}): {Message}",
                ex.GetType().Name,
                ex.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            Log.CloseAndFlush();
        }
    }

    private static string[] NormalizeBooleanFlags(IEnumerable<string> args)
    {
        var normalized = new List<string>();
        foreach (var argument in args)
        {
            normalized.Add(argument);
            if (argument is "--download-transcripts" or "--force")
            {
                normalized.Add("true");
            }
        }

        return normalized.ToArray();
    }

    private static void AddHttpClients(IServiceCollection services)
    {
        services.AddHttpClient("ZoomAuth", ConfigureHttpClient)
            .ConfigurePrimaryHttpMessageHandler(CreateHttpHandler);
        services.AddHttpClient("ZoomRecordings", ConfigureHttpClient)
            .ConfigurePrimaryHttpMessageHandler(CreateHttpHandler);
        services.AddHttpClient("ZoomDownload", ConfigureHttpClient)
            .ConfigurePrimaryHttpMessageHandler(CreateHttpHandler);
    }

    private static HttpMessageHandler CreateHttpHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    };

    private static void ConfigureHttpClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(100);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZoomRecordingSync/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    private static void ConfigureLogging(IConfiguration configuration)
    {
        var configuredDirectory = configuration["Logging:Directory"];
        var logDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "logs")
            : Path.GetFullPath(configuredDirectory);
        Directory.CreateDirectory(logDirectory);

        var minimumLevel = configuration["Serilog:MinimumLevel"] switch
        {
            "Debug" => LogEventLevel.Debug,
            "Warning" => LogEventLevel.Warning,
            "Error" => LogEventLevel.Error,
            "Fatal" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Error)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Error)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(logDirectory, "zoom-recording-sync-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true,
                outputTemplate: "{Timestamp:o} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static void ValidateApiOptions(ZoomApiOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId) ||
            string.IsNullOrWhiteSpace(options.ClientSecret) ||
            string.IsNullOrWhiteSpace(options.AccountId))
        {
            throw new InvalidOperationException(
                "Zoom client_id, client_secret, and account_id are required. Configure them via CLI, environment variables, or a secret store.");
        }

        if (options.RetryAttempts < 1)
        {
            throw new InvalidOperationException("retry_attempts must be at least 1.");
        }

        if (options.TokenRefreshBufferSeconds < 0)
        {
            throw new InvalidOperationException("token_refresh_buffer_seconds cannot be negative.");
        }
    }

    private static void EnsureStorageModeIsAvailable(string storageMode)
    {
        if (!storageMode.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"storage_mode '{storageMode}' is reserved and is not implemented. Use 'local' for this version.");
        }
    }
}
