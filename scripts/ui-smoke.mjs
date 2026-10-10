import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const demo = path.join(root, "artifacts", "demo");
const playwrightRoot = path.join(process.env.LOCALAPPDATA ?? "", "ms-playwright");
const playwrightBrowser = fs.existsSync(playwrightRoot)
  ? fs.readdirSync(playwrightRoot)
      .filter(name => name.startsWith("chromium_headless_shell-"))
      .map(name => path.join(playwrightRoot, name, "chrome-headless-shell-win64", "chrome-headless-shell.exe"))
      .find(fs.existsSync)
  : undefined;
const edgeCandidates = [
  playwrightBrowser,
  "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
  "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe"
].filter(Boolean);
const edge = edgeCandidates.find(fs.existsSync);
if (!edge) throw new Error("Chromium/Edge headless no encontrado.");

const server = http.createServer((req, res) => {
  const pathname = new URL(req.url ?? "/", "http://127.0.0.1").pathname;
  const target = path.join(demo, pathname === "/" ? "index.html" : pathname.slice(1));
  if (!target.startsWith(demo) || !fs.existsSync(target)) {
    res.writeHead(404).end();
    return;
  }
  const ext = path.extname(target);
  const type =
    ext === ".js" ? "text/javascript" :
    ext === ".css" ? "text/css" :
    ext === ".svg" ? "image/svg+xml" :
    ext === ".png" ? "image/png" :
    ext === ".ico" ? "image/x-icon" :
    "text/html";
  res.writeHead(200, { "Content-Type": type });
  fs.createReadStream(target).pipe(res);
});
await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
const appPort = server.address().port;

async function reserveFreePort() {
  const probe = http.createServer();
  await new Promise(resolve => probe.listen(0, "127.0.0.1", resolve));
  const port = probe.address().port;
  await new Promise(resolve => probe.close(resolve));
  return port;
}

const debugPort = await reserveFreePort();
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "wpcc-ui-smoke-"));
const edgeProcess = spawn(edge, [
  "--headless=new",
  "--disable-gpu",
  `--remote-debugging-port=${debugPort}`,
  `--user-data-dir=${profile}`,
  `http://127.0.0.1:${appPort}/?mode=developer`
], { stdio: "ignore" });

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

async function cleanup(socket) {
  try { socket?.close(); } catch {}
  edgeProcess.kill();
  await sleep(300);
  await new Promise(resolve => server.close(resolve));
  try {
    fs.rmSync(profile, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 });
  } catch {
    // Edge can briefly retain profile handles after headless shutdown.
  }
}

let socket;
try {
let target;
// A cold browser on a busy CI runner can take several seconds to expose the
// debugger and may list other pages first; only the app's own page counts.
const appOrigin = `http://127.0.0.1:${appPort}/`;
for (let i = 0; i < 300; i++) {
  try {
    const pages = await (await fetch(`http://127.0.0.1:${debugPort}/json`)).json();
    target = pages.find(item =>
      item.type === "page" && String(item.url ?? "").startsWith(appOrigin));
    if (target?.webSocketDebuggerUrl) break;
  } catch {}
  await sleep(100);
}
if (!target?.webSocketDebuggerUrl) throw new Error("CDP no disponible.");

socket = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((resolve, reject) => {
  socket.addEventListener("open", resolve, { once: true });
  socket.addEventListener("error", reject, { once: true });
});
let nextId = 1;
const pending = new Map();
const exceptions = [];
socket.addEventListener("message", event => {
  const message = JSON.parse(event.data);
  if (message.method === "Runtime.exceptionThrown")
    exceptions.push(message.params?.exceptionDetails?.text ?? "JS exception");
  if (message.method === "Runtime.consoleAPICalled") {
    const values = (message.params?.args ?? [])
      .map(arg => arg.value ?? arg.description ?? "")
      .join(" ");
    if (values.startsWith("SMOKE_ACTION"))
      console.log(values);
  }
  if (!message.id) return;
  const waiter = pending.get(message.id);
  if (!waiter) return;
  pending.delete(message.id);
  message.error ? waiter.reject(new Error(message.error.message)) : waiter.resolve(message.result);
});
const call = (method, params = {}) => new Promise((resolve, reject) => {
  const id = nextId++;
  pending.set(id, { resolve, reject });
  socket.send(JSON.stringify({ id, method, params }));
});

// Assertions against a document that has not finished loading report a wall
// of unrelated failures, so readiness is awaited instead of assumed.
async function waitForPage(predicate, label, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const probe = await call("Runtime.evaluate", {
        expression: predicate,
        returnByValue: true
      });
      if (probe.result?.value === true) return;
    } catch {
      // The execution context is replaced while a navigation commits.
    }
    await sleep(100);
  }
  let state = "sin contexto";
  try {
    const probe = await call("Runtime.evaluate", {
      expression:
        `location.href + " · " + document.readyState + " · body=" + document.body?.className`,
      returnByValue: true
    });
    state = String(probe.result?.value ?? state);
  } catch {
    // Keep the generic description.
  }
  throw new Error(
    "UI_SMOKE_FAIL la página no estuvo lista: " + label + " (" + state + ")" +
    (exceptions.length ? "\n" + exceptions.join("\n") : ""));
}

