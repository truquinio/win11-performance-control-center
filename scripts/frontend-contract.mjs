import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const html = fs.readFileSync(path.join(root, "src", "Frontend", "index.html"), "utf8");
const app = fs.readFileSync(path.join(root, "src", "Frontend", "app.ts"), "utf8");
const styles = fs.readFileSync(path.join(root, "src", "Frontend", "style.css"), "utf8");
if (/transition\s*:\s*(?:all\b|(?:\d*\.\d+|\d+)(?:s|ms)\s*(?=[;}]))/i.test(styles))
  throw new Error("Las transiciones deben enumerar propiedades; evitar animar todos los atributos y el layout.");
if (!html.includes('<a class="skip-link" href="#main-workspace">') ||
    !html.includes('<main id="main-workspace" class="workspace" tabindex="-1">') ||
    !styles.includes(".skip-link:focus-visible"))
  throw new Error("La navegación por teclado necesita un enlace visible al enfocar para saltar al contenido principal.");
if (!html.includes('<div id="toast-host" class="toast-host" aria-live="polite">') ||
    !app.includes('toast(result.success ? "Acción completada"') ||
    !app.includes('toast("Acción fallida", message, "error");'))
  throw new Error("Los resultados y errores de acción deben anunciarse en la región viva.");
if (!html.includes('<strong id="tablet-system-status" aria-live="polite" aria-atomic="true">') ||
    !app.includes("tabletStatus.textContent !== nextTabletStatus") ||
    !html.includes('id="tablet-system-status"') ||
    !html.includes('id="tablet-status-source"') ||
    !html.includes('id="tablet-state-badge" role="status" aria-live="polite" aria-atomic="true"') ||
    !app.includes("tabletBadge.textContent !== state") ||
    !html.includes('id="tablet-state-badge"') ||
    !app.includes('byId("tablet-system-status")') ||
    !app.includes('byId("tablet-status-source")') ||
    !app.includes('getElementById("tablet-state-badge")') ||
    !app.includes('provider.surface === "demo"') ||
    !app.includes('Datos simulados · no se accede a Windows') ||
    !styles.includes("body[data-mode=\"compact\"] .tablet-status-panel{display:flex;grid-column:1/-1;order:0}"))
  throw new Error("El resumen tablet debe distinguir DEMO de telemetría local y mostrar el estado operativo real.");
const actionRenderer = fs.readFileSync(path.join(root, "src", "Frontend", "components", "action-results.ts"), "utf8");
if (app.includes("function renderStructuredResult(") ||
    !app.includes("configureResultRenderer({") ||
    !actionRenderer.includes("export function renderStructuredResult(") ||
    !actionRenderer.includes("getCurrentCatalog()"))
  throw new Error("El frontend volvió a acoplar renderizado, catálogo o ejecución al monolito.");
if (!actionRenderer.includes('Cobertura de políticas') ||
    !actionRenderer.includes('No acredita ejecución física'))
  throw new Error('Auditoría WRITE debe diferenciar cobertura de políticas y certificación física.');
if (!app.includes('SNAPSHOT_RETAINED_REVIEW_REQUIRED') ||
    !app.includes('El estado coincide con el baseline guardado'))
  throw new Error('La UI debe explicar el interlock del snapshot pagefile retenido.');
if (!app.includes('data.nameCollisions') ||
    !actionRenderer.includes('Nombres repetidos') ||
    !actionRenderer.includes('item.extensionIds') ||
    !actionRenderer.includes('desinstalar manualmente'))
  throw new Error('La auditoría Edge debe diferenciar colisiones de nombres e IDs sin ofrecer borrado automático.');
if (app.split(/\r?\n/).length > 1500)
  throw new Error("app.ts volvió a exceder el límite modular de 1500 líneas.");
const modules = fs.readFileSync(path.join(root, "src", "Frontend", "module-definitions.ts"), "utf8");
const localProvider = fs.readFileSync(path.join(root, "src", "Frontend", "providers", "local.ts"), "utf8");
const generatedCatalog = JSON.parse(fs.readFileSync(path.join(root, "src", "Frontend", "generated", "action-catalog.json"), "utf8"));
const backendCatalog = fs.readFileSync(
  path.join(root, "src", "App", "Core", "ActionCatalog.cs"),
  "utf8");
const backendExecutor = fs.readFileSync(
  path.join(root, "src", "App", "Core", "ActionExecutor.cs"),
  "utf8");

