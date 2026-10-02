namespace Win11PerformanceControlCenter.App.Core;

public static class SingleInstanceGuard
{
    /// <summary>
    /// Tries to become the owner of the named single-instance mutex.
    /// Returns false, without throwing, whenever another instance already
    /// holds the name, including one this process is not allowed to open.
    /// </summary>
    public static bool TryAcquire(string name, out Mutex? mutex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        mutex = null;

        try
        {
            var candidate = new Mutex(
                initiallyOwned: true,
                name,
                out var createdNew);
            if (createdNew)
            {
                mutex = candidate;
                return true;
            }

            candidate.Dispose();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // The name exists but belongs to an instance started elevated:
            // its default ACL refuses a standard-integrity open. That still
            // means "already running", not a startup failure.
            return false;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The name is taken by a different kind of kernel object.
            return false;
        }
    }
}
