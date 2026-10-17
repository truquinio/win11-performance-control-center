namespace Win11PerformanceControlCenter.App.Core;

public sealed class OperationCoordinator
{
    private readonly Lock _gate = new();
    private string _state = "IDLE";
    private string? _activeAction;
    private long _leaseSequence;
    private long _activeLeaseId;

    public string State
    {
        get { lock (_gate) return _state; }
    }

    public string? ActiveAction
    {
        get { lock (_gate) return _activeAction; }
    }

    public OperationLease Begin(string actionId, string state = "ANALYZING")
    {
        lock (_gate)
        {
            if (_state is not "IDLE")
                throw new InvalidOperationException(
                    $"Hay una operación activa ({_activeAction ?? _state}).");

            _state = state;
            _activeAction = actionId;
            _activeLeaseId = ++_leaseSequence;
            return new OperationLease(this, _activeLeaseId);
        }
    }

    private void End(long leaseId)
    {
        lock (_gate)
        {
            if (_activeLeaseId != leaseId)
                return;

            _activeAction = null;
            _activeLeaseId = 0;
            _state = "IDLE";
        }
    }

    public sealed class OperationLease(
        OperationCoordinator owner,
        long leaseId) : IDisposable
    {
        private readonly object _gate = new();
        private OperationCoordinator? _owner = owner;
        private bool _retained;
        private bool _disposed;

        public void RetainUntil(Task work)
        {
            ArgumentNullException.ThrowIfNull(work);
            lock (_gate)
            {
                if (_owner is null || _disposed)
                    throw new ObjectDisposedException(nameof(OperationLease));
                if (_retained)
                    return;
                _retained = true;
            }

            _ = work.ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                        _ = task.Exception;
                    Release();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public void Dispose()
        {
            OperationCoordinator? owner = null;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                if (!_retained)
                {
                    owner = _owner;
                    _owner = null;
                }
            }

            owner?.End(leaseId);
        }

        private void Release()
        {
            OperationCoordinator? owner;
            lock (_gate)
            {
                owner = _owner;
                _owner = null;
            }

            owner?.End(leaseId);
        }
    }
}
