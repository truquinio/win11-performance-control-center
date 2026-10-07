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
        string? storageWatchStatePath = null;
        string? evaluationRoot = null;

        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            var root = Path.GetFullPath(dataRoot);
            logPath = Path.Combine(root, "Logs", "app.jsonl");
            statePath = Path.Combine(root, "State", "ecoqos.json");
            storageWatchStatePath = Path.Combine(root, "State", "storage-watch.json");
            evaluationRoot = root;
        }

        var coordinator = new OperationCoordinator();
        var catalog = new ActionCatalog();
        var logger = new AppLogger(logPath);
        var healthState = new SystemHealthStateStore();
        var snapshot = new SystemSnapshotService(coordinator, healthState);
        var reliability = new ReliabilityService();
        StorageAnalysisService storage;
        if (evaluationRoot is null)
        {
            storage = new StorageAnalysisService();
        }
        else
        {
            var storageFixture = Path.Combine(
                evaluationRoot,
                "StorageFixture");
            Directory.CreateDirectory(storageFixture);
            storage = new StorageAnalysisService(
                [
                    new StorageAnalysisService.CacheTarget(
                        "eval.bridge-temp",
                        "Bridge storage fixture",
                        storageFixture)
                ],
                _ => false,
                evaluationRoot);
        }
        var storageWatch = new StorageWatchService(
            storageWatchStatePath,
            evaluationRoot);
        var processes = new ProcessAnalysisService();
        var ecoQosState = new EcoQosStateStore(statePath);
        var tuning = new ProcessTuningService(ecoQosState);
        var pageFile = new PageFileService();
        var integrity = new IntegrityService();
        var drivers = new DriverService();
        var activation = new ActivationService();
        var browsers = new BrowserInventoryService();
        string? edgeUserDataRoot = null;
        string? edgeQuarantineRoot = null;
        Func<bool>? edgeRunning = null;
        if (evaluationRoot is not null)
        {
            edgeUserDataRoot = Path.Combine(evaluationRoot, "EdgeUserData");
            edgeQuarantineRoot = Path.Combine(
                evaluationRoot,
                "Quarantine",
                "EdgeExtensions");
            Directory.CreateDirectory(Path.Combine(edgeUserDataRoot, "Default"));
            edgeRunning = () => false;
        }
        var browserExtensions = new BrowserExtensionHealthService(
            edgeUserDataRoot);
        var browserExtensionRemediation =
            new BrowserExtensionRemediationService(
                edgeUserDataRoot,
                edgeQuarantineRoot,
                edgeRunning);
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
            storageWatch,
            processes,
            tuning,
            pageFile,
            integrity,
            drivers,
            activation,
            browsers,
            browserExtensions,
            browserExtensionRemediation,
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
            await _logger.TryWriteAsync("bridge", "REJECTED", new
            {
                requestMethod = request?.Method,
                errorType = ex.GetType().Name,
                ex.Message
            });
            return new BridgeResponse(requestId, false, null, ex.Message);
        }
    }

    /// <summary>
    /// Handles a request and always returns a serialized response, so a
    /// result that cannot be represented as JSON reaches the caller as an
    /// error instead of escaping as an unhandled exception.
    /// </summary>
    public async Task<string> HandleSerializedAsync(string json)
    {
        var response = await HandleAsync(json);
        try
        {
            return JsonSerializer.Serialize(response, JsonOptions);
        }
        catch (Exception ex) when (
            ex is NotSupportedException or
            JsonException or
            ArgumentException or
            InvalidOperationException)
        {
            await _logger.TryWriteAsync("bridge", "REJECTED", new
            {
                errorType = ex.GetType().Name,
                ex.Message
            });
            return JsonSerializer.Serialize(
                new BridgeResponse(
                    response.RequestId,
                    false,
                    null,
                    "No se pudo serializar la respuesta del backend local."),
                JsonOptions);
        }
    }

    private Task<Models.ActionResult> RunActionAsync(JsonElement payload)
    {
        if (payload.ValueKind is not JsonValueKind.Object ||
            !payload.TryGetProperty("id", out var idElement))
        {
            throw new InvalidOperationException("Falta Action ID.");
        }

        var id = idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : null;
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
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("requestId", out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
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
