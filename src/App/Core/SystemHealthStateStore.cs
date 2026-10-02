namespace Win11PerformanceControlCenter.App.Core;

public sealed class SystemHealthStateStore
{
    private readonly Lock _gate = new();
    private string _integrityStatus = "NOT_EVALUATED";
    private string _driverStatus = "NOT_EVALUATED";
    private string _activationStatus = "NOT_EVALUATED";

    public (string Integrity, string Drivers, string Activation) Snapshot()
    {
        lock (_gate)
        {
            return (
                _integrityStatus,
                _driverStatus,
                _activationStatus);
        }
    }

    public void SetIntegrity(string status)
    {
        lock (_gate)
            _integrityStatus = Normalize(status);
    }

    public void SetDrivers(string status)
    {
        lock (_gate)
            _driverStatus = Normalize(status);
    }

    public void SetActivation(string status)
    {
        lock (_gate)
            _activationStatus = Normalize(status);
    }

    private static string Normalize(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "UNKNOWN";

        return status.Trim().ToUpperInvariant();
    }
}
