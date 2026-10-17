using System.IO;
using System.Text.Json;

namespace Win11PerformanceControlCenter.App.Services;

/// <summary>
/// Audit journal for native Windows Recycle Bin operations. The manifest records
/// original paths and logical sizes, not bytes physically freed on the volume.
/// </summary>
public sealed class StorageRecycleJournal(string path)
{
    private readonly object gate = new();

    public void Append(string operationId, string originalPath, long logicalBytes, string status)
    {
        if (status is not ("INTENT" or "MOVED"))
            throw new ArgumentOutOfRangeException(nameof(status));

        var entry = new
        {
            operationId,
            originalPath,
            logicalBytes,
            status,
            recordedAtUtc = DateTimeOffset.UtcNow,
            restoration = "Windows Recycle Bin; restore manually from Explorer"
        };
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new IOException("Manifest parent directory missing.");
        var json = JsonSerializer.Serialize(entry);
        lock (gate)
        {
            Directory.CreateDirectory(folder);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
    }
}
