import type { OperationState, SystemSnapshot, ReliabilityEvent, ActionDefinition, ActionResult, Provider } from "./contracts";
import { LocalProvider } from "./providers/local";
import { DemoProvider } from "./providers/demo";
import { moduleDefinitions } from "./module-definitions";
import { formatBytes, formatDuration, asRecord, asArray, displayValue } from "./components/formatting";
import { configureResultRenderer, renderStructuredResult } from "./components/action-results";

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
// Keep one verified result per module when navigating or when an action
// finishes after the user has moved elsewhere. Never show it in a new module.
const latestResultByView = new Map<string, { id: string; result: ActionResult }>();
// The backend runs one operation at a time. Background telemetry and
// user-triggered actions are therefore serialized here as well, so neither
// is rejected with "operación activa" because of the other.
let backgroundWork: Promise<void> | null = null;
const cpuHistory: number[] = [];
const networkHistory: number[] = [];
const HistoryLimit = 36;
configureResultRenderer({
  runAction: (id, button, parameters) => runAction(id, button, parameters),
  getCatalog: () => currentCatalog,
  toast: (title, message, kind) => toast(title, message, kind)
});
// When Windows text scaling collapses the sidebar to icons, native tooltips
// keep every destination discoverable by mouse. The original span text stays
// in the accessibility tree for screen readers and keyboard navigation.
document.querySelectorAll<HTMLButtonElement>(".nav-item").forEach(button => {
  const label = button.querySelector("span:not(.chev)")?.textContent?.trim();
  if (label) button.title = label;
});

