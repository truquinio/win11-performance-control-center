using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Win11PerformanceControlCenter.App.Services;

namespace Win11PerformanceControlCenter.App.Core;

public sealed record BridgeResponse(
    string RequestId,
    bool Ok,
    object? Result = null,
    string? Error = null);

public sealed class HostBridge : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ActionCatalog _catalog;
    private readonly SystemSnapshotService _snapshotService;
    private readonly ReliabilityService _reliabilityService;
    private readonly ActionExecutor _executor;
    private readonly AppLogger _logger;

    private HostBridge(
        ActionCatalog catalog,
        SystemSnapshotService snapshotService,
        ReliabilityService reliabilityService,
        ActionExecutor executor,
        AppLogger logger)
    {
        _catalog = catalog;
        _snapshotService = snapshotService;
        _reliabilityService = reliabilityService;
        _executor = executor;
        _logger = logger;
    }

    public static HostBridge CreateDefault(string? dataRoot = null)
    {
        string? logPath = null;
        string? statePath = null;

        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            var root = Path.GetFullPath(dataRoot);
            logPath = Path.Combine(root, "Logs", "app.jsonl");
            statePath = Path.Combine(root, "State", "ecoqos.json");
        }

        var coordinator = new OperationCoordinator();
        var catalog = new ActionCatalog();
        var logger = new AppLogger(logPath);
        var healthState = new SystemHealthStateStore();
        var snapshot = new SystemSnapshotService(coordinator, healthState);
        var reliability = new ReliabilityService();
        var storage = new StorageAnalysisService();
        var processes = new ProcessAnalysisService();
        var ecoQosState = new EcoQosStateStore(statePath);
        var tuning = new ProcessTuningService(ecoQosState);
        var pageFile = new PageFileService();
        var integrity = new IntegrityService();
        var drivers = new DriverService();
        var activation = new ActivationService();
        var browsers = new BrowserInventoryService();
        var browserExtensions = new BrowserExtensionHealthService();
        var multimedia = new MultimediaService();
        var startup = new StartupAuditService();
        var updates = new WindowsUpdateAuditService();
        var apps = new InstalledAppsService();
        var privacy = new PrivacyAuditService();
        var developer = new DeveloperToolingService();
        var thermal = new ThermalEnergyService();
        var bootSleep = new BootSleepAuditService();
        var explorer = new ExplorerAuditService();
        var recovery = new OperationRecoveryService(ecoQosState, logPath);
        var privilege = new PrivilegeBoundary();
        var elevatedActions = new ElevatedActionClient();
        var executor = new ActionExecutor(
            catalog,
            coordinator,
            logger,
            privilege,
            snapshot,
            reliability,
            storage,
            processes,
            tuning,
            pageFile,
            integrity,
            drivers,
            activation,
            browsers,
            browserExtensions,
            multimedia,
            startup,
            updates,
            apps,
            privacy,
            developer,
            thermal,
            bootSleep,
            explorer,
            recovery,
            healthState,
            elevatedActions);

        return new HostBridge(catalog, snapshot, reliability, executor, logger);
    }

    public async Task<BridgeResponse> HandleAsync(string json)
    {
        BridgeRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(json, JsonOptions);
            if (request is null ||
                !string.Equals(request.Type, "request", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(request.RequestId) ||
                string.IsNullOrWhiteSpace(request.Method))
            {
                throw new InvalidOperationException("Solicitud IPC inválida.");
            }

            object result = request.Method switch
            {
                "system.snapshot" => await _snapshotService.CaptureAsync(),
                "system.reliability" => await _reliabilityService.GetRecentAsync(),
                "actions.catalog" => _catalog.All,
                "actions.run" => await RunActionAsync(request.Payload),
                _ => throw new InvalidOperationException("Método IPC no permitido: " + request.Method)
            };

            return new BridgeResponse(request.RequestId, true, result);
        }
        catch (Exception ex)
        {
            var requestId = request?.RequestId ?? TryReadRequestId(json) ?? "invalid";
            await _logger.WriteAsync("bridge", "REJECTED", new
            {
                requestMethod = request?.Method,
                errorType = ex.GetType().Name,
                ex.Message
            });
            return new BridgeResponse(requestId, false, null, ex.Message);
        }
    }

    private Task<Models.ActionResult> RunActionAsync(JsonElement payload)
    {
        if (payload.ValueKind is not JsonValueKind.Object ||
            !payload.TryGetProperty("id", out var idElement))
        {
            throw new InvalidOperationException("Falta Action ID.");
        }

        var id = idElement.GetString();
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Action ID vacía.");

        JsonElement? parameters = null;
        if (payload.TryGetProperty("parameters", out var parameterElement))
        {
            if (parameterElement.ValueKind is not JsonValueKind.Object)
                throw new InvalidOperationException(
                    "Los parámetros de Action deben ser un objeto tipado.");
            parameters = parameterElement.Clone();
        }

        return _executor.ExecuteAsync(id, parameters);
    }

    private static string? TryReadRequestId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("requestId", out var value)
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _logger.Dispose();

    private sealed record BridgeRequest(
        string Type,
        string RequestId,
        string Method,
        JsonElement Payload);
}
