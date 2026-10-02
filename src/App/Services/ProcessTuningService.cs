using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class ProcessTuningService(EcoQosStateStore ecoQosStateStore)
{
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessSetInformation = 0x0200;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessPowerThrottling = 4;
    private const uint PowerThrottlingVersion = 1;
    private const uint ExecutionSpeed = 0x1;
    private const int MaxSelection = 20;

    public Task<ProcessTuningResult> TrimWorkingSetsAsync(
        IReadOnlyCollection<ValidatedProcessTarget> targets) =>
        Task.Run(() => ExecuteTrim(targets));

    public Task<ProcessTuningResult> ApplyEcoQosAsync(
        IReadOnlyCollection<ValidatedProcessTarget> targets) =>
        Task.Run(() => ExecuteEcoQosApply(targets));

    public Task<ProcessTuningResult> RestoreEcoQosAsync(
        IReadOnlyCollection<int> processIds) =>
        Task.Run(() => ExecuteEcoQosRestore(processIds));

    private static ProcessTuningResult ExecuteTrim(
        IReadOnlyCollection<ValidatedProcessTarget> targets)
    {
        var items = NormalizeTargets(targets)
            .Select(TrimOne)
            .ToArray();
        return Summarize(items);
    }

    private static ProcessTuningItem TrimOne(
        ValidatedProcessTarget target)
    {
        var processId = target.ProcessId;
        try
        {
            using var process = Process.GetProcessById(processId);
            var candidate = ReadCandidate(process);
            ValidateCandidate(process, candidate, requireBackground: true);

            var before = candidate.WorkingSetBytes;
            using var handle = OpenProcess(
                ProcessSetQuota | ProcessQueryInformation,
                false,
                processId);

            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            ValidateHandleIdentity(handle, target, "MemoryTrim");

            if (!EmptyWorkingSet(handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            Thread.Sleep(80);
            process.Refresh();
            var after = Math.Max(0, process.WorkingSet64);

            return new ProcessTuningItem(
                processId,
                candidate.Name,
                true,
                null,
                before,
                after,
                null,
                null);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            InvalidOperationException or
            Win32Exception or
            NotSupportedException)
        {
            return FailedItem(processId, ex.Message);
        }
    }

    private ProcessTuningItem ApplyEcoQosOne(
        ValidatedProcessTarget target)
    {
        var processId = target.ProcessId;
        var baselineCreated = false;
        var mutationApplied = false;

        try
        {
            using var process = Process.GetProcessById(processId);
            var candidate = ReadCandidate(process);
            ValidateCandidate(process, candidate, requireBackground: true);

            using var handle = OpenProcess(
                ProcessSetInformation | ProcessQueryLimitedInformation,
                false,
                processId);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            ValidateHandleIdentity(handle, target, "EcoQoS");

            var before = GetPowerThrottlingState(handle);
            var originalEnabled =
                (before.StateMask & ExecutionSpeed) != 0;
            baselineCreated = ecoQosStateStore.SaveBaseline(
                new EcoQosOriginalState(
                    processId,
                    candidate.Name,
                    target.StartTime,
                    before.ControlMask,
                    before.StateMask,
                    DateTimeOffset.Now));

            // SetProcessInformation replaces the whole throttling state, so
            // every policy bit the process already controls is carried over.
            var desired = new ProcessPowerThrottlingState
            {
                Version = PowerThrottlingVersion,
                ControlMask = before.ControlMask | ExecutionSpeed,
                StateMask = before.StateMask | ExecutionSpeed
            };
            SetPowerThrottlingState(handle, desired);
            mutationApplied = true;

            var after = GetPowerThrottlingState(handle);
            return new ProcessTuningItem(
                processId,
                candidate.Name,
                true,
                null,
                null,
                null,
                originalEnabled,
                (after.StateMask & ExecutionSpeed) != 0);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            InvalidOperationException or
            Win32Exception or
            NotSupportedException or
            IOException or
            UnauthorizedAccessException)
        {
            if (baselineCreated && !mutationApplied)
                TryRemoveBaseline(processId);

            return FailedItem(
                processId,
                ex is IOException or UnauthorizedAccessException
                    ? "No se pudo persistir el snapshot de rollback; EcoQoS no se aplicó. " + ex.Message
                    : ex.Message);
        }
    }

    private ProcessTuningItem RestoreEcoQosOne(int processId)
    {
        try
        {
            if (!ecoQosStateStore.TryGet(processId, out var original))
                throw new InvalidOperationException(
                    "No existe snapshot EcoQoS previo para este PID.");

            if (IsSameInstanceRunning(original) == false)
            {
                // The throttled instance is gone, so there is nothing left
                // to restore and the snapshot can never apply again.
                TryRemoveBaseline(processId);
                throw new InvalidOperationException(
                    "El proceso ya no existe o el PID fue reutilizado; rollback bloqueado y snapshot obsoleto descartado.");
            }

            using var process = Process.GetProcessById(processId);
            var candidate = ReadCandidate(process);

            if (!string.Equals(
                    candidate.Name,
                    original.Name,
                    StringComparison.OrdinalIgnoreCase) ||
                GetProcessStartTime(process) != original.ProcessStartTime)
            {
                throw new InvalidOperationException(
                    "El PID fue reutilizado o cambió de proceso; rollback bloqueado.");
            }

            ValidateCandidate(process, candidate, requireBackground: false);

            using var handle = OpenProcess(
                ProcessSetInformation | ProcessQueryLimitedInformation,
                false,
                processId);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            ValidateHandleIdentity(
                handle,
                new ValidatedProcessTarget(
                    processId,
                    original.Name,
                    original.ProcessStartTime),
                "EcoQoS rollback");

            var before = GetPowerThrottlingState(handle);

            // Restore only the ExecutionSpeed policy to its recorded
            // control/state pair. A process that was system-managed returns
            // to system-managed instead of being pinned to HighQoS, and any
            // other policy bit keeps its current value.
            var desired = new ProcessPowerThrottlingState
            {
                Version = PowerThrottlingVersion,
                ControlMask =
                    (before.ControlMask & ~ExecutionSpeed) |
                    (original.ControlMask & ExecutionSpeed),
                StateMask =
                    (before.StateMask & ~ExecutionSpeed) |
                    (original.StateMask & original.ControlMask & ExecutionSpeed)
            };
            SetPowerThrottlingState(handle, desired);

            var after = GetPowerThrottlingState(handle);
            var snapshotRemoved = TryRemoveBaseline(processId);

            return new ProcessTuningItem(
                processId,
                candidate.Name,
                true,
                snapshotRemoved
                    ? null
                    : "Estado restaurado; el snapshot persistido no pudo eliminarse y se conserva.",
                null,
                null,
                (before.StateMask & ExecutionSpeed) != 0,
                (after.StateMask & ExecutionSpeed) != 0);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            InvalidOperationException or
            Win32Exception or
            NotSupportedException)
        {
            return FailedItem(processId, ex.Message);
        }
    }

    private bool TryRemoveBaseline(int processId)
    {
        try
        {
            ecoQosStateStore.Remove(processId);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private ProcessTuningResult ExecuteEcoQosApply(
        IReadOnlyCollection<ValidatedProcessTarget> targets)
    {
        var items = NormalizeTargets(targets)
            .Select(ApplyEcoQosOne)
            .ToArray();
        PruneObsoleteSnapshots();
        return Summarize(items);
    }

    private ProcessTuningResult ExecuteEcoQosRestore(
        IReadOnlyCollection<int> processIds)
    {
        var items = Normalize(processIds)
            .Select(RestoreEcoQosOne)
            .ToArray();
        PruneObsoleteSnapshots();
        return Summarize(items);
    }

    /// <summary>
    /// Drops snapshots whose process instance has exited. They cannot be
    /// restored and would otherwise accumulate in the state file forever.
    /// </summary>
    private void PruneObsoleteSnapshots()
    {
        foreach (var state in ecoQosStateStore.Snapshot())
        {
            if (IsSameInstanceRunning(state) == false)
                TryRemoveBaseline(state.ProcessId);
        }
    }

    /// <summary>
    /// True when the recorded process instance is still running, false when
    /// it has exited or its PID now belongs to another process, and null
    /// when Windows does not allow the identity to be verified.
    /// </summary>
    public static bool? IsSameInstanceRunning(EcoQosOriginalState state)
    {
        try
        {
            using var process = Process.GetProcessById(state.ProcessId);
            if (process.HasExited)
                return false;

            return string.Equals(
                       process.ProcessName,
                       state.Name,
                       StringComparison.OrdinalIgnoreCase) &&
                   new DateTimeOffset(process.StartTime.ToUniversalTime()) ==
                   state.ProcessStartTime;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
            Win32Exception or
            NotSupportedException)
        {
            return null;
        }
    }

    private static ValidatedProcessTarget[] NormalizeTargets(
        IReadOnlyCollection<ValidatedProcessTarget> targets)
    {
        if (targets.Count is < 1 or > MaxSelection)
            throw new InvalidOperationException(
                $"Seleccione entre 1 y {MaxSelection} procesos.");

        if (targets.Any(target =>
                target.ProcessId <= 0 ||
                string.IsNullOrWhiteSpace(target.Name) ||
                target.StartTime == default))
        {
            throw new InvalidOperationException(
                "La identidad validada del proceso es inválida.");
        }

        var normalized = targets
            .GroupBy(target => target.ProcessId)
            .Select(group => group.First())
            .ToArray();

        if (normalized.Length is < 1 or > MaxSelection)
            throw new InvalidOperationException(
                $"Seleccione entre 1 y {MaxSelection} procesos únicos.");

        return normalized;
    }

    private static int[] Normalize(
        IReadOnlyCollection<int> processIds)
    {
        if (processIds.Count is < 1 or > MaxSelection)
            throw new InvalidOperationException(
                $"Seleccione entre 1 y {MaxSelection} procesos.");

        if (processIds.Any(id => id <= 0))
            throw new InvalidOperationException(
                "Todos los PIDs seleccionados deben ser positivos.");

        var normalized = processIds
            .Distinct()
            .ToArray();

        if (normalized.Length is < 1 or > MaxSelection)
            throw new InvalidOperationException(
                $"Seleccione entre 1 y {MaxSelection} procesos únicos.");

        return normalized;
    }

    private static DateTimeOffset GetProcessStartTime(
        Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
            NotSupportedException or
            System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "No se pudo verificar la identidad temporal del proceso.",
                ex);
        }
    }

    private static ProcessCandidate ReadCandidate(Process process)
    {
        return new ProcessCandidate(
            process.Id,
            string.IsNullOrWhiteSpace(process.ProcessName)
                ? "Unknown"
                : process.ProcessName,
            Math.Max(0, process.WorkingSet64),
            process.MainWindowHandle != IntPtr.Zero);
    }

    private static void ValidateCandidate(
        Process process,
        ProcessCandidate candidate,
        bool requireBackground)
    {
        if (process.HasExited ||
            !ProcessSafetyPolicy.IsEligible(
                candidate,
                requireBackground))
        {
            throw new InvalidOperationException(
                "Proceso no elegible por la política de seguridad.");
        }

        using var current = Process.GetCurrentProcess();
        if (process.SessionId != current.SessionId)
            throw new InvalidOperationException(
                "Proceso de otra sesión; operación bloqueada.");
    }
    private static void ValidateHandleIdentity(
        SafeProcessHandle handle,
        ValidatedProcessTarget expected,
        string operation)
    {
        if (!GetProcessTimes(
                handle,
                out var creation,
                out _,
                out _,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var fileTime =
            ((long)creation.HighDateTime << 32) |
            creation.LowDateTime;
        var actualStart = new DateTimeOffset(
            DateTime.FromFileTimeUtc(fileTime));

        if (actualStart.UtcTicks != expected.StartTime.UtcTicks)
        {
            throw new InvalidOperationException(
                $"{operation}: el PID {expected.ProcessId} fue reutilizado; operación bloqueada.");
        }
    }

    private static ProcessPowerThrottlingState GetPowerThrottlingState(
        SafeProcessHandle handle)
    {
        // The kernel rejects the query with ERROR_INVALID_PARAMETER unless
        // the caller supplies the structure version up front.
        var state = new ProcessPowerThrottlingState
        {
            Version = PowerThrottlingVersion
        };
        if (!GetProcessInformation(
                handle,
                ProcessPowerThrottling,
                ref state,
                (uint)Marshal.SizeOf<ProcessPowerThrottlingState>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return state;
    }

    private static void SetPowerThrottlingState(
        SafeProcessHandle handle,
        ProcessPowerThrottlingState state)
    {
        if (!SetProcessInformation(
                handle,
                ProcessPowerThrottling,
                ref state,
                (uint)Marshal.SizeOf<ProcessPowerThrottlingState>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static ProcessTuningResult Summarize(
        IReadOnlyList<ProcessTuningItem> items)
    {
        var succeeded = items.Count(item => item.Success);
        return new ProcessTuningResult(
            items.Count,
            succeeded,
            items.Count - succeeded,
            items);
    }

    private static ProcessTuningItem FailedItem(
        int processId,
        string error) =>
        new(
            processId,
            "Unknown",
            false,
            error,
            null,
            null,
            null,
            null);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(
        SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(
        SafeProcessHandle process,
        uint processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        SafeProcessHandle process,
        uint processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        uint processInformationSize);
}
