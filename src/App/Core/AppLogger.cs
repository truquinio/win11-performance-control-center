using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Win11PerformanceControlCenter.App.Core;

public sealed class AppLogger : IDisposable
{
    private const int DefaultMaxLogBytes = 2 * 1024 * 1024;
    private const int MaxArchives = 5;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly string _lockPath;

    public long MaxLogBytes { get; init; } = DefaultMaxLogBytes;

    public AppLogger(string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            AppPaths.EnsureDirectories();
            _path = AppPaths.AppLog;
        }
        else
        {
            _path = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
        }

        _lockPath = _path + ".lock";
    }

    public async Task<bool> TryWriteAsync(
        string actionId,
        string status,
        object? data = null,
        string? operationId = null)
    {
        try
        {
            await WriteAsync(actionId, status, data, operationId);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public async Task WriteAsync(
        string actionId,
        string status,
        object? data = null,
        string? operationId = null)
    {
        var entry = new
        {
            timestamp = DateTimeOffset.Now,
            operationId,
            actionId,
            status,
            data
        };
        var line = JsonSerializer.Serialize(entry, HostBridge.JsonOptions) + Environment.NewLine;

        await _gate.WaitAsync();
        try
        {
            await using var crossProcessLock =
                await AcquireCrossProcessLockAsync();

            await File.AppendAllTextAsync(_path, line);
            TryRotate();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FileStream> AcquireCrossProcessLockAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (true)
        {
            try
            {
                return new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(40);
            }
        }
    }

    private void TryRotate()
    {
        try
        {
            RotateIfNeeded();
        }
        catch (IOException)
        {
            // The entry is already durable. Another reader can hold the
            // active log; rotation is retried on the next write.
        }
        catch (UnauthorizedAccessException)
        {
            // Rotation is maintenance and must not fail the audited action.
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxLogBytes) return;

        var archive = Path.Combine(
            info.DirectoryName!,
            "app-" +
            DateTime.UtcNow.ToString(
                "yyyyMMdd-HHmmss-fff",
                CultureInfo.InvariantCulture) + "-" +
            Guid.NewGuid().ToString("N")[..8] +
            ".jsonl");
        File.Move(_path, archive, false);

        // Archive names start with a sortable timestamp. Creation time is not
        // reliable here: NTFS tunnelling hands the old creation time to the
        // next app.jsonl, so several archives can share it.
        var old = new DirectoryInfo(info.DirectoryName!)
            .GetFiles("app-*.jsonl")
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(MaxArchives);
        foreach (var file in old)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // Retention cleanup is best-effort.
            }
            catch (UnauthorizedAccessException)
            {
                // A log can still be held by another process.
            }
        }
    }

    public void Dispose() => _gate.Dispose();
}
