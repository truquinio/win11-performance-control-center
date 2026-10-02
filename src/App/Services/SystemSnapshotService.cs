using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Win11PerformanceControlCenter.App.Core;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class SystemSnapshotService(
    OperationCoordinator coordinator,
    SystemHealthStateStore healthState)
{
    public async Task<SystemSnapshot> CaptureAsync()
    {
        var cpuTask = SampleCpuAsync();
        var networkTask = SampleNetworkAsync();
        var (Total, Used, Available) = GetMemory();
        var disk = GetSystemDrive();

        await Task.WhenAll(cpuTask, networkTask);
        var (Integrity, Drivers, Activation) = healthState.Snapshot();

        return new SystemSnapshot(
            DateTimeOffset.Now,
            Math.Round(cpuTask.Result, 1),
            Total,
            Used,
            Available,
            disk.Name,
            disk.TotalSize,
            disk.AvailableFreeSpace,
            networkTask.Result,
            Integrity,
            Drivers,
            Activation,
            IsRebootPending(),
            Math.Max(0, Environment.TickCount64 / 1000),
            coordinator.State);
    }

    private static (ulong Total, ulong Used, ulong Available) GetMemory()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(ref status))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "No se pudo leer el estado de memoria física.");

        var used = status.TotalPhysical - status.AvailablePhysical;
        return (status.TotalPhysical, used, status.AvailablePhysical);
    }

    private static DriveInfo GetSystemDrive()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        return new DriveInfo(root);
    }

    private static async Task<double> SampleCpuAsync()
    {
        if (!GetSystemTimes(out var idle0, out var kernel0, out var user0))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "No se pudo iniciar la muestra de CPU.");

        await Task.Delay(450);

        if (!GetSystemTimes(out var idle1, out var kernel1, out var user1))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "No se pudo completar la muestra de CPU.");

        var idle = ToUInt64(idle1) - ToUInt64(idle0);
        var kernel = ToUInt64(kernel1) - ToUInt64(kernel0);
        var user = ToUInt64(user1) - ToUInt64(user0);
        var total = kernel + user;
        if (total == 0) return 0;

        var busy = total > idle ? total - idle : 0;
        return busy * 100d / total;
    }

    private static async Task<NetworkSnapshot?> SampleNetworkAsync()
    {
        var adapter = SelectBestActiveAdapter();
        if (adapter is null) return null;

        try
        {
            var before = adapter.GetIPv4Statistics();
            var rx0 = before.BytesReceived;
            var tx0 = before.BytesSent;
            var started = DateTime.UtcNow;

            await Task.Delay(450);

            var after = adapter.GetIPv4Statistics();
            var elapsed = Math.Max(0.1, (DateTime.UtcNow - started).TotalSeconds);
            var rxMegabytesPerSecond =
                Math.Max(0, after.BytesReceived - rx0) / elapsed / 1024d / 1024d;
            var txMegabytesPerSecond =
                Math.Max(0, after.BytesSent - tx0) / elapsed / 1024d / 1024d;

            return new NetworkSnapshot(
                adapter.Name,
                Math.Max(0, adapter.Speed / 1_000_000d),
                Math.Round(rxMegabytesPerSecond, 2),
                Math.Round(txMegabytesPerSecond, 2));
        }
        catch (Exception ex) when (
            ex is NetworkInformationException or
            ObjectDisposedException or
            PlatformNotSupportedException)
        {
            return new NetworkSnapshot(
                adapter.Name,
                Math.Max(0, adapter.Speed / 1_000_000d),
                0,
                0);
        }
    }

    private static NetworkInterface? SelectBestActiveAdapter()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(item =>
                    item.OperationalStatus == OperationalStatus.Up)
                .Where(item =>
                    item.NetworkInterfaceType is not
                        NetworkInterfaceType.Loopback and not
                        NetworkInterfaceType.Tunnel)
                .Select(item => new
                {
                    Adapter = item,
                    HasGateway = HasDefaultGateway(item)
                })
                .OrderByDescending(item => item.HasGateway)
                .ThenByDescending(item => item.Adapter.Speed)
                .Select(item => item.Adapter)
                .FirstOrDefault();
        }
        catch (Exception ex) when (
            ex is NetworkInformationException or
            PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static bool HasDefaultGateway(NetworkInterface adapter)
    {
        try
        {
            return adapter
                .GetIPProperties()
                .GatewayAddresses
                .Any(gateway =>
                    !gateway.Address.Equals(
                        System.Net.IPAddress.Any) &&
                    !gateway.Address.Equals(
                        System.Net.IPAddress.IPv6Any));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static bool? IsRebootPending()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is not null)
                return true;
            if (baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is not null)
                return true;
            using var session = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (session?.GetValue("PendingFileRenameOperations") is not string[] pending ||
                pending.Length == 0)
                return false;

            return HasActionablePendingRename(pending);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or
            SecurityException or
            IOException)
        {
            return null;
        }
    }

    private static bool HasActionablePendingRename(string[] entries)
    {
        foreach (var raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var normalized = raw
                .Trim()
                .Replace(@"\??\", "", StringComparison.OrdinalIgnoreCase)
                .Replace('/', '\\');

            if (normalized.EndsWith(@"\pagefile.sys", StringComparison.OrdinalIgnoreCase))
                continue;

            return true;
        }

        return false;
    }

    private static ulong ToUInt64(FileTime value) =>
        ((ulong)value.High << 32) | value.Low;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;

        public MemoryStatusEx()
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
            MemoryLoad = 0;
            TotalPhysical = 0;
            AvailablePhysical = 0;
            TotalPageFile = 0;
            AvailablePageFile = 0;
            TotalVirtual = 0;
            AvailableVirtual = 0;
            AvailableExtendedVirtual = 0;
        }
    }
}
