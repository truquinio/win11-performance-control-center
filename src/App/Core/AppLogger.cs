using System.IO;
using System.Text.Json;

namespace Win11PerformanceControlCenter.App.Core;

public sealed class AppLogger : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly string _lockPath;

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
            RotateIfNeeded();
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

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < 2 * 1024 * 1024) return;

        var archive = Path.Combine(
            info.DirectoryName!,
            "app-" +
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" +
            Guid.NewGuid().ToString("N")[..8] +
            ".jsonl");
        File.Move(_path, archive, false);

        var old = new DirectoryInfo(info.DirectoryName!)
            .GetFiles("app-*.jsonl")
            .OrderByDescending(file => file.CreationTimeUtc)
            .Skip(5);
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
