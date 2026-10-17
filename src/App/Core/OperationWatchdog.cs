namespace Win11PerformanceControlCenter.App.Core;

/// <summary>
/// Bounds read-only work. At timeout the provider receives cancellation; work
/// that ignores it keeps its coordinator lease until it actually completes.
/// </summary>
public static class OperationWatchdog
{
    public static Task<T> RunAsync<T>(
        Task<T> work,
        TimeSpan budget,
        string operation,
        OperationCoordinator.OperationLease? lease = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(_ => work, budget, operation, lease);
    }

    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> workFactory,
        TimeSpan budget,
        string operation,
        OperationCoordinator.OperationLease? lease = null)
    {
        ArgumentNullException.ThrowIfNull(workFactory);
        if (budget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(budget));

        var workCancellation = new CancellationTokenSource();
        Task<T> work;
        try
        {
            work = workFactory(workCancellation.Token);
            ArgumentNullException.ThrowIfNull(work);
        }
        catch
        {
            workCancellation.Dispose();
            throw;
        }

        using var timeoutCancellation = new CancellationTokenSource();
        var delay = Task.Delay(budget, timeoutCancellation.Token);
        var first = await Task.WhenAny(work, delay);
        if (first == work || work.IsCompleted)
        {
            await timeoutCancellation.CancelAsync();
            workCancellation.Dispose();
            return await work;
        }

        // Keep the lease before signaling cancellation. Non-cooperative native
        // calls remain serialized until their actual completion.
        lease?.RetainUntil(work);
        await timeoutCancellation.CancelAsync();

        Exception? cancellationFailure = null;
        try
        {
            await workCancellation.CancelAsync();
        }
        catch (Exception ex)
        {
            cancellationFailure = ex;
        }
        DisposeCancellationWhenWorkCompletes(work, workCancellation);

        throw new TimeoutException(
            $"{operation}: la operación de lectura superó el tiempo máximo y recibió una solicitud de cancelación.",
            cancellationFailure);
    }

    private static void DisposeCancellationWhenWorkCompletes<T>(
        Task<T> work,
        CancellationTokenSource cancellation)
    {
        _ = work.ContinueWith(
            task =>
            {
                if (task.IsFaulted)
                    _ = task.Exception;
                cancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
