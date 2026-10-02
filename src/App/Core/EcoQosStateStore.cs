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

            _states[state.ProcessId] = state;
            Persist();
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
            if (_states.Remove(processId))
                Persist();
        }
    }

    public IReadOnlyList<EcoQosOriginalState> Snapshot()
    {
        lock (_gate)
            return [.. _states.Values.OrderBy(item => item.Name)];
    }
    private void Persist()
    {
        var temp = _path + ".tmp";
        var json = JsonSerializer.Serialize(
            _states.Values.ToArray(),
            HostBridge.JsonOptions);
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }

    private static Dictionary<int, EcoQosOriginalState> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];

            var json = File.ReadAllText(path);
            var values = JsonSerializer.Deserialize<EcoQosOriginalState[]>(
                json,
                HostBridge.JsonOptions)
                ?? [];

            return values
                .GroupBy(item => item.ProcessId)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(item => item.CapturedAt).First());
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
