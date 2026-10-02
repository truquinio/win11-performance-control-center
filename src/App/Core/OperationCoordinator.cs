namespace Win11PerformanceControlCenter.App.Core;

public sealed class OperationCoordinator
{
    private readonly Lock _gate = new();
    private string _state = "IDLE";
    private string? _activeAction;

    public string State
    {
        get { lock (_gate) return _state; }
    }

    public string? ActiveAction
    {
        get { lock (_gate) return _activeAction; }
    }

    public IDisposable Begin(string actionId, string state = "ANALYZING")
    {
        lock (_gate)
        {
            if (_state is not "IDLE")
                throw new InvalidOperationException(
                    $"Hay una operación activa ({_activeAction ?? _state}).");

            _state = state;
            _activeAction = actionId;
            return new Lease(this);
        }
    }

    private void End()
    {
        lock (_gate)
        {
            _activeAction = null;
            _state = "IDLE";
        }
    }

    private sealed class Lease(OperationCoordinator owner) : IDisposable
    {
        private OperationCoordinator? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.End();
        }
    }
}
