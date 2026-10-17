using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Win11PerformanceControlCenter.App.Core;

public sealed class AppLogger : IDisposable
{
    private const int DefaultMaxLogBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding RecordEncoding = new(false);
    private static readonly byte[] RecordSeparator =
        RecordEncoding.GetBytes(Environment.NewLine);

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

            await AppendRecordAsync(line);
            TryRotate();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task AppendRecordAsync(string line)
    {
        await using var stream = new FileStream(
            _path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous);

        if (stream.Length > 0)
        {
            // A process killed in the middle of a write leaves a record
            // without its line terminator. Appending straight after it would
            // fuse the next record into the same unreadable line.
            stream.Seek(-1, SeekOrigin.End);
            var last = new byte[1];
            if (await stream.ReadAsync(last) == 1 && last[0] != (byte)'\n')
            {
                stream.Seek(0, SeekOrigin.End);
                await stream.WriteAsync(RecordSeparator);
            }
        }

        stream.Seek(0, SeekOrigin.End);
        await stream.WriteAsync(RecordEncoding.GetBytes(line));
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

        // Keep all rotated audit journals. Recovery can depend on a STARTED
        // record in an older archive, and automatic pruning destroys evidence.
        // Any future retention/export policy must preserve the originals
        // until the user explicitly approves their disposal.
    }

    public void Dispose() => _gate.Dispose();
}