await call("Runtime.enable");
await waitForPage(
  `document.readyState === "complete" &&
   document.body.classList.contains("demo") &&
   document.querySelectorAll("#catalog-grid .catalog-item").length > 0`,
  "demo");
await sleep(1200);
const expression = String.raw`
(async () => {
  const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
  const visible = el => {
    const style = getComputedStyle(el);
    return style.display !== "none" && style.visibility !== "hidden" && el.getClientRects().length > 0;
  };
  const errors = [];

  const iconImages = [...document.querySelectorAll("img.asset-icon")];
  await Promise.all(iconImages.map(img => img.complete ? Promise.resolve() : new Promise(resolve => {
    img.addEventListener("load", resolve, { once: true });
    img.addEventListener("error", resolve, { once: true });
  })));
  const brokenIcons = iconImages.filter(img => img.naturalWidth === 0 || img.naturalHeight === 0);
  if (brokenIcons.length) errors.push("Iconos rotos: " + brokenIcons.length);

  const allButtons = [...document.querySelectorAll("button")];
  for (const button of allButtons) {
    const accessibleName = (
      button.getAttribute("aria-label") ||
      button.getAttribute("title") ||
      button.textContent ||
      ""
    ).trim();
    if (!accessibleName)
      errors.push("Botón sin nombre accesible");
    if (!button.disabled && button.tabIndex < 0)
      errors.push("Botón fuera del orden de teclado: " + accessibleName);
  }

  const developerButtons = [...document.querySelectorAll(".developer-nav")].filter(visible);
  const developerMetrics = [...document.querySelectorAll(".metrics-grid .metric-card")].filter(visible);
  const developerActions = [...document.querySelectorAll(".actions-grid .action-card")].filter(visible);
  if (developerMetrics.length !== 8 ||
      !visible(document.querySelector(".developer-driver-card")) ||
      visible(document.querySelector(".developer-reboot-card")))
    errors.push("Developer debe mostrar 8 métricas con Drivers y Activación; reinicio sigue en Interlocks");
  if (developerActions.length !== 6 ||
      !visible(document.querySelector(".action-memory")))
    errors.push("Developer debe mostrar 6 acciones recomendadas, incluida MemoryTrim");
  // Browser tests explicitly approve the safe cleanup confirmation dialog.
  window.confirm = () => true;
  let moduleButtonsSeen = 0;
  let moduleActionsExecuted = 0;
  for (const button of developerButtons) {
    button.click();
    await wait(15);
    const activeVisible = developerButtons.filter(node => node.classList.contains("active"));
    if (activeVisible.length !== 1 || activeVisible[0] !== button)
      errors.push("Ruta Developer ambigua: " + button.textContent.trim());
    if (!document.querySelector("#module-view.active"))
      errors.push("Módulo no visible: " + button.textContent.trim());
    if (!document.querySelector("#module-title")?.textContent?.trim())
      errors.push("Módulo sin título: " + button.textContent.trim());

    const moduleButtons = [...document.querySelectorAll("#module-actions button")];
    moduleButtonsSeen += moduleButtons.length;
    for (const actionButton of moduleButtons) {
      const label = actionButton.textContent.trim();
      if (actionButton.disabled) {
        if (!actionButton.title)
          errors.push("Acción deshabilitada sin explicación: " + label);
        continue;
      }
      if (!label) {
        errors.push("Acción de módulo sin etiqueta: " + button.textContent.trim());
        continue;
      }

      const resultPanel = document.querySelector("#module-result-panel");
      resultPanel?.classList.add("is-hidden");
      console.log("SMOKE_ACTION", actionButton.dataset.action || label);
      actionButton.click();
      for (let i = 0; i < 160 && actionButton.hasAttribute("aria-busy"); i++) await wait(25);
      if (actionButton.hasAttribute("aria-busy")) {
        errors.push("Acción de módulo quedó bloqueada: " + label);
        continue;
      }
      if (actionButton.dataset.action === "disk.cleanup.execute") {
        if (!actionButton.disabled)
          errors.push("Limpieza permite repetir WRITE sin nueva previsualización");
      } else if (actionButton.disabled) {
        errors.push("Acción de módulo quedó deshabilitada inesperadamente: " + label);
      }
      for (let i = 0; i < 40 &&
           resultPanel?.classList.contains("is-hidden"); i++) await wait(25);
      if (resultPanel?.classList.contains("is-hidden"))
        errors.push("Acción de módulo sin resultado visible: " + label);
      if (!document.querySelector("#module-result-title")?.textContent?.trim())
        errors.push("Resultado de módulo sin título: " + label);
      if (document.body.classList.contains("demo") &&
          !document.querySelector("#module-result-subtitle")?.textContent?.toLowerCase().includes("simulad"))
        errors.push("La demo presenta un resultado simulado como evidencia local: " + label);
      if (actionButton.dataset.action === "disk.cleanup.safe") {
        const cautionRow = [...document.querySelectorAll("#recovery-body tr")].find(
          row => row.cells[0]?.textContent?.includes("Temporales de Windows"));
        if (!cautionRow || cautionRow.cells[3]?.textContent?.trim() !== "—")
          errors.push("Recuperable incluye cachés de categoría CAUTION no autorizadas");
        if (!document.querySelector("#storage-recoverable")?.textContent?.includes("5.2"))
          errors.push("DEMO calcula un recuperable diferente de la suma SAFE del backend");
      }
      moduleActionsExecuted++;
    }
  }

  let writeFlowsExecuted = 0;
  window.confirm = () => true;

  async function runSelectionFlow(view, analyzeId, expectedWriteTitle, expectRollback) {
    const nav = developerButtons.find(node => node.dataset.view === view);
    if (!nav) {
      errors.push("Falta navegación para flujo WRITE: " + view);
      return;
    }

    nav.click();
    await wait(20);
    const analyzeButton = document.querySelector(
      '#module-actions button[data-action="' + analyzeId + '"]');
    if (!analyzeButton || analyzeButton.disabled) {
      errors.push("Análisis previo no disponible: " + analyzeId);
      return;
    }

    analyzeButton.click();
    for (let i = 0; i < 120 &&
         !document.querySelector("#module-result-content input[type=checkbox]"); i++)
      await wait(25);

    const checkbox = document.querySelector(
      "#module-result-content input[type=checkbox]");
    const applyButton = document.querySelector(
      "#module-result-content button.btn-primary");
    if (!checkbox || !applyButton) {
      errors.push("Flujo WRITE sin selección/aplicar: " + analyzeId);
      return;
    }

    checkbox.checked = true;
    applyButton.click();
    for (let i = 0; i < 120 &&
         document.querySelector("#module-result-title")?.textContent?.trim() !== expectedWriteTitle; i++)
      await wait(25);

    if (document.querySelector("#module-result-title")?.textContent?.trim() !== expectedWriteTitle) {
      errors.push("WRITE no mostró resultado: " + expectedWriteTitle);
      return;
    }
    writeFlowsExecuted++;

    if (expectRollback) {
      const rollbackButton = [...document.querySelectorAll("#module-result-content button")]
        .find(node => node.textContent.trim() === "Restaurar EcoQoS");
      if (!rollbackButton) {
        errors.push("EcoQoS aplicado sin botón de rollback");
        return;
      }
      rollbackButton.click();
      for (let i = 0; i < 120 &&
           document.querySelector("#module-result-title")?.textContent?.trim() !== "Restaurar EcoQoS"; i++)
        await wait(25);
      if (document.querySelector("#module-result-title")?.textContent?.trim() !== "Restaurar EcoQoS")
        errors.push("Rollback EcoQoS no mostró resultado");
      else
        writeFlowsExecuted++;
    }
  }

  await runSelectionFlow("memory", "memory.trim.preview", "MemoryTrim seleccionado", false);
  await runSelectionFlow("cpu", "cpu.ecoqos.analyze", "Aplicar EcoQoS seleccionado", true);

  // Every contextual write is accessible only after its READ/DRY_RUN
  // evidence has rendered. Exercise the user-facing button, never call
  // the DEMO provider directly; verify the dispatched ID in activity.
  const contextualScenarios = [
    ["startup", "startup.services.preview", "Dependencias", "startup.service.dependencies"],
    ["startup", "startup.services.preview", "Manual", "startup.service.setmode"],
    ["startup", "startup.entries.preview", "Deshabilitar ·", "startup.entry.disable"],
    ["startup", "startup.tasks.preview", "Deshabilitar ·", "startup.task.disable"],
    ["drivers", "drivers.usb.analyze", "Reiniciar ·", "drivers.usb.restart"],
    ["browsers", "browsers.edge.performance.audit", "Optimizar Edge", "browsers.edge.performance.optimize"],
    ["browsers", "browsers.extensions.orphans.preview", "Poner en cuarentena", "browsers.extensions.orphans.quarantine"]
  ];
  let contextualFlowsExecuted = 0;
  for (const [view, analyzeId, label, expectedId] of contextualScenarios) {
    const nav = developerButtons.find(node => node.dataset.view === view);
    nav?.click();
    await wait(30);
    const analyzeButton = document.querySelector(
      '#module-actions button[data-action="' + analyzeId + '"]');
    if (!analyzeButton || analyzeButton.disabled) {
      errors.push("Análisis contextual inaccesible: " + analyzeId);
      continue;
    }
    analyzeButton.click();
    for (let i = 0; i < 180 && analyzeButton.hasAttribute("aria-busy"); i++)
      await wait(25);
    const buttons = [...document.querySelectorAll("#module-result-content button")];
    const followup = buttons.find(node => node.textContent.trim().startsWith(label));
    if (!followup || followup.disabled) {
      errors.push("Control contextual sin funcionar: " + expectedId);
      continue;
    }
    followup.click();
    // Activity is written before the post-action refresh/render completes;
    // wait for aria-busy to clear before interacting with another preview.
    for (let i = 0; i < 240 && followup.hasAttribute("aria-busy"); i++)
      await wait(25);
    if (followup.hasAttribute("aria-busy")) {
      errors.push("La acción contextual quedó bloqueada: " + expectedId);
      continue;
    }
    if (document.querySelector("#activity-list .activity-row b")?.textContent !== expectedId) {
      errors.push("El botón contextual no despachó: " + expectedId);
      continue;
    }
    if (document.querySelector("#module-result-panel")?.classList.contains("is-hidden"))
      errors.push("La acción contextual no mostró resultado: " + expectedId);
    contextualFlowsExecuted++;
  }

  document.querySelector('[data-view="dashboard"]')?.click();
  await wait(20);
  const actions = [...document.querySelectorAll(".action-trigger")].filter(visible);
  for (const button of actions) {
    const before = document.querySelector("#activity-list")?.textContent;
    button.click();
    for (let i = 0; i < 80 && button.disabled; i++) await wait(25);
    if (button.disabled) errors.push("Botón quedó bloqueado: " + button.dataset.action);
    const after = document.querySelector("#activity-list")?.textContent;
    if (before === after) errors.push("Sin feedback de actividad: " + button.dataset.action);
  }

  const refresh = document.querySelector("#refresh-btn");
  if (!refresh) {
    errors.push("Falta botón Actualizar");
  } else {
    refresh.click();
    await wait(400);
    if (!document.querySelector("#cpu-value")?.textContent?.trim())
      errors.push("Actualizar dejó el dashboard sin métricas");
  }

  const diagnosticButton = document.querySelector("#diagnostic-btn");
  if (!diagnosticButton) {
    errors.push("Falta Diagnóstico completo");
  } else {
    const beforeDiagnostic = document.querySelector("#activity-list")?.textContent;
    diagnosticButton.click();
    await wait(1800);
    if (diagnosticButton.disabled)
      errors.push("Diagnóstico completo quedó bloqueado");
    if (beforeDiagnostic === document.querySelector("#activity-list")?.textContent)
      errors.push("Diagnóstico completo no generó actividad");
  }

  const more = document.querySelector(".link-btn[data-view='tools']");
  more?.click();
  await wait(15);
  if (document.querySelector("#module-title")?.textContent?.trim() !== "Action Catalog")
    errors.push("Ver más acciones no abrió Herramientas");

  const back = document.querySelector("#module-view [data-view='dashboard']");
  back?.click();
  await wait(15);
  if (!document.querySelector("#dashboard-view.active"))
    errors.push("Volver al Dashboard no funcionó");

  const mode = document.querySelector("#mode-toggle");
  const beforeMode = document.body.dataset.mode;
  mode?.click();
  await wait(15);
  if (document.body.dataset.mode === beforeMode)
    errors.push("Modo experto/compacto no cambió");
  const ramText = document.querySelector("#ram-value")?.textContent?.trim() ?? "";
  if (document.body.dataset.mode === "compact" && ramText.includes("/"))
    errors.push("La RAM compacta muestra un valor combinado que se trunca en pantallas de 1366px");
  if (document.body.dataset.mode === "developer" && !ramText.includes("/"))
    errors.push("El modo Developer perdió la capacidad total de RAM");
  const ramSub = document.querySelector("#ram-sub");
  if (document.body.dataset.mode === "compact" && !ramSub?.textContent?.trim().endsWith("% en uso"))
    errors.push("El subtítulo compacto de RAM se trunca en pantallas estrechas");
  if (document.body.dataset.mode === "developer" && !ramSub?.textContent?.includes("disponibles"))
    errors.push("El modo Developer no muestra la memoria disponible");
  if (!ramSub?.getAttribute("aria-label")?.includes("disponibles"))
    errors.push("Falta el detalle completo de RAM para accesibilidad");

  // Keyboard focus and the action buttons must survive the periodic
  // snapshot refresh (5 s) while a module view is open.
  const moduleNav = [...document.querySelectorAll(".nav-item")]
    .filter(visible)
    .find(node => node.dataset.view === "performance");
  moduleNav?.click();
  await wait(20);
  const focusTarget = document.querySelector("#module-actions button:not([disabled])");
  if (!focusTarget) {
    errors.push("Módulo Rendimiento sin acciones habilitadas");
  } else {
    focusTarget.focus();
    await wait(5600);
    if (!focusTarget.isConnected)
      errors.push("El refresco periódico reconstruyó los botones del módulo");
    if (document.activeElement !== focusTarget)
      errors.push("El refresco periódico quitó el foco de teclado");
  }
  if (moduleNav && moduleNav.getAttribute("aria-current") !== "page")
    errors.push("La navegación activa no expone aria-current");

  // A result belongs to the module that requested it: leaving the module
  // while the action is still running must not show it somewhere else.
  const resultPanel = document.querySelector("#module-result-panel");
  const resultHidden = () => resultPanel?.classList.contains("is-hidden");
  const slowAction = document.querySelector("#module-actions button:not([disabled])");
  const otherNav = [...document.querySelectorAll(".nav-item")]
    .filter(visible)
    .find(node => node.dataset.view === "cleanup");
  if (!slowAction || !otherNav || !resultPanel) {
    errors.push("No se pudo preparar la prueba de resultado fuera de módulo");
  } else {
    const activityBefore = document.querySelector("#activity-list")?.textContent;
    const originTitle = slowAction.closest(".module-action")?.querySelector("h3")?.textContent?.trim();
    slowAction.click();
    otherNav.click();
    // Completion is observed, not timed: the activity entry is written when
    // the action returns, shortly before the in-flight flag is released.
    for (let i = 0; i < 400 &&
         document.querySelector("#activity-list")?.textContent === activityBefore; i++)
      await wait(25);
    await wait(700);
    if (!resultHidden() &&
        document.querySelector("#module-result-title")?.textContent?.trim() === originTitle)
      errors.push("El resultado de un módulo apareció en otro módulo");

    // When the original operation completes off-screen, returning to its
    // module must reveal its verified result without rerunning the action.
    moduleNav?.click();
    await wait(40);
    if (resultHidden() ||
        document.querySelector("#module-result-title")?.textContent?.trim() !== originTitle)
      errors.push("El resultado terminado se perdió al volver al módulo de origen");
    otherNav.click();
    await wait(20);
    if (!resultHidden() &&
        document.querySelector("#module-result-title")?.textContent?.trim() === originTitle)
      errors.push("El resultado del módulo anterior invadió el módulo actual");

    document.querySelector("#module-actions button:not([disabled])")?.click();
    for (let i = 0; i < 400 && resultHidden(); i++) await wait(25);
    if (resultHidden())
      errors.push("El resultado no se mostró en su propio módulo");

    const headers = [...document.querySelectorAll("#module-result-content th")];
    if (!headers.length ||
        headers.some(cell => cell.scope !== "col" || !cell.textContent.trim()))
      errors.push("Tabla de resultados sin encabezados de columna accesibles");

    const toasts = [...document.querySelectorAll("#toast-host .toast")];
    if (!toasts.length ||
        toasts.some(node => !["status", "alert"].includes(node.getAttribute("role"))))
      errors.push("Toast sin rol accesible");
    await wait(400);
  }

  if (!document.querySelector("nav[aria-label]"))
    errors.push("Navegación principal sin etiqueta accesible");
  if (document.querySelector("svg:not([aria-hidden='true']):not([aria-label]):not([role])"))
    errors.push("SVG decorativo expuesto a lectores de pantalla");

  document.querySelector('[data-view="dashboard"]')?.click();
  await wait(20);

  const diagnostic = document.querySelector("#diagnostic-btn");
  if (!diagnostic || !diagnostic.textContent.trim())
    errors.push("Diagnóstico completo ausente o sin etiqueta");

  return {
    errors,
    developerButtons: developerButtons.length,
    compactButtons: [...document.querySelectorAll(".nav-item")].filter(visible).length,
    actions: actions.length,
    moduleButtonsSeen,
    moduleActionsExecuted,
    contextualFlowsExecuted,
    writeFlowsExecuted,
    literalButtons: document.querySelectorAll("button").length
  };
})()
`;
const evaluated = await call("Runtime.evaluate", {
  expression,
  awaitPromise: true,
  returnByValue: true
});
const result = evaluated.result?.value;
if (!result) throw new Error("UI smoke no devolvió resultado.");
if (exceptions.length) result.errors.push(...exceptions);
if (result.errors.length)
  throw new Error("UI_SMOKE_FAIL\n" + result.errors.join("\n"));

