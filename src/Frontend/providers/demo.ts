import type { Provider, SystemSnapshot, ReliabilityEvent, ActionDefinition, ActionResult } from "../contracts";
import { demoCatalog } from "../generated/demo-catalog";
const delay = (ms: number) => new Promise<void>(resolve => window.setTimeout(resolve, ms));

export class DemoProvider implements Provider {
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

    if (id === "processes.hygiene.analyze") {
      return {
        success: true,
        dryRun: true,
        message: "DEMO: 3 residuos detenibles y 2 procesos protegidos.",
        data: {
          capturedAt: new Date().toISOString(),
          stoppableCount: 3,
          protectedCount: 2,
          estimatedReclaimMb: 1878,
          items: [
            {
              processId: 100,
              processName: "msedge.exe",
              category: "HEADLESS_SIG_TEST",
              identity: "sig-edge-headless-profile3",
              reason: "Navegador Edge headless con perfil dedicado de pruebas SIG.",
              protected: false,
              stoppable: true,
              fingerprint: "AAAAAAAAAAAAAAAAAAAAAAAA",
              estimatedReclaimMb: 150,
              ageMinutes: 120,
              relatedProcessIds: [100, 101]
            },
            {
              processId: 200,
              processName: "qemu-system-x86_64-headless.exe",
              category: "ANDROID_LAB",
              identity: "spotify_api28_hooklab2",
              reason: "Emulador Android marcado como laboratorio/prueba.",
              protected: false,
              stoppable: true,
              fingerprint: "BBBBBBBBBBBBBBBBBBBBBBBB",
              estimatedReclaimMb: 1536,
              ageMinutes: 480,
              relatedProcessIds: [200]
            },
            {
              processId: 300,
              processName: "python.exe",
              category: "ORPHAN_UVICORN",
              identity: "127.0.0.1:8773",
              reason: "Servidor Uvicorn cuyo proceso iniciador ya no existe.",
              protected: false,
              stoppable: true,
              fingerprint: "CCCCCCCCCCCCCCCCCCCCCCCC",
              estimatedReclaimMb: 102,
              ageMinutes: 240,
              relatedProcessIds: [300, 301]
            },
            {
              processId: 400,
              processName: "python.exe",
              category: "ACTIVE_DEV_SERVER",
              identity: "127.0.0.1:8877",
              reason: "Servidor de desarrollo con cadena activa.",
              protected: true,
              stoppable: false,
              fingerprint: "DDDDDDDDDDDDDDDDDDDDDDDD",
              estimatedReclaimMb: 132,
              ageMinutes: 20,
              relatedProcessIds: [400, 401]
            },
            {
              processId: 500,
              processName: "chrome-headless-shell.exe",
              category: "PROTECTED_HEADLESS_BROWSER",
              identity: "Playwright / Chromium",
              reason: "Pertenece a PM2/bot/automatización protegida.",
              protected: true,
              stoppable: false,
              fingerprint: "EEEEEEEEEEEEEEEEEEEEEEEE",
              estimatedReclaimMb: 100,
              ageMinutes: 30,
              relatedProcessIds: [500]
            }
          ]
        }
      };
    }

