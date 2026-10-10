export const moduleDefinitions: Record<string, {
  kicker: string;
  title: string;
  description: string;
  actionIds: string[];
}> = {
  performance: {
    kicker: "RENDIMIENTO",
    title: "CPU y memoria",
    description: "Métricas locales y análisis seguros antes de aplicar optimizaciones.",
    actionIds: ["memory.analyze", "processes.hygiene.analyze", "memory.trim.preview", "memory.pagefile.analyze", "cpu.ecoqos.analyze"]
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
    description: "Rendimiento de Edge, integridad de extensiones y limpieza reversible de residuos sin perder sesiones ni funciones.",
    actionIds: ["browsers.automation.audit", "browsers.inventory", "browsers.edge.performance.audit", "browsers.extensions.health", "browsers.extensions.orphans.preview"]
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
    description: "Presión de memoria, residuos de laboratorio, MemoryTrim y pagefile limitado con rollback.",
    actionIds: ["memory.analyze", "browsers.automation.audit", "processes.hygiene.analyze", "memory.trim.preview", "memory.pagefile.analyze", "memory.pagefile.capped", "memory.pagefile.restore"]
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
    actionIds: ["startup.audit", "startup.services.preview", "startup.entries.preview", "startup.tasks.preview"]
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
    description: "Diagnóstico avanzado, evidencia Windows read-only y reparación controlada de Explorer.",
    actionIds: ["boot.audit", "sleepresume.audit", "explorer.audit", "system.registry.evidence", "system.com.evidence", "system.certificates.audit", "explorer.restart"]
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
    description: "Operaciones incompletas, snapshots y bundle diagnóstico sanitizado.",
    actionIds: ["backup.status", "backup.rollback.center", "diagnostics.bundle.create"]
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
