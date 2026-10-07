using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Win11PerformanceControlCenter.App.Core;

namespace Win11PerformanceControlCenter.App.Services;

public sealed class ServiceStartupRemediationService
{
    private static readonly Regex ServiceNamePattern =
        new("^[A-Za-z0-9_. -]{1,128}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ProtectedServiceNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Appinfo", "AudioEndpointBuilder", "Audiosrv", "BFE",
            "BITS", "BrokerInfrastructure", "CoreMessagingRegistrar",
            "CryptSvc", "DcomLaunch", "Dhcp", "Dnscache", "EventLog",
            "gpsvc", "LSM", "MpsSvc", "NlaSvc", "PlugPlay",
            "Power", "ProfSvc", "RpcEptMapper", "RpcSs", "SamSs",
            "Schedule", "SecurityHealthService", "SENS", "ShellHWDetection",
            "StateRepository", "SystemEventsBroker", "Themes", "TimeBrokerSvc",
            "TokenBroker", "UsoSvc", "UserManager", "W32Time",
            "WinDefend", "Winmgmt", "WpnService", "wscsvc", "wuauserv"
        };

    private static readonly string[] ProtectedKeywords =
    [
        "antivirus", "defender", "security", "malwarebytes",
        "vpn", "openai", "chatgpt", "claude", "desktop commander",
        "mcp", "ollama"
    ];

    private readonly string statePath;
    private readonly bool evaluationMode;

