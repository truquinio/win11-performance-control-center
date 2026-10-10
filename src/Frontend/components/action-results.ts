import type { ActionDefinition } from "../contracts";
import { formatBytes, formatDuration, asRecord, asArray, displayValue } from "./formatting";
import { createResultGrid, createResultTable } from "./result-primitives";

// The renderer is testable independently of the WebView2 host: actions and
// catalog are injected rather than duplicated or cached in this module.
type Dispatch = (id: string, button?: HTMLButtonElement,
  parameters?: Record<string, unknown>) => Promise<void>;
type Notify = (title: string, message: string,
  kind?: "success" | "error" | "info") => void;
let dispatchAction: Dispatch = () => Promise.reject(new Error("Renderer not configured"));
let getCurrentCatalog: () => ActionDefinition[] = () => [];
let toastNotice: Notify = () => { throw new Error("Renderer not configured"); };
const MaxProcessSelection = 20;

export function configureResultRenderer(deps: {
  runAction: Dispatch;
  getCatalog: () => ActionDefinition[];
  toast: Notify;
}): void {
  dispatchAction = deps.runAction;
  getCurrentCatalog = deps.getCatalog;
  toastNotice = deps.toast;
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
      toastNotice("Selección requerida", "Seleccioná al menos un proceso.", "error");
      return;
    }

    if (processIds.length > MaxProcessSelection) {
      toastNotice(
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

    await dispatchAction(
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

    await dispatchAction(
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
      displayValue(service.reason) +
        (typeof service.dependentServiceCount === "number" &&
         service.dependentServiceCount > 0
          ? " · Dependientes: " + service.dependentServiceCount
          : "")
    ]) {
      const cell = row.insertCell();
      cell.textContent = value;
    }

    const actions = row.insertCell();
    actions.className = "inline-actions";
    if (!serviceName) {
      actions.textContent = "Sin acciones";
      continue;
    }

    const dependencies = document.createElement("button");
    dependencies.className = "btn btn-small";
    dependencies.textContent = "Dependencias";
    dependencies.addEventListener("click", () => {
      void dispatchAction(
        "startup.service.dependencies",
        dependencies,
        { serviceName });
    });
    actions.append(dependencies);

    if (protectedService) {
      const note = document.createElement("span");
      note.textContent = "Cambios bloqueados";
      actions.append(note);
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
      void dispatchAction(
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
      void dispatchAction(
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
        void dispatchAction(
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

export function renderStructuredResult(  actionId: string,
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
        void dispatchAction(
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
        void dispatchAction("drivers.rescan", rescan, { confirmed: true });
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

  if (actionId === "browsers.edge.performance.audit" ||
      actionId === "browsers.edge.performance.optimize" ||
      actionId === "browsers.edge.performance.restore") {
    const root = actionId === "browsers.edge.performance.audit"
      ? data
      : (asRecord(data.report) ?? {});
    const policy = asRecord(root.policy);
    const optimized = root.optimized === true;
    const restoreAvailable = actionId === "browsers.edge.performance.audit"
      ? root.restoreAvailable === true
      : data.restoreAvailable === true;

    content.append(createResultGrid([
      ["Estado", optimized ? "Optimizado" : "Revisar"],
      ["Procesos Edge", displayValue(root.processCount)],
      ["Ventanas visibles", displayValue(root.visibleWindowCount)],
      ["RAM (working set)", typeof root.workingSetBytes === "number" ? formatBytes(root.workingSetBytes) : "—"],
      ["Memoria privada", typeof root.privateBytes === "number" ? formatBytes(root.privateBytes) : "—"],
      ["AutoLaunch", displayValue(root.autoLaunchEntryCount)],
      ["Sleeping Tabs", policy?.sleepingTabsEnabled === 1 ? "ON" : "No optimizado"],
      ["Timeout", policy?.sleepingTabsTimeout === 300 ? "5 min" : displayValue(policy?.sleepingTabsTimeout)],
      ["Efficiency Mode", policy?.efficiencyModeEnabled === 1 && policy?.efficiencyMode === 0 ? "Siempre activo" : "No optimizado"],
      ["Background / Boost", policy?.backgroundModeEnabled === 0 && policy?.startupBoostEnabled === 0 ? "OFF / OFF" : "Revisar"],
      ["Rollback", restoreAvailable ? "Disponible" : "Sin snapshot"]
    ]));

    const guidance = document.createElement("div");
    guidance.className = "result-guidance";
    const title = document.createElement("strong");
    title.textContent = optimized ? "Configuración correcta" : "Qué puede mejorar";
    const detail = document.createElement("span");
    detail.textContent = String(root.guidance ?? data.detail ?? "Sin detalles.");
    guidance.append(title, detail);
    content.append(guidance);

    const controls = document.createElement("div");
    controls.className = "result-actions";

    if (!optimized) {
      const optimize = document.createElement("button");
      optimize.className = "btn btn-primary";
      optimize.textContent = "Optimizar Edge";
      optimize.addEventListener("click", () => {
        if (!window.confirm(
          "Se aplicarán políticas reversibles de rendimiento de Edge.\n\nNo se cerrará Edge ni se borrarán sesiones, favoritos o extensiones. ¿Continuar?"
        )) return;
        void dispatchAction(
          "browsers.edge.performance.optimize",
          optimize,
          { confirmed: true });
      });
      controls.append(optimize);
    }

    if (restoreAvailable) {
      const restore = document.createElement("button");
      restore.className = "btn btn-secondary";
      restore.textContent = "Restaurar configuración anterior";
      restore.addEventListener("click", () => {
        if (!window.confirm(
          "¿Restaurar las políticas y entradas de autoarranque capturadas antes de la optimización?"
        )) return;
        void dispatchAction(
          "browsers.edge.performance.restore",
          restore,
          { confirmed: true });
      });
      controls.append(restore);
    }

    if (controls.childElementCount) content.append(controls);
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
        void dispatchAction("browsers.extensions.orphans.preview", button));
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
        void dispatchAction(
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
        void dispatchAction(
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

  if (actionId === "disk.directories.watch") {
    const items = asArray(data.items)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    content.append(createResultGrid([
      ["Carpetas medidas", String(items.length)],
      ["Baseline inicial", data.baselineCreated ? "Si" : "No"],
      ["Estado", data.partial ? "PARCIAL (sin inferencias)" : "Comparacion disponible"],
      ["Archivos borrados", "0"]
    ]));
    if (items.length) {
      content.append(createResultTable(
        ["Carpeta", "Tamano medido", "Cambio comprobable", "Cobertura"],
        items.map(item => [
          displayValue(item.path),
          typeof item.measuredBytes === "number" ? formatBytes(item.measuredBytes) : "?",
          typeof item.growthBytes === "number"
            ? (item.growthBytes >= 0 ? "+" : "-") + formatBytes(Math.abs(item.growthBytes))
            : "No comparable",
          item.complete ? "Completa" : "Parcial"
        ])
      ));
    }
    return;
  }
  if (actionId === "disk.duplicates.scan") {
    const groups = asArray(data.duplicates)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    content.append(createResultGrid([
      ["Archivos inspeccionados", displayValue(data.scannedFiles)],
      ["Grupos SHA-256", String(groups.length)],
      ["Ahorro logico potencial", typeof data.logicalPotentialBytes === "number"
        ? formatBytes(data.logicalPotentialBytes) : "No disponible"],
      ["Estado", data.partial ? "PARCIAL: no se reviso todo" : "Carpetas elegidas completas"],
      ["Espacio liberado", "0 B (solo lectura)"]
    ]));
    if (groups.length) {
      content.append(createResultTable(
        ["SHA-256", "Tamano por copia", "Archivos identicos"],
        groups.slice(0, 12).map(item => [
          String(item.sha256 ?? "").slice(0, 16),
          typeof item.fileBytes === "number" ? formatBytes(item.fileBytes) : "?",
          asArray(item.paths).map(displayValue).join(" | ")
        ])
      ));
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
          void dispatchAction(actionIdValue, button));
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
          void dispatchAction(
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

  if (actionId === "startup.service.dependencies") {
    const dependents = asArray(data.dependents);
    content.append(createResultGrid([
      ["Servicio", displayValue(data.displayName)],
      ["Nombre", displayValue(data.serviceName)],
      ["Dependientes", displayValue(data.dependentCount)],
      ["Decisión", Number(data.dependentCount) > 0 ? "Cambios protegidos" : "Sin dependientes detectados"]
    ]));
    if (dependents.length) {
      const pre = document.createElement("pre");
      pre.className = "result-pre";
      pre.textContent = dependents.map(displayValue).join("\n");
      content.append(pre);
    }
    return;
  }

  if (actionId === "startup.tasks.preview") {
    const tasks = asArray(data.tasks)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);

    content.append(createResultGrid([
      ["Tareas", displayValue(data.taskCount)],
      ["Revisables", displayValue(data.eligibleCount)],
      ["Protegidas", displayValue(data.protectedCount)],
      ["Con rollback", displayValue(data.restoreAvailableCount)]
    ]));

    if (tasks.length) {
      content.append(createResultTable(
        ["Tarea", "Estado", "Acción", "Diagnóstico", "Clasificación", "Motivo"],
        tasks.map(item => [
          displayValue(item.fullName),
          displayValue(item.state),
          displayValue(item.actionKind),
          displayValue(item.actionStatus),
          item.protected === true ? "Protegida" : "Revisable",
          displayValue(item.reason)
        ])
      ));

      const controls = document.createElement("div");
      controls.className = "result-actions";
      for (const task of tasks.slice(0, 30)) {
        if (typeof task.entryId !== "string") continue;

        if (task.protected !== true &&
            String(task.state ?? "").toLowerCase() !== "disabled") {
          const disable = document.createElement("button");
          disable.className = "btn btn-secondary";
          disable.textContent = "Deshabilitar · " + displayValue(task.taskName);
          disable.addEventListener("click", () => {
            if (!window.confirm(
              "¿Deshabilitar " + displayValue(task.fullName) +
              " para futuras ejecuciones?\n\nNo se terminará ningún proceso actual y se guardará rollback."
            )) return;
            void dispatchAction(
              "startup.task.disable",
              disable,
              { entryId: String(task.entryId), confirmed: true });
          });
          controls.append(disable);
        }

        if (task.restoreAvailable === true) {
          const restore = document.createElement("button");
          restore.className = "btn btn-secondary";
          restore.textContent = "Restaurar · " + displayValue(task.taskName);
          restore.addEventListener("click", () => {
            if (!window.confirm(
              "¿Volver a habilitar " + displayValue(task.fullName) + "?"
            )) return;
            void dispatchAction(
              "startup.task.restore",
              restore,
              { entryId: String(task.entryId), confirmed: true });
          });
          controls.append(restore);
        }
      }
      if (controls.childElementCount) content.append(controls);
    }
    return;
  }

  if (actionId === "startup.task.disable" ||
      actionId === "startup.task.restore") {
    content.append(createResultGrid([
      ["Estado", displayValue(data.status)],
      ["Tarea", displayValue(data.fullName)],
      ["Antes", displayValue(data.beforeState)],
      ["Después", displayValue(data.afterState)],
      ["Rollback", data.restoreAvailable === true ? "Disponible" : "—"],
      ["Detalle", displayValue(data.detail)]
    ]));
    return;
  }

  if (actionId === "system.registry.evidence" ||
      actionId === "system.com.evidence" ||
      actionId === "system.certificates.audit") {
    const items = asArray(data.items)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    content.append(createResultGrid([
      ["Área", displayValue(data.area)],
      ["Estado", displayValue(data.status)],
      ["Atención", displayValue(data.attentionCount)],
      ["Escaneo", data.partial === true ? "Parcial / acotado" : "Completo dentro del scope"],
      ["Modo", "Read-only"]
    ]));
    if (items.length) {
      content.append(createResultTable(
        ["Elemento", "Estado", "Fuente", "Detalle"],
        items.map(item => [
          displayValue(item.name),
          displayValue(item.status),
          displayValue(item.source),
          displayValue(item.detail)
        ])
      ));
    }
    return;
  }

  if (actionId === "diagnostics.bundle.create") {
    content.append(createResultGrid([
      ["Sanitizado", data.sanitized === true ? "Sí" : "No"],
      ["JSON", displayValue(data.jsonFiles)],
      ["Tamaño", typeof data.bytes === "number" ? formatBytes(data.bytes) : "—"],
      ["Ruta local", displayValue(data.path)]
    ]));
    const note = document.createElement("p");
    note.className = "result-message";
    note.textContent =
      "Excluye comandos de autoarranque, argumentos de tareas, IDs PnP/USB, claves de producto, cookies, sesiones y logs crudos.";
    content.append(note);
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
            void dispatchAction(
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
            void dispatchAction(
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

  if (actionId === "processes.hygiene.analyze") {
    const items = asArray(data.items)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);

    content.append(createResultGrid([
      ["Detenibles", displayValue(data.stoppableCount)],
      ["Protegidos", displayValue(data.protectedCount)],
      ["RAM potencial", typeof data.estimatedReclaimMb === "number"
        ? Number(data.estimatedReclaimMb).toFixed(1) + " MiB" : "—"],
      ["Política", "PID + huella + reclasificación"]
    ]));

    if (items.length) {
      content.append(createResultTable(
        ["Tipo", "Identidad", "PID", "RAM", "Edad", "Decisión", "Motivo"],
        items.map(item => [
          displayValue(item.category),
          displayValue(item.identity),
          displayValue(item.processId),
          typeof item.estimatedReclaimMb === "number"
            ? Number(item.estimatedReclaimMb).toFixed(1) + " MiB" : "—",
          typeof item.ageMinutes === "number"
            ? formatDuration(Number(item.ageMinutes) * 60) : "—",
          item.protected === true
            ? "Protegido"
            : item.stoppable === true
              ? "Puede detenerse"
              : "Solo observar",
          displayValue(item.reason)
        ])
      ));

      const controls = document.createElement("div");
      controls.className = "result-actions";
      for (const item of items.filter(candidate =>
        candidate.stoppable === true &&
        candidate.protected !== true &&
        typeof candidate.processId === "number" &&
        typeof candidate.fingerprint === "string").slice(0, 12)) {
        const button = document.createElement("button");
        button.className = "btn btn-secondary";
        button.textContent = "Detener · " + displayValue(item.identity);
        button.addEventListener("click", () => {
          if (!window.confirm(
            "¿Detener únicamente " + displayValue(item.identity) +
            "?\n\nLa app volverá a verificar PID, huella y clasificación antes de detenerlo. " +
            "Bots, PM2, Desktop Commander, ChatGPT, Claude y procesos no clasificados quedan fuera."
          )) return;
          void dispatchAction(
            "processes.hygiene.stop",
            button,
            {
              processId: Number(item.processId),
              fingerprint: String(item.fingerprint),
              confirmed: true
            });
        });
        controls.append(button);
      }
      if (controls.childElementCount) content.append(controls);
    }
    return;
  }

  if (actionId === "processes.hygiene.stop") {
    content.append(createResultGrid([
      ["Estado", displayValue(data.status)],
      ["Tipo", displayValue(data.category)],
      ["Elemento", displayValue(data.identity)],
      ["Detenidos", asArray(data.stoppedProcessIds).map(displayValue).join(", ") || "—"],
      ["Siguen vivos", asArray(data.stillAliveProcessIds).map(displayValue).join(", ") || "0"],
      ["RAM observada", typeof data.estimatedReclaimMb === "number"
        ? Number(data.estimatedReclaimMb).toFixed(1) + " MiB" : "—"]
    ]));
    const detail = document.createElement("p");
    detail.className = "result-message";
    detail.textContent = displayValue(data.detail);
    content.append(detail);
    return;
  }

  if (actionId === "browsers.automation.audit") {
    const groups = asArray(data.families)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);
    const processes = asArray(data.processes)
      .map(asRecord)
      .filter((item): item is Record<string, unknown> => item !== null);

    content.append(createResultGrid([
      ["Procesos observados", displayValue(data.totalBrowserProcesses)],
      ["Working sets sumados", typeof data.workingSetBytes === "number"
        ? formatBytes(data.workingSetBytes) : "—"],
      ["Memoria privada", typeof data.privateBytes === "number"
        ? formatBytes(data.privateBytes) : "—"],
      ["Capturas", displayValue(data.sampleCount)],
      ["Ejecución", "Solo lectura, sin cierres"]
    ]));

    if (groups.length) {
      content.append(createResultTable(
        ["Familia", "Procesos", "Working set", "Privada", "Estado", "Propietario confirmado"],
        groups.map(item => [
          displayValue(item.family),
          displayValue(item.processCount),
          typeof item.workingSetBytes === "number"
            ? formatBytes(item.workingSetBytes) : "—",
          typeof item.privateBytes === "number"
            ? formatBytes(item.privateBytes) : "—",
          displayValue(item.status),
          asArray(item.confirmedOwners).map(displayValue).join(", ") || "Sin atribución"
        ])
      ));
      const detail = document.createElement("p");
      detail.className = "result-message";
      detail.textContent = groups.map(item =>
        displayValue(item.family) + ": " + displayValue(item.detail)
      ).join(" ");
      content.append(detail);
    }

    if (processes.length) {
      content.append(createResultTable(
        ["PID", "PPID", "Familia", "Rol", "Dueño", "Working set", "Privada"],
        processes.slice(0, 40).map(item => [
          displayValue(item.processId),
          displayValue(item.parentProcessId),
          displayValue(item.family),
          displayValue(item.role),
          displayValue(item.owner),
          typeof item.workingSetBytes === "number"
            ? formatBytes(item.workingSetBytes) : "—",
          typeof item.privateBytes === "number"
            ? formatBytes(item.privateBytes) : "—"
        ])
      ));
    }

    const note = document.createElement("p");
    note.className = "result-message";
    note.textContent = displayValue(data.accountingNotice) + " " +
      displayValue(data.safetyNotice);
    content.append(note);

    const actions = document.createElement("div");
    actions.className = "result-actions";
    for (const [label, next] of [
      ["Volver a medir", "browsers.automation.audit"],
      ["Revisar Edge", "browsers.edge.performance.audit"],
      ["Revisar residuos de laboratorio", "processes.hygiene.analyze"]
    ] as const) {
      const button = document.createElement("button");
      button.className = "btn btn-secondary";
      button.textContent = label;
      button.addEventListener("click", () => {
        void dispatchAction(next, button);
      });
      actions.append(button);
    }
    content.append(actions);
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
        const definition = getCurrentCatalog().find(action =>
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
              void dispatchAction(actionIdValue, button, { confirmed: true });
            } else {
              void dispatchAction(actionIdValue, button);
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
          void dispatchAction(
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