    if (id === "processes.hygiene.stop") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: residuo detenido y verificado.",
        data: {
          success: true,
          status: "EVALUATION",
          category: "ANDROID_LAB",
          identity: "spotify_api28_hooklab2",
          stoppedProcessIds: [200],
          stillAliveProcessIds: [],
          estimatedReclaimMb: 1536,
          detail: "Detención sintética verificada; no se tocó el host."
        }
      };
    }

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

    if (id === "browsers.edge.performance.audit") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: Edge Performance requiere optimización.",
        data: {
          processCount: 24,
          visibleWindowCount: 1,
          workingSetBytes: 5.5 * 1024 ** 3,
          privateBytes: 6.1 * 1024 ** 3,
          policy: {
            backgroundModeEnabled: 0,
            startupBoostEnabled: 0,
            sleepingTabsEnabled: null,
            sleepingTabsTimeout: null,
            autoDiscardSleepingTabsEnabled: null,
            efficiencyModeEnabled: null,
            efficiencyMode: null,
            launchEdgeOnWindowsStartupEnabled: null,
            mandatoryPerformanceOverridesPresent: true
          },
          autoLaunchEntryCount: 1,
          restoreAvailable: false,
          optimized: false,
          guidance: "Pendiente: sleeping tabs, timeout 5 min, auto-discard, efficiency mode, Windows startup, Run auto-launch."
        }
      };
    }

    if (id === "browsers.edge.performance.optimize" ||
        id === "browsers.edge.performance.restore") {
      const optimized = id.endsWith(".optimize");
      return {
        success: true,
        dryRun: false,
        message: optimized
          ? "DEMO: perfil Edge aplicado."
          : "DEMO: perfil Edge restaurado.",
        data: {
          success: true,
          status: optimized ? "OPTIMIZED" : "RESTORED",
          restoreAvailable: optimized,
          detail: "DEMO",
          report: {
            processCount: 12,
            visibleWindowCount: 1,
            workingSetBytes: 1.8 * 1024 ** 3,
            privateBytes: 2.4 * 1024 ** 3,
            policy: {
              backgroundModeEnabled: optimized ? 0 : 1,
              startupBoostEnabled: optimized ? 0 : 1,
              sleepingTabsEnabled: optimized ? 1 : null,
              sleepingTabsTimeout: optimized ? 300 : null,
              autoDiscardSleepingTabsEnabled: optimized ? 1 : null,
              efficiencyModeEnabled: optimized ? 1 : null,
              efficiencyMode: optimized ? 0 : null,
              launchEdgeOnWindowsStartupEnabled: optimized ? 0 : null,
              mandatoryPerformanceOverridesPresent: false
            },
            autoLaunchEntryCount: optimized ? 0 : 1,
            restoreAvailable: optimized,
            optimized,
            guidance: optimized ? "Optimizado." : "Restaurado."
          }
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

    if (id === "browsers.automation.audit") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: Chrome Playwright y Chromium headless separados, sin cerrar procesos.",
        data: {
          capturedAt: new Date().toISOString(),
          totalBrowserProcesses: 16,
          workingSetBytes: 1940000000,
          privateBytes: 1300000000,
          sampleCount: 3,
          accountingNotice: "La suma de working sets puede duplicar páginas compartidas. La memoria privada representa compromiso, no RAM exclusiva.",
          safetyNotice: "Solo observación, no se ejecuta ningún cierre ni EcoQoS.",
          families: [
            {
              family: "PLAYWRIGHT_CHROME", processCount: 13,
              workingSetBytes: 1500000000, privateBytes: 1100000000,
              peakPrivateBytes: 1550000000, status: "OBSERVED",
              detail: "Una muestra no demuestra fuga. El navegador se reduce al acabar el trabajo.",
              confirmedOwners: ["LINKEDIN_BOT"],
              recentSamples: []
            },
            {
              family: "PLAYWRIGHT_HEADLESS", processCount: 3,
              workingSetBytes: 145000000, privateBytes: 125000000,
              peakPrivateBytes: 300000000, status: "TRANSIENT_RECOVERED",
              detail: "Un pico temporal volvió al nivel base: no intervenir.",
              confirmedOwners: ["SEPE_AGENT"],
              recentSamples: []
            }
          ],
          processes: [
            {
              processId: 12345, parentProcessId: 23456,
              family: "PLAYWRIGHT_CHROME", role: "renderer",
              owner: "LINKEDIN_BOT", attribution: "CONFIRMED",
              workingSetBytes: 368000000, privateBytes: 320000000
            }
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

    if (id === "startup.service.dependencies") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: servicio sin dependientes.",
        data: {
          serviceName: "DemoVendorSvc",
          displayName: "Demo Vendor Updater",
          dependentCount: 0,
          dependents: []
        }
      };
    }

    if (id === "startup.tasks.preview") {
      return {
        success: true,
        dryRun: true,
        message: "DEMO: tareas programadas clasificadas.",
        data: {
          taskCount: 4,
          eligibleCount: 2,
          protectedCount: 2,
          restoreAvailableCount: 0,
          tasks: [
            {
              entryId: "111111111111111111111111",
              fullName: "\\DemoVendor\\Updater",
              taskName: "Updater",
              taskPath: "\\DemoVendor\\",
              state: "Ready",
              execute: "C:\\Program Files\\DemoVendor\\updater.exe",
              arguments: "--background",
              protected: false,
              reason: "Tarea de terceros revisable.",
              restoreAvailable: false
            },
            {
              entryId: "222222222222222222222222",
              fullName: "\\Microsoft\\Windows\\Defrag\\ScheduledDefrag",
              taskName: "ScheduledDefrag",
              taskPath: "\\Microsoft\\Windows\\Defrag\\",
              state: "Ready",
              execute: "C:\\Windows\\System32\\defrag.exe",
              arguments: null,
              protected: true,
              reason: "Tarea bajo el namespace Microsoft.",
              restoreAvailable: false
            },
            {
              entryId: "333333333333333333333333",
              fullName: "\\Automation\\PM2 resurrect",
              taskName: "PM2 resurrect",
              taskPath: "\\Automation\\",
              state: "Ready",
              execute: "C:\\Program Files\\nodejs\\node.exe",
              arguments: "pm2 resurrect",
              protected: true,
              reason: "Protegida por política local.",
              restoreAvailable: false,
              actionKind: "EXEC",
              actionStatus: "NORMAL"
            },
            {
              entryId: "444444444444444444444444",
              fullName: "\\SoftLanding\\StaleCreativeTask",
              taskName: "StaleCreativeTask",
              taskPath: "\\SoftLanding\\",
              state: "Ready",
              execute: null,
              arguments: null,
              protected: false,
              reason: "COM handler sin registro CLSID. La tarea está stale y puede deshabilitarse con rollback.",
              restoreAvailable: false,
              actionKind: "COM_HANDLER",
              comHandlerClassId: "{F576B2F9-7850-4226-ADB0-E5993FED4F02}",
              actionStatus: "STALE_COM_HANDLER"
            }
          ]
        }
      };
    }

    if (id === "system.registry.evidence" ||
        id === "system.com.evidence" ||
        id === "system.certificates.audit") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: evidencia read-only simulada.",
        data: {
          area: id.includes("registry") ? "Registry" :
            id.includes(".com.") ? "COM" : "Certificates",
          status: "OK",
          partial: id.includes(".com."),
          attentionCount: 1,
          items: [
            {
              category: "DEMO",
              name: "Hallazgo de ejemplo",
              status: "ATTENTION",
              source: "Read-only",
              detail: "La demo no modifica ni elimina este elemento."
            }
          ]
        }
      };
    }

    if (id === "diagnostics.bundle.create") {
      return {
        success: true,
        dryRun: false,
        message: "DEMO: bundle diagnóstico sanitizado creado.",
        data: {
          success: true,
          generatedAt: new Date().toISOString(),
          path: "C:\\Users\\demo\\AppData\\Local\\Win11PerformanceControlCenter\\Exports\\Win11PCC-diagnostic-demo.zip",
          bytes: 24000,
          jsonFiles: 10,
          sanitized: true,
          message: "No incluye comandos, argumentos, IDs PnP/USB, claves, cookies, sesiones ni logs crudos."
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