function byId<T extends HTMLElement>(id: string): T {
  const element = document.getElementById(id);
  if (!element) throw new Error("Elemento faltante: " + id);
  return element as T;
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
      button.textContent = "A Papelera (confirmar)";
      button.disabled = !cleanupPreviewReady;
      button.title = cleanupPreviewReady ? "Limpiar solo cachés antiguas, con confirmación." : "Primero previsualizá la limpieza.";
      button.addEventListener("click", () => {
        if (!cleanupPreviewReady) return;
        if (!window.confirm("¿Enviar a la Papelera únicamente cachés regenerables con más de 7 días?\nNo se tocarán sesiones, extensiones instaladas, WhatsApp ni documentos.")) return;
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











function invalidateRecoveryEstimate(): void {
  recoverableBytes = null;
  byId("storage-recoverable").textContent = "—";
  const body = document.getElementById("recovery-body") as HTMLTableSectionElement | null;
  if (body) {
    body.replaceChildren();
    const cell = body.insertRow().insertCell();
    cell.colSpan = 6;
    cell.textContent = "Previsualización pendiente. Ejecutá un análisis nuevo.";
  }
  if (activeView === "cleanup") renderModuleSummary(activeView);
}

function renderRecoveryCategories(data: Record<string, unknown>): void {
  const body = document.getElementById("recovery-body") as HTMLTableSectionElement | null;
  if (!body) return;

  const categories = asArray(data.categories)
    .map(asRecord)
    .filter((item): item is Record<string, unknown> => item !== null);

  if (!categories.length) {
    body.replaceChildren();
    const cell = body.insertRow().insertCell();
    cell.colSpan = 6;
    cell.textContent = "No se detectaron categorías regenerables en el análisis.";
    return;
  }

  body.replaceChildren();

  for (const item of categories) {
    const row = body.insertRow();
    const label = displayValue(item.label);
    const bytes = typeof item.bytes === "number" ? formatBytes(item.bytes) : "—";
    const risk = displayValue(item.risk);
    const riskLower = risk.toLowerCase();
    const recoverable = riskLower === "safe";

    row.insertCell().textContent = label;
    row.insertCell().textContent = label.includes("Windows")
      ? "Temporales del sistema detectados"
      : "Archivos regenerables detectados";
    row.insertCell().textContent = bytes;
    // Only SAFE categories contribute to backend EstimatedBytes. Never
    // show CAUTION/EN_USO bytes as recoverable capacity.
    row.insertCell().textContent = recoverable ? bytes : "—";

    const riskCell = row.insertCell();
    const chip = document.createElement("span");
    chip.className = "risk-chip " + (recoverable ? "safe" : "caution");
    chip.textContent = recoverable ? "Seguro" : "Precaución";
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
  byId("module-result-subtitle").textContent = provider.surface === "demo"
    ? "Resultado simulado (DEMO). No se ejecutó ninguna operación en Windows."
    : "Evidencia de la última ejecución local.";
  const failedPagefile = !result.success && actionId.startsWith("memory.pagefile.");
  status.textContent = result.success ? (result.dryRun ? "DRY-RUN" : "OK")
    : failedPagefile ? "FALLIDO" : "WARNING";
  status.className = "badge " + (result.success ? "badge-green" : "");
  if (result.success && data &&
      (actionId === "backup.status" || actionId === "backup.rollback.center") &&
      (asArray(data.incompleteOperations).length > 0 ||
       asArray(data.potentiallyChangedFailedOperations).length > 0 ||
       asArray(data.potentiallyChangedCancelledOperations).length > 0)) {
    status.textContent = "REVISAR";
    status.className = "badge";
  }
  // Never override an unsuccessful operation with an optimistic fixture/partial-data badge.
  if (actionId === "browsers.extensions.health" && result.success && data) {
    const broken = typeof data.brokenCount === "number" ? data.brokenCount : 0;
    const residues = typeof data.dataOnlyCount === "number" ? data.dataOnlyCount : 0;
    const collisions = asArray(data.nameCollisions).length;
    status.textContent = broken > 0
      ? "REVISAR"
      : collisions > 0
        ? "REVISAR NOMBRES"
        : residues > 0
          ? "LIMPIEZA OPCIONAL"
          : "OK";
    status.className = "badge " +
      (broken === 0 && residues === 0 && collisions === 0 ? "badge-green" : "");
  }

  const message = document.createElement("p");
  message.className = "result-message";
  message.textContent = result.message;
  content.append(message);

  // These pagefile interlocks fail before OS mutation. Preserve the recovery
  // record; a postcheck failure must never claim that no change occurred.
  if (failedPagefile) {
    const guidanceByStatus: Record<string, string> = {
      SNAPSHOT_UNREADABLE: "No se ejecutó ningún cambio. Conservá el archivo de recuperación original; revisá permisos o corrupción antes de reintentar.",
      SNAPSHOT_INCOMPLETE: "No se ejecutó ningún cambio. Conservá el snapshot incompleto; falta la configuración original necesaria para restaurar con seguridad.",
      SNAPSHOT_RETAINED_REVIEW_REQUIRED: "No se ejecutó ningún cambio. El estado coincide con el baseline guardado; revisá y conservá el registro de recuperación antes de volver a aplicar un perfil.",
      SNAPSHOT_SAVE_FAILED: "No se ejecutó ningún cambio. Conservá cualquier snapshot existente y comprobá permisos y espacio del directorio de estado."
    };
    const guidanceText = typeof data?.status === "string"
      ? guidanceByStatus[data.status] : undefined;
    if (guidanceText) {
      const guidance = document.createElement("p");
      guidance.className = "result-message";
      guidance.textContent = guidanceText;
      content.append(guidance);
    }
  }

  renderStructuredResult(actionId, data, content);
  if (!result.success) {
    // Partial failure evidence remains readable, but it must never provide
    // operational controls or create a WRITE flow from incomplete analysis.
    content.querySelectorAll<
      HTMLButtonElement | HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement
    >("button, input, select, textarea").forEach(control => {
      control.disabled = true;
      control.setAttribute("aria-disabled", "true");
      control.title = "Acción no disponible tras un análisis fallido.";
    });
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

  // Compact has five metric columns: show the used value in full rather than
  // silently ellipsizing the used/total pair at 1366px. Total stays in Developer.
  byId("ram-value").textContent = document.body.dataset.mode === "compact"
    ? formatBytes(snapshot.memoryUsedBytes)
    : formatBytes(snapshot.memoryUsedBytes) + " / " + formatBytes(snapshot.memoryTotalBytes);
  const ramDetail = memoryPercent.toFixed(0) + "% en uso · " + formatBytes(snapshot.memoryAvailableBytes) + " disponibles";
  const ramSub = byId("ram-sub");
  ramSub.textContent = document.body.dataset.mode === "compact"
    ? memoryPercent.toFixed(0) + "% en uso"
    : ramDetail;
  ramSub.setAttribute("title", ramDetail);
  ramSub.setAttribute("aria-label", ramDetail);
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
  const tabletStatus = byId("tablet-system-status");
  const tabletSource = byId("tablet-status-source");
  const nextTabletStatus = provider.surface === "demo"
    ? "Estado de demostración"
    : snapshot.integrityStatus === "NOT_EVALUATED"
      ? "Métricas locales actualizadas"
      : snapshot.integrityStatus === "OK"
        ? "Integridad comprobada"
        : "Integridad: " + snapshot.integrityStatus.replaceAll("_", " ");
  if (tabletStatus.textContent !== nextTabletStatus) tabletStatus.textContent = nextTabletStatus;
  const nextTabletSource = provider.surface === "demo"
    ? "Datos simulados · no se accede a Windows"
    : "Lecturas locales · sin cambios automáticos";
  if (tabletSource.textContent !== nextTabletSource) tabletSource.textContent = nextTabletSource;
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

  const tabletBadge = document.getElementById("tablet-state-badge");
  if (tabletBadge) {
    tabletBadge.className = className;
    if (tabletBadge.textContent !== state) {
      tabletBadge.replaceChildren(
        document.createElement("i"),
        document.createTextNode(state));
    }
  }
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
  if (originView !== "dashboard") {
    // A previous result (and its selection buttons) must not remain usable
    // while the replacement operation is running.
    latestResultByView.delete(originView);
    hideModuleResult();
  }
  const originalNodes = button
    ? Array.from(button.childNodes).map(node => node.cloneNode(true))
    : [];
  const action = currentCatalog.find(item => item.id === id);
  const isWrite = action?.mode === "WRITE";
  // A cleanup attempt consumes its previous preview even if the backend
  // fails or returns a partial outcome. Require a new preview to retry.
  if (id === "disk.scan" || id === "disk.cleanup.execute" || id === "disk.cleanup.safe") {
    // A new scan invalidates both previous estimates and permission to clean.
    invalidateRecoveryEstimate();
    cleanupPreviewReady = false;
    const cleanupButton = document.querySelector<HTMLButtonElement>(
      'button[data-action="disk.cleanup.execute"]');
    if (cleanupButton) {
      cleanupButton.disabled = true;
      cleanupButton.title = "Primero previsualizá la limpieza.";
    }
  }
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
    if (originView !== "dashboard") {
      latestResultByView.set(originView, { id, result });
    }
    if (id === "disk.cleanup.safe") {
      const previewBytes = result.data?.estimatedBytes;
      cleanupPreviewReady = result.success && result.dryRun &&
        typeof previewBytes === "number" && Number.isFinite(previewBytes) &&
        previewBytes > 0;
      const cleanButton = document.querySelector<HTMLButtonElement>('button[data-action="disk.cleanup.execute"]');
      if (cleanButton) {
        cleanButton.disabled = !cleanupPreviewReady;
        cleanButton.title = cleanupPreviewReady
          ? "Previsualización realizada. Confirmá para limpiar."
          : "Primero previsualizá la limpieza.";
      }
    }
    pushActivity(id, result.message, result.success ? "ok" : "warn");
    const failedPagefile = !result.success && id.startsWith("memory.pagefile.");
    toast(result.success ? "Acción completada" : failedPagefile ? "Acción fallida" : "Acción con advertencias",
      result.message, result.success ? "success" : failedPagefile ? "error" : "info");
    const estimated = result.data?.estimatedBytes;
    if (result.success && (id === "disk.scan" || id === "disk.cleanup.safe") &&
        typeof estimated === "number" && Number.isFinite(estimated) && estimated >= 0) {
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
    // Keep the failure visible after the toast expires and across navigation;
    // a rejected local bridge call is evidence, not an empty result panel.
    if (originView !== "dashboard") {
      const failed: ActionResult = {
        success: false,
        dryRun: false,
        message,
        data: { status: "ERROR", actionId: id }
      };
      latestResultByView.set(originView, { id, result: failed });
      if (activeView === originView) renderActionResult(id, failed);
    }
  } finally {
    actionInFlight = false;
    if (button) {
      button.disabled = id === "disk.cleanup.execute" && !cleanupPreviewReady;
      if (id === "disk.cleanup.execute" && !cleanupPreviewReady)
        button.title = "Primero previsualizá la limpieza.";
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
    const saved = latestResultByView.get(view);
    if (saved) renderActionResult(saved.id, saved.result);
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
  byId("mode-status").textContent =
    initialMode === "developer" ? "Vista experta activa" : "Vista compacta activa";
  byId("mode-toggle").addEventListener("click", async () => {
    const developer = document.body.dataset.mode !== "developer";
    document.body.dataset.mode = developer ? "developer" : "compact";
    byId("mode-toggle").textContent = developer ? "Modo compacto" : "Modo experto";
    byId("mode-status").textContent = developer ? "Vista experta activa" : "Vista compacta activa";
    if (currentSnapshot) renderSnapshot(currentSnapshot);

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
