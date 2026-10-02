namespace Win11PerformanceControlCenter.App.Models;

public sealed record NetworkSnapshot(
    string Name,
    double LinkMbps,
    double ReceiveMegabytesPerSecond,
    double SendMegabytesPerSecond);

public sealed record SystemSnapshot(
    DateTimeOffset CapturedAt,
    double CpuPercent,
    ulong MemoryTotalBytes,
    ulong MemoryUsedBytes,
    ulong MemoryAvailableBytes,
    string DiskDrive,
    long DiskTotalBytes,
    long DiskFreeBytes,
    NetworkSnapshot? Network,
    string IntegrityStatus,
    string DriverStatus,
    string ActivationStatus,
    bool? RebootRequired,
    long UptimeSeconds,
    string OperationState);

public enum EvidenceClassification
{
    HECHO,
    INDICIO,
    CAUSA_POSIBLE,
    NO_DETERMINADO
}

public sealed record ReliabilityEventDto(
    DateTimeOffset Time,
    int Id,
    string Provider,
    EvidenceClassification Classification,
    string Message);

public sealed record StorageEstimate(
    long EstimatedBytes,
    IReadOnlyList<StorageCategory> Categories);

public sealed record StorageCategory(
    string Id,
    string Label,
    long Bytes,
    string Risk);

public sealed record ProcessCandidate(
    int ProcessId,
    string Name,
    long WorkingSetBytes,
    bool HasMainWindow);

public sealed record ProcessAnalysis(
    int ObservedProcesses,
    IReadOnlyList<ProcessCandidate> Candidates);

public sealed record ValidatedProcessTarget(
    int ProcessId,
    string Name,
    DateTimeOffset StartTime);

public sealed record DriverIssue(
    string Name,
    uint ErrorCode,
    string? Manufacturer,
    string? DeviceId);

public sealed record DriverAnalysis(
    string Status,
    int ProblemCount,
    IReadOnlyList<DriverIssue> Problems);

public sealed record ActivationAnalysis(
    string Status,
    int LicenseStatus,
    string? Description,
    string? PartialProductKey);

public sealed record BrowserInstallation(
    string Name,
    string? Version,
    string? ExecutablePath,
    bool ProfileDetected);

public sealed record BrowserInventory(
    IReadOnlyList<BrowserInstallation> Browsers);

public sealed record BrowserExtensionHealthItem(
    string Profile,
    string ExtensionId,
    string? Name,
    string Status,
    long DataBytes,
    string? InstallationPath,
    bool DeveloperLoaded);

public sealed record BrowserExtensionHealth(
    string Browser,
    string Status,
    int ProfilesScanned,
    bool? DeveloperMode,
    int InstalledCount,
    int DeveloperLoadedCount,
    int DataOnlyCount,
    int BrokenCount,
    IReadOnlyList<BrowserExtensionHealthItem> Items);

public sealed record MultimediaDevice(
    string Kind,
    string Name,
    string Status);

public sealed record MultimediaInventory(
    IReadOnlyList<MultimediaDevice> Devices);

public sealed record PageFileEntry(
    string Name,
    ulong AllocatedMb,
    ulong CurrentUsageMb,
    ulong PeakUsageMb);

public sealed record PageFileAnalysis(
    bool? AutomaticallyManaged,
    IReadOnlyList<PageFileEntry> Entries);

public sealed record ProcessTuningItem(
    int ProcessId,
    string Name,
    bool Success,
    string? Error,
    long? BeforeWorkingSetBytes,
    long? AfterWorkingSetBytes,
    bool? BeforeEcoQos,
    bool? AfterEcoQos);

public sealed record ProcessTuningResult(
    int Attempted,
    int Succeeded,
    int Failed,
    IReadOnlyList<ProcessTuningItem> Items);

public sealed record AuditItem(
    string Category,
    string Name,
    string Value,
    string? Detail,
    string Status);

public sealed record AuditResult(
    string Status,
    IReadOnlyList<AuditItem> Items);

public sealed record RecoveryOperation(
    string ActionId,
    DateTimeOffset StartedAt,
    string LastStatus,
    bool Incomplete);

public sealed record RollbackTarget(
    int ProcessId,
    string Name,
    DateTimeOffset CapturedAt,
    bool Restorable);

public sealed record RecoveryStatus(
    IReadOnlyList<RecoveryOperation> IncompleteOperations,
    IReadOnlyList<string> RollbackSnapshots,
    IReadOnlyList<RollbackTarget> EcoQosTargets);

public enum ActionCategory
{
    System,
    Reliability,
    Memory,
    CPU,
    Storage,
    Network,
    Integrity,
    Drivers,
    Browsers,
    Multimedia,
    Startup,
    WindowsUpdate,
    Apps,
    Privacy,
    Developer,
    Thermal,
    Boot,
    SleepResume,
    Explorer,
    Backup
}

public enum ActionRisk
{
    SAFE,
    CAUTION,
    EXPLICIT_CONFIRMATION
}

public enum ConnectivityRequirement
{
    OFFLINE,
    OPTIONAL_ONLINE,
    ONLINE_REQUIRED
}

public enum ActionMode
{
    READ,
    DRY_RUN,
    WRITE
}

public enum ActionParameterType
{
    STRING,
    INTEGER,
    INTEGER_ARRAY,
    BOOLEAN
}

public sealed record ActionParameterDefinition(
    string Name,
    ActionParameterType Type,
    bool Required,
    string Description);

public sealed record ActionDefinition(
    string Id,
    string Title,
    string Description,
    ActionCategory Category,
    ActionRisk Risk,
    bool RequiresAdmin,
    ConnectivityRequirement Connectivity,
    bool Reversible,
    ActionMode Mode,
    IReadOnlyList<ActionParameterDefinition>? Parameters = null);

public sealed record ActionResult(
    bool Success,
    bool DryRun,
    string Message,
    object? Data = null);
