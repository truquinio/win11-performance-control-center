export {};

type OperationState = "IDLE" | "ANALYZING" | "OPTIMIZING" | "MAINTENANCE" | "REBOOT_REQUIRED" | "ERROR";
type Risk = "SAFE" | "CAUTION" | "EXPLICIT_CONFIRMATION";
type EvidenceClassification = "HECHO" | "INDICIO" | "CAUSA_POSIBLE" | "NO_DETERMINADO";
type ActionCategory = "System" | "Reliability" | "Memory" | "CPU" | "Storage" | "Network" | "Integrity" | "Drivers" | "Browsers" | "Multimedia" | "Startup" | "WindowsUpdate" | "Apps" | "Privacy" | "Developer" | "Thermal" | "Boot" | "SleepResume" | "Explorer" | "Backup";
type Connectivity = "OFFLINE" | "OPTIONAL_ONLINE" | "ONLINE_REQUIRED";
type ActionMode = "READ" | "DRY_RUN" | "WRITE";

interface NetworkSnapshot {
  name: string;
  linkMbps: number;
  receiveMegabytesPerSecond: number;
  sendMegabytesPerSecond: number;
}

interface SystemSnapshot {
  capturedAt: string;
  cpuPercent: number;
  memoryTotalBytes: number;
  memoryUsedBytes: number;
  memoryAvailableBytes: number;
  diskDrive: string;
  diskTotalBytes: number;
  diskFreeBytes: number;
  network: NetworkSnapshot | null;
  integrityStatus: string;
  driverStatus: string;
  activationStatus: string;
  rebootRequired: boolean | null;
  uptimeSeconds: number;
  operationState: OperationState;
}

interface ReliabilityEvent {
  time: string;
  id: number;
  provider: string;
  classification: EvidenceClassification;
  message: string;
}

interface ActionParameterDefinition {
  name: string;
  type: string;
  required: boolean;
  description: string;
}

interface ActionDefinition {
  id: string;
  title: string;
  description: string;
  category: ActionCategory;
  risk: Risk;
  requiresAdmin: boolean;
  connectivity: Connectivity;
  reversible: boolean;
  mode: ActionMode;
  parameters?: ActionParameterDefinition[] | null;
}

interface ActionResult {
  success: boolean;
  dryRun: boolean;
  message: string;
  data?: Record<string, unknown>;
}

interface HostResponse<T> {
  requestId: string;
  ok: boolean;
  result?: T;
  error?: string;
}

interface Provider {
  readonly surface: "local" | "demo";
  snapshot(): Promise<SystemSnapshot>;
  reliability(): Promise<ReliabilityEvent[]>;
  catalog(): Promise<ActionDefinition[]>;
  runAction(id: string, parameters?: Record<string, unknown>): Promise<ActionResult>;
}

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage(message: unknown): void;
        addEventListener(type: "message", listener: (event: MessageEvent) => void): void;
      };
    };
  }
}

class LocalProvider implements Provider {
  readonly surface = "local" as const;
  private pending = new Map<string, { resolve: (value: unknown) => void; reject: (reason: Error) => void; timer: number }>();

  constructor() {
    window.chrome?.webview?.addEventListener("message", event => {
      const message = event.data as HostResponse<unknown>;
      if (!message?.requestId) return;
      const pending = this.pending.get(message.requestId);
      if (!pending) return;
      window.clearTimeout(pending.timer);
      this.pending.delete(message.requestId);
      if (message.ok) pending.resolve(message.result);
      else pending.reject(new Error(message.error ?? "Error del backend local"));
    });
  }

  private request<T>(
    method: string,
    payload: Record<string, unknown> = {},
    timeoutMs = 20000
  ): Promise<T> {
    const requestId = crypto.randomUUID();
    return new Promise<T>((resolve, reject) => {
      const timer = window.setTimeout(() => {
        this.pending.delete(requestId);
        reject(new Error("Timeout esperando al backend local"));
      }, timeoutMs);
      this.pending.set(requestId, { resolve: value => resolve(value as T), reject, timer });
      window.chrome?.webview?.postMessage({ type: "request", requestId, method, payload });
    });
  }

  snapshot(): Promise<SystemSnapshot> { return this.request("system.snapshot"); }
  reliability(): Promise<ReliabilityEvent[]> { return this.request("system.reliability"); }
  catalog(): Promise<ActionDefinition[]> { return this.request("actions.catalog"); }
  runAction(id: string, parameters: Record<string, unknown> = {}): Promise<ActionResult> {
    const payload: Record<string, unknown> = { id };
    if (Object.keys(parameters).length > 0) payload.parameters = parameters;
    // The backend can legitimately keep a read operation alive for up to
    // four minutes (and an elevated helper for up to three). The UI timeout
    // must outlive those budgets or it can report a false failure while the
    // global operation interlock is still held.
    const defaultActionTimeoutMs = 270000;
    const integrityRepairTimeoutMs = 60 * 60 * 1000;
    const timeoutMs = id === "system.integrity.repair"
      ? integrityRepairTimeoutMs
      : defaultActionTimeoutMs;
    return this.request("actions.run", payload, timeoutMs);
  }
}

class DemoProvider implements Provider {
  readonly surface = "demo" as const;

  async snapshot(): Promise<SystemSnapshot> {
    await delay(220);
    return {
      capturedAt: new Date().toISOString(),
      cpuPercent: 18,
      memoryTotalBytes: 15.8 * 1024 ** 3,
      memoryUsedBytes: 11.2 * 1024 ** 3,
      memoryAvailableBytes: 4.6 * 1024 ** 3,
      diskDrive: "C:",
      diskTotalBytes: 476 * 1024 ** 3,
      diskFreeBytes: 91.6 * 1024 ** 3,
      network: { name: "Wi-Fi", linkMbps: 866, receiveMegabytesPerSecond: 12.4, sendMegabytesPerSecond: 8.1 },
      integrityStatus: "OK",
      driverStatus: "OK",
      activationStatus: "OK",
      rebootRequired: false,
      uptimeSeconds: 18632,
      operationState: "IDLE"
    };
  }

  async reliability(): Promise<ReliabilityEvent[]> {
    return [
      { time: new Date(Date.now() - 8.5e6).toISOString(), id: 6008, provider: "EventLog", classification: "HECHO", message: "Ejemplo DEMO: el cierre anterior fue inesperado." },
      { time: new Date(Date.now() - 8.4e6).toISOString(), id: 41, provider: "Microsoft-Windows-Kernel-Power", classification: "INDICIO", message: "Ejemplo DEMO: Windows detectó un inicio posterior a un apagado no limpio." },
      { time: new Date(Date.now() - 3.4e6).toISOString(), id: 1074, provider: "User32", classification: "HECHO", message: "Ejemplo DEMO: reinicio solicitado por un proceso." }
    ];
  }

  async catalog(): Promise<ActionDefinition[]> {
    return demoCatalog;
  }

  async runAction(id: string, parameters: Record<string, unknown> = {}): Promise<ActionResult> {
    await delay(450);

    if (id === "memory.trim" || id === "cpu.ecoqos.apply" || id === "cpu.ecoqos.restore") {
      const processIds = Array.isArray(parameters.processIds)
        ? parameters.processIds.filter((value): value is number => typeof value === "number")
        : [];
      return {
        success: processIds.length > 0,
        dryRun: false,
        message: `DEMO: ${id} simulado sobre ${processIds.length} proceso(s).`,
        data: {
          attempted: processIds.length,
          succeeded: processIds.length,
          failed: 0,
          items: processIds.map(processId => ({
            processId,
            name: "demo-process",
            success: true,
            beforeWorkingSetBytes: 400 * 1024 ** 2,
            afterWorkingSetBytes: id === "memory.trim" ? 260 * 1024 ** 2 : null,
            beforeEcoQos: id === "cpu.ecoqos.restore",
            afterEcoQos: id === "cpu.ecoqos.apply"
          })),
          rollbackAvailable: id === "cpu.ecoqos.apply"
        }
      };
    }

    if (id === "disk.cleanup.execute") {
      return { success: true, dryRun: false, message: "DEMO: limpieza simulada; no se elimina ningún archivo.", data: { deletedBytes: 0, deletedFiles: 0, failedFiles: 0 } };
    }
    if (id === "disk.scan" || id === "disk.cleanup.safe") {
      return {
        success: true,
        dryRun: id === "disk.cleanup.safe",
        message: "DEMO: análisis de almacenamiento simulado.",
        data: {
          estimatedBytes: 13.4 * 1024 ** 3,
          categories: [
            { label: "Temporales de usuario", bytes: 5.2 * 1024 ** 3, risk: "LOW" },
            { label: "Temporales de Windows", bytes: 8.2 * 1024 ** 3, risk: "CAUTION" }
          ]
        }
      };
    }

    if (id === "system.integrity.check") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: comprobación de integridad simulada.",
        data: {
          status: "OK",
          exitCode: 0,
          durationMs: 620,
          output: "DEMO: No component store corruption detected."
        }
      };
    }

    if (id === "drivers.analyze") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: Windows no reporta dispositivos con problemas.",
        data: { status: "OK", problemCount: 0, problems: [] }
      };
    }

    if (id === "system.activation.analyze") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: Windows informa una licencia activa.",
        data: {
          status: "LICENSED",
          licenseStatus: 1,
          description: "DEMO: Windows edition"
        }
      };
    }

    if (id === "browsers.inventory") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: 2 navegadores detectados.",
        data: {
          browsers: [
            { name: "Microsoft Edge", version: "DEMO", profileDetected: true, executablePath: "DEMO" },
            { name: "Google Chrome", version: "DEMO", profileDetected: true, executablePath: "DEMO" }
          ]
        }
      };
    }

    if (id === "browsers.extensions.health") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: salud de extensiones Edge simulada.",
        data: {
          browser: "Microsoft Edge",
          status: "WARNING",
          profilesScanned: 1,
          developerMode: true,
          installedCount: 5,
          developerLoadedCount: 1,
          dataOnlyCount: 2,
          brokenCount: 1,
          items: [
            { profile: "Default", extensionId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", name: "Demo instalada", status: "OK", dataBytes: 1200000, installationPath: "DEMO", developerLoaded: false },
            { profile: "Default", extensionId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", name: "Demo datos huérfanos", status: "DATA_WITHOUT_INSTALLATION", dataBytes: 2400000, installationPath: null, developerLoaded: false }
          ]
        }
      };
    }

    if (id === "startup.services.preview") {
      return {
        success: true,
        dryRun: true,
        message: "DEMO: 1 servicio revisable y 1 protegido.",
        data: {
          automaticCount: 2,
          eligibleCount: 1,
          protectedCount: 1,
          services: [
            {
              serviceName: "DemoVendorSvc",
              displayName: "Demo Vendor Updater",
              state: "Stopped",
              startMode: "Auto",
              pathName: "C:\\Program Files\\DemoVendor\\updater.exe",
              protected: false,
              reason: "Servicio de terceros automático y actualmente detenido.",
              restoreAvailable: false
            },
            {
              serviceName: "RpcSs",
              displayName: "Remote Procedure Call",
              state: "Running",
              startMode: "Auto",
              pathName: "C:\\Windows\\System32\\svchost.exe",
              protected: true,
              reason: "Servicio esencial o de infraestructura de Windows.",
              restoreAvailable: false
            }
          ]
        }
      };
    }

    if (id === "multimedia.inventory") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: inventario multimedia simulado.",
        data: {
          devices: [
            { kind: "Audio", name: "DEMO Audio Device", status: "OK" },
            { kind: "Video", name: "DEMO Display Adapter", status: "OK" }
          ]
        }
      };
    }

    if (id === "memory.pagefile.analyze") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: pagefile administrado automáticamente.",
        data: {
          automaticallyManaged: true,
          entries: [
            { name: "C:\\\\pagefile.sys", allocatedMb: 4096, currentUsageMb: 620, peakUsageMb: 1240 }
          ]
        }
      };
    }

    if (id === "memory.trim.preview" || id === "cpu.ecoqos.analyze") {
      return {
        success: true,
        dryRun: true,
        message: "DEMO: candidatos simulados; no se aplicaron cambios.",
        data: {
          observedProcesses: 128,
          candidates: [
            { processId: 4242, name: "example", workingSetBytes: 420 * 1024 ** 2, hasMainWindow: false }
          ]
        }
      };
    }

    if (id === "system.crash.analyze") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: Crash Intelligence agrupó patrones de evidencia.",
        data: {
          eventsRead: 6,
          highSeverityCount: 1,
          mediumSeverityCount: 1,
          insights: [
            {
              category: "Estabilidad",
              severity: "HIGH",
              title: "Apagados o reinicios no limpios",
              occurrences: 2,
              latestAt: new Date().toISOString(),
              recommendedActionId: "system.health.scan",
              rationale: "El evento confirma un cierre no limpio, pero no determina la causa.",
              latestEvidence: "DEMO Kernel-Power"
            },
            {
              category: "Aplicaciones",
              severity: "MEDIUM",
              title: "Aplicaciones sin responder",
              occurrences: 1,
              latestAt: new Date().toISOString(),
              recommendedActionId: "memory.analyze",
              rationale: "Se requiere contexto antes de reparar.",
              latestEvidence: "DEMO Application Hang"
            }
          ]
        }
      };
    }

    if (id === "drivers.usb.analyze") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: 1 USB con problema y elegible para reinicio controlado.",
        data: {
          deviceCount: 2,
          problemCount: 1,
          restartEligibleCount: 1,
          devices: [
            {
              deviceInstanceId: "USB\\VID_1234&PID_5678\\DEMO",
              name: "Demo USB Camera",
              manufacturer: "Demo Vendor",
              pnpClass: "Camera",
              service: "usbvideo",
              problemCode: 43,
              status: "Error",
              restartEligible: true,
              reason: "Dispositivo USB con problema y fuera de categorías protegidas."
            },
            {
              deviceInstanceId: "USB\\ROOT_HUB30\\DEMO",
              name: "USB Root Hub (USB 3.0)",
              manufacturer: "Microsoft",
              pnpClass: "USB",
              service: "USBHUB3",
              problemCode: 0,
              status: "OK",
              restartEligible: false,
              reason: "Sin código de problema."
            }
          ]
        }
      };
    }

    if (id === "startup.entries.preview") {
      return {
        success: true,
        dryRun: true,
        message: "DEMO: autoarranque clasificado con rollback.",
        data: {
          registeredCount: 2,
          eligibleCount: 1,
          protectedCount: 1,
          disabledByAppCount: 0,
          entries: [
            {
              entryId: "0123456789ABCDEF01234567",
              name: "DemoVendor",
              command: "C:\\Program Files\\DemoVendor\\agent.exe --background",
              location: "HKCU\\Run",
              scope: "HKCU",
              view: "Registry64",
              enabled: true,
              protected: false,
              reason: "Entrada de terceros revisable.",
              restoreAvailable: false
            },
            {
              entryId: "89ABCDEF0123456789ABCDEF",
              name: "PM2",
              command: "node.exe pm2 resurrect",
              location: "HKCU\\Run",
              scope: "HKCU",
              view: "Registry64",
              enabled: true,
              protected: true,
              reason: "Protegido por política local.",
              restoreAvailable: false
            }
          ]
        }
      };
    }

    if (id === "network.test") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: adaptador simulado medido.",
        data: {
          network: { name: "Wi-Fi", linkMbps: 866, receiveMegabytesPerSecond: 12.4, sendMegabytesPerSecond: 8.1 }
        }
      };
    }

    if (id === "memory.analyze") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: análisis de memoria simulado.",
        data: {
          memoryTotalBytes: 15.8 * 1024 ** 3,
          memoryUsedBytes: 11.2 * 1024 ** 3,
          memoryAvailableBytes: 4.6 * 1024 ** 3
        }
      };
    }

    if (id === "system.actionplan.preview") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: plan de acción construido.",
        data: {
          highPriorityCount: 1,
          mediumPriorityCount: 1,
          actionableCount: 2,
          deferredCount: 1,
          workload: {
            configuredMode: "AUTO",
            effectiveMode: "IN_USE",
            idleSeconds: 42,
            dFreePercent: 12.5,
            storageCritical: false,
            heavyActionsAllowed: false,
            reason: "El equipo está en uso."
          },
          items: [
            {
              priority: "HIGH",
              title: "Espacio bajo en D:",
              reason: "12.5% libre.",
              actionId: "disk.hotspots.scan",
              mode: "READ",
              risk: "SAFE",
              requiresAdmin: false,
              deferred: true,
              deferReason: "PC en uso"
            },
            {
              priority: "LOW",
              title: "Rollbacks disponibles",
              reason: "Hay cambios recuperables.",
              actionId: "backup.rollback.center",
              mode: "READ",
              risk: "SAFE",
              requiresAdmin: false,
              deferred: false,
              deferReason: null
            }
          ]
        }
      };
    }

    if (id.startsWith("system.workload.")) {
      const configured = id.endsWith(".inuse")
        ? "IN_USE"
        : id.endsWith(".maintenance")
          ? "MAINTENANCE"
          : "AUTO";
      return {
        success: true,
        dryRun: false,
        message: "DEMO: modo de carga actualizado.",
        data: {
          configuredMode: configured,
          effectiveMode: configured === "AUTO" ? "IDLE" : configured,
          idleSeconds: 900,
          dFreePercent: 50,
          storageCritical: false,
          heavyActionsAllowed: configured !== "IN_USE",
          reason: configured === "IN_USE"
            ? "PC protegido mientras está en uso."
            : "Mantenimiento permitido."
        }
      };
    }

    if (id === "backup.rollback.center") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: 2 cambios con rollback disponible.",
        data: {
          incompleteOperations: [],
          entries: [
            {
              id: "pagefile",
              title: "Archivo de paginación",
              detail: "Configuración previa guardada.",
              actionId: "memory.pagefile.restore",
              parameters: {},
              requiresAdmin: true,
              risk: "SAFE"
            },
            {
              id: "power-plan",
              title: "Plan de energía",
              detail: "Plan anterior disponible.",
              actionId: "thermal.power.restore",
              parameters: {},
              requiresAdmin: false,
              risk: "SAFE"
            }
          ]
        }
      };
    }

    if (id === "system.healthscore.analyze") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: Health Score 82/100 (GOOD).",
        data: {
          capturedAt: new Date().toISOString(),
          score: 82,
          band: "GOOD",
          previousCapturedAt: new Date(Date.now() - 86400000).toISOString(),
          baselineCreated: false,
          historyCount: 4,
          factors: [
            { category: "Almacenamiento", status: "WATCH", penalty: 10, detail: "12.4% libre en D:." },
            { category: "Memoria", status: "WATCH", penalty: 8, detail: "82% de RAM en uso." }
          ],
          changes: [
            { category: "Métrica", name: "Health Score", impact: "IMPROVED", before: "77", after: "82", delta: 5, unit: "puntos" }
          ],
          trend: [
            { capturedAt: new Date(Date.now() - 86400000).toISOString(), score: 77, band: "GOOD", memoryUsedPercent: 79, systemDiskFreePercent: 22 },
            { capturedAt: new Date().toISOString(), score: 82, band: "GOOD", memoryUsedPercent: 76, systemDiskFreePercent: 25 }
          ]
        }
      };
    }

    if (id === "system.changes.analyze") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: 2 cambios materiales.",
        data: {
          previousCapturedAt: new Date(Date.now() - 86400000).toISOString(),
          currentCapturedAt: new Date().toISOString(),
          baselineRequired: false,
          changes: [
            { category: "Métrica", name: "Health Score", impact: "IMPROVED", before: "77", after: "82", delta: 5, unit: "puntos" },
            { category: "Volumen", name: "D:\\", impact: "IMPROVED", before: "40 GB", after: "45 GB", delta: 5368709120, unit: "bytes" }
          ],
          trend: []
        }
      };
    }

    if (id === "lab.outcomes.status") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: 100% de acciones WRITE clasificadas.",
        data: {
          writeActionCount: 25,
          classifiedCount: 25,
          postcheckedCount: 17,
          coveragePercent: 100,
          incompleteRuns: 0,
          actions: [
            { actionId: "system.integrity.repair", title: "Reparar integridad de Windows", verificationLevel: "POSTCHECK", verificationDetail: "DISM/SFC + CheckHealth", reversible: false, rollbackExpected: true, completedRuns: 1, failedRuns: 0, rejectedRuns: 0, incompleteRuns: 0 },
            { actionId: "network.winsock.reset", title: "Restablecer Winsock", verificationLevel: "REBOOT_REQUIRED", verificationDetail: "Estado final tras reinicio", reversible: false, rollbackExpected: false, completedRuns: 0, failedRuns: 0, rejectedRuns: 0, incompleteRuns: 0 }
          ]
        }
      };
    }

    if (id.startsWith("maintenance.policy.")) {
      const mode = id.endsWith(".readonly") ? "READ_ONLY_IDLE" :
        id.endsWith(".off") ? "OFF" : "OFF";
      return {
        success: true,
        dryRun: false,
        message: mode === "OFF"
          ? "DEMO: automatización segura OFF."
          : "DEMO: automatización READ_ONLY_IDLE activada.",
        data: {
          mode,
          canRunNow: mode === "READ_ONLY_IDLE",
          workload: {
            configuredMode: "AUTO",
            effectiveMode: "IDLE",
            idleSeconds: 900,
            dFreePercent: 50,
            storageCritical: false,
            heavyActionsAllowed: true,
            reason: "Equipo inactivo."
          },
          fixedReadOnlyScope: [
            "system.health.scan",
            "disk.volumes.audit",
            "drivers.analyze",
            "system.crash.analyze"
          ],
          updatedAt: new Date().toISOString(),
          reason: mode === "OFF"
            ? "Automatización desactivada."
            : "Lote read-only elegible."
        }
      };
    }

    if (id === "maintenance.safe.run") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: mantenimiento read-only completado.",
        data: {
          completedAt: new Date().toISOString(),
          executed: true,
          message: "No se ejecutó ninguna acción WRITE.",
          policy: {
            mode: "READ_ONLY_IDLE",
            canRunNow: true,
            fixedReadOnlyScope: ["drivers.analyze", "disk.volumes.audit"]
          },
          items: [
            { actionId: "drivers.analyze", status: "OK", summary: "0 dispositivos con problema.", errorType: null },
            { actionId: "disk.volumes.audit", status: "OK", summary: "2 volúmenes, 0 con presión.", errorType: null }
          ]
        }
      };
    }

    if (id === "system.reliability.analyze") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: Reliability Analyzer completado.",
        data: { events: await this.reliability() }
      };
    }

    if (id === "backup.status") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: estado de recovery simulado.",
        data: {
          incompleteOperations: [],
          rollbackSnapshots: ["DEMO: EcoQoS snapshot"],
          ecoQosTargets: [
            { processId: 4242, name: "example", capturedAt: new Date().toISOString(), restorable: true }
          ]
        }
      };
    }

    if ([
      "startup.audit",
      "windows.update.audit",
      "apps.inventory",
      "privacy.audit",
      "developer.audit",
      "thermal.audit",
      "boot.audit",
      "sleepresume.audit",
      "explorer.audit"
    ].includes(id)) {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: auditoría avanzada simulada.",
        data: {
          status: "OK",
          items: [
            {
              category: "DEMO",
              name: id,
              value: "Example",
              detail: "Datos simulados; la demo no inspecciona Windows.",
              status: "OBSERVED"
            }
          ]
        }
      };
    }

    return {
      success: true,
      dryRun: true,
      message: "DEMO: acción simulada. Los datos reales solo están disponibles en la aplicación local.",
      data: { reliabilityEvents: 3 }
    };
  }
}

