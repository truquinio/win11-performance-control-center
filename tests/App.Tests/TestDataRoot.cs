using Win11PerformanceControlCenter.App.Core;

namespace App.Tests;

internal sealed class TestDataRoot : IDisposable
{
    public TestDataRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "WPCC-Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public HostBridge CreateBridge() =>
        HostBridge.CreateDefault(Path);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Test cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup only.
        }
    }
}
