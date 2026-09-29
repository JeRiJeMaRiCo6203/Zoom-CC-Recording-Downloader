# Zoom Contact Center Recording Sync

A .NET 8 console application that downloads Zoom Contact Center voice recordings for a UTC date range. Recordings are stored locally by queue/owner name and can optionally include raw transcripts.

## Requirements

- .NET 8 SDK/runtime
- A Zoom Server-to-Server OAuth app with the Contact Center recording read scope
- A writable output directory

The OAuth app credentials are never written to the log. Supply the client secret with an environment variable or a secret store rather than committing it to `appsettings.json`. For local .NET development (`DOTNET_ENVIRONMENT=Development`), user-secrets are supported:

```bash
dotnet user-secrets set "ZoomApi:client_id" "..."
dotnet user-secrets set "ZoomApi:client_secret" "..."
dotnet user-secrets set "ZoomApi:account_id" "..."
```

## Configuration

`appsettings.json` contains non-secret defaults. The most useful settings are:

```json
{
  "ZoomApi": {
    "client_id": "",
    "client_secret": "",
    "account_id": ""
  },
  "ZoomRecordingSync": {
    "page_size": 50,
    "output_path": "/var/zoom-recordings",
    "download_transcripts": false,
    "storage_mode": "local",
    "force": false
  }
}
```

Supported environment variables include:

| Setting | Variables |
| --- | --- |
| Zoom client ID | `ZOOM_CLIENT_ID` (or `CLIENT_ID`) |
| Zoom client secret | `ZOOM_CLIENT_SECRET` (or `CLIENT_SECRET`) |
| Zoom account ID | `ZOOM_ACCOUNT_ID` (or `ACCOUNT_ID`) |
| API base URL | `ZOOM_API_BASE_URL` |
| OAuth token URL | `ZOOM_OAUTH_TOKEN_URL` |
| Output path | `ZOOM_OUTPUT_PATH` |
| Page size | `ZOOM_PAGE_SIZE` |
| Owner filter | `ZOOM_OWNER_NAME` |
| Transcript option | `ZOOM_DOWNLOAD_TRANSCRIPTS` |
| Storage mode | `ZOOM_STORAGE_MODE` |
| Force re-download | `ZOOM_FORCE` |

Configuration precedence is **CLI > environment > `appsettings.json`**. CLI options are intended for non-secret values; avoid putting `--client-secret` in scheduled command lines.

## Build and run

```bash
dotnet restore
dotnet build -c Release

export ZOOM_CLIENT_ID='...'
export ZOOM_CLIENT_SECRET='...'
export ZOOM_ACCOUNT_ID='...'

dotnet run --project ZoomRecordingSync.csproj -- \
  --from 2026-09-01 \
  --to 2026-09-23 \
  --output ./recordings \
  --page-size 100
```

`--to` is inclusive as a calendar date. The application sends an exclusive UTC end boundary at the following midnight, so the entire requested day is covered. `--force` re-downloads files even when the target already exists. `--download-transcripts` downloads `transcript_url` files when Zoom returns one; it can also be enabled in configuration.

To run a published DLL:

```bash
dotnet ZoomRecordingSync.dll \
  --from 2026-09-01 \
  --to 2026-09-23 \
  --output /srv/zoom-recordings
```

## Output layout

```text
{output_path}/
└── {sanitized_owner_name}/
    ├── {recording_id}_{yyyyMMdd_HHmmss}.mp3
    └── {recording_id}_{yyyyMMdd_HHmmss}.vtt
```

The extension is inferred from the response `Content-Type`, then the download URL/metadata, with an `.mp3` audio or `.vtt` transcript fallback. Owner and file names are sanitized for Windows and Linux. Writes use a temporary file and atomic replacement, so an interrupted run does not publish a partial recording.

## Reliability and security behavior

- The OAuth token is cached in memory and refreshed 60 seconds before expiry.
- A 401 from the recordings endpoint triggers one token refresh and one retry.
- Transient HTTP failures (408, 429, 5xx, timeouts, and network errors) use Polly exponential backoff, up to three total attempts; `Retry-After` is honored.
- Individual recording failures are logged and do not stop later recordings. The process returns exit code `1` when any recording/asset fails, `2` for invalid configuration/arguments, and `130` after cancellation.
- Download redirects are followed manually. The initial Zoom-provided URL receives the Bearer header over HTTPS; on every redirect, it is re-sent only to configured Zoom HTTPS host suffixes and omitted for third-party targets.
- The API query is fixed to automatic voice recordings owned by queues; a defensive service check skips an unexpected non-queue owner returned by Zoom.
- No retry queue is persisted between runs. Re-running is safe because existing target files are skipped unless `--force` is supplied.

## Cloud storage extension

`IRecordingStorage` is the storage seam. Implement it for Azure Blob, S3, or another provider and register the implementation in the composition root (`Program.cs`). Authentication, pagination, and download logic do not need to change. `cloud` storage mode is reserved and currently fails clearly rather than silently falling back to local disk.

## Scheduling

Run the same published DLL from Windows Task Scheduler or cron. Compute the date arguments in the wrapper script and provide credentials through the scheduler's protected environment or a secret store.
