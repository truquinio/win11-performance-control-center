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
let target;
for (let i = 0; i < 40; i++) {
  try {
    const pages = await (await fetch(`http://127.0.0.1:${debugPort}/json`)).json();
    target = pages.find(item => item.type === "page");
    if (target?.webSocketDebuggerUrl) break;
  } catch {}
  await sleep(100);
}
if (!target?.webSocketDebuggerUrl) throw new Error("CDP no disponible.");

const socket = new WebSocket(target.webSocketDebuggerUrl);
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

await call("Runtime.enable");
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

  const developerButtons = [...document.querySelectorAll(".developer-nav")].filter(visible);
  let moduleButtonsSeen = 0;
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
      if (actionButton.disabled && !actionButton.title)
        errors.push("Acción deshabilitada sin explicación: " + actionButton.textContent.trim());
      if (!actionButton.disabled && !actionButton.textContent.trim())
        errors.push("Acción de módulo sin etiqueta: " + button.textContent.trim());
    }
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

  const diagnostic = document.querySelector("#diagnostic-btn");
  if (!diagnostic || !diagnostic.textContent.trim())
    errors.push("Diagnóstico completo ausente o sin etiqueta");

  return {
    errors,
    developerButtons: developerButtons.length,
    compactButtons: [...document.querySelectorAll(".nav-item")].filter(visible).length,
    actions: actions.length,
    moduleButtonsSeen,
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

console.log(
  `UI_SMOKE_OK developerButtons=${result.developerButtons} compactButtons=${result.compactButtons} dashboardActions=${result.actions} moduleButtons=${result.moduleButtonsSeen} buttons=${result.literalButtons}`
);

socket.close();
edgeProcess.kill();
await sleep(300);
server.close();
try {
  fs.rmSync(profile, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 });
} catch {
  // Edge can briefly retain profile handles after headless shutdown.
}
