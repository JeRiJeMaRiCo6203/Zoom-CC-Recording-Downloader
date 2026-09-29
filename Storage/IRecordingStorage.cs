namespace ZoomRecordingSync.Storage;

public interface IRecordingStorage
{
    Task<bool> ExistsAsync(string ownerName, string fileName);

    Task SaveAsync(string ownerName, string fileName, Stream content);
}