const views = [...html.matchAll(/data-view="([^"]+)"/g)].map(match => match[1]);
const definitionBlock = modules.match(/const moduleDefinitions:[\s\S]*?= \{([\s\S]*?)\r?\n\};/)?.[1] ?? "";
const definitions = new Set(
  [...definitionBlock.matchAll(/^\s*(?:"([^"]+)"|([A-Za-z][\w-]*)):\s*\{/gm)]
    .map(match => match[1] ?? match[2])
);

const missingViews = [...new Set(views)]
  .filter(view => view !== "dashboard" && !definitions.has(view));
if (missingViews.length) {
  throw new Error("Vistas sin módulo: " + missingViews.join(", "));
}

const developerViews = [...html.matchAll(
  /<button class="[^"]*developer-nav[^"]*" data-view="([^"]+)"/g
)].map(match => match[1]);
const duplicateDeveloperViews = developerViews
  .filter((view, index) => developerViews.indexOf(view) !== index);
if (duplicateDeveloperViews.length) {
  throw new Error(
    "Botones Developer comparten una ruta y parecen inertes: " +
    [...new Set(duplicateDeveloperViews)].join(", ")
  );
}

const actionButtons = [...html.matchAll(
  /<button[^>]*class="[^"]*action-trigger[^"]*"[^>]*data-action="([^"]*)"/g
)].map(match => match[1]);
if (actionButtons.some(action => !action.trim())) {
  throw new Error("Hay botones action-trigger sin Action ID.");
}

const demoActions = new Set(generatedCatalog.map(action => action.id));
if (demoActions.size !== generatedCatalog.length) {
  throw new Error("Catálogo generado con Action IDs duplicadas.");
}
const missingActions = [...new Set(actionButtons)]
  .filter(action => !demoActions.has(action));
if (missingActions.length) {
  throw new Error("Botones sin Action catalogada: " + missingActions.join(", "));
}

