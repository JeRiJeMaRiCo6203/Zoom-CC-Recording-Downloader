namespace ZoomRecordingSync.Models;

public sealed record DownloadResult(
    string OwnerName,
    string FileName,
    bool Skipped,
    long Bytes = 0)
{
    public static DownloadResult Skip(string ownerName, string fileName) =>
        new(ownerName, fileName, true);

    public static DownloadResult Saved(string ownerName, string fileName, long bytes) =>
        new(ownerName, fileName, false, bytes);
}