const demoCatalog: ActionDefinition[] = [
  { id: "system.actionplan.preview", title: "Construir plan de acción", description: "Prioriza problemas y enlaza cada hallazgo con su acción segura.", category: "System", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "system.healthscore.analyze", title: "Health Score y baseline", description: "Captura una línea base y calcula un score explicable.", category: "System", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "system.changes.analyze", title: "Qué cambió", description: "Compara las dos últimas líneas base.", category: "System", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "system.workload.status", title: "Estado PC en uso", description: "Indica si el mantenimiento pesado está permitido.", category: "System", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "system.workload.inuse", title: "Proteger PC en uso", description: "Bloquea tareas pesadas o disruptivas.", category: "System", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: true, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "system.workload.auto", title: "Volver a modo automático", description: "Decide según inactividad.", category: "System", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: true, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "system.workload.maintenance", title: "Permitir mantenimiento", description: "Habilita tareas pesadas de forma explícita.", category: "System", risk: "CAUTION", requiresAdmin: false, connectivity: "OFFLINE", reversible: true, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "system.health.scan", title: "Diagnóstico del sistema", description: "Obtiene una línea base segura.", category: "System", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "system.integrity.check", title: "Comprobar integridad de Windows", description: "Ejecuta DISM /CheckHealth en modo lectura.", category: "System", risk: "SAFE", requiresAdmin: true, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "system.integrity.repair", title: "Reparar integridad de Windows", description: "Ejecuta DISM /RestoreHealth + SFC y verifica.", category: "System", risk: "CAUTION", requiresAdmin: true, connectivity: "OPTIONAL_ONLINE", reversible: false, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "system.reliability.analyze", title: "Reliability Analyzer", description: "Correlaciona eventos de arranque y apagado.", category: "Reliability", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "system.crash.analyze", title: "Crash Intelligence", description: "Agrupa fallos, hangs, WHEA y apagados no limpios sin inventar causas.", category: "Reliability", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "memory.analyze", title: "Analizar memoria", description: "Mide presión y uso de RAM.", category: "Memory", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "memory.trim.preview", title: "Previsualizar MemoryTrim", description: "Identifica working sets altos sin modificar memoria.", category: "Memory", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "DRY_RUN" },
  { id: "memory.trim", title: "MemoryTrim seleccionado", description: "Recorta working sets sólo de procesos seleccionados.", category: "Memory", risk: "CAUTION", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "WRITE", parameters: [{ name: "processIds", type: "INTEGER_ARRAY", required: true, description: "PIDs seleccionados" }, { name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "memory.pagefile.analyze", title: "Analizar archivo de paginación", description: "Lee configuración y uso del pagefile.", category: "Memory", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "memory.pagefile.capped", title: "Aplicar pagefile 4–8 GB en D:", description: "Mantiene 512 MB en C: y limita D: a 4 GB inicial / 8 GB máximo.", category: "Memory", risk: "CAUTION", requiresAdmin: true, connectivity: "OFFLINE", reversible: true, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "memory.pagefile.restore", title: "Restaurar configuración de pagefile", description: "Restaura la configuración previa guardada.", category: "Memory", risk: "SAFE", requiresAdmin: true, connectivity: "OFFLINE", reversible: false, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "cpu.ecoqos.analyze", title: "Analizar EcoQoS", description: "Detecta candidatos sin aplicar cambios.", category: "CPU", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "cpu.ecoqos.apply", title: "Aplicar EcoQoS seleccionado", description: "Aplica EcoQoS sólo a procesos seleccionados.", category: "CPU", risk: "CAUTION", requiresAdmin: false, connectivity: "OFFLINE", reversible: true, mode: "WRITE", parameters: [{ name: "processIds", type: "INTEGER_ARRAY", required: true, description: "PIDs seleccionados" }, { name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "cpu.ecoqos.restore", title: "Restaurar EcoQoS", description: "Restaura el estado previo registrado.", category: "CPU", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: true, mode: "WRITE", parameters: [{ name: "processIds", type: "INTEGER_ARRAY", required: true, description: "PIDs modificados" }, { name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "disk.scan", title: "Analizar almacenamiento", description: "Calcula espacio y temporales.", category: "Storage", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "disk.cleanup.safe", title: "Limpieza segura", description: "Dry-run del pipeline de limpieza.", category: "Storage", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: true, mode: "DRY_RUN" },
  { id: "disk.cleanup.execute", title: "Eliminar cachés regenerables", description: "Solo después de la previsualización y una confirmación explícita.", category: "Storage", risk: "CAUTION", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "disk.appdata.rank", title: "Ranking de AppData Local", description: "Tamaños por carpeta, nunca borra programas ni datos.", category: "Storage", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "disk.volumes.audit", title: "Auditar C: / D: y volúmenes", description: "Mide presión de almacenamiento en todos los volúmenes fijos.", category: "Storage", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "disk.storage.watch", title: "Storage Watch", description: "Compara espacio libre con el baseline anterior y detecta crecimiento anormal.", category: "Storage", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "disk.hotspots.scan", title: "Buscar hotspots de disco", description: "Escaneo read-only con presupuesto temporal en unidades con poco espacio.", category: "Storage", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "disk.hibernate.status", title: "Estado de hibernación", description: "Consulta estados disponibles con powercfg.", category: "Storage", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "disk.hibernate.reduce", title: "Hibernación reducida", description: "Con UAC; conserva inicio rápido, deshabilita hibernación completa.", category: "Storage", risk: "CAUTION", requiresAdmin: true, connectivity: "OFFLINE", reversible: false, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "network.test", title: "Analizar red", description: "Mide el adaptador activo.", category: "Network", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "network.flushdns", title: "Vaciar caché DNS", description: "Vacía la caché DNS local.", category: "Network", risk: "SAFE", requiresAdmin: true, connectivity: "OFFLINE", reversible: false, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "network.winsock.reset", title: "Restablecer Winsock", description: "Restablece Winsock; puede requerir reinicio.", category: "Network", risk: "EXPLICIT_CONFIRMATION", requiresAdmin: true, connectivity: "OFFLINE", reversible: false, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "drivers.analyze", title: "Analizar drivers", description: "Detecta dispositivos con códigos de problema.", category: "Drivers", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "drivers.rescan", title: "Reescanear hardware", description: "Pide a Plug and Play que vuelva a detectar dispositivos.", category: "Drivers", risk: "SAFE", requiresAdmin: true, connectivity: "OFFLINE", reversible: false, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "drivers.usb.analyze", title: "USB Repair Center", description: "Clasifica USB con problemas y cuáles pueden reiniciarse de forma controlada.", category: "Drivers", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "drivers.usb.restart", title: "Reiniciar dispositivo USB", description: "Reinicia solo el USB seleccionado y elegible.", category: "Drivers", risk: "CAUTION", requiresAdmin: true, connectivity: "OFFLINE", reversible: false, mode: "WRITE", parameters: [{ name: "deviceInstanceId", type: "STRING", required: true, description: "Instance ID seleccionado" }, { name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "system.activation.analyze", title: "Comprobar activación", description: "Lee el estado de licencia de Windows.", category: "System", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "browsers.inventory", title: "Inventario de navegadores", description: "Detecta navegadores y perfiles locales.", category: "Browsers", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "browsers.extensions.health", title: "Integridad de extensiones Edge", description: "Explica qué está correcto, qué son residuos y qué requiere reparación.", category: "Browsers", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "browsers.extensions.orphans.preview", title: "Revisar residuos de extensiones", description: "Muestra datos de extensiones ya desinstaladas y cuánto espacio ocupan.", category: "Browsers", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "browsers.extensions.orphans.quarantine", title: "Poner residuos en cuarentena", description: "Con Edge cerrado, mueve residuos a una cuarentena reversible.", category: "Browsers", risk: "CAUTION", requiresAdmin: false, connectivity: "OFFLINE", reversible: true, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "browsers.extensions.orphans.restore", title: "Restaurar última cuarentena Edge", description: "Devuelve los datos del último lote a su ubicación original.", category: "Browsers", risk: "CAUTION", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "multimedia.inventory", title: "Inventario multimedia", description: "Enumera dispositivos de audio y vídeo.", category: "Multimedia", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "multimedia.audio.restart", title: "Reiniciar audio de Windows", description: "Reinicia Windows Audio y verifica dispositivos.", category: "Multimedia", risk: "CAUTION", requiresAdmin: true, connectivity: "OFFLINE", reversible: false, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "startup.audit", title: "Auditar inicio y servicios", description: "Enumera inicio y servicios automáticos.", category: "Startup", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "startup.services.preview", title: "Revisar servicios automáticos", description: "Clasifica servicios protegidos y revisables.", category: "Startup", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "startup.service.setmode", title: "Cambiar inicio de servicio", description: "Cambia Manual/Disabled solo sobre servicios revisables.", category: "Startup", risk: "CAUTION", requiresAdmin: true, connectivity: "OFFLINE", reversible: true, mode: "WRITE", parameters: [{ name: "serviceName", type: "STRING", required: true, description: "Servicio exacto" }, { name: "targetMode", type: "STRING", required: true, description: "Manual o Disabled" }, { name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "startup.service.restore", title: "Restaurar inicio de servicio", description: "Restaura StartMode guardado.", category: "Startup", risk: "SAFE", requiresAdmin: true, connectivity: "OFFLINE", reversible: false, mode: "WRITE", parameters: [{ name: "serviceName", type: "STRING", required: true, description: "Servicio exacto" }, { name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "startup.entries.preview", title: "Revisar autoarranque Run/RunOnce", description: "Clasifica entradas protegidas, revisables y con rollback.", category: "Startup", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "startup.entry.disable", title: "Deshabilitar entrada de autoarranque", description: "Quita la entrada seleccionada y guarda snapshot sin cerrar el proceso actual.", category: "Startup", risk: "CAUTION", requiresAdmin: true, connectivity: "OFFLINE", reversible: true, mode: "WRITE", parameters: [{ name: "entryId", type: "STRING", required: true, description: "ID exacto" }, { name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "startup.entry.restore", title: "Restaurar entrada de autoarranque", description: "Restaura una entrada Run/RunOnce deshabilitada por la app.", category: "Startup", risk: "SAFE", requiresAdmin: true, connectivity: "OFFLINE", reversible: false, mode: "WRITE", parameters: [{ name: "entryId", type: "STRING", required: true, description: "ID exacto" }, { name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "windows.update.audit", title: "Auditar Windows Update", description: "Resume eventos recientes de Windows Update.", category: "WindowsUpdate", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "windows.update.services.restart", title: "Reiniciar servicios de Windows Update", description: "Reinicia BITS y Windows Update sin borrar historial.", category: "WindowsUpdate", risk: "CAUTION", requiresAdmin: true, connectivity: "OFFLINE", reversible: false, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "apps.inventory", title: "Inventario de aplicaciones", description: "Enumera aplicaciones instaladas.", category: "Apps", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "privacy.audit", title: "Auditar privacidad", description: "Lee configuraciones seleccionadas sin modificarlas.", category: "Privacy", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "developer.audit", title: "Auditar developer tooling", description: "Detecta toolchains y versiones.", category: "Developer", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "lab.reliability.status", title: "Reliability Lab", description: "Verifica regresiones históricas y barreras de seguridad sin tocar el Windows real.", category: "Developer", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "thermal.audit", title: "Auditar energía y temperaturas", description: "Lee plan de energía y sensores disponibles.", category: "Thermal", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "thermal.power.balanced", title: "Usar plan Equilibrado", description: "Activa Equilibrado y guarda rollback.", category: "Thermal", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: true, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "thermal.power.performance", title: "Usar Alto rendimiento", description: "Activa Alto rendimiento; aumenta consumo/temperatura.", category: "Thermal", risk: "CAUTION", requiresAdmin: false, connectivity: "OFFLINE", reversible: true, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "thermal.power.restore", title: "Restaurar plan de energía", description: "Restaura el plan anterior guardado.", category: "Thermal", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "boot.audit", title: "Auditar arranque", description: "Lee eventos de rendimiento de arranque.", category: "Boot", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "sleepresume.audit", title: "Auditar suspensión/reanudación", description: "Construye timeline de sleep/resume.", category: "SleepResume", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "explorer.audit", title: "Auditar Explorer", description: "Mide memoria, threads y respuesta.", category: "Explorer", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "explorer.restart", title: "Reiniciar Explorer", description: "Reinicia explorer.exe y verifica que vuelva.", category: "Explorer", risk: "CAUTION", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "backup.rollback.center", title: "Rollback Center", description: "Reúne todos los cambios reversibles disponibles.", category: "Backup", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "maintenance.policy.status", title: "Estado de automatización segura", description: "Muestra la política read-only; por defecto OFF.", category: "System", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "maintenance.policy.readonly", title: "Activar mantenimiento read-only en reposo", description: "Habilita solo el lote fijo de diagnósticos, nunca WRITE.", category: "System", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: true, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "maintenance.policy.off", title: "Desactivar automatización", description: "Vuelve a OFF.", category: "System", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: true, mode: "WRITE", parameters: [{ name: "confirmed", type: "BOOLEAN", required: true, description: "Confirmación explícita" }] },
  { id: "maintenance.safe.run", title: "Ejecutar lote read-only", description: "Ejecuta diagnósticos fijos solo cuando la política y el estado del PC lo permiten.", category: "System", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" },
  { id: "backup.status", title: "Estado de backup y rollback", description: "Detecta operaciones incompletas y snapshots.", category: "Backup", risk: "SAFE", requiresAdmin: false, connectivity: "OFFLINE", reversible: false, mode: "READ" }
];

const delay = (ms: number) => new Promise<void>(resolve => window.setTimeout(resolve, ms));
const hasLocalBridge = Boolean(window.chrome?.webview);
const provider: Provider = hasLocalBridge ? new LocalProvider() : new DemoProvider();
const queryParameters = new URLSearchParams(window.location.search);
const activity: Array<{ time: Date; title: string; detail: string; kind: "ok" | "warn" | "info" }> = [];

let currentSnapshot: SystemSnapshot | null = null;
let currentCatalog: ActionDefinition[] = [];
let currentReliabilityEvents: ReliabilityEvent[] = [];
let recoverableBytes: number | null = null;
let cleanupPreviewReady = false;
let latestTemperatureCelsius: number | null = null;
let latestTemperatureSensor = "Sensor no expuesto";
let activeView = "dashboard";
let actionInFlight = false;
// The backend runs one operation at a time. Background telemetry and
// user-triggered actions are therefore serialized here as well, so neither
// is rejected with "operación activa" because of the other.
let backgroundWork: Promise<void> | null = null;
const cpuHistory: number[] = [];
const networkHistory: number[] = [];
const HistoryLimit = 36;
const MaxProcessSelection = 20;

function byId<T extends HTMLElement>(id: string): T {
  const element = document.getElementById(id);
  if (!element) throw new Error("Elemento faltante: " + id);
  return element as T;
}

function formatBytes(value: number, decimals = 1): string {
  if (!Number.isFinite(value) || value < 0) return "No disponible";
  if (value === 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  // Values below one byte would otherwise produce a negative unit index.
  const index = Math.max(0, Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1));
  return (value / 1024 ** index).toFixed(index === 0 ? 0 : decimals) + " " + units[index];
}

function clampPercent(value: number): number {
  return Math.max(0, Math.min(100, Number.isFinite(value) ? value : 0));
}

function setBar(id: string, percent: number): void {
  (byId(id) as HTMLElement).style.width = clampPercent(percent).toFixed(1) + "%";
}

function pushHistory(history: number[], value: number): void {
  history.push(clampPercent(value));
  if (history.length > HistoryLimit)
    history.splice(0, history.length - HistoryLimit);
}

function renderSparkline(id: string, history: number[]): void {
  const line = document.getElementById(id) as SVGPolylineElement | null;
  if (!line || history.length === 0) return;

  const firstSample = history[0] ?? 0;
  const samples = history.length === 1
    ? [firstSample, firstSample]
    : history;

  line.setAttribute("points", samples
    .map((value, index) => {
      const x = samples.length <= 1
        ? 0
        : index / (samples.length - 1) * 100;
      const y = 22 - clampPercent(value) / 100 * 18;
      return x.toFixed(1) + "," + y.toFixed(1);
    })
    .join(" "));
}

function setHealth(prefix: string, status: string): void {
  const dot = document.getElementById("health-" + prefix);
  const text = document.getElementById("health-" + prefix + "-text");
  if (!dot || !text) return;

  const normalized = status.toUpperCase();
  const good = ["OK", "NO_REQUERIDO", "LICENSED"].includes(normalized);
  const warn = normalized.includes("WARNING") ||
    normalized.includes("PENDING") ||
    normalized.includes("REPAIRABLE") ||
    normalized.includes("GRACE");
  const bad = ["ERROR", "UNREPAIRABLE", "UNLICENSED"].includes(normalized);

  dot.className = "dot " + (good ? "good" : warn ? "warn" : bad ? "bad" : "neutral");
  text.textContent = normalized === "NOT_EVALUATED"
    ? "Sin evaluar"
    : normalized === "UNKNOWN"
      ? "Sin determinar"
      : status.replaceAll("_", " ");
}

const moduleDefinitions: Record<string, {
  kicker: string;
  title: string;
  description: string;
  actionIds: string[];
}> = {
  performance: {
    kicker: "RENDIMIENTO",
    title: "CPU y memoria",
    description: "Métricas locales y análisis seguros antes de aplicar optimizaciones.",
    actionIds: ["memory.analyze", "memory.trim.preview", "memory.pagefile.analyze", "cpu.ecoqos.analyze"]
  },
  cleanup: {
    kicker: "LIMPIEZA",
    title: "Almacenamiento seguro",
    description: "Análisis multiunidad, tendencias y previsualización de espacio recuperable sin borrar archivos.",
    actionIds: ["disk.scan", "disk.cleanup.safe", "disk.cleanup.execute", "disk.appdata.rank", "disk.volumes.audit", "disk.storage.watch", "disk.hotspots.scan", "disk.hibernate.status", "disk.hibernate.reduce"]
  },
  system: {
    kicker: "SISTEMA",
    title: "Estado y fiabilidad",
    description: "Evidencia de Windows, reinicios y señales de mantenimiento.",
    actionIds: ["system.actionplan.preview", "system.healthscore.analyze", "system.changes.analyze", "system.workload.status", "system.health.scan", "system.integrity.check", "system.integrity.repair", "system.reliability.analyze", "system.crash.analyze", "drivers.analyze", "drivers.rescan", "system.activation.analyze"]
  },
  network: {
    kicker: "RED",
    title: "Conectividad",
    description: "Diagnóstico y reparación controlada de conectividad local.",
    actionIds: ["network.test", "network.flushdns", "network.winsock.reset"]
  },
  browsers: {
    kicker: "NAVEGADORES",
    title: "Navegadores",
    description: "Diagnóstico comprensible de Edge con limpieza opcional y reversible de residuos.",
    actionIds: ["browsers.inventory", "browsers.extensions.health", "browsers.extensions.orphans.preview"]
  },
  multimedia: {
    kicker: "MULTIMEDIA",
    title: "Audio y vídeo",
    description: "Inventario multimedia y reinicio controlado del subsistema de audio.",
    actionIds: ["multimedia.inventory", "multimedia.audio.restart"]
  },
  memory: {
    kicker: "MEMORIA",
    title: "RAM",
    description: "Presión de memoria, MemoryTrim y pagefile limitado a un máximo razonable con rollback.",
    actionIds: ["memory.analyze", "memory.trim.preview", "memory.pagefile.analyze", "memory.pagefile.capped", "memory.pagefile.restore"]
  },
  cpu: {
    kicker: "CPU / ECOQOS",
    title: "Procesador y EcoQoS",
    description: "Carga de CPU y análisis de procesos elegibles antes de aplicar EcoQoS.",
    actionIds: ["cpu.ecoqos.analyze"]
  },
  startup: {
    kicker: "INICIO",
    title: "Inicio y servicios",
    description: "Audita inicio y permite ajustar servicios de terceros con protección y rollback.",
    actionIds: ["startup.audit", "startup.services.preview", "startup.entries.preview"]
  },
  integrity: {
    kicker: "INTEGRIDAD",
    title: "Integridad de Windows",
    description: "Comprueba, repara y verifica la integridad de Windows.",
    actionIds: ["system.health.scan", "system.integrity.check", "system.integrity.repair"]
  },
  drivers: {
    kicker: "HARDWARE",
    title: "Drivers y hardware",
    description: "Detecta problemas y permite reescanear Plug and Play sin instalar drivers arbitrarios.",
    actionIds: ["drivers.analyze", "drivers.usb.analyze", "drivers.rescan"]
  },
  "windows-update": {
    kicker: "WINDOWS UPDATE",
    title: "Actualizaciones",
    description: "Audita Windows Update y permite reiniciar sus servicios sin borrar historial.",
    actionIds: ["windows.update.audit", "windows.update.services.restart"]
  },
  apps: {
    kicker: "APLICACIONES",
    title: "Aplicaciones instaladas",
    description: "Inventario local read-only de software detectado.",
    actionIds: ["apps.inventory"]
  },
  privacy: {
    kicker: "PRIVACIDAD",
    title: "Privacidad",
    description: "Auditoría read-only de configuraciones seleccionadas.",
    actionIds: ["privacy.audit"]
  },
  developer: {
    kicker: "DEVELOPER",
    title: "Developer Performance",
    description: "Toolchains, versiones y señales relevantes para desarrollo.",
    actionIds: ["developer.audit"]
  },
  "reliability-lab": {
    kicker: "REAL-WORLD RELIABILITY",
    title: "Reliability Lab",
    description: "Regresiones históricas, outcome checks y barreras fail-closed. Los escenarios destructivos nunca se ejecutan sobre el Windows real.",
    actionIds: ["lab.reliability.status", "lab.outcomes.status"]
  },
  thermal: {
    kicker: "ENERGÍA / TEMPERATURAS",
    title: "Energía y sensores",
    description: "Observa temperaturas y permite cambiar el plan de energía con rollback.",
    actionIds: ["thermal.audit", "thermal.power.balanced", "thermal.power.performance", "thermal.power.restore"]
  },
  diagnostics: {
    kicker: "DIAGNÓSTICO AVANZADO",
    title: "Arranque, suspensión y Explorer",
    description: "Diagnóstico avanzado y reparación controlada de Explorer cuando corresponde.",
    actionIds: ["boot.audit", "sleepresume.audit", "explorer.audit", "explorer.restart"]
  },
  activation: {
    kicker: "ACTIVACIÓN",
    title: "Licencia de Windows",
    description: "Estado de activación reportado por Windows.",
    actionIds: ["system.activation.analyze"]
  },
  backup: {
    kicker: "RECOVERY",
    title: "Backups y rollback",
    description: "Operaciones incompletas y snapshots disponibles para rollback.",
    actionIds: ["backup.status", "backup.rollback.center"]
  },
  history: {
    kicker: "HISTORIAL",
    title: "Fiabilidad y reinicios",
    description: "Eventos recientes que explican apagados, reinicios y estabilidad.",
    actionIds: ["system.healthscore.analyze", "system.changes.analyze", "system.reliability.analyze", "system.crash.analyze"]
  },
  tools: {
    kicker: "HERRAMIENTAS",
    title: "Action Catalog",
    description: "Capacidades allowlisted disponibles en esta build.",
    actionIds: []
  },
  settings: {
    kicker: "CONFIGURACIÓN",
    title: "Aplicación y carga",
    description: "Controla carga y automatización segura. La automatización está OFF por defecto y nunca habilita WRITE.",
    actionIds: ["system.workload.status", "system.workload.inuse", "system.workload.auto", "system.workload.maintenance", "maintenance.policy.status", "maintenance.policy.readonly", "maintenance.policy.off", "maintenance.safe.run"]
  }
};

function formatDuration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return "No disponible";
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  if (days > 0) return days + " d " + hours + " h";
  const minutes = Math.floor((seconds % 3600) / 60);
  return hours + " h " + minutes + " min";
}

function createSummaryCard(
  label: string,
  value: string,
  detail: string
): HTMLElement {
  const card = document.createElement("article");
  card.className = "module-summary-card";
  const labelNode = document.createElement("span");
  labelNode.textContent = label;
  const valueNode = document.createElement("strong");
  valueNode.textContent = value;
  const detailNode = document.createElement("small");
  detailNode.textContent = detail;
  card.append(labelNode, valueNode, detailNode);
  return card;
}

function getModuleSummary(
  view: string
): Array<[string, string, string]> {
  const snapshot = currentSnapshot;
  if (!snapshot) {
    return [["Estado", "Cargando…", "Esperando datos del proveedor."]];
  }

  const memoryPercent = snapshot.memoryTotalBytes > 0
    ? snapshot.memoryUsedBytes / snapshot.memoryTotalBytes * 100
    : 0;

  switch (view) {
    case "performance":
      return [
        ["CPU", snapshot.cpuPercent.toFixed(0) + "%", "Uso total medido"],
        ["RAM", memoryPercent.toFixed(0) + "%", formatBytes(snapshot.memoryAvailableBytes) + " disponibles"],
        ["Uptime", formatDuration(snapshot.uptimeSeconds), "Desde el último arranque"]
      ];
    case "cleanup":
      return [
        ["Espacio libre", formatBytes(snapshot.diskFreeBytes), snapshot.diskDrive + " · unidad del sistema"],
        ["Capacidad", formatBytes(snapshot.diskTotalBytes), "Medición read-only"],
        ["Recuperable", recoverableBytes === null ? "Sin analizar" : formatBytes(recoverableBytes), "Estimación segura"]
      ];
    case "system":
      return [
        ["Reinicio", snapshot.rebootRequired === null ? "Sin determinar" : snapshot.rebootRequired ? "Pendiente" : "No requerido", "Estado detectado en Windows"],
        ["Evidencia", String(currentReliabilityEvents.length), "Eventos relevantes cargados"],
        ["Integridad", snapshot.integrityStatus === "NOT_EVALUATED" ? "Sin evaluar" : snapshot.integrityStatus, "No se inventan estados"]
      ];
    case "network":
      return [
        ["Adaptador", snapshot.network?.name ?? "Sin conexión", "Interfaz activa"],
        ["Enlace", snapshot.network ? Math.round(snapshot.network.linkMbps) + " Mbps" : "—", "Velocidad reportada"],
        ["Tráfico", snapshot.network ? "↓ " + snapshot.network.receiveMegabytesPerSecond.toFixed(1) + " · ↑ " + snapshot.network.sendMegabytesPerSecond.toFixed(1) + " MB/s" : "—", "Muestra local"]
      ];
    case "browsers":
      return [
        ["Modo", "Read-only", "No abre ni modifica perfiles"],
        ["Acción", "Inventario", "Detección local"],
        ["Privilegios", "Estándar", "Sin UAC"]
      ];
    case "multimedia":
      return [
        ["Modo", "Read-only", "WMI local"],
        ["Ámbitos", "Audio + vídeo", "Dispositivos registrados"],
        ["Privilegios", "Estándar", "Sin UAC"]
      ];
    case "thermal":
      return [
        ["Temperatura", latestTemperatureCelsius === null ? "No disponible" : latestTemperatureCelsius.toFixed(0) + " °C", latestTemperatureSensor],
        ["Modo", "Read-only", "Sin cambios en Windows"],
        ["Fuente", latestTemperatureCelsius === null ? "No expuesta" : "Sensor real", "ACPI / driver compatible"]
      ];
    case "memory":
      return [
        ["RAM", memoryPercent.toFixed(0) + "%", formatBytes(snapshot.memoryAvailableBytes) + " disponibles"],
        ["Uptime", formatDuration(snapshot.uptimeSeconds), "Desde el último arranque"],
        ["Modo", "Read-only", "MemoryTrim requiere selección explícita"]
      ];
    case "cpu":
      return [
        ["CPU", snapshot.cpuPercent.toFixed(0) + "%", "Uso total medido"],
        ["EcoQoS", "Análisis previo", "No aplica cambios automáticamente"],
        ["Modo", "Seguro", "Selección explícita antes de WRITE"]
      ];
    case "backup":
      return [
        ["Centro", "Rollback", "Cambios reversibles en una sola vista"],
        ["Política", "Explícita", "Nunca restaura sin confirmación"],
        ["Evidencia", "Persistida", "Estado propio de la app"]
      ];
    case "settings":
      return [
        ["Modo", "AUTO / EN USO", "Protección de tareas pesadas"],
        ["Mantenimiento", "Explícito", "Se habilita solo cuando lo decidís"],
        ["Disco D:", "Guardado", "Por debajo del 3% bloquea carga pesada"]
      ];
    case "history":
      return [
        ["Evidencia", String(currentReliabilityEvents.length), "Eventos relevantes cargados"],
        ["Reinicio", snapshot.rebootRequired === null ? "Sin determinar" : snapshot.rebootRequired ? "Pendiente" : "No requerido", "Estado de Windows"],
        ["Modo", "Read-only", "Sin cambios automáticos"]
      ];
    case "reliability-lab":
      return [
        ["Método", "Outcome-first", "Estado final, no solo exit code"],
        ["Evals", "Fixtures / CI", "Nunca destructivos sobre el Windows real"],
        ["Política", "Fail-closed", "Targets desconocidos se bloquean"]
      ];
    case "tools":
      return [
        ["Acciones", String(currentCatalog.length), "Allowlisted"],
        ["Seguras", String(currentCatalog.filter(action => action.risk === "SAFE").length), "Riesgo SAFE"],
        ["Escritura", String(currentCatalog.filter(action => action.mode === "WRITE").length), "Capacidades WRITE habilitadas"]
      ];
    default:
      return [
        ["Superficie", provider.surface === "local" ? "LOCAL" : "DEMO", "Proveedor activo"],
        ["Estado", snapshot.operationState, "Interlock global"],
        ["Runtime UI", provider.surface === "local" ? "WebView2" : "Browser", "Frontend compartido"]
      ];
  }
}

// Periodic refreshes only touch the read-only summary cards. Rebuilding the
// action buttons on every snapshot would drop keyboard focus and discard the
// busy state of a running action.
function renderModuleSummary(view: string): void {
  if (!moduleDefinitions[view]) return;

  byId("module-summary").replaceChildren(
    ...getModuleSummary(view).map(([label, value, detail]) =>
      createSummaryCard(label, value, detail))
  );
}

function renderModuleView(view: string): void {
  const definition = moduleDefinitions[view];
  if (!definition) throw new Error("Vista no soportada: " + view);

  byId("module-kicker").textContent = definition.kicker;
  byId("module-title").textContent = definition.title;
  byId("module-description").textContent = definition.description;

  renderModuleSummary(view);

  const actionsHost = byId("module-actions");
  const actions = view === "tools"
    ? currentCatalog
    : currentCatalog.filter(action =>
        definition.actionIds.includes(action.id));

  if (actions.length === 0) {
    const empty = document.createElement("div");
    empty.className = "empty";
    empty.textContent = view === "settings"
      ? "No hay acciones operativas en Configuración."
      : "No hay acciones disponibles para este módulo.";
    actionsHost.replaceChildren(empty);
    return;
  }

  actionsHost.replaceChildren(...actions.map(action => {
    const card = document.createElement("article");
    card.className = "module-action";

    const copy = document.createElement("div");
    const title = document.createElement("h3");
    title.textContent = action.title;
    const description = document.createElement("p");
    description.textContent =
      action.description + " · " + action.risk + " · " + action.mode;
    copy.append(title, description);

    const button = document.createElement("button");
    button.className = "btn btn-primary";
    button.dataset.action = action.id;
    const requiredParameters =
      action.parameters?.filter(parameter => parameter.required) ?? [];
    const selectionParameters = requiredParameters.filter(
      parameter => parameter.name !== "confirmed");
    const hasRequiredParameters = requiredParameters.length > 0;
    const needsSelection = selectionParameters.length > 0;
    button.textContent = action.mode === "WRITE"
      ? needsSelection
        ? "Desde análisis previo"
        : action.requiresAdmin
          ? "Aplicar (UAC)"
          : "Aplicar"
      : action.requiresAdmin
        ? "Solicitar UAC"
        : action.mode === "READ" ? "Analizar" : "Previsualizar";
    if (action.id === "disk.cleanup.execute") {
      button.textContent = "Limpiar (confirmar)";
      button.disabled = !cleanupPreviewReady;
      button.title = cleanupPreviewReady ? "Limpiar solo cachés antiguas, con confirmación." : "Primero previsualizá la limpieza.";
      button.addEventListener("click", () => {
        if (!cleanupPreviewReady) return;
        if (!window.confirm("¿Eliminar únicamente cachés regenerables con más de 7 días?\nNo se tocarán sesiones, extensiones instaladas, WhatsApp ni documentos.")) return;
        void runAction(action.id, button, { confirmed: true });
      });
    } else if (action.id === "disk.hibernate.reduce") {
      button.textContent = "Reducir (UAC)";
      button.title = "Conserva Inicio rápido, pero deshabilita la hibernación completa.";
      button.addEventListener("click", () => {
        if (!window.confirm("¿Reducir hiberfil.sys? Se conserva Inicio rápido, pero ya no podrás hibernar completamente. Windows solicitará UAC.")) return;
        void runAction(action.id, button, { confirmed: true });
      });
    } else if (needsSelection && action.mode === "WRITE") {
      button.disabled = true;
      button.title = "Primero ejecutá el análisis que genera una selección segura.";
    } else if (action.mode === "WRITE") {
      button.addEventListener("click", () => {
        const warning = action.risk === "EXPLICIT_CONFIRMATION"
          ? "Este cambio puede requerir reinicio o afectar temporalmente la conectividad."
          : action.reversible
            ? "La app guardará rollback cuando la acción lo soporte."
            : "La app verificará el resultado después de aplicar el cambio.";
        if (!window.confirm(
          action.title + "\n\n" + action.description + "\n\n" +
          warning + "\n\n¿Continuar?"
        )) return;
        void runAction(action.id, button, hasRequiredParameters
          ? { confirmed: true }
          : {});
      });
    } else {
      button.addEventListener("click", () => runAction(action.id, button));
    }

    card.append(copy, button);
    return card;
  }));
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null;
}

function asArray(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function displayValue(value: unknown): string {
  if (value === null || value === undefined) return "—";
  if (typeof value === "boolean") return value ? "Sí" : "No";
  if (typeof value === "number") return Number.isFinite(value)
    ? value.toLocaleString()
    : "—";
  return String(value);
}

function createResultGrid(
  entries: Array<[string, string]>
): HTMLElement {
  const grid = document.createElement("div");
  grid.className = "result-grid";

  for (const [label, value] of entries) {
    const item = document.createElement("div");
    item.className = "result-kv";
    const labelNode = document.createElement("span");
    labelNode.textContent = label;
    const valueNode = document.createElement("strong");
    valueNode.textContent = value;
    item.append(labelNode, valueNode);
    grid.append(item);
  }
  return grid;
}

function createResultTable(
  headers: string[],
  rows: string[][]
): HTMLElement {
  const wrap = document.createElement("div");
  wrap.className = "result-table-wrap";
  const table = document.createElement("table");
  const head = table.createTHead().insertRow();
  for (const header of headers) {
    const cell = document.createElement("th");
    cell.scope = "col";
    cell.textContent = header;
    head.append(cell);
  }

  const body = table.createTBody();
  for (const values of rows) {
    const row = body.insertRow();
    for (const value of values) {
      row.insertCell().textContent = value;
    }
  }
  wrap.append(table);
  return wrap;
}

function renderRecoveryCategories(data: Record<string, unknown>): void {
  const body = document.getElementById("recovery-body") as HTMLTableSectionElement | null;
  if (!body) return;

  const categories = asArray(data.categories)
    .map(asRecord)
    .filter((item): item is Record<string, unknown> => item !== null);

  if (!categories.length) return;

  body.replaceChildren();

  for (const item of categories) {
    const row = body.insertRow();
    const label = displayValue(item.label);
    const bytes = typeof item.bytes === "number" ? formatBytes(item.bytes) : "—";
    const risk = displayValue(item.risk);
    const riskLower = risk.toLowerCase();

    row.insertCell().textContent = label;
    row.insertCell().textContent = label.includes("Windows")
      ? "Temporales del sistema detectados"
      : "Archivos regenerables detectados";
    row.insertCell().textContent = bytes;
    row.insertCell().textContent = bytes;

    const riskCell = row.insertCell();
    const chip = document.createElement("span");
    chip.className = "risk-chip " + (riskLower.includes("low") || riskLower.includes("safe") ? "safe" : "caution");
    chip.textContent = riskLower.includes("low") || riskLower.includes("safe") ? "Seguro" : "Precaución";
    riskCell.append(chip);

    row.insertCell().textContent = "Revisar";
  }
}

function hideModuleResult(): void {
  byId("module-result-panel").classList.add("is-hidden");
  byId("module-result-content").replaceChildren();
}

function renderActionResult(
  actionId: string,
  result: ActionResult
): void {
  if (activeView === "dashboard") return;

  const panel = byId("module-result-panel");
  const content = byId("module-result-content");
  const status = byId("module-result-status");
  const data = asRecord(result.data);

  panel.classList.remove("is-hidden");
  content.replaceChildren();
  byId("module-result-title").textContent =
    currentCatalog.find(action => action.id === actionId)?.title ?? actionId;
  byId("module-result-subtitle").textContent =
    "Evidencia de la última ejecución local.";
  status.textContent = result.success ? (result.dryRun ? "DRY-RUN" : "OK") : "WARNING";
  status.className = "badge " + (result.success ? "badge-green" : "");
  if (actionId === "browsers.extensions.health" && data) {
    const broken = typeof data.brokenCount === "number" ? data.brokenCount : 0;
    const residues = typeof data.dataOnlyCount === "number" ? data.dataOnlyCount : 0;
    status.textContent = broken > 0
      ? "REVISAR"
      : residues > 0
        ? "LIMPIEZA OPCIONAL"
        : "OK";
    status.className = "badge " +
      (broken === 0 && residues === 0 ? "badge-green" : "");
  }

  const message = document.createElement("p");
  message.className = "result-message";
  message.textContent = result.message;
  content.append(message);

  renderStructuredResult(actionId, data, content);
}

function createProcessSelection(
  candidates: Record<string, unknown>[],
  actionId: "memory.trim" | "cpu.ecoqos.apply",
  label: string
): HTMLElement {
  const host = document.createElement("div");
  host.className = "process-selection";

  const wrap = document.createElement("div");
  wrap.className = "result-table-wrap";
  const table = document.createElement("table");
  const head = table.createTHead().insertRow();
  for (const header of ["Seleccionar", "PID", "Proceso", "Working set"]) {
    const cell = document.createElement("th");
    cell.scope = "col";
    cell.textContent = header;
    head.append(cell);
  }

  const body = table.createTBody();
  for (const item of candidates) {
    const processId = typeof item.processId === "number"
      ? item.processId
      : 0;
    if (processId <= 0) continue;

    const row = body.insertRow();
    const selectCell = row.insertCell();
    const input = document.createElement("input");
    input.type = "checkbox";
    input.checked = false;
    input.dataset.processId = String(processId);
    input.setAttribute("aria-label", "Seleccionar " + displayValue(item.name));
    selectCell.append(input);
    row.insertCell().textContent = String(processId);
    row.insertCell().textContent = displayValue(item.name);
    row.insertCell().textContent =
      typeof item.workingSetBytes === "number"
        ? formatBytes(item.workingSetBytes)
        : "—";
  }
  wrap.append(table);

  const controls = document.createElement("div");
  controls.className = "result-actions";
  const notice = document.createElement("span");
  notice.textContent = actionId === "memory.trim"
    ? "El efecto sobre working sets es transitorio."
    : "Se guardará estado previo para rollback.";
  const button = document.createElement("button");
  button.className = "btn btn-primary";
  button.textContent = label;
  button.addEventListener("click", async () => {
    const processIds = Array.from(
      host.querySelectorAll<HTMLInputElement>(
        'input[type="checkbox"]:checked'))
      .map(input => Number(input.dataset.processId))
      .filter(Number.isInteger);

    if (processIds.length === 0) {
      toast("Selección requerida", "Seleccioná al menos un proceso.", "error");
      return;
    }

    if (processIds.length > MaxProcessSelection) {
      toast(
        "Demasiados procesos",
        `Seleccioná como máximo ${MaxProcessSelection} procesos por operación.`,
        "error");
      return;
    }

    const confirmed = window.confirm(
      actionId === "memory.trim"
        ? `MemoryTrim actuará sobre ${processIds.length} proceso(s). El cambio de working set es transitorio. ¿Continuar?`
        : `EcoQoS se aplicará a ${processIds.length} proceso(s) de fondo. Se guardará estado para rollback. ¿Continuar?`);
    if (!confirmed) return;

    await runAction(
      actionId,
      button,
      { processIds, confirmed: true });
  });

  controls.append(notice, button);
  host.append(wrap, controls);
  return host;
}

function createEcoQosRollback(
  items: Record<string, unknown>[]
): HTMLElement | null {
  const processIds = items
    .filter(item => item.success === true &&
      typeof item.processId === "number")
    .map(item => item.processId as number)
    .slice(0, MaxProcessSelection);

  if (!processIds.length) return null;

  const controls = document.createElement("div");
  controls.className = "result-actions";
  const notice = document.createElement("span");
  notice.textContent = "Rollback disponible para los procesos modificados.";
  const button = document.createElement("button");
  button.className = "btn btn-secondary";
  button.textContent = "Restaurar EcoQoS";
  button.addEventListener("click", async () => {
    if (!window.confirm(
      `Restaurar el estado EcoQoS previo de ${processIds.length} proceso(s)?`))
      return;

    await runAction(
      "cpu.ecoqos.restore",
      button,
      { processIds, confirmed: true });
  });
  controls.append(notice, button);
  return controls;
}

function createServiceStartupControls(
  services: Record<string, unknown>[]
): HTMLElement {
  const host = document.createElement("div");
  host.className = "process-selection";

  const wrap = document.createElement("div");
  wrap.className = "result-table-wrap";
  const table = document.createElement("table");
  const head = table.createTHead().insertRow();
  for (const header of ["Servicio", "Estado", "Clasificación", "Motivo", "Acciones"]) {
    const cell = document.createElement("th");
    cell.textContent = header;
    head.append(cell);
  }

  const body = table.createTBody();
  for (const service of services) {
    const row = body.insertRow();
    const protectedService = service.protected === true;
    const serviceName = String(service.serviceName ?? "");
    const displayName = String(service.displayName ?? serviceName);

    for (const value of [
      displayName,
      displayValue(service.state),
      protectedService ? "Protegido" : "Revisable",
      displayValue(service.reason)
    ]) {
      const cell = row.insertCell();
      cell.textContent = value;
    }

    const actions = row.insertCell();
    actions.className = "inline-actions";
    if (protectedService || !serviceName) {
      actions.textContent = "Sin cambios automáticos";
      continue;
    }

    const manual = document.createElement("button");
    manual.className = "btn btn-small";
    manual.textContent = "Manual";
    manual.addEventListener("click", () => {
      if (!window.confirm(
        displayName +
        "\n\nCambiar inicio de Automático a Manual. " +
        "El servicio que ya esté ejecutándose NO se detendrá ahora. ¿Continuar?"
      )) return;
      void runAction(
        "startup.service.setmode",
        manual,
        { serviceName, targetMode: "Manual", confirmed: true });
    });

    const disable = document.createElement("button");
    disable.className = "btn btn-small";
    disable.textContent = "Deshabilitar";
    disable.addEventListener("click", () => {
      if (!window.confirm(
        displayName +
        "\n\nDESHABILITAR el inicio del servicio. " +
        "No se forzará su cierre ahora y habrá rollback del StartMode. ¿Continuar?"
      )) return;
      void runAction(
        "startup.service.setmode",
        disable,
        { serviceName, targetMode: "Disabled", confirmed: true });
    });

    actions.append(manual, disable);

    if (service.restoreAvailable === true) {
      const restore = document.createElement("button");
      restore.className = "btn btn-small";
      restore.textContent = "Restaurar";
      restore.addEventListener("click", () => {
        if (!window.confirm(
          "¿Restaurar el modo de inicio guardado para " + displayName + "?"
        )) return;
        void runAction(
          "startup.service.restore",
          restore,
          { serviceName, confirmed: true });
      });
      actions.append(restore);
    }
  }

  wrap.append(table);
  host.append(wrap);
  return host;
}

function renderStructuredResult(  actionId: string,
  data: Record<string, unknown> | null,
  content: HTMLElement
): void {
  if (!data) return;

  if (actionId === "system.integrity.check") {
    content.append(createResultGrid([
      ["Estado", displayValue(data.status)],
      ["Código DISM", displayValue(data.exitCode)],
      ["Duración", typeof data.durationMs === "number"
        ? Math.round(data.durationMs) + " ms" : "—"]
    ]));
    if (typeof data.output === "string" && data.output.trim()) {
      const pre = document.createElement("pre");
      pre.className = "result-pre";
      pre.textContent = data.output;
      content.append(pre);
    }
    if (data.status === "REPAIRABLE") {
      const controls = document.createElement("div");
      controls.className = "result-actions";
      const note = document.createElement("span");
      note.textContent = "Windows informó corrupción reparable. DISM /RestoreHealth + SFC puede tardar bastante.";
      const repair = document.createElement("button");
      repair.className = "btn btn-primary";
      repair.textContent = "Reparar y verificar (UAC)";
      repair.addEventListener("click", () => {
        if (!window.confirm(
          "¿Ejecutar DISM /RestoreHealth y después SFC /scannow? " +
          "La operación puede tardar varios minutos."
        )) return;
        void runAction(
          "system.integrity.repair",
          repair,
          { confirmed: true });
      });
      controls.append(note, repair);
      content.append(controls);
    }
    return;
  }

  if (actionId === "drivers.analyze") {
    const problems = asArray(data.problems)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    content.append(createResultGrid([
      ["Estado", displayValue(data.status)],
      ["Problemas", displayValue(data.problemCount)],
      ["Mostrados", String(problems.length)]
    ]));
    if (problems.length) {
      content.append(createResultTable(
        ["Dispositivo", "Código", "Fabricante"],
        problems.map(item => [
          displayValue(item.name),
          displayValue(item.errorCode),
          displayValue(item.manufacturer)
        ])
      ));
      const controls = document.createElement("div");
      controls.className = "result-actions";
      const note = document.createElement("span");
      note.textContent = "Primero reescanea Plug and Play. La app no instalará un driver arbitrario.";
      const rescan = document.createElement("button");
      rescan.className = "btn btn-primary";
      rescan.textContent = "Reescanear hardware (UAC)";
      rescan.addEventListener("click", () => {
        if (!window.confirm("¿Pedir a Windows que vuelva a detectar el hardware?")) return;
        void runAction("drivers.rescan", rescan, { confirmed: true });
      });
      controls.append(note, rescan);
      content.append(controls);
    }
    return;
  }

  if (actionId === "system.activation.analyze") {
    content.append(createResultGrid([
      ["Estado", displayValue(data.status)],
      ["LicenseStatus", displayValue(data.licenseStatus)],
      ["Descripción", displayValue(data.description)]
    ]));
    return;
  }

  if (actionId === "browsers.inventory") {
    const browsers = asArray(data.browsers)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    content.append(createResultTable(
      ["Navegador", "Versión", "Perfil", "Ejecutable"],
      browsers.map(item => [
        displayValue(item.name),
        displayValue(item.version),
        item.profileDetected === true ? "Detectado" : "No",
        displayValue(item.executablePath)
      ])
    ));
    return;
  }

  if (actionId === "browsers.extensions.health") {
    const items = asArray(data.items)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    const dataOnlyCount = typeof data.dataOnlyCount === "number" ? data.dataOnlyCount : 0;
    const brokenCount = typeof data.brokenCount === "number" ? data.brokenCount : 0;
    const orphanBytes = typeof data.orphanBytes === "number" ? data.orphanBytes : 0;
    const humanStatus = brokenCount > 0
      ? "Requiere revisión"
      : dataOnlyCount > 0
        ? "Limpieza opcional"
        : "Todo correcto";

    content.append(createResultGrid([
      ["Resultado", humanStatus],
      ["Instaladas", displayValue(data.installedCount)],
      ["Residuos", String(dataOnlyCount)],
      ["Espacio de residuos", formatBytes(orphanBytes)],
      ["Problemas reales", String(brokenCount)],
      ["Developer Mode", data.developerMode === true
        ? "Activo" : data.developerMode === false ? "Desactivado" : "Sin determinar"]
    ]));

    const guidance = document.createElement("div");
    guidance.className = "result-guidance";
    const guidanceTitle = document.createElement("strong");
    guidanceTitle.textContent = brokenCount > 0
      ? "Qué requiere tu atención"
      : dataOnlyCount > 0
        ? "Qué significa"
        : "Qué hacer ahora";
    const guidanceText = document.createElement("span");
    guidanceText.textContent = brokenCount > 0
      ? String(brokenCount) + " extensión(es) sí tienen código o manifest ausente. No se eliminan automáticamente porque podrían ser instalaciones recuperables."
      : dataOnlyCount > 0
        ? String(dataOnlyCount) + " entradas son solo datos de extensiones que ya no están instaladas. No están corriendo ni consumen RAM/CPU; únicamente ocupan " + formatBytes(orphanBytes) + "."
        : "No necesitas hacer nada. Las extensiones instaladas están íntegras.";
    guidance.append(guidanceTitle, guidanceText);
    content.append(guidance);

    const statusInfo = (status: unknown): [string, string, string] => {
      switch (String(status ?? "")) {
        case "DATA_WITHOUT_INSTALLATION":
          return ["Residuo", "Datos de una extensión ya desinstalada", "Cuarentena opcional"];
        case "CODE_MISSING":
          return ["Instalación rota", "Edge la registra pero faltan archivos", "Revisar en Edge"];
        case "MANIFEST_MISSING":
          return ["Instalación incompleta", "Existe código pero falta manifest.json", "Revisar / reinstalar"];
        case "UNPACKED":
          return ["Extensión local", "Cargada manualmente en modo desarrollador", "Sin acción automática"];
        case "POLICY_REMOVED":
          return ["Retirada por política", "Windows/Edge la retiró mediante política", "Informativo"];
        case "STALE_METADATA":
          return ["Metadatos antiguos", "Registro residual sin instalación activa", "Informativo"];
        case "INSTALLED_NO_LOCAL_DATA":
          return ["Correcta", "Instalada sin datos locales propios", "Nada"];
        case "OK":
          return ["Correcta", "Instalación y datos coherentes", "Nada"];
        default:
          return [displayValue(status), "Estado técnico sin clasificar", "Revisar"];
      }
    };

    const relevantItems = items.filter(item =>
      String(item.status ?? "") !== "OK" &&
      String(item.status ?? "") !== "INSTALLED_NO_LOCAL_DATA");

    if (relevantItems.length) {
      content.append(createResultTable(
        ["Perfil", "Extensión", "Situación", "Qué significa", "Datos", "Qué hacer"],
        relevantItems.map(item => {
          const info = statusInfo(item.status);
          const rawName = typeof item.name === "string" && item.name.trim()
            ? item.name
            : typeof item.extensionId === "string"
              ? "Extensión " + item.extensionId.slice(0, 8) + "…"
              : "Extensión";
          return [
            displayValue(item.profile),
            rawName,
            info[0],
            info[1],
            typeof item.dataBytes === "number" ? formatBytes(item.dataBytes) : "—",
            info[2]
          ];
        })
      ));
    }

    if (dataOnlyCount > 0) {
      const controls = document.createElement("div");
      controls.className = "result-actions";
      const note = document.createElement("span");
      note.textContent = "Primero previsualiza. La limpieza moverá residuos a cuarentena; no los borrará definitivamente.";
      const button = document.createElement("button");
      button.className = "btn btn-primary";
      button.textContent = "Revisar residuos →";
      button.addEventListener("click", () =>
        void runAction("browsers.extensions.orphans.preview", button));
      controls.append(note, button);
      content.append(controls);
    }
    return;
  }

  if (actionId === "browsers.extensions.orphans.preview") {
    const candidates = asArray(data.candidates)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    const count = typeof data.candidateCount === "number" ? data.candidateCount : candidates.length;
    const totalBytes = typeof data.totalBytes === "number" ? data.totalBytes : 0;
    const edgeRunning = data.edgeRunning === true;
    const restoreAvailable = data.restoreAvailable === true;

    content.append(createResultGrid([
      ["Residuos", String(count)],
      ["Espacio", formatBytes(totalBytes)],
      ["Edge", edgeRunning ? "Abierto" : "Cerrado"],
      ["Acción", count > 0 ? "Cuarentena reversible" : "Nada que limpiar"],
      ["Rollback", restoreAvailable ? "Disponible" : "Sin cuarentenas previas"],
      ["Impacto RAM/CPU", "Ninguno: no están ejecutándose"]
    ]));

    const guidance = document.createElement("div");
    guidance.className = "result-guidance";
    const strong = document.createElement("strong");
    strong.textContent = edgeRunning
      ? "Cierra Edge para continuar"
      : "Limpieza segura disponible";
    const span = document.createElement("span");
    span.textContent = edgeRunning
      ? "No se moverá ningún dato mientras Edge esté abierto. Cierra todas sus ventanas y vuelve a ejecutar esta revisión."
      : "Solo se moverán carpetas de Local Extension Settings que pertenecen a extensiones ya no instaladas. Puedes restaurarlas después.";
    guidance.append(strong, span);
    content.append(guidance);

    if (candidates.length) {
      content.append(createResultTable(
        ["Perfil", "Extensión", "Datos", "Resultado si aplicas"],
        candidates.map(item => [
          displayValue(item.profile),
          typeof item.extensionId === "string"
            ? item.extensionId.slice(0, 8) + "…"
            : "—",
          typeof item.dataBytes === "number" ? formatBytes(item.dataBytes) : "—",
          "Mover a cuarentena"
        ])
      ));
    }

    const controls = document.createElement("div");
    controls.className = "result-actions";
    const note = document.createElement("span");
    note.textContent = count === 0
      ? "No hay residuos que resolver."
      : edgeRunning
        ? "Bloqueado mientras Edge esté abierto."
        : "No borra definitivamente; crea un lote de rollback.";

    if (count > 0) {
      const quarantine = document.createElement("button");
      quarantine.className = "btn btn-primary";
      quarantine.textContent = "Poner en cuarentena";
      quarantine.disabled = edgeRunning;
      quarantine.addEventListener("click", () => {
        if (!window.confirm(
          "¿Mover " + String(count) + " residuo(s) (" + formatBytes(totalBytes) + ") a cuarentena reversible?"
        )) return;
        void runAction(
          "browsers.extensions.orphans.quarantine",
          quarantine,
          { confirmed: true });
      });
      controls.append(note, quarantine);
    } else {
      controls.append(note);
    }

    if (restoreAvailable) {
      const restore = document.createElement("button");
      restore.className = "btn";
      restore.textContent = "Restaurar última cuarentena";
      restore.disabled = edgeRunning;
      restore.addEventListener("click", () => {
        if (!window.confirm(
          "¿Restaurar el último lote de residuos de Edge a su ubicación original?"
        )) return;
        void runAction(
          "browsers.extensions.orphans.restore",
          restore,
          { confirmed: true });
      });
      controls.append(restore);
    }
    content.append(controls);
    return;
  }

  if (actionId === "browsers.extensions.orphans.quarantine") {
    content.append(createResultGrid([
      ["Movidos", displayValue(data.movedCount)],
      ["Datos", typeof data.movedBytes === "number" ? formatBytes(data.movedBytes) : "—"],
      ["Omitidos", displayValue(data.skippedCount)],
      ["Rollback", data.rollbackAvailable === true ? "Disponible" : "No"],
      ["Lote", displayValue(data.batchId)],
      ["Estado", displayValue(data.status)]
    ]));
    return;
  }

  if (actionId === "browsers.extensions.orphans.restore") {
    content.append(createResultGrid([
      ["Restaurados", displayValue(data.restoredCount)],
      ["Datos", typeof data.restoredBytes === "number" ? formatBytes(data.restoredBytes) : "—"],
      ["Lote", displayValue(data.batchId)],
      ["Estado", displayValue(data.status)]
    ]));
    return;
  }

  if (actionId === "startup.services.preview") {
    const services = asArray(data.services)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    content.append(createResultGrid([
      ["Automáticos", displayValue(data.automaticCount)],
      ["Revisables", displayValue(data.eligibleCount)],
      ["Protegidos", displayValue(data.protectedCount)],
      ["Política", "Fail-closed"],
      ["Cambio seguro", "Manual antes que Disabled"],
      ["Rollback", "StartMode guardado"]
    ]));
    if (services.length) {
      content.append(createServiceStartupControls(services));
    }
    return;
  }

  if (actionId === "multimedia.inventory") {
    const devices = asArray(data.devices)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    content.append(createResultTable(
      ["Tipo", "Dispositivo", "Estado"],
      devices.map(item => [
        displayValue(item.kind),
        displayValue(item.name),
        displayValue(item.status)
      ])
    ));
    return;
  }

  if (actionId === "memory.pagefile.analyze") {
    const entries = asArray(data.entries)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    content.append(createResultGrid([
      ["Administración", data.automaticallyManaged === true
        ? "Automática" : data.automaticallyManaged === false ? "Manual" : "No determinada"],
      ["Pagefiles", String(entries.length)],
      ["Perfil recomendado", "C: 512 MB + D: 4096–8192 MB"],
      ["Techo recomendado", "8 GB en D: (< 10 GB)"]
    ]));
    if (entries.length) {
      content.append(createResultTable(
        ["Archivo", "Inicial", "Máximo", "Asignado", "Uso actual", "Pico"],
        entries.map(item => [
          displayValue(item.name),
          typeof item.initialSizeMb === "number" ? item.initialSizeMb + " MB" : "—",
          typeof item.maximumSizeMb === "number" ? item.maximumSizeMb + " MB" : "—",
          typeof item.allocatedMb === "number" ? item.allocatedMb + " MB" : "—",
          typeof item.currentUsageMb === "number" ? item.currentUsageMb + " MB" : "—",
          typeof item.peakUsageMb === "number" ? item.peakUsageMb + " MB" : "—"
        ])
      ));
    }
    return;
  }

  if (actionId === "memory.trim.preview" ||
      actionId === "cpu.ecoqos.analyze") {
    const candidates = asArray(data.candidates)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    content.append(createResultGrid([
      ["Procesos observados", displayValue(data.observedProcesses)],
      ["Candidatos elegibles", String(candidates.length)],
      ["Modo", "Análisis previo"]
    ]));
    if (candidates.length) {
      content.append(createProcessSelection(
        candidates,
        actionId === "memory.trim.preview"
          ? "memory.trim"
          : "cpu.ecoqos.apply",
        actionId === "memory.trim.preview"
          ? "Aplicar MemoryTrim seleccionado"
          : "Aplicar EcoQoS seleccionado"));
    }
    return;
  }

  if (actionId === "memory.trim" ||
      actionId === "cpu.ecoqos.apply" ||
      actionId === "cpu.ecoqos.restore") {
    const items = asArray(data.items)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);

    content.append(createResultGrid([
      ["Intentados", displayValue(data.attempted)],
      ["Correctos", displayValue(data.succeeded)],
      ["Fallidos", displayValue(data.failed)]
    ]));

    if (items.length) {
      content.append(createResultTable(
        ["PID", "Proceso", "Resultado", "Antes", "Después"],
        items.map(item => [
          displayValue(item.processId),
          displayValue(item.name),
          item.success === true ? "OK" : displayValue(item.error),
          actionId === "memory.trim" &&
          typeof item.beforeWorkingSetBytes === "number"
            ? formatBytes(item.beforeWorkingSetBytes)
            : displayValue(item.beforeEcoQos),
          actionId === "memory.trim" &&
          typeof item.afterWorkingSetBytes === "number"
            ? formatBytes(item.afterWorkingSetBytes)
            : displayValue(item.afterEcoQos)
        ])
      ));
    }

    if (actionId === "cpu.ecoqos.apply") {
      const rollback = createEcoQosRollback(items);
      if (rollback) content.append(rollback);
    }
    return;
  }

  if (actionId === "disk.scan" ||
      actionId === "disk.cleanup.safe") {
    const categories = asArray(data.categories)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    content.append(createResultGrid([
      ["Recuperable", typeof data.estimatedBytes === "number"
        ? formatBytes(data.estimatedBytes) : "—"],
      ["Categorías", String(categories.length)],
      ["Modo", actionId === "disk.cleanup.safe" ? "Dry-run" : "Read-only"]
    ]));
    if (categories.length) {
      content.append(createResultTable(
        ["Categoría", "Tamaño", "Riesgo"],
        categories.map(item => [
          displayValue(item.label),
          typeof item.bytes === "number" ? formatBytes(item.bytes) : "—",
          displayValue(item.risk)
        ])
      ));
    }
    return;
  }

  if (actionId === "network.test") {
    const network = asRecord(data.network);
    if (network) {
      content.append(createResultGrid([
        ["Adaptador", displayValue(network.name)],
        ["Enlace", typeof network.linkMbps === "number"
          ? Math.round(network.linkMbps) + " Mbps" : "—"],
        ["Tráfico", "↓ " + displayValue(network.receiveMegabytesPerSecond) +
          " MB/s · ↑ " + displayValue(network.sendMegabytesPerSecond) + " MB/s"]
      ]));
    }
    return;
  }

  if (actionId === "system.crash.analyze") {
    const insights = asArray(data.insights)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);

    content.append(createResultGrid([
      ["Eventos leídos", displayValue(data.eventsRead)],
      ["Patrones", String(insights.length)],
      ["Alta prioridad", displayValue(data.highSeverityCount)],
      ["Prioridad media", displayValue(data.mediumSeverityCount)]
    ]));

    if (insights.length) {
      content.append(createResultTable(
        ["Prioridad", "Patrón", "Veces", "Evidencia", "Siguiente paso"],
        insights.map(item => [
          displayValue(item.severity),
          displayValue(item.title),
          displayValue(item.occurrences),
          displayValue(item.latestEvidence),
          displayValue(item.recommendedActionId)
        ])
      ));

      const controls = document.createElement("div");
      controls.className = "result-actions";
      const seen = new Set<string>();
      for (const item of insights) {
        if (typeof item.recommendedActionId !== "string" ||
            seen.has(item.recommendedActionId)) continue;
        seen.add(item.recommendedActionId);
        const actionIdValue = item.recommendedActionId;
        const button = document.createElement("button");
        button.className = "btn btn-secondary";
        button.textContent = "Analizar · " + displayValue(item.title);
        button.addEventListener("click", () =>
          void runAction(actionIdValue, button));
        controls.append(button);
      }
      if (controls.childElementCount) content.append(controls);
    }
    return;
  }

  if (actionId === "drivers.usb.analyze") {
    const devices = asArray(data.devices)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);

    content.append(createResultGrid([
      ["USB observados", displayValue(data.deviceCount)],
      ["Con problema", displayValue(data.problemCount)],
      ["Reinicio elegible", displayValue(data.restartEligibleCount)],
      ["Política", "Fail-closed"]
    ]));

    if (devices.length) {
      content.append(createResultTable(
        ["Dispositivo", "Código", "Clase", "Estado", "Decisión"],
        devices.map(item => [
          displayValue(item.name),
          displayValue(item.problemCode),
          displayValue(item.pnpClass),
          displayValue(item.status),
          item.restartEligible === true ? "Reinicio disponible" : displayValue(item.reason)
        ])
      ));

      const controls = document.createElement("div");
      controls.className = "result-actions";
      for (const device of devices.filter(item =>
        item.restartEligible === true &&
        typeof item.deviceInstanceId === "string").slice(0, 8)) {
        const button = document.createElement("button");
        button.className = "btn btn-primary";
        button.textContent = "Reiniciar · " + displayValue(device.name);
        button.addEventListener("click", () => {
          if (!window.confirm(
            "¿Reiniciar únicamente " + displayValue(device.name) +
            "?\n\nWindows solicitará UAC. No se reiniciarán hubs, teclado, ratón, almacenamiento ni red protegidos."
          )) return;
          void runAction(
            "drivers.usb.restart",
            button,
            {
              deviceInstanceId: String(device.deviceInstanceId),
              confirmed: true
            });
        });
        controls.append(button);
      }
      if (controls.childElementCount) content.append(controls);
    }
    return;
  }

  if (actionId === "startup.entries.preview") {
    const entries = asArray(data.entries)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);

    content.append(createResultGrid([
      ["Registradas", displayValue(data.registeredCount)],
      ["Revisables", displayValue(data.eligibleCount)],
      ["Protegidas", displayValue(data.protectedCount)],
      ["Con rollback", displayValue(data.disabledByAppCount)]
    ]));

    if (entries.length) {
      content.append(createResultTable(
        ["Entrada", "Ubicación", "Estado", "Protección", "Motivo"],
        entries.map(item => [
          displayValue(item.name),
          displayValue(item.scope),
          item.enabled === true ? "Activa" : "Deshabilitada por la app",
          item.protected === true ? "Protegida" : "Revisable",
          displayValue(item.reason)
        ])
      ));

      const controls = document.createElement("div");
      controls.className = "result-actions";
      for (const entry of entries.slice(0, 24)) {
        if (typeof entry.entryId !== "string") continue;

        if (entry.enabled === true && entry.protected !== true) {
          const disable = document.createElement("button");
          disable.className = "btn btn-secondary";
          disable.textContent = "Deshabilitar · " + displayValue(entry.name);
          disable.addEventListener("click", () => {
            if (!window.confirm(
              "¿Quitar " + displayValue(entry.name) +
              " del próximo autoarranque?\n\nEl proceso actual NO se cerrará y la app guardará rollback."
            )) return;
            void runAction(
              "startup.entry.disable",
              disable,
              { entryId: String(entry.entryId), confirmed: true });
          });
          controls.append(disable);
        }

        if (entry.restoreAvailable === true) {
          const restore = document.createElement("button");
          restore.className = "btn btn-secondary";
          restore.textContent = "Restaurar · " + displayValue(entry.name);
          restore.addEventListener("click", () => {
            if (!window.confirm(
              "¿Restaurar " + displayValue(entry.name) +
              " al autoarranque original?"
            )) return;
            void runAction(
              "startup.entry.restore",
              restore,
              { entryId: String(entry.entryId), confirmed: true });
          });
          controls.append(restore);
        }
      }
      if (controls.childElementCount) content.append(controls);
    }
    return;
  }

  if (actionId === "drivers.usb.restart" ||
      actionId === "startup.entry.disable" ||
      actionId === "startup.entry.restore") {
    content.append(createResultGrid([
      ["Estado", displayValue(data.status)],
      ["Elemento", displayValue(data.name ?? data.deviceInstanceId)],
      ["Detalle", displayValue(data.detail)],
      ["Rollback", data.restoreAvailable === true ? "Disponible" : "—"]
    ]));
    return;
  }

  if (actionId === "memory.analyze") {
    content.append(createResultGrid([
      ["RAM total", typeof data.memoryTotalBytes === "number"
        ? formatBytes(data.memoryTotalBytes) : "—"],
      ["RAM usada", typeof data.memoryUsedBytes === "number"
        ? formatBytes(data.memoryUsedBytes) : "—"],
      ["Disponible", typeof data.memoryAvailableBytes === "number"
        ? formatBytes(data.memoryAvailableBytes) : "—"]
    ]));
    return;
  }

  if (actionId === "system.reliability.analyze") {
    const events = asArray(data.events);
    content.append(createResultGrid([
      ["Eventos", String(events.length)],
      ["Ventana", "7 días"],
      ["Modelo", "HECHO / INDICIO"]
    ]));
    return;
  }

  if (actionId === "system.health.scan") {
    content.append(createResultGrid([
      ["Reliability", displayValue(data.reliabilityEvents) + " eventos"],
      ["Drivers", "Evaluados"],
      ["Activación", "Evaluada"]
    ]));
    return;
  }

  if (actionId === "system.healthscore.analyze") {
    const factors = asArray(data.factors)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    const changes = asArray(data.changes)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);

    content.append(createResultGrid([
      ["Health Score", displayValue(data.score) + "/100"],
      ["Banda", displayValue(data.band)],
      ["Historial", displayValue(data.historyCount) + " captura(s)"],
      ["Baseline", data.baselineCreated === true ? "Creada" : "Comparada"]
    ]));

    if (factors.length) {
      content.append(createResultTable(
        ["Factor", "Estado", "Penalización", "Evidencia"],
        factors.map(item => [
          displayValue(item.category),
          displayValue(item.status),
          "-" + displayValue(item.penalty),
          displayValue(item.detail)
        ])
      ));
    }

    if (changes.length) {
      content.append(createResultTable(
        ["Cambio", "Impacto", "Antes", "Después", "Delta"],
        changes.map(item => [
          displayValue(item.name),
          displayValue(item.impact),
          displayValue(item.before),
          displayValue(item.after),
          displayValue(item.delta) + (item.unit ? " " + displayValue(item.unit) : "")
        ])
      ));
    }
    return;
  }

  if (actionId === "system.changes.analyze") {
    const changes = asArray(data.changes)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);

    content.append(createResultGrid([
      ["Baseline previa", displayValue(data.previousCapturedAt)],
      ["Baseline actual", displayValue(data.currentCapturedAt)],
      ["Cambios materiales", String(changes.length)],
      ["Comparación", data.baselineRequired === true ? "Falta baseline" : "Disponible"]
    ]));

    if (changes.length) {
      content.append(createResultTable(
        ["Categoría", "Elemento", "Impacto", "Antes", "Después"],
        changes.map(item => [
          displayValue(item.category),
          displayValue(item.name),
          displayValue(item.impact),
          displayValue(item.before),
          displayValue(item.after)
        ])
      ));
    }
    return;
  }

  if (actionId === "lab.outcomes.status") {
    const actions = asArray(data.actions)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);

    content.append(createResultGrid([
      ["WRITE", displayValue(data.writeActionCount)],
      ["Clasificadas", displayValue(data.classifiedCount)],
      ["Post-check", displayValue(data.postcheckedCount)],
      ["Cobertura", displayValue(data.coveragePercent) + "%"],
      ["Incompletas", displayValue(data.incompleteRuns)]
    ]));

    if (actions.length) {
      content.append(createResultTable(
        ["Acción", "Verificación", "Rollback", "OK / Fallos", "Detalle"],
        actions.map(item => [
          displayValue(item.actionId),
          displayValue(item.verificationLevel),
          item.rollbackExpected === true ? "Esperado" : item.reversible === true ? "Disponible" : "No",
          displayValue(item.completedRuns) + " / " + displayValue(item.failedRuns),
          displayValue(item.verificationDetail)
        ])
      ));
    }
    return;
  }

  if (actionId.startsWith("maintenance.policy.")) {
    const workload = asRecord(data.workload);
    const scope = asArray(data.fixedReadOnlyScope);
    content.append(createResultGrid([
      ["Política", displayValue(data.mode)],
      ["Ejecutable ahora", data.canRunNow === true ? "Sí" : "No"],
      ["PC", workload ? displayValue(workload.effectiveMode) : "—"],
      ["Scope", String(scope.length) + " diagnósticos fijos"],
      ["Motivo", displayValue(data.reason)]
    ]));
    if (scope.length) {
      const pre = document.createElement("pre");
      pre.className = "result-pre";
      pre.textContent = scope.map(displayValue).join("\n");
      content.append(pre);
    }
    return;
  }

  if (actionId === "maintenance.safe.run") {
    const items = asArray(data.items)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    const policy = asRecord(data.policy);

    content.append(createResultGrid([
      ["Ejecutado", data.executed === true ? "Sí" : "No"],
      ["Política", policy ? displayValue(policy.mode) : "—"],
      ["Diagnósticos", String(items.length)],
      ["WRITE ejecutados", "0"]
    ]));

    if (items.length) {
      content.append(createResultTable(
        ["Diagnóstico", "Estado", "Resumen"],
        items.map(item => [
          displayValue(item.actionId),
          displayValue(item.status),
          displayValue(item.summary)
        ])
      ));
    }
    return;
  }

  if (actionId === "system.actionplan.preview") {
    const items = asArray(data.items)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    const workload = asRecord(data.workload);

    content.append(createResultGrid([
      ["Prioridad alta", displayValue(data.highPriorityCount)],
      ["Prioridad media", displayValue(data.mediumPriorityCount)],
      ["Accionables", displayValue(data.actionableCount)],
      ["Diferidas", displayValue(data.deferredCount)],
      ["PC", workload ? displayValue(workload.effectiveMode) : "—"],
      ["Mantenimiento pesado", workload?.heavyActionsAllowed === true ? "Permitido" : "Protegido"]
    ]));

    if (items.length) {
      content.append(createResultTable(
        ["Prioridad", "Hallazgo", "Motivo", "Acción", "Estado"],
        items.map(item => [
          displayValue(item.priority),
          displayValue(item.title),
          displayValue(item.reason),
          displayValue(item.actionId ?? "Informativo"),
          item.deferred === true ? "Diferida" : "Disponible"
        ])
      ));

      const controls = document.createElement("div");
      controls.className = "result-actions";
      const actionable = items.filter(item =>
        typeof item.actionId === "string" &&
        item.deferred !== true);
      for (const item of actionable.slice(0, 8)) {
        const actionIdValue = String(item.actionId);
        const definition = currentCatalog.find(action =>
          action.id === actionIdValue);
        const button = document.createElement("button");
        button.className = "btn btn-secondary";
        button.textContent = "Abrir · " + displayValue(item.title);

        const requiredSelection = definition?.parameters?.some(parameter =>
          parameter.required && parameter.name !== "confirmed") === true;
        if (requiredSelection) {
          button.disabled = true;
          button.title = "Primero ejecutá el análisis específico que genera una selección segura.";
        } else {
          button.addEventListener("click", () => {
            if (definition?.mode === "WRITE") {
              if (!window.confirm(
                definition.title + "\n\n" + definition.description +
                "\n\n¿Ejecutar esta acción del plan?"
              )) return;
              void runAction(actionIdValue, button, { confirmed: true });
            } else {
              void runAction(actionIdValue, button);
            }
          });
        }
        controls.append(button);
      }
      if (controls.childElementCount) content.append(controls);
    }
    return;
  }

  if (actionId.startsWith("system.workload.")) {
    content.append(createResultGrid([
      ["Configurado", displayValue(data.configuredMode)],
      ["Efectivo", displayValue(data.effectiveMode)],
      ["Inactividad", typeof data.idleSeconds === "number"
        ? formatDuration(data.idleSeconds) : "—"],
      ["D: libre", typeof data.dFreePercent === "number"
        ? Number(data.dFreePercent).toFixed(1) + "%" : "—"],
      ["Carga pesada", data.heavyActionsAllowed === true
        ? "Permitida" : "Protegida"],
      ["Motivo", displayValue(data.reason)]
    ]));
    return;
  }

  if (actionId === "backup.rollback.center") {
    const entries = asArray(data.entries)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    const incomplete = asArray(data.incompleteOperations);

    content.append(createResultGrid([
      ["Rollbacks", String(entries.length)],
      ["Operaciones incompletas", String(incomplete.length)],
      ["Estado", incomplete.length ? "REVISAR" : entries.length ? "RECUPERABLE" : "LIMPIO"]
    ]));

    if (entries.length) {
      content.append(createResultTable(
        ["Cambio", "Detalle", "Acción", "UAC"],
        entries.map(item => [
          displayValue(item.title),
          displayValue(item.detail),
          displayValue(item.actionId),
          item.requiresAdmin === true ? "Sí" : "No"
        ])
      ));

      const controls = document.createElement("div");
      controls.className = "result-actions";
      for (const entry of entries.slice(0, 12)) {
        if (typeof entry.actionId !== "string") continue;
        const params = asRecord(entry.parameters) ?? {};
        const button = document.createElement("button");
        button.className = "btn btn-secondary";
        button.textContent = "Restaurar · " + displayValue(entry.title);
        button.addEventListener("click", () => {
          if (!window.confirm(
            "¿Restaurar " + displayValue(entry.title) +
            "?\n\n" + displayValue(entry.detail)
          )) return;
          void runAction(
            String(entry.actionId),
            button,
            { ...params, confirmed: true });
        });
        controls.append(button);
      }
      content.append(controls);
    }
    return;
  }

  if (actionId === "backup.status") {
    const incomplete = asArray(data.incompleteOperations)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    const snapshots = asArray(data.rollbackSnapshots);
    content.append(createResultGrid([
      ["Operaciones incompletas", String(incomplete.length)],
      ["Snapshots rollback", String(snapshots.length)],
      ["Estado", incomplete.length ? "REVISAR" : "OK"]
    ]));
    if (incomplete.length) {
      content.append(createResultTable(
        ["Acción", "Inicio", "Último estado"],
        incomplete.map(item => [
          displayValue(item.actionId),
          displayValue(item.startedAt),
          displayValue(item.lastStatus)
        ])
      ));
    }
    if (snapshots.length) {
      const pre = document.createElement("pre");
      pre.className = "result-pre";
      pre.textContent = snapshots.map(displayValue).join("\n");
      content.append(pre);
    }

    // The rollback offered right after an apply disappears with that
    // result panel; persisted snapshots stay restorable from here.
    const rollback = createEcoQosRollback(
      asArray(data.ecoQosTargets)
        .map(asRecord)
        .filter((item): item is Record<string, unknown> => item !== null)
        .map(item => ({ processId: item.processId, success: item.restorable === true })));
    if (rollback) content.append(rollback);
    return;
  }

  const auditItems = asArray(data.items)
    .map(asRecord)
    .filter((item): item is Record<string, unknown> => item !== null);
  if (auditItems.length || typeof data.status === "string") {
    content.append(createResultGrid([
      ["Estado", displayValue(data.status)],
      ["Elementos", String(auditItems.length)],
      ["Modo", "Read-only"]
    ]));
    if (auditItems.length) {
      content.append(createResultTable(
        ["Categoría", "Nombre", "Valor", "Estado"],
        auditItems.map(item => [
          displayValue(item.category),
          displayValue(item.name),
          displayValue(item.value),
          displayValue(item.status)
        ])
      ));
    }
  }
}

function renderSnapshot(snapshot: SystemSnapshot): void {
  currentSnapshot = snapshot;
  const memoryPercent = snapshot.memoryTotalBytes > 0 ? snapshot.memoryUsedBytes / snapshot.memoryTotalBytes * 100 : 0;
  const diskUsedPercent = snapshot.diskTotalBytes > 0 ? (snapshot.diskTotalBytes - snapshot.diskFreeBytes) / snapshot.diskTotalBytes * 100 : 0;

  byId("cpu-value").textContent = snapshot.cpuPercent.toFixed(0) + "%";
  byId("cpu-sub").textContent = "Uso total";
  setBar("cpu-bar", snapshot.cpuPercent);
  pushHistory(cpuHistory, snapshot.cpuPercent);
  renderSparkline("cpu-sparkline", cpuHistory);

  byId("ram-value").textContent = formatBytes(snapshot.memoryUsedBytes) + " / " + formatBytes(snapshot.memoryTotalBytes);
  byId("ram-sub").textContent = memoryPercent.toFixed(0) + "% en uso · " + formatBytes(snapshot.memoryAvailableBytes) + " disponibles";
  setBar("ram-bar", memoryPercent);

  byId("disk-label").textContent = "Disco " + snapshot.diskDrive;
  byId("disk-value").textContent = formatBytes(snapshot.diskFreeBytes);
  byId("disk-sub").textContent = "libres de " + formatBytes(snapshot.diskTotalBytes);
  setBar("disk-bar", diskUsedPercent);

  if (snapshot.network) {
    byId("network-value").textContent = snapshot.network.linkMbps > 0 ? Math.round(snapshot.network.linkMbps) + " Mbps" : snapshot.network.name;
    byId("network-sub").textContent = "↓ " + snapshot.network.receiveMegabytesPerSecond.toFixed(1) + " MB/s · ↑ " + snapshot.network.sendMegabytesPerSecond.toFixed(1) + " MB/s";
    const networkUtilization = snapshot.network.linkMbps > 0
      ? Math.min(100, snapshot.network.receiveMegabytesPerSecond * 8 / snapshot.network.linkMbps * 100)
      : 0;
    setBar("network-bar", networkUtilization);
    pushHistory(networkHistory, networkUtilization);
    renderSparkline("network-sparkline", networkHistory);
  } else {
    byId("network-value").textContent = "Sin conexión";
    byId("network-sub").textContent = "No hay adaptador activo";
    setBar("network-bar", 0);
  }

  const integrity = snapshot.integrityStatus === "NOT_EVALUATED"
    ? "No comprobada"
    : snapshot.integrityStatus === "OK"
      ? "Correcta"
      : snapshot.integrityStatus.replaceAll("_", " ");
  byId("integrity-value").textContent = integrity;
  byId("integrity-sub").textContent = snapshot.integrityStatus === "OK"
    ? "Sin corrupción detectada"
    : snapshot.integrityStatus === "NOT_EVALUATED"
      ? "Ejecuta la comprobación de integridad"
      : "Revisa el resultado del diagnóstico";
  setHealth("integrity", snapshot.integrityStatus);
  setHealth("drivers", snapshot.driverStatus);
  setHealth("activation", snapshot.activationStatus);
  setHealth("reboot", snapshot.rebootRequired === null ? "UNKNOWN" : snapshot.rebootRequired ? "PENDING" : "NO_REQUERIDO");

  const driverValue = document.getElementById("developer-driver-value");
  const driverSub = document.getElementById("developer-driver-sub");
  if (driverValue) {
    driverValue.textContent = snapshot.driverStatus === "NOT_EVALUATED"
      ? "Sin evaluar"
      : snapshot.driverStatus.replaceAll("_", " ");
  }
  if (driverSub) {
    driverSub.textContent = snapshot.driverStatus === "OK"
      ? "Sin problemas detectados"
      : "Ejecuta diagnóstico";
  }

  const activationValue = document.getElementById("developer-activation-value");
  const activationSub = document.getElementById("developer-activation-sub");
  if (activationValue) {
    activationValue.textContent = snapshot.activationStatus === "LICENSED"
      ? "Activado"
      : snapshot.activationStatus === "UNLICENSED"
        ? "Sin activar"
        : snapshot.activationStatus === "NOT_EVALUATED"
          ? "Comprobando…"
          : snapshot.activationStatus === "UNKNOWN"
            ? "Sin determinar"
            : snapshot.activationStatus.replaceAll("_", " ");
  }
  if (activationSub) {
    activationSub.textContent = snapshot.activationStatus === "LICENSED"
      ? "Licencia de Windows válida"
      : snapshot.activationStatus === "NOT_EVALUATED"
        ? "Leyendo estado de licencia"
        : "Estado de licencia";
  }

  const developerRebootValue = document.getElementById("developer-reboot-value");
  const developerRebootSub = document.getElementById("developer-reboot-sub");
  if (developerRebootValue && developerRebootSub) {
    developerRebootValue.textContent = snapshot.rebootRequired === null
      ? "Sin determinar"
      : snapshot.rebootRequired ? "Pendiente" : "No requerido";
    developerRebootSub.textContent = snapshot.rebootRequired === null
      ? "Estado no disponible"
      : snapshot.rebootRequired
        ? "Mantenimiento pendiente"
        : "Sistema estable";
  }

  const rebootTitle = document.getElementById("reboot-interlock-title");
  const rebootText = document.getElementById("reboot-interlock-text");
  if (rebootTitle && rebootText) {
    rebootTitle.textContent = snapshot.rebootRequired === null
      ? "Reinicio sin determinar"
      : snapshot.rebootRequired ? "Reinicio pendiente" : "Reinicio no requerido";
    rebootText.textContent = snapshot.rebootRequired === null
      ? "Windows no permitió determinar el estado."
      : snapshot.rebootRequired
        ? "Hay una señal de mantenimiento pendiente."
        : "Las operaciones pueden continuar en caliente.";
  }

  byId("storage-drive").textContent = snapshot.diskDrive + " · unidad del sistema";
  byId("storage-free").textContent = formatBytes(snapshot.diskFreeBytes);
  byId("storage-total").textContent = formatBytes(snapshot.diskTotalBytes);
  setBar("storage-bar", diskUsedPercent);
  const freePercent = snapshot.diskTotalBytes > 0 ? snapshot.diskFreeBytes / snapshot.diskTotalBytes * 100 : 0;
  const risk = byId("storage-risk");
  risk.textContent = freePercent < 10 ? "Espacio crítico" : freePercent < 20 ? "Poco espacio" : "Espacio correcto";
  risk.className = "status-text " + (freePercent < 10 ? "bad" : freePercent < 20 ? "warn" : "good");
  setGlobalState(snapshot.operationState);
  if (activeView !== "dashboard") renderModuleSummary(activeView);
}

function setGlobalState(state: OperationState): void {
  const className = "badge " +
    (state === "IDLE" ? "badge-green" : state === "ERROR" ? "" : "badge-blue");

  const badge = byId("global-state");
  badge.replaceChildren(
    document.createElement("i"),
    document.createTextNode(state));
  badge.className = className;

  const headerBadge = document.getElementById("header-state");
  if (headerBadge) {
    headerBadge.replaceChildren(
      document.createElement("i"),
      document.createTextNode("Estado: " + state));
    headerBadge.className = className;
  }

  window.chrome?.webview?.postMessage({ type: "ui-state", state });
}

function renderReliability(events: ReliabilityEvent[]): void {
  currentReliabilityEvents = events;
  if (activeView === "system" || activeView === "history")
    renderModuleSummary(activeView);
  const body = byId<HTMLTableSectionElement>("reliability-body");
  body.replaceChildren();
  if (!events.length) {
    const row = body.insertRow();
    const cell = row.insertCell();
    cell.colSpan = 5;
    cell.textContent = "No se encontraron eventos relevantes en la ventana analizada.";
    return;
  }
  for (const event of events) {
    const row = body.insertRow();
    row.insertCell().textContent = new Date(event.time).toLocaleString();
    row.insertCell().textContent = String(event.id);
    row.insertCell().textContent = event.provider;
    row.insertCell().textContent = event.classification;
    row.insertCell().textContent = event.message;
  }
}

function renderCatalog(actions: ActionDefinition[]): void {
  currentCatalog = actions;
  if (activeView !== "dashboard") renderModuleView(activeView);
  const host = byId("catalog-grid");
  host.replaceChildren();
  for (const action of actions) {
    const item = document.createElement("div");
    item.className = "catalog-item";
    const code = document.createElement("code");
    code.textContent = action.id;
    const meta = document.createElement("span");
    meta.textContent = action.category + " · " + action.risk + " · " + action.mode + (action.requiresAdmin ? " · Admin" : "");
    item.append(code, meta);
    host.append(item);
  }
}

function pushActivity(title: string, detail: string, kind: "ok" | "warn" | "info" = "info"): void {
  activity.unshift({ time: new Date(), title, detail, kind });
  if (activity.length > 12) activity.length = 12;
  renderActivity();
}

function renderActivity(): void {
  const host = byId("activity-list");
  host.replaceChildren();
  byId("activity-count").textContent = activity.length + (activity.length === 1 ? " evento" : " eventos");
  if (!activity.length) {
    const empty = document.createElement("div");
    empty.className = "empty";
    empty.textContent = "Aún no hay actividad.";
    host.append(empty);
    return;
  }
  for (const item of activity.slice(0, 7)) {
    const row = document.createElement("div");
    row.className = "activity-row";
    const time = document.createElement("span");
    time.className = "activity-time";
    time.textContent = item.time.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
    const dot = document.createElement("i");
    dot.className = "activity-dot " + (item.kind === "ok" ? "ok" : item.kind === "warn" ? "warn" : "");
    const text = document.createElement("div");
    const strong = document.createElement("b");
    strong.textContent = item.title;
    const small = document.createElement("small");
    small.textContent = item.detail;
    text.append(strong, small);
    row.append(time, dot, text);
    host.append(row);
  }
}

function toast(title: string, message: string, kind: "success" | "error" | "info" = "info"): void {
  const host = byId("toast-host");
  const element = document.createElement("div");
  element.className = "toast " + kind;
  // Failures interrupt a screen reader; routine feedback waits its turn.
  element.setAttribute("role", kind === "error" ? "alert" : "status");
  const strong = document.createElement("strong");
  strong.textContent = title;
  const body = document.createElement("div");
  body.textContent = message;
  element.append(strong, body);
  host.append(element);
  window.setTimeout(() => element.remove(), 5200);
}

function applyTemperatureResult(result: ActionResult): void {
  const value = document.getElementById("temperature-value");
  const sub = document.getElementById("temperature-sub");
  const bar = document.getElementById("temperature-bar") as HTMLElement | null;
  if (!value || !sub || !bar) return;

  const data = asRecord(result.data);
  const thermalItems = asArray(data?.items)
    .map(asRecord)
    .filter((item): item is Record<string, unknown> =>
      item !== null && String(item.category ?? "").toLowerCase() === "thermal");

  const readings = thermalItems
    .map(item => {
      const match = String(item.value ?? "").match(/(-?\d+(?:[.,]\d+)?)\s*°?c/i);
      if (!match) return null;
      const celsius = Number(match[1]?.replace(",", "."));
      if (!Number.isFinite(celsius)) return null;
      return { celsius, sensor: displayValue(item.name) };
    })
    .filter((reading): reading is { celsius: number; sensor: string } => reading !== null);

  if (!readings.length) {
    latestTemperatureCelsius = null;
    latestTemperatureSensor = "Sensor no expuesto por firmware/driver";
    value.textContent = "No disponible";
    sub.textContent = latestTemperatureSensor;
    bar.style.width = "0%";
    return;
  }

  const hottest = readings.reduce((current, reading) =>
    reading.celsius > current.celsius ? reading : current);
  latestTemperatureCelsius = hottest.celsius;
  latestTemperatureSensor = hottest.sensor;
  value.textContent = hottest.celsius.toFixed(0) + " °C";
  sub.textContent = hottest.sensor;
  bar.style.width = clampPercent((hottest.celsius / 100) * 100).toFixed(1) + "%";
}

function runInBackground(work: () => Promise<void>): Promise<void> {
  if (actionInFlight || backgroundWork) return Promise.resolve();

  const tracked = work()
    .catch(() => undefined)
    .finally(() => {
      if (backgroundWork === tracked) backgroundWork = null;
    });
  backgroundWork = tracked;
  return tracked;
}

function refreshTemperature(): Promise<void> {
  return runInBackground(async () => {
    try {
      const result = await provider.runAction("thermal.audit");
      applyTemperatureResult(result);
      if (activeView === "thermal") renderModuleSummary(activeView);
    } catch (error) {
      const value = document.getElementById("temperature-value");
      const sub = document.getElementById("temperature-sub");
      if (value) value.textContent = "No disponible";
      if (sub) sub.textContent = error instanceof Error ? error.message : String(error);
    }
  });
}

let snapshotFailing = false;
let baselineStarted = false;

async function refreshSnapshot(silent = false): Promise<void> {
  try {
    const snapshot = await provider.snapshot();
    snapshotFailing = false;
    renderSnapshot(snapshot);
    if (!silent) pushActivity("Estado actualizado", "Métricas locales renovadas.", "ok");
  } catch (error) {
    setGlobalState("ERROR");
    // The periodic refresh reports an outage once, not every five seconds.
    if (!silent || !snapshotFailing)
      toast("No se pudo actualizar", error instanceof Error ? error.message : String(error), "error");
    snapshotFailing = true;
  }
}

async function ensureCatalog(): Promise<void> {
  if (currentCatalog.length > 0) return;
  try {
    renderCatalog(await provider.catalog());
  } catch {
    // Retried on the next refresh; the snapshot path reports the outage.
  }
}

async function refreshReliability(): Promise<void> {
  try {
    const events = await provider.reliability();
    renderReliability(events);
  } catch (error) {
    renderReliability([]);
    toast("Reliability Analyzer", error instanceof Error ? error.message : String(error), "error");
  }
}

async function refreshBaselineHealth(): Promise<void> {
  await runInBackground(async () => {
    // One at a time: the backend rejects overlapping actions, so a parallel
    // pair would always lose its second member.
    for (const id of ["drivers.analyze", "system.activation.analyze"]) {
      try {
        await provider.runAction(id);
      } catch {
        // Baseline health is best-effort; each module stays available manually.
      }
    }
  });
  await refreshSnapshot(true);
}

async function runFullDiagnostic(button: HTMLButtonElement): Promise<void> {
  await runAction("system.health.scan", button);
  await runAction("system.integrity.check", button);
}

async function runAction(
  id: string,
  button?: HTMLButtonElement,
  parameters: Record<string, unknown> = {}
): Promise<void> {
  if (actionInFlight) {
    toast(
      "Operación en curso",
      "Esperá a que termine la operación actual antes de iniciar otra.",
      "info");
    return;
  }

  actionInFlight = true;
  // A result belongs to the module it was requested from. If the user has
  // moved to another view by the time it arrives, it must not appear there.
  const originView = activeView;
  const originalNodes = button
    ? Array.from(button.childNodes).map(node => node.cloneNode(true))
    : [];
  const action = currentCatalog.find(item => item.id === id);
  const isWrite = action?.mode === "WRITE";
  if (button) {
    button.disabled = true;
    button.setAttribute("aria-busy", "true");
    button.replaceChildren(
      document.createTextNode(isWrite ? "Aplicando…" : "Analizando…"));
  }
  setGlobalState(isWrite ? "OPTIMIZING" : "ANALYZING");
  try {
    while (backgroundWork) await backgroundWork;
    const result = await provider.runAction(id, parameters);
    if (id === "disk.cleanup.safe" && result.success) {
      cleanupPreviewReady = true;
      const cleanButton = document.querySelector<HTMLButtonElement>('button[data-action="disk.cleanup.execute"]');
      if (cleanButton) { cleanButton.disabled = false; cleanButton.title = "Previsualización realizada. Confirmá para limpiar."; }
    }
    if (id === "disk.cleanup.execute") cleanupPreviewReady = false;
    pushActivity(id, result.message, result.success ? "ok" : "warn");
    toast(result.success ? "Acción completada" : "Acción con advertencias", result.message, result.success ? "success" : "info");
    const estimated = result.data?.estimatedBytes;
    if (typeof estimated === "number") {
      recoverableBytes = estimated;
      byId("storage-recoverable").textContent = formatBytes(estimated);
      const data = asRecord(result.data);
      if (data) renderRecoveryCategories(data);
      if (activeView === "cleanup") renderModuleSummary(activeView);
    }

    const integrityStatus = result.data?.status;
    if (id === "system.integrity.check" &&
        typeof integrityStatus === "string" &&
        currentSnapshot) {
      currentSnapshot = { ...currentSnapshot, integrityStatus };
      renderSnapshot(currentSnapshot);
    }

    if (id === "thermal.audit") applyTemperatureResult(result);
    if (id === "system.reliability.analyze" || id === "system.health.scan") await refreshReliability();
    await refreshSnapshot(true);
    // Interactive follow-up controls (MemoryTrim/EcoQoS/rollback) must only
    // become visible once this action is fully finished. Rendering earlier
    // exposes clickable controls while actionInFlight is still true and a
    // fast click is rejected as "Operación en curso".
    if (activeView === originView) renderActionResult(id, result);
  } catch (error) {
    setGlobalState("ERROR");
    const message = error instanceof Error ? error.message : String(error);
    pushActivity(id, message, "warn");
    toast("Acción fallida", message, "error");
  } finally {
    actionInFlight = false;
    if (button) {
      button.disabled = false;
      button.removeAttribute("aria-busy");
      button.replaceChildren(...originalNodes);
    }
  }
}

function showView(view: string): void {
  activeView = view;
  document.querySelectorAll(".nav-item").forEach(node => {
    const current = (node as HTMLElement).dataset.view === view;
    node.classList.toggle("active", current);
    if (current) node.setAttribute("aria-current", "page");
    else node.removeAttribute("aria-current");
  });

  const dashboard = byId("dashboard-view");
  const module = byId("module-view");

  hideModuleResult();

  if (view === "dashboard") {
    dashboard.classList.add("active");
    module.classList.remove("active");
  } else {
    dashboard.classList.remove("active");
    module.classList.add("active");
    renderModuleView(view);
  }

  document.querySelector<HTMLElement>(".workspace")
    ?.scrollTo({ top: 0, left: 0, behavior: "auto" });
}

async function boot(): Promise<void> {
  window.history.scrollRestoration = "manual";
  const initialMode = queryParameters.get("mode") === "developer"
    ? "developer"
    : "compact";
  document.body.dataset.mode = initialMode;
  const workspace = document.querySelector<HTMLElement>(".workspace");
  workspace?.scrollTo({ top: 0, left: 0, behavior: "auto" });

  const surfaceBadge = byId("surface-badge");
  if (provider.surface === "demo") {
    document.body.classList.add("demo");
    surfaceBadge.textContent = "DEMO MODE";
  } else {
    document.body.classList.add("local");
    surfaceBadge.textContent = "LOCAL";
  }

  document.querySelectorAll<HTMLElement>("[data-view]").forEach(node => node.addEventListener("click", () => showView(node.dataset.view ?? "dashboard")));
  document.querySelectorAll<HTMLButtonElement>(".action-trigger").forEach(button => button.addEventListener("click", () => runAction(button.dataset.action ?? "", button)));
  byId<HTMLButtonElement>("diagnostic-btn").addEventListener("click", event =>
    void runFullDiagnostic(event.currentTarget as HTMLButtonElement));
  document.getElementById("refresh-btn")
    ?.addEventListener("click", () => refreshSnapshot());
  byId("mode-toggle").textContent =
    initialMode === "developer" ? "Modo compacto" : "Modo experto";
  byId("mode-toggle").addEventListener("click", async () => {
    const developer = document.body.dataset.mode !== "developer";
    document.body.dataset.mode = developer ? "developer" : "compact";
    byId("mode-toggle").textContent = developer ? "Modo compacto" : "Modo experto";

    if (!developer &&
        ["browsers", "multimedia", "memory", "cpu", "startup", "integrity", "drivers",
         "windows-update", "apps", "privacy", "developer", "reliability-lab", "thermal", "diagnostics",
         "activation", "backup", "history"].includes(activeView)) {
      showView("dashboard");
    }

    if (developer) {
      await Promise.all([refreshReliability(), refreshTemperature()]);
    }
  });

  try {
    // The two initial reads are independent: a snapshot that fails once
    // (slow provider, transient Win32 error) must not leave the session
    // without its catalog or without the periodic refresh that recovers it.
    const [snapshot, actions] = await Promise.allSettled([provider.snapshot(), provider.catalog()]);
    if (actions.status === "fulfilled") renderCatalog(actions.value);
    if (snapshot.status === "fulfilled") {
      renderSnapshot(snapshot.value);
    } else {
      snapshotFailing = true;
      setGlobalState("ERROR");
      toast(
        "Error de inicio",
        snapshot.reason instanceof Error ? snapshot.reason.message : String(snapshot.reason),
        "error");
    }
    if (actions.status === "rejected" && snapshot.status === "fulfilled") {
      toast(
        "Error de inicio",
        actions.reason instanceof Error ? actions.reason.message : String(actions.reason),
        "error");
    }
    // With no answer from the provider, queuing actions behind it would
    // only stall the start for their full timeouts; the periodic refresh
    // runs the baseline as soon as a snapshot succeeds.
    if (!snapshotFailing) {
      baselineStarted = true;
      await refreshBaselineHealth();
      if (initialMode === "developer") {
        await Promise.all([refreshReliability(), refreshTemperature()]);
      }
    }

    if (provider.surface === "demo") {
      const now = Date.now();
      activity.push(
        { time: new Date(now - 3 * 60_000), title: "MemoryTrim completado", detail: "Simulado · liberación segura de memoria.", kind: "ok" },
        { time: new Date(now - 6 * 60_000), title: "DISM ScanHealth completado", detail: "Simulado · no se encontraron problemas.", kind: "info" },
        { time: new Date(now - 9 * 60_000), title: "Análisis EcoQoS finalizado", detail: "Simulado · procesos elegibles detectados.", kind: "ok" },
        { time: new Date(now - 12 * 60_000), title: "Limpieza de TEMP analizada", detail: "Simulado · vista previa sin borrado.", kind: "ok" },
        { time: new Date(now - 15 * 60_000), title: "Windows Update", detail: "Simulado · estado de ejemplo.", kind: "info" },
        { time: new Date(now - 18 * 60_000), title: "Diagnóstico multimedia completado", detail: "Simulado · inventario de dispositivos.", kind: "ok" }
      );
    }

    if (snapshotFailing) {
      pushActivity("Aplicación iniciada", "El proveedor de datos no respondió; se reintenta automáticamente.", "warn");
    } else {
      pushActivity("Aplicación iniciada", provider.surface === "local" ? "Backend local conectado." : "Datos simulados; no se accede al PC.", "ok");
    }

    workspace?.scrollTo({ top: 0, left: 0, behavior: "auto" });
  } catch (error) {
    setGlobalState("ERROR");
    toast("Error de inicio", error instanceof Error ? error.message : String(error), "error");
  }

  // Installed even after a failed start: this is what brings a degraded
  // session back once the provider answers again.
  window.setInterval(() => {
    if (document.visibilityState !== "visible") return;
    void ensureCatalog();
    void refreshSnapshot(true).then(() => {
      if (snapshotFailing || baselineStarted) return;
      baselineStarted = true;
      void refreshBaselineHealth();
    });
  }, 5000);

  window.setInterval(() => {
    if (document.visibilityState === "visible" &&
        document.body.dataset.mode === "developer")
      void refreshTemperature();
  }, 15000);
}

void boot();