    public ServiceStartupRemediationService(
        string? statePath = null,
        bool evaluationMode = false)
    {
        AppPaths.EnsureDirectories();
        this.statePath = string.IsNullOrWhiteSpace(statePath)
            ? AppPaths.ServiceStartupState
            : Path.GetFullPath(statePath);
        this.evaluationMode = evaluationMode;
        var directory = Path.GetDirectoryName(this.statePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public Task<ServiceStartupPreview> PreviewAsync() => Task.Run(() =>
    {
        var services = evaluationMode
            ? SyntheticServices()
            : ReadServices();

        var snapshots = LoadState();
        var items = services
            .Where(service => service.StartMode == "Auto")
            .Select(service =>
            {
                var dependents = evaluationMode
                    ? Array.Empty<string>()
                    : ReadDependentServices(service.Name);
                var protection = Classify(service, dependents);
                return new ServiceStartupCandidate(
                    service.Name,
                    service.DisplayName,
                    service.State,
                    service.StartMode,
                    service.PathName,
                    protection.Protected,
                    protection.Reason,
                    snapshots.ContainsKey(service.Name),
                    dependents.Count,
                    dependents);
            })
            .OrderBy(candidate => candidate.Protected)
            .ThenBy(candidate => candidate.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ServiceStartupPreview(
            items.Length,
            items.Count(item => !item.Protected),
            items.Count(item => item.Protected),
            items);
    });

    public Task<ServiceStartupChangeResult> ChangeModeAsync(
        string serviceName,
        string targetMode) => Task.Run(() =>
    {
        ValidateServiceName(serviceName);
        var requested = NormalizeRequestedMode(targetMode);

        if (evaluationMode)
        {
            var evaluationService = SyntheticServices().FirstOrDefault(
                service => string.Equals(
                    service.Name,
                    serviceName,
                    StringComparison.OrdinalIgnoreCase)) ??
                throw new InvalidOperationException(
                    "Servicio sintético no permitido.");
            var evaluationProtection = Classify(
                evaluationService,
                Array.Empty<string>());
            if (evaluationProtection.Protected)
                throw new InvalidOperationException(
                    "Servicio protegido: " + evaluationProtection.Reason);

            return new ServiceStartupChangeResult(
                true,
                serviceName,
                evaluationService.StartMode,
                RequestedToWmiMode(requested),
                evaluationService.State,
                true,
                "EVALUATION");
        }

        var service = FindService(serviceName) ??
            throw new InvalidOperationException(
                "El servicio solicitado ya no existe.");

        var dependents = ReadDependentServices(service.Name);
        var protection = Classify(service, dependents);
        if (protection.Protected)
            throw new InvalidOperationException(
                "Servicio protegido: " + protection.Reason);

        var state = LoadState();
        if (!state.ContainsKey(service.Name))
        {
            state[service.Name] = new ServiceStartupSnapshot(
                service.Name,
                service.DisplayName,
                service.StartMode,
                service.State,
                DateTimeOffset.UtcNow);
            PersistState(state);
        }

        var returnCode = InvokeChangeStartMode(
            service.Name,
            requested);
        if (returnCode != 0)
            throw new InvalidOperationException(
                $"Windows rechazó ChangeStartMode (código {returnCode}).");

        var verified = FindService(service.Name) ??
            throw new InvalidOperationException(
                "No se pudo verificar el servicio después del cambio.");

        var expected = RequestedToWmiMode(requested);
        var success = string.Equals(
            verified.StartMode,
            expected,
            StringComparison.OrdinalIgnoreCase);

        return new ServiceStartupChangeResult(
            success,
            service.Name,
            service.StartMode,
            verified.StartMode,
            verified.State,
            state.ContainsKey(service.Name),
            success ? "APPLIED" : "VERIFY_FAILED");
    });

    public Task<ServiceStartupChangeResult> RestoreAsync(
        string serviceName) => Task.Run(() =>
    {
        ValidateServiceName(serviceName);

        if (evaluationMode)
        {
            return new ServiceStartupChangeResult(
                true,
                serviceName,
                "Disabled",
                "Auto",
                "Running",
                false,
                "RESTORED");
        }

        var state = LoadState();
        if (!state.TryGetValue(serviceName, out var snapshot))
        {
            return new ServiceStartupChangeResult(
                true,
                serviceName,
                "UNKNOWN",
                "UNKNOWN",
                "UNKNOWN",
                false,
                "NO_SNAPSHOT");
        }

        var current = FindService(serviceName) ??
            throw new InvalidOperationException(
                "El servicio guardado ya no existe.");
        var protection = Classify(current, Array.Empty<string>());
        if (protection.Protected)
            throw new InvalidOperationException(
                "El servicio ahora está clasificado como protegido: " +
                protection.Reason);

        var restoreMode = snapshot.StartMode switch
        {
            "Auto" => "Automatic",
            "Manual" => "Manual",
            "Disabled" => "Disabled",
            _ => throw new InvalidOperationException(
                "Snapshot de StartMode no reconocido.")
        };

        var returnCode = InvokeChangeStartMode(
            current.Name,
            restoreMode);
        if (returnCode != 0)
            throw new InvalidOperationException(
                $"Windows rechazó el rollback (código {returnCode}).");

        var verified = FindService(current.Name) ??
            throw new InvalidOperationException(
                "No se pudo verificar el rollback.");

        var success = string.Equals(
            verified.StartMode,
            snapshot.StartMode,
            StringComparison.OrdinalIgnoreCase);

        if (success)
        {
            state.Remove(serviceName);
            PersistState(state);
        }

        return new ServiceStartupChangeResult(
            success,
            serviceName,
            current.StartMode,
            verified.StartMode,
            verified.State,
            !success,
            success ? "RESTORED" : "VERIFY_FAILED");
    });

    private static uint InvokeChangeStartMode(
        string serviceName,
        string requestedMode)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name FROM Win32_Service");
        using var results = searcher.Get();
        foreach (var raw in results)
        {
            using (raw)
            {
                if (raw is not ManagementObject item)
                    continue;
                var name = Convert.ToString(item["Name"]);
                if (!string.Equals(
                        name,
                        serviceName,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                var result = item.InvokeMethod(
                    "ChangeStartMode",
                    [requestedMode]);
                return result is null ? uint.MaxValue : Convert.ToUInt32(result);
            }
        }

        throw new InvalidOperationException(
            "No se encontró el servicio para aplicar el cambio.");
    }

    private static ServiceDescriptor? FindService(string name) =>
        ReadServices().FirstOrDefault(service =>
            string.Equals(
                service.Name,
                name,
                StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<ServiceDescriptor> ReadServices()
    {
        var result = new List<ServiceDescriptor>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DisplayName, State, StartMode, PathName FROM Win32_Service");
            using var results = searcher.Get();
            foreach (var raw in results)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    var name = Convert.ToString(item["Name"]);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    result.Add(new ServiceDescriptor(
                        name,
                        Convert.ToString(item["DisplayName"]) ?? name,
                        Convert.ToString(item["State"]) ?? "Unknown",
                        Convert.ToString(item["StartMode"]) ?? "Unknown",
                        Convert.ToString(item["PathName"])));
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

    public Task<ServiceDependencyReport> AnalyzeDependenciesAsync(
        string serviceName) => Task.Run(() =>
    {
        ValidateServiceName(serviceName);
        var service = evaluationMode
            ? SyntheticServices().FirstOrDefault(item =>
                string.Equals(
                    item.Name,
                    serviceName,
                    StringComparison.OrdinalIgnoreCase))
            : FindService(serviceName);

        if (service is null)
            throw new InvalidOperationException(
                "El servicio solicitado no existe.");

        var dependents = evaluationMode
            ? Array.Empty<string>()
            : ReadDependentServices(service.Name);

        return new ServiceDependencyReport(
            service.Name,
            service.DisplayName,
            dependents.Count,
            dependents);
    });

    private static IReadOnlyList<string> ReadDependentServices(
        string serviceName)
    {
        var result = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_Service.Name='{serviceName}'}} " +
                "WHERE AssocClass=Win32_DependentService Role=Antecedent");
            using var rows = searcher.Get();

            foreach (var raw in rows)
            {
                using (raw)
                {
                    if (raw is not ManagementObject item)
                        continue;

                    var name = Convert.ToString(item["Name"]);
                    var display = Convert.ToString(item["DisplayName"]);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    result.Add(string.IsNullOrWhiteSpace(display)
                        ? name
                        : display + " (" + name + ")");
                }
            }
        }
        catch (ManagementException)
        {
            return ["UNKNOWN_WMI"];
        }
        catch (COMException)
        {
            return ["UNKNOWN_WMI"];
        }
        catch (UnauthorizedAccessException)
        {
            return ["UNKNOWN_POLICY"];
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
    }

    private static IReadOnlyList<ServiceDescriptor> SyntheticServices() =>
    [
        new ServiceDescriptor(
            "DemoVendorSvc",
            "Demo Vendor Updater",
            "Stopped",
            "Auto",
            @"C:\Program Files\DemoVendor\updater.exe"),
        new ServiceDescriptor(
            "RpcSs",
            "Remote Procedure Call",
            "Running",
            "Auto",
            @"C:\Windows\System32\svchost.exe -k rpcss")
    ];

    private static ServiceProtection Classify(
        ServiceDescriptor service,
        IReadOnlyList<string> dependents)
    {
        if (ProtectedServiceNames.Contains(service.Name))
            return new(true, "Servicio esencial o de infraestructura de Windows.");

        if (dependents.Count > 0)
        {
            return new(
                true,
                $"Tiene {dependents.Count} servicio(s) dependiente(s): {string.Join(", ", dependents.Take(5))}.");
        }

        var combined = string.Join(
            " ",
            service.Name,
            service.DisplayName,
            service.PathName ?? string.Empty);
        if (ProtectedKeywords.Any(keyword =>
                combined.Contains(
                    keyword,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return new(true,
                "Protegido por política local: seguridad, conectividad o automatización.");
        }

        var executable = ResolveExecutable(service.PathName);
        if (string.IsNullOrWhiteSpace(executable))
            return new(true, "No se pudo identificar de forma segura el ejecutable.");

        var windows = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        if (IsUnderRoot(executable, windows))
            return new(true, "Ejecutable alojado dentro de Windows.");

        return new(false,
            service.State.Equals("Stopped", StringComparison.OrdinalIgnoreCase)
                ? "Servicio de terceros automático y actualmente detenido."
                : "Servicio de terceros automático; requiere decisión explícita.");
    }

    private static string? ResolveExecutable(string? pathName)
    {
        if (string.IsNullOrWhiteSpace(pathName))
            return null;

        var value = Environment.ExpandEnvironmentVariables(pathName.Trim());
        string candidate;
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end <= 1)
                return null;
            candidate = value[1..end];
        }
        else
        {
            var exe = value.IndexOf(
                ".exe",
                StringComparison.OrdinalIgnoreCase);
            candidate = exe >= 0
                ? value[..(exe + 4)]
                : value.Split(' ', 2)[0];
        }

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        try
        {
            var p = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar);
            var r = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar);
            return p.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(
                    r + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static void ValidateServiceName(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName) ||
            !ServiceNamePattern.IsMatch(serviceName))
        {
            throw new InvalidOperationException(
                "Nombre de servicio no permitido.");
        }
    }

    private static string NormalizeRequestedMode(string mode) =>
        mode.Trim().ToUpperInvariant() switch
        {
            "MANUAL" => "Manual",
            "DISABLED" => "Disabled",
            "AUTOMATIC" => "Automatic",
            _ => throw new InvalidOperationException(
                "Modo de inicio no permitido.")
        };

    private static string RequestedToWmiMode(string mode) =>
        mode == "Automatic" ? "Auto" : mode;

    private Dictionary<string, ServiceStartupSnapshot> LoadState()
    {
        try
        {
            if (!File.Exists(statePath))
                return new(StringComparer.OrdinalIgnoreCase);

            var value = JsonSerializer.Deserialize<
                Dictionary<string, ServiceStartupSnapshot>>(
                    File.ReadAllText(statePath),
                    HostBridge.JsonOptions);
            return value is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(value, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void PersistState(
        Dictionary<string, ServiceStartupSnapshot> state)
    {
        var temp = statePath + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(state, HostBridge.JsonOptions));
        File.Move(temp, statePath, overwrite: true);
    }

    private sealed record ServiceDescriptor(
        string Name,
        string DisplayName,
        string State,
        string StartMode,
        string? PathName);

    private sealed record ServiceProtection(
        bool Protected,
        string Reason);
}

public sealed record ServiceStartupCandidate(
    string ServiceName,
    string DisplayName,
    string State,
    string StartMode,
    string? PathName,
    bool Protected,
    string Reason,
    bool RestoreAvailable,
    int DependentServiceCount = 0,
    IReadOnlyList<string>? Dependents = null);

public sealed record ServiceDependencyReport(
    string ServiceName,
    string DisplayName,
    int DependentCount,
    IReadOnlyList<string> Dependents);

public sealed record ServiceStartupPreview(
    int AutomaticCount,
    int EligibleCount,
    int ProtectedCount,
    IReadOnlyList<ServiceStartupCandidate> Services);

public sealed record ServiceStartupSnapshot(
    string ServiceName,
    string DisplayName,
    string StartMode,
    string State,
    DateTimeOffset CapturedAt);

public sealed record ServiceStartupChangeResult(
    bool Success,
    string ServiceName,
    string BeforeMode,
    string AfterMode,
    string CurrentState,
    bool RestoreAvailable,
    string Status);
