using System.ComponentModel;
using System.Diagnostics;
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
        IReadOnlyCollection<int> processIds) =>
        Task.Run(() => ExecuteTrim(processIds));

    public Task<ProcessTuningResult> ApplyEcoQosAsync(
        IReadOnlyCollection<int> processIds) =>
        Task.Run(() => ExecuteEcoQosApply(processIds));

    public Task<ProcessTuningResult> RestoreEcoQosAsync(
        IReadOnlyCollection<int> processIds) =>
        Task.Run(() => ExecuteEcoQosRestore(processIds));

    private static ProcessTuningResult ExecuteTrim(
        IReadOnlyCollection<int> processIds)
    {
        var items = new List<ProcessTuningItem>();
        foreach (var processId in Normalize(processIds))
        {
            items.Add(TrimOne(processId));
        }
        return Summarize(items);
    }
    private static ProcessTuningItem TrimOne(int processId)
    {
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

    private ProcessTuningItem ApplyEcoQosOne(int processId)
    {
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

            var before = GetPowerThrottlingState(handle);
            var originalEnabled =
                (before.StateMask & ExecutionSpeed) != 0;
            baselineCreated = ecoQosStateStore.SaveBaseline(
                new EcoQosOriginalState(
                    processId,
                    candidate.Name,
                    GetProcessStartTime(process),
                    before.ControlMask,
                    before.StateMask,
                    DateTimeOffset.Now));

            var desired = new ProcessPowerThrottlingState
            {
                Version = PowerThrottlingVersion,
                ControlMask = ExecutionSpeed,
                StateMask = ExecutionSpeed
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
            NotSupportedException)
        {
            if (baselineCreated && !mutationApplied)
                ecoQosStateStore.Remove(processId);

            return FailedItem(processId, ex.Message);
        }
    }

    private ProcessTuningItem RestoreEcoQosOne(int processId)
    {
        try
        {
            if (!ecoQosStateStore.TryGet(processId, out var original))
                throw new InvalidOperationException(
                    "No existe snapshot EcoQoS previo para este PID.");

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

            var before = GetPowerThrottlingState(handle);
            var originalEnabled =
                (original.StateMask & ExecutionSpeed) != 0;
            var desired = new ProcessPowerThrottlingState
            {
                Version = PowerThrottlingVersion,
                ControlMask = ExecutionSpeed,
                StateMask = originalEnabled ? ExecutionSpeed : 0
            };
            SetPowerThrottlingState(handle, desired);

            var after = GetPowerThrottlingState(handle);
            ecoQosStateStore.Remove(processId);

            return new ProcessTuningItem(
                processId,
                candidate.Name,
                true,
                null,
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
    private ProcessTuningResult ExecuteEcoQosApply(
        IReadOnlyCollection<int> processIds)
    {
        var items = Normalize(processIds)
            .Select(ApplyEcoQosOne)
            .ToArray();
        return Summarize(items);
    }

    private ProcessTuningResult ExecuteEcoQosRestore(
        IReadOnlyCollection<int> processIds)
    {
        var items = Normalize(processIds)
            .Select(RestoreEcoQosOne)
            .ToArray();
        return Summarize(items);
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
    private static ProcessPowerThrottlingState GetPowerThrottlingState(
        SafeProcessHandle handle)
    {
        if (!GetProcessInformation(
                handle,
                ProcessPowerThrottling,
                out var state,
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
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(
        SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(
        SafeProcessHandle process,
        uint processInformationClass,
        out ProcessPowerThrottlingState processInformation,
        uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        SafeProcessHandle process,
        uint processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        uint processInformationSize);
}
