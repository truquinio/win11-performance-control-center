import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const html = fs.readFileSync(path.join(root, "src", "Frontend", "index.html"), "utf8");
const app = fs.readFileSync(path.join(root, "src", "Frontend", "app.ts"), "utf8");
const backendCatalog = fs.readFileSync(
  path.join(root, "src", "App", "Core", "ActionCatalog.cs"),
  "utf8");

const views = [...html.matchAll(/data-view="([^"]+)"/g)].map(match => match[1]);
const definitionBlock = app.match(/const moduleDefinitions:[\s\S]*?= \{([\s\S]*?)\n\};/)?.[1] ?? "";
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

const demoActions = new Set(
  [...app.matchAll(/\{ id: "([^"]+)", title:/g)].map(match => match[1])
);
const missingActions = [...new Set(actionButtons)]
  .filter(action => !demoActions.has(action));
if (missingActions.length) {
  throw new Error("Botones sin Action catalogada: " + missingActions.join(", "));
}

const backendActions = new Set(
  [...backendCatalog.matchAll(/new ActionDefinition\("([^"]+)"/g)]
    .map(match => match[1])
);
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
  return !["diagnostic-btn", "mode-toggle"].includes(id ?? "");
});
if (inertButtons.length) {
  throw new Error(
    "Botones literales sin contrato de interacción: " +
    inertButtons.map(attrs => "<button" + attrs + ">").join(" | ")
  );
}

for (const controlId of ["diagnostic-btn", "mode-toggle"]) {
  const quoted = `"${controlId}"`;
  if (!app.includes(quoted))
    throw new Error("Control sin binding en app.ts: " + controlId);
}

console.log(
  `FRONTEND_CONTRACT_OK views=${new Set(views).size} developerViews=${developerViews.length} actionButtons=${actionButtons.length} literalButtons=${literalButtons.length}`
);