// Degraded start: the same frontend, now behind a fake local bridge whose
// first snapshot and first catalog request fail. The session must recover
// on its own through the periodic refresh instead of staying empty.
const fakeBridge = String.raw`
(() => {
  const listeners = [];
  const seen = { snapshot: 0, catalog: 0 };
  const requests = [];
  let failNextRun = false;
  let nextActionResult = null;
  const reply = (requestId, ok, result, error) => setTimeout(() => {
    for (const listener of listeners)
      listener({ data: { requestId, ok, result, error } });
  }, 5);
  const snapshot = () => ({
    capturedAt: new Date().toISOString(),
    cpuPercent: 37,
    memoryTotalBytes: 16 * 1024 ** 3,
    memoryUsedBytes: 8 * 1024 ** 3,
    memoryAvailableBytes: 8 * 1024 ** 3,
    diskDrive: "C:",
    diskTotalBytes: 500 * 1024 ** 3,
    diskFreeBytes: 100 * 1024 ** 3,
    network: null,
    integrityStatus: "NOT_EVALUATED",
    driverStatus: "NOT_EVALUATED",
    activationStatus: "NOT_EVALUATED",
    rebootRequired: null,
    uptimeSeconds: 60,
    operationState: "IDLE"
  });
  const catalog = [{
    id: "drivers.analyze", title: "Analizar drivers", description: "Prueba",
    category: "Drivers", risk: "SAFE", requiresAdmin: false,
    connectivity: "OFFLINE", reversible: false, mode: "READ"
  }, {
    id: "browsers.extensions.health", title: "Salud extensiones", description: "Prueba",
    category: "Browsers", risk: "SAFE", requiresAdmin: false,
    connectivity: "OFFLINE", reversible: false, mode: "READ"
  }, {
    id: "disk.cleanup.safe", title: "Previsualizar limpieza segura", description: "Prueba",
    category: "Storage", risk: "SAFE", requiresAdmin: false,
    connectivity: "OFFLINE", reversible: false, mode: "DRY_RUN"
  }, {
    id: "memory.trim.preview", title: "Previsualizar MemoryTrim", description: "Prueba",
    category: "Memory", risk: "SAFE", requiresAdmin: false,
    connectivity: "OFFLINE", reversible: false, mode: "DRY_RUN"
  }];
  const host = window.chrome ?? {};
  host.webview = {
    addEventListener(type, listener) {
      if (type === "message") listeners.push(listener);
    },
    postMessage(message) {
      if (!message || message.type !== "request") return;
      requests.push(message.method + (message.payload?.id ? ":" + message.payload.id : ""));
      if (message.method === "system.snapshot") {
        seen.snapshot++;
        return seen.snapshot === 1
          ? reply(message.requestId, false, null, "Fallo inyectado de snapshot")
          : reply(message.requestId, true, snapshot());
      }
      if (message.method === "actions.catalog") {
        seen.catalog++;
        return seen.catalog === 1
          ? reply(message.requestId, false, null, "Fallo inyectado de catálogo")
          : reply(message.requestId, true, catalog);
      }
      if (message.method === "system.reliability")
        return reply(message.requestId, true, []);
      if (message.method === "actions.run" && nextActionResult) {
        const result = nextActionResult;
        nextActionResult = null;
        return reply(message.requestId, true, result);
      }
      if (message.method === "actions.run" && failNextRun) {
        failNextRun = false;
        return reply(message.requestId, false, null, "Fallo de acción inyectado para QA");
      }
      return reply(message.requestId, true, {
        success: true, dryRun: false, message: "ok",
        data: { status: "OK", items: [] }
      });
    }
  };
  if (!window.chrome) window.chrome = host;
  window.__wpccFakeBridge = {
    requests,
    setFailNextRun() { failNextRun = true; },
    setNextActionResult(value) { nextActionResult = value; }
  };
})();
`;
await call("Page.enable");
await call("Page.addScriptToEvaluateOnNewDocument", { source: fakeBridge });
await call("Page.navigate", { url: `http://127.0.0.1:${appPort}/?mode=compact` });
await waitForPage(
  `document.readyState === "complete" &&
   Boolean(window.__wpccFakeBridge) &&
   document.body.classList.contains("local")`,
  "arranque degradado");