const backendActions = new Set(
  [...backendCatalog.matchAll(/new ActionDefinition\("([^"]+)"/g)]
    .map(match => match[1])
);
const executorSwitch = backendExecutor.match(
  /return action\.Id switch\s*\{([\s\S]*?)_ => throw/
)?.[1];
if (!executorSwitch)
  throw new Error("No se pudo localizar ExecuteAllowedAsync en ActionExecutor.");

const executorActions = new Set(
  [...executorSwitch.matchAll(/"([^"]+)"\s*=>/g)]
    .map(match => match[1])
);
const catalogWithoutExecutor = [...backendActions]
  .filter(action => !executorActions.has(action));
const executorWithoutCatalog = [...executorActions]
  .filter(action => !backendActions.has(action));
if (catalogWithoutExecutor.length || executorWithoutCatalog.length) {
  throw new Error(
    "ActionCatalog/ActionExecutor desalineados. " +
    "Sin implementación: " + (catalogWithoutExecutor.join(", ") || "ninguno") +
    " · Sin catálogo: " + (executorWithoutCatalog.join(", ") || "ninguno")
  );
}

const frontendOnly = [...demoActions].filter(action => !backendActions.has(action));
const backendOnly = [...backendActions].filter(action => !demoActions.has(action));
if (frontendOnly.length || backendOnly.length) {
  throw new Error(
    "Catálogos frontend/backend desalineados. " +
    "Solo frontend: " + (frontendOnly.join(", ") || "ninguno") +
    " · Solo backend: " + (backendOnly.join(", ") || "ninguno")
  );
}

const moduleActionIds = [...definitionBlock.matchAll(/"([a-z][a-z0-9.-]+)"/g)]
  .map(match => match[1])
  .filter(value => value.includes("."));
const missingModuleActions = [...new Set(moduleActionIds)]
  .filter(action => !demoActions.has(action));
if (missingModuleActions.length) {
  throw new Error(
    "Módulos con Action IDs inexistentes: " + missingModuleActions.join(", ")
  );
}

const literalButtons = [...html.matchAll(/<button\b([^>]*)>/g)]
  .map(match => match[1]);
const inertButtons = literalButtons.filter(attrs => {
  if (/data-view="[^"]+"/.test(attrs)) return false;
  if (/class="[^"]*action-trigger[^"]*"/.test(attrs) &&
      /data-action="[^"]+"/.test(attrs)) return false;
  const id = attrs.match(/id="([^"]+)"/)?.[1];
  return !["diagnostic-btn", "refresh-btn", "mode-toggle"].includes(id ?? "");
});
if (inertButtons.length) {
  throw new Error(
    "Botones literales sin contrato de interacción: " +
    inertButtons.map(attrs => "<button" + attrs + ">").join(" | ")
  );
}

for (const controlId of ["diagnostic-btn", "refresh-btn", "mode-toggle"]) {
  const quoted = `"${controlId}"`;
  if (!app.includes(quoted))
    throw new Error("Control sin binding en app.ts: " + controlId);
}

// The backend coordinator runs one action at a time, so firing several
// actions in parallel always makes all but the first fail.
const actionTimeoutMatch = localProvider.match(
  /const defaultActionTimeoutMs = (\d+);/
);
if (!actionTimeoutMatch) {
  throw new Error("No se pudo verificar el timeout base de actions.run.");
}
const actionTimeoutFloor = Number(actionTimeoutMatch[1]);
if (actionTimeoutFloor < 270000) {
  throw new Error(
    "El timeout del frontend expira antes que el presupuesto máximo del backend: " +
    actionTimeoutFloor + " ms"
  );
}

const repairTimeoutMatch = localProvider.match(
  /const integrityRepairTimeoutMs = (\d+) \* (\d+) \* (\d+);/
);
if (!repairTimeoutMatch ||
    !localProvider.includes('id === "system.integrity.repair"')) {
  throw new Error(
    "No se pudo verificar el presupuesto extendido de la reparación de integridad."
  );
}
const repairTimeoutMs =
  Number(repairTimeoutMatch[1]) *
  Number(repairTimeoutMatch[2]) *
  Number(repairTimeoutMatch[3]);
if (repairTimeoutMs < 55 * 60 * 1000) {
  throw new Error(
    "El timeout de reparación de integridad es menor al presupuesto del helper elevado: " +
    repairTimeoutMs + " ms"
  );
}

const parallelActions = [...app.matchAll(/Promise\.all\(\s*\[([\s\S]*?)\]\s*\)/g)]
  .filter(match => (match[1].match(/\brunAction\(/g) ?? []).length > 1);
if (parallelActions.length) {
  throw new Error(
    "Acciones lanzadas en paralelo contra un backend de operación única: " +
    parallelActions.length
  );
}

// Snapshot polling must not rebuild module action buttons (focus loss).
const snapshotRenderer = app.match(/function renderSnapshot\([\s\S]*?\r?\n\}\r?\n/)?.[0] ?? "";
if (!snapshotRenderer)
  throw new Error("No se encontró renderSnapshot en app.ts.");
if (/\brenderModuleView\(/.test(snapshotRenderer)) {
  throw new Error(
    "renderSnapshot reconstruye las acciones del módulo en cada refresco."
  );
}

// Tablet widths follow the supplied portrait hierarchy: top navigation and paired content.
const narrowShellRule = styles.match(
  /@media\s*\(max-width:\s*720px\)\s*\{\s*\.app-shell,\s*body\[data-mode="developer"\]\s*\.app-shell\s*\{([^}]*)\}/
)?.[1] ?? "";
const narrowSidebarRule = styles.match(
  /@media\s*\(max-width:\s*720px\)[\s\S]*?\.sidebar,\s*body\[data-mode="developer"\]\s*\.sidebar\s*\{([^}]*)\}/
)?.[1] ?? "";
const narrowNavRule = styles.match(
  /@media\s*\(max-width:\s*720px\)[\s\S]*?\.nav\s*\{([^}]*)\}/
)?.[1] ?? "";
const narrowMetricsRule = styles.match(
  /@media\s*\(max-width:\s*720px\)[\s\S]*?\.metrics-grid,\s*body\[data-mode="developer"\]\s*\.metrics-grid\s*\{([^}]*)\}/
)?.[1] ?? "";
const narrowActionsRule = styles.match(
  /@media\s*\(max-width:\s*720px\)[\s\S]*?\.actions-grid,\s*body\[data-mode="developer"\]\s*\.actions-grid\s*\{([^}]*)\}/
)?.[1] ?? "";
const phoneStyles = styles.slice(styles.lastIndexOf("@media (max-width:480px)"));
if (!/display:\s*flex/.test(narrowShellRule) ||
    !/flex-direction:\s*column/.test(narrowShellRule) ||
    !/width:\s*100%/.test(narrowSidebarRule) ||
    !/flex-direction:\s*row/.test(narrowNavRule) ||
    !/overflow-x:\s*auto/.test(narrowNavRule) ||
    !/grid-template-columns:\s*repeat\(2,minmax\(0,1fr\)\)/.test(narrowMetricsRule) ||
    !/grid-template-columns:\s*repeat\(2,minmax\(0,1fr\)\)/.test(narrowActionsRule) ||
    !/\.metrics-grid,[\s\S]*?grid-template-columns:\s*minmax\(0,1fr\)/.test(phoneStyles) ||
    !/body\[data-mode="compact"\] #dashboard-view>\.action-section \.actions-grid\s*\{[^}]*grid-template-columns:\s*minmax\(0,1fr\)/.test(phoneStyles)) {
  throw new Error("La tableta debe mostrar navegación superior y bloques a dos columnas; el teléfono, una columna.");
}

console.log(
  `FRONTEND_CONTRACT_OK views=${new Set(views).size} developerViews=${developerViews.length} actionButtons=${actionButtons.length} literalButtons=${literalButtons.length}`
);
