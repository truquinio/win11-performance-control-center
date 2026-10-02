using System.IO;
using System.Text.Json;

namespace Win11PerformanceControlCenter.App.Core;

public sealed record EcoQosOriginalState(
    int ProcessId,
    string Name,
    DateTimeOffset ProcessStartTime,
    uint ControlMask,
    uint StateMask,
    DateTimeOffset CapturedAt);

public sealed class EcoQosStateStore
{
    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly Dictionary<int, EcoQosOriginalState> _states;

    public EcoQosStateStore(string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            AppPaths.EnsureDirectories();
            _path = AppPaths.EcoQosState;
        }
        else
        {
            _path = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
        }

        _states = Load(_path);
    }

    public bool SaveBaseline(EcoQosOriginalState state)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(state.ProcessId, out var existing) &&
                string.Equals(
                    existing.Name,
                    state.Name,
                    StringComparison.OrdinalIgnoreCase) &&
                existing.ProcessStartTime == state.ProcessStartTime)
            {
                return false;
            }

            // Persist first: a baseline that only exists in memory would let
            // a later apply proceed without a durable rollback snapshot.
            var next = new Dictionary<int, EcoQosOriginalState>(_states)
            {
                [state.ProcessId] = state
            };
            Persist(next.Values);
            _states[state.ProcessId] = state;
            return true;
        }
    }

    public bool TryGet(int processId, out EcoQosOriginalState state)
    {
        lock (_gate)
            return _states.TryGetValue(processId, out state!);
    }

    public void Remove(int processId)
    {
        lock (_gate)
        {
            if (!_states.ContainsKey(processId))
                return;

            var next = new Dictionary<int, EcoQosOriginalState>(_states);
            next.Remove(processId);
            Persist(next.Values);
            _states.Remove(processId);
        }
    }

    public IReadOnlyList<EcoQosOriginalState> Snapshot()
    {
        lock (_gate)
            return [.. _states.Values.OrderBy(item => item.Name)];
    }

    private void Persist(IEnumerable<EcoQosOriginalState> states)
    {
        var temp = _path + ".tmp";
        var backup = _path + ".bak";
        var json = JsonSerializer.SerializeToUtf8Bytes(
            states.ToArray(),
            HostBridge.JsonOptions);

        using (var stream = new FileStream(
                   temp,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.Write(json);
            // The rename below is only crash-safe once the bytes are on disk.
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, _path, overwrite: true);
        TryRefreshBackup(_path, backup);
    }

    private static Dictionary<int, EcoQosOriginalState> Load(string path)
    {
        return TryLoad(path) ??
            TryLoad(path + ".bak") ??
            [];
    }

    private static Dictionary<int, EcoQosOriginalState>? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            var values = JsonSerializer.Deserialize<EcoQosOriginalState[]>(
                json,
                HostBridge.JsonOptions);

            if (values is null)
                return null;

            return values
                .Where(IsUsable)
                .GroupBy(item => item.ProcessId)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(item => item.CapturedAt).First());
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsUsable(EcoQosOriginalState? item) =>
        item is not null &&
        item.ProcessId > 0 &&
        !string.IsNullOrWhiteSpace(item.Name) &&
        item.ProcessStartTime != default;

    private static void TryRefreshBackup(
        string source,
        string backup)
    {
        try
        {
            File.Copy(source, backup, overwrite: true);
        }
        catch (IOException)
        {
            // Primary state is already durable; backup refresh is best-effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Local policy may block the secondary backup only.
        }
    }
}
