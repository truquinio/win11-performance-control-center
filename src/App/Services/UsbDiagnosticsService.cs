using System.IO;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class UsbDiagnosticsService
{
    private const int MaxDevices = 80;
    private readonly bool evaluationMode;

    public UsbDiagnosticsService(bool evaluationMode = false)
    {
        this.evaluationMode = evaluationMode;
    }

    public Task<UsbDiagnosticsReport> AnalyzeAsync() =>
        Task.Run(() =>
        {
            var devices = evaluationMode
                ? SyntheticDevices()
                : ReadUsbDevices();

            var items = devices
                .Select(ToItem)
                .OrderByDescending(item => item.ProblemCode != 0)
                .ThenByDescending(item => item.RestartEligible)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxDevices)
                .ToArray();

            return new UsbDiagnosticsReport(
                items.Length,
                items.Count(item => item.ProblemCode != 0),
                items.Count(item => item.RestartEligible),
                items);
        });

    public async Task<UsbRestartResult> RestartAsync(
        string deviceInstanceId)
    {
        if (string.IsNullOrWhiteSpace(deviceInstanceId) ||
            deviceInstanceId.Length > 512)
        {
            throw new InvalidOperationException(
                "Instance ID USB no permitido.");
        }

        var descriptor = (evaluationMode
                ? SyntheticDevices()
                : ReadUsbDevices())
            .FirstOrDefault(item => string.Equals(
                item.DeviceInstanceId,
                deviceInstanceId,
                StringComparison.OrdinalIgnoreCase));

        if (descriptor is null)
            throw new InvalidOperationException(
                "El dispositivo USB ya no existe o no pertenece al inventario USB.");

        var item = ToItem(descriptor);
        if (!item.RestartEligible)
        {
            throw new InvalidOperationException(
                "El dispositivo está protegido para reinicio: " +
                item.Reason);
        }

        if (evaluationMode)
        {
            return new UsbRestartResult(
                true,
                "EVALUATION",
                deviceInstanceId,
                descriptor.ProblemCode,
                0,
                false,
                "Reinicio sintético verificado.");
        }

        var system = Environment.GetFolderPath(
            Environment.SpecialFolder.System);
        var pnputil = Path.Combine(system, "pnputil.exe");
        if (!File.Exists(pnputil))
        {
            return new UsbRestartResult(
                false,
                "PNPUTIL_NOT_FOUND",
                deviceInstanceId,
                descriptor.ProblemCode,
                descriptor.ProblemCode,
                false,
                "pnputil.exe no está disponible.");
        }

        var restart = await RunAsync(
            pnputil,
            ["/restart-device", deviceInstanceId],
            TimeSpan.FromSeconds(60));
        if (restart.ExitCode != 0)
        {
            return new UsbRestartResult(
                false,
                "RESTART_FAILED",
                deviceInstanceId,
                descriptor.ProblemCode,
                descriptor.ProblemCode,
                false,
                restart.Output);
        }

        await RunAsync(
            pnputil,
            ["/scan-devices"],
            TimeSpan.FromSeconds(60));

        await Task.Delay(700);
        var verified = ReadUsbDevices().FirstOrDefault(item =>
            string.Equals(
                item.DeviceInstanceId,
                deviceInstanceId,
                StringComparison.OrdinalIgnoreCase));
        var afterCode = verified?.ProblemCode ?? 0u;
        var success = verified is not null && afterCode == 0;

        return new UsbRestartResult(
            success,
            success ? "RESTARTED_AND_VERIFIED" : "VERIFY_FAILED",
            deviceInstanceId,
            descriptor.ProblemCode,
            afterCode,
            false,
            success
                ? "Windows volvió a enumerar el dispositivo sin código de problema."
                : "El dispositivo se reinició, pero Windows sigue reportando un problema.");
    }

    private static UsbDeviceItem ToItem(
        UsbDescriptor descriptor)
    {
        var combined = string.Join(
            " ",
            descriptor.Name,
            descriptor.Manufacturer ?? string.Empty,
            descriptor.PnpClass ?? string.Empty,
            descriptor.Service ?? string.Empty);

        var protectedDevice =
            combined.Contains("root hub", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("generic usb hub", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("keyboard", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("mouse", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("hid", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("mass storage", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("storage", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("network", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("bluetooth", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("controller", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                descriptor.PnpClass,
                "HIDClass",
                StringComparison.OrdinalIgnoreCase);

        var eligible =
            descriptor.ProblemCode != 0 &&
            descriptor.DeviceInstanceId.StartsWith(
                @"USB\",
                StringComparison.OrdinalIgnoreCase) &&
            !protectedDevice;

        var reason = descriptor.ProblemCode == 0
            ? "Sin código de problema: no hay motivo para reiniciarlo."
            : protectedDevice
                ? "Dispositivo USB crítico o de entrada/almacenamiento; reinicio automático bloqueado."
                : eligible
                    ? "Dispositivo USB con problema y fuera de categorías protegidas."
                    : "No cumple la política de reinicio seguro.";

        return new UsbDeviceItem(
            descriptor.DeviceInstanceId,
            descriptor.Name,
            descriptor.Manufacturer,
            descriptor.PnpClass,
            descriptor.Service,
            descriptor.ProblemCode,
            descriptor.Status,
            eligible,
            reason);
    }

    private static IReadOnlyList<UsbDescriptor> ReadUsbDevices()
    {
        var result = new List<UsbDescriptor>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, ConfigManagerErrorCode, Manufacturer, " +
                "PNPDeviceID, PNPClass, Service, Status FROM Win32_PnPEntity");
            using var rows = searcher.Get();

            foreach (var raw in rows)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    var id = Convert.ToString(item["PNPDeviceID"]);
                    var name = Convert.ToString(item["Name"]) ?? "USB device";
                    var pnpClass = Convert.ToString(item["PNPClass"]);

                    var usbLike =
                        !string.IsNullOrWhiteSpace(id) &&
                        id.StartsWith(
                            @"USB\",
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            pnpClass,
                            "USB",
                            StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(
                            "USB",
                            StringComparison.OrdinalIgnoreCase);

                    if (!usbLike || string.IsNullOrWhiteSpace(id))
                        continue;

                    result.Add(new UsbDescriptor(
                        id,
                        name,
                        Convert.ToString(item["Manufacturer"]),
                        pnpClass,
                        Convert.ToString(item["Service"]),
                        ToUInt32(item["ConfigManagerErrorCode"]),
                        Convert.ToString(item["Status"]) ?? "Unknown"));
                }
            }
        }
        catch (ManagementException)
        {
        }
        catch (COMException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return result;
    }

    private static IReadOnlyList<UsbDescriptor> SyntheticDevices() =>
    [
        new UsbDescriptor(
            @"USB\VID_1234&PID_5678\DEMO",
            "Demo USB Camera",
            "Demo Vendor",
            "Camera",
            "usbvideo",
            43,
            "Error"),
        new UsbDescriptor(
            @"USB\ROOT_HUB30\DEMO",
            "USB Root Hub (USB 3.0)",
            "Microsoft",
            "USB",
            "USBHUB3",
            0,
            "OK")
    ];

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            return (-1, "No se pudo iniciar pnputil.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return (-2, "Tiempo máximo excedido.");
        }

        var text = string.Join(
            " ",
            new[] { await stdout, await stderr }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()));
        if (text.Length > 1200)
            text = text[..1200] + "...";

        return (process.ExitCode, text);
    }

    private static uint ToUInt32(object? value)
    {
        try
        {
            return value is null ? 0u : Convert.ToUInt32(value);
        }
        catch
        {
            return 0u;
        }
    }

    private sealed record UsbDescriptor(
        string DeviceInstanceId,
        string Name,
        string? Manufacturer,
        string? PnpClass,
        string? Service,
        uint ProblemCode,
        string Status);
}

public sealed record UsbDeviceItem(
    string DeviceInstanceId,
    string Name,
    string? Manufacturer,
    string? PnpClass,
    string? Service,
    uint ProblemCode,
    string Status,
    bool RestartEligible,
    string Reason);

public sealed record UsbDiagnosticsReport(
    int DeviceCount,
    int ProblemCount,
    int RestartEligibleCount,
    IReadOnlyList<UsbDeviceItem> Devices);

public sealed record UsbRestartResult(
    bool Success,
    string Status,
    string DeviceInstanceId,
    uint BeforeProblemCode,
    uint AfterProblemCode,
    bool RebootRequired,
    string Detail);