const degradedExpression = String.raw`
(async () => {
  const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
  const errors = [];
  const fake = () => window.__wpccFakeBridge;
  if (!fake().requests.includes("system.snapshot") ||
      !fake().requests.includes("actions.catalog"))
    await wait(600);
  // The injected failures answer first; a later refresh is what recovers.
  if (fake().requests.filter(item => item === "system.snapshot").length === 1 &&
      document.querySelector("#cpu-value")?.textContent?.trim() !== "—")
    errors.push("El arranque degradado no partió de un snapshot fallido");

  const recovered = () =>
    document.querySelector("#cpu-value")?.textContent?.trim() === "37%" &&
    document.querySelectorAll("#catalog-grid .catalog-item").length === 4 &&
    fake().requests.includes("actions.run:drivers.analyze");
  for (let i = 0; i < 180 && !recovered(); i++) await wait(50);

  if (document.querySelector("#cpu-value")?.textContent?.trim() !== "37%")
    errors.push("El snapshot no se recuperó tras un fallo de arranque");
  if (document.querySelectorAll("#catalog-grid .catalog-item").length !== 4)
    errors.push("El catálogo no se recuperó tras un fallo de arranque");
  if (!fake().requests.includes("actions.run:drivers.analyze"))
    errors.push("La línea base de salud no se ejecutó tras la recuperación");
  if (document.querySelectorAll("#toast-host .toast").length > 2)
    errors.push("El arranque degradado acumuló avisos repetidos");

  // Regression: failed LOCAL action must remain visible after its toast expires
  // and its result must not leak across module navigation.
  const system = document.querySelector('.nav-item[data-view="system"]');
  system?.click();
  await wait(40);
  const button = document.querySelector('#module-actions button[data-action="drivers.analyze"]');
  if (!button) {
    errors.push("No se pudo probar error de acción local");
  } else {
    fake().setFailNextRun();
    button.click();
    for (let i = 0; i < 200 && button.hasAttribute("aria-busy"); i++) await wait(25);
    const panel = document.querySelector("#module-result-panel");
    if (panel?.classList.contains("is-hidden") ||
        !panel?.textContent?.includes("Fallo de acción inyectado para QA") ||
        document.querySelector("#module-result-status")?.textContent?.trim() !== "WARNING")
      errors.push("El error de acción local no permanece visible en su módulo");
    document.querySelector('.nav-item[data-view="cleanup"]')?.click();
    await wait(25);
    if (!panel?.classList.contains("is-hidden"))
      errors.push("El error de acción local se filtró a otro módulo");
    system.click();
    await wait(25);
    if (panel?.classList.contains("is-hidden") ||
        !panel?.textContent?.includes("Fallo de acción inyectado para QA"))
      errors.push("Se perdió la evidencia del fallo al volver al módulo");
  }

  // A failed ActionResult with data present must never get an OK badge.
  document.querySelector("#mode-toggle")?.click();
  document.querySelector('.nav-item[data-view="browsers"]')?.click();
  await wait(30);
  const browserButton = document.querySelector(
    '#module-actions button[data-action="browsers.extensions.health"]');
  if (!browserButton) {
    errors.push("No hay botón de salud de extensiones para la regresión");
  } else {
    fake().setNextActionResult({
      success: false, dryRun: true,
      message: "Inspección fallida con datos parciales",
      data: { status: "FAILED", brokenCount: 0, dataOnlyCount: 0 }
    });
    browserButton.click();
    for (let i = 0; i < 200 && browserButton.hasAttribute("aria-busy"); i++)
      await wait(25);
    const status = document.querySelector("#module-result-status");
    if (status?.textContent?.trim() === "OK" ||
        status?.classList.contains("badge-green"))
      errors.push("Resultado de salud de extensiones fallido mostrado falsamente como OK");
    if (!document.querySelector("#module-result-content")?.textContent?.includes("Inspección fallida"))
      errors.push("Resultado de salud de extensiones fallido perdió su mensaje");
  }

  // Even if a failed analysis returns candidates, its WRITE follow-up
  // must be inert while retaining the partial diagnostic evidence.
  document.querySelector('.nav-item[data-view="memory"]')?.click();
  await wait(35);
  const trimPreview = document.querySelector(
    '#module-actions button[data-action="memory.trim.preview"]');
  if (!trimPreview) {
    errors.push("No se pudo probar un análisis MemoryTrim fallido con candidatos");
  } else {
    fake().setNextActionResult({
      success: false, dryRun: true, message: "Fallo con candidatos incompletos",
      data: { observedProcesses: 1,
        candidates: [{ name: "QA fixture", processId: 98765, workingSetBytes: 65536 }] }
    });
    trimPreview.click();
    for (let i = 0; i < 200 && trimPreview.hasAttribute("aria-busy"); i++) await wait(25);
    const unsafeButtons = [...document.querySelectorAll("#module-result-content button")]
      .filter(control => !control.disabled);
    if (unsafeButtons.length > 0)
      errors.push("El resultado fallido habilita acciones WRITE a partir de candidatos parciales");
    if (!document.querySelector("#module-result-content")?.textContent?.includes("Fallo con candidatos"))
      errors.push("Se perdió la evidencia parcial de MemoryTrim");
  }

  // A second cleanup preview that fails must invalidate the previous
  // recoverable estimate, even if the old scan detected SAFE bytes.
  document.querySelector('.nav-item[data-view="cleanup"]')?.click();
  await wait(35);
  const preview = document.querySelector(
    '#module-actions button[data-action="disk.cleanup.safe"]');
  if (!preview) {
    errors.push("Falta el DRY_RUN de limpieza segura en el módulo");
  } else {
    fake().setNextActionResult({
      success: true, dryRun: true,
      message: "Previsualización de QA",
      data: { estimatedBytes: 2 * 1024 ** 3,
        categories: [{ label: "Temporales de usuario", bytes: 2 * 1024 ** 3, risk: "SAFE" }] }
    });
    preview.click();
    for (let i = 0; i < 200 && preview.hasAttribute("aria-busy"); i++) await wait(25);
    if (!document.querySelector("#storage-recoverable")?.textContent?.includes("2"))
      errors.push("No se registró el recuperable inicial del DRY_RUN");
    fake().setFailNextRun();
    preview.click();
    for (let i = 0; i < 200 && preview.hasAttribute("aria-busy"); i++) await wait(25);
    if (document.querySelector("#storage-recoverable")?.textContent?.includes("2") ||
        document.querySelector("#module-summary")?.textContent?.includes("2 GB"))
      errors.push("Previsualización fallida conserva cifras recuperables antiguas");
    if (document.querySelector("#recovery-body")?.textContent?.includes("2 GB"))
      errors.push("Previsualización fallida conserva filas de limpieza antiguas");
    fake().setNextActionResult({
      success: true, dryRun: true,
      message: "No hay archivos regenerables seguros",
      data: { estimatedBytes: 0, categories: [] }
    });
    preview.click();
    for (let i = 0; i < 200 && preview.hasAttribute("aria-busy"); i++) await wait(25);
    const cleanupExecute = document.querySelector(
      '#module-actions button[data-action="disk.cleanup.execute"]');
    if (cleanupExecute && !cleanupExecute.disabled)
      errors.push("DRY_RUN de cero bytes permite ejecutar limpieza WRITE");
    if (document.querySelector("#storage-recoverable")?.textContent?.includes("2"))
      errors.push("DRY_RUN sin candidatos mantiene bytes obsoletos");
    if (document.querySelector("#recovery-body")?.textContent?.includes("Temporales de usuario"))
      errors.push("DRY_RUN sin candidatos conserva la tabla anterior");
  }
  return { errors, requests: fake().requests };
})()
`;
const degradedEvaluated = await call("Runtime.evaluate", {
  expression: degradedExpression,
  awaitPromise: true,
  returnByValue: true
});
const degraded = degradedEvaluated.result?.value;
if (!degraded) throw new Error("UI smoke degradado no devolvió resultado.");
if (exceptions.length) degraded.errors.push(...exceptions);
if (degraded.errors.length)
  throw new Error("UI_SMOKE_DEGRADED_FAIL\n" + degraded.errors.join("\n"));

console.log(
  `UI_SMOKE_DEGRADED_OK requests=${degraded.requests.length} recovered=snapshot,catalog,baseline`
);

console.log(
  `UI_SMOKE_OK developerButtons=${result.developerButtons} compactButtons=${result.compactButtons} dashboardActions=${result.actions} moduleButtons=${result.moduleButtonsSeen} moduleActionsExecuted=${result.moduleActionsExecuted} contextualFlows=${result.contextualFlowsExecuted} writeFlows=${result.writeFlowsExecuted} buttons=${result.literalButtons}`
);

} finally {
  // A failed assertion must not leave the browser and the HTTP server
  // running: that keeps Node alive and stalls CI until its timeout.
  await cleanup(socket);
}
