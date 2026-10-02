namespace Win11PerformanceControlCenter.App.Core;

/// <summary>
/// Bounds read-only work so a provider that never answers (WMI, event log,
/// an unresponsive volume) cannot hold the global operation lease forever.
/// </summary>
public static class OperationWatchdog
{
    public static async Task<T> RunAsync<T>(
        Task<T> work,
        TimeSpan budget,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (budget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(budget));

        using var cts = new CancellationTokenSource();
        var delay = Task.Delay(budget, cts.Token);
        var first = await Task.WhenAny(work, delay);
        if (first == work)
        {
            await cts.CancelAsync();
            return await work;
        }

        // The abandoned work may still fault later; observe it so it never
        // surfaces as an unobserved task exception.
        _ = work.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        throw new TimeoutException(
            $"{operation}: la operación de lectura superó el tiempo máximo y fue abandonada.");
    }
}
